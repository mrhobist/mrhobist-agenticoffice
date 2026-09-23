"""Anthropic saglayicisi — Claude Agent SDK uzerinden.

Ayri API anahtari yoktur: SDK, makinedeki `claude.exe` (Claude Code) oturumunu
kullanir. Farkli Windows kullanicilari farkli oturumdur. Yapisal cikti
`output_format={"type": "json_schema", ...}` ile alinir ve
`ResultMessage.structured_output` uzerinden okunur.

Burada is mantigi YOKTUR: istek geldigi gibi tek bir cagriya cevrilir.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import claude_agent_sdk as sdk
import httpx
from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKError,
    CLINotFoundError,
    ProcessError,
    PermissionResultAllow,
    PermissionResultDeny,
    ResultMessage,
    TextBlock,
    ToolPermissionContext,
    ToolUseBlock,
)
from fastapi import HTTPException

from .. import credentials
from ..contracts import (
    DESTINATION_OF,
    AuthStatus,
    LoginRequest,
    LoginStarted,
    ModelInfo,
    ProviderLimits,
    ToolUse,
    UsageLimit,
    TurnRequest,
    TurnResponse,
    Usage,
)

PROVIDER_NAME = "anthropic"

#: Yalniz ek kesif: listelenen modellerin tek dogru kaynagi .NET'in config/models.json'udur (ModelListService);
#: yeni model icin burayi degil o dosyayi guncelle.
ANTHROPIC_MODELS: list[str] = [
    "claude-fable-5-1",
    "claude-opus-5",
    "claude-sonnet-5",
    "claude-haiku-4-5-20251001",
]

#: Kimlik onbellegi omru (saniye). Is durumu degil, oturum bilgisidir.
AUTH_CACHE_TTL_S = 60.0
#: Kalan kullanim onbellegi (saniye): ust bar 60 s'de bir yoklar, saglayiciyi dovmesin.
LIMITS_CACHE_TTL_S = 90.0
#: Kota ucu 429 verdiyse bu sure yeniden sorulmaz (uc sik sorguyu cezalandiriyor); son iyi deger gosterilir.
LIMITS_BACKOFF_429_S = 600.0


#: Bu uzunlugu asan sistem istemi komut satiri yerine gecici dosyadan verilir. Windows'ta komut satiri ~32k karakter:
#: 2026-09-23'te iki ek bilgi dosyasi + ajan md'si (~24 KB) + --json-schema siniri asti ve surec "Access is denied"
#: (40 KB'ta "claude.exe bulunamadi") diye YANILTICI bir hatayla hic baslamadi. Esik kucuk tutuldu: kisa istemler eskisi gibi.
PROMPT_FILE_THRESHOLD = 8000


def _write_prompt_file(text: str) -> str | None:
    """Uzun istemi gecici dosyaya yazar, yolunu dondurur; kisaysa None. Tasima ayrintisi: cagri bitince silinir,
    durum degildir (CLAUDE.md §1: runtime is ciktisi yazmaz -- bu dosya tek cagrinin argumanidir)."""
    if len(text) <= PROMPT_FILE_THRESHOLD:
        return None
    fd, path = tempfile.mkstemp(prefix="aiteam-prompt-", suffix=".md")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    return path


def _limits_cache_file() -> Path:
    """Son iyi kota degeri: yeniden baslatmada bar bos kalmasin (kullanici profili, credentials ile ayni dizin; kimlik icermez).
    Yol her cagrida cozulur: testler AITEAM_CREDENTIALS_FILE ile gecici dizine yonlendirir."""
    return credentials.credentials_path().with_name("limits-anthropic.json")
#: Claude Code'un kendi /usage ekraninin okudugu uc; oturum belirteci CLI'nin dosyasindan alinir, hicbir yere yazilmaz.
USAGE_URL = "https://api.anthropic.com/api/oauth/usage"
#: API anahtari dogrulamasi icin (GET /v1/models: token harcamaz).
API_BASE = "https://api.anthropic.com"


def _credentials_path() -> Path:
    base = os.environ.get("CLAUDE_CONFIG_DIR") or str(Path.home() / ".claude")
    return Path(base) / ".credentials.json"


DETAIL_LOGGED_IN = "Claude Code oturumu: giriş var"


def _opt_str(v: Any) -> str | None:
    """Kota yanitindaki serbest alanlar: str degilse None (pydantic 'str | None' sayiyi reddeder)."""
    return v if isinstance(v, str) and v else None


def _scope_name(v: Any) -> str | None:
    """
    Kota penceresinin kapsami -> gosterilecek ad. Ust uc NESNE doner:
    ``{"model": {"display_name": "Fable", "id": ...}, "surface": ...}``. Duz string bekleyen
    okuyucu bunu sessizce None'a dusuruyordu ve modele ozel pencere "hafta" diye etiketleniyordu
    (kullanici 2026-09-20). Eski duz string bicimi de kabul edilir.
    """
    if isinstance(v, str):
        return v or None
    if not isinstance(v, dict):
        return None
    model = v.get("model")
    if isinstance(model, dict):
        for key in ("display_name", "id"):
            name = model.get(key)
            if isinstance(name, str) and name:
                return name
    elif isinstance(model, str) and model:
        return model
    surface = v.get("surface")
    return surface if isinstance(surface, str) and surface else None


def _pct(v: Any) -> float:
    """Kullanilan yuzde; sayi degilse 0."""
    try:
        return float(v)
    except (TypeError, ValueError):
        return 0.0


def _iso(v: Any) -> str | None:
    """Sifirlanma zamani: ISO metin oldugu gibi; epoch (s ya da ms) ISO'ya cevrilir; baska sey None."""
    if isinstance(v, str):
        return v or None
    if isinstance(v, (int, float)) and not isinstance(v, bool):
        secs = v / 1000.0 if v > 1e11 else float(v)
        return datetime.fromtimestamp(secs, tz=timezone.utc).isoformat()
    return None


DETAIL_LOGGED_OUT = (
    "giriş yok — terminalde `claude login` çalıştır (aynı Windows kullanıcısı)"
)


def _natural_key(text: str) -> list[int | str]:
    return [int(p) if p.isdigit() else p for p in re.split(r"(\d+)", text)]


def find_claude_cli() -> str | None:
    """`claude.exe` yolu: env -> %APPDATA%\\Claude\\claude-code\\<en yeni>\\claude.exe -> None (SDK arar)."""
    env_path = os.environ.get("CLAUDE_CLI_PATH")
    if env_path:
        return env_path

    appdata = os.environ.get("APPDATA")
    if appdata:
        base = Path(appdata) / "Claude" / "claude-code"
        if base.is_dir():
            versions = sorted(
                (d for d in base.iterdir() if d.is_dir() and (d / "claude.exe").is_file()),
                key=lambda d: _natural_key(d.name),
                reverse=True,
            )
            if versions:
                return str(versions[0] / "claude.exe")
    return None



def _auth_failed(text: str) -> bool:
    """CLI oturumu yok/gecersiz: 'Not logged in', 'authentication_failed', 'invalid api key' vb."""
    t = text.lower()
    return any(k in t for k in ("not logged in", "authentication_failed", "authentication failed", "invalid api key", "please run /login"))


def _limit_reached(text: str) -> bool:
    """
    Saglayici kota penceresi doldu: 'usage limit reached', 'rate_limit', 429...

    LimitGuard cagri ONCESI bakar ama yuzdeleri 90 s onbellekler; pencere tam o aralikta dolarsa tur
    yine de baslar ve saglayici reddeder. Bu bir HATA degil BEKLEMEDIR: ayri kodla donmezse .NET onu
    "provider_error" sanip calismayi Failed yapar ve pencere sifirlaninca kendiliginden surmez
    (2026-09-22). Siniflandirma burada, KARAR .NET'te: runtime yalniz saglayicinin cevabini adlandirir.
    """
    t = text.lower()
    return any(k in t for k in ("usage limit reached", "rate_limit", "rate limit", "429", "quota exceeded", "limit exceeded"))


def _error(status: int, code: str, message: str) -> HTTPException:
    return HTTPException(status_code=status, detail={"errorCode": code, "message": message})


def _classify(exc: Exception) -> HTTPException:
    text = str(exc)
    if isinstance(exc, CLINotFoundError):
        return _error(503, "runtime.cli_missing", f"claude.exe bulunamadı: {text}")
    if _auth_failed(text):
        return _error(503, "runtime.not_logged_in", text)
    if _limit_reached(text):
        return _error(503, "runtime.provider_limit", text)
    return _error(502, "runtime.provider_error", text)


async def _report_progress(url: str | None, use: ToolUse) -> None:
    """Canli arac akisi: .NET'e tek POST, 2 s zaman asimi, hata yutulur (akis gorunurluk icindir, turu bozmaz)."""
    if not url:
        return
    try:
        async with httpx.AsyncClient(timeout=2.0) as client:
            await client.post(url, json=use.model_dump())
    except Exception:  # noqa: BLE001 — bildirim basarisizligi turu etkilemez
        pass


async def _report_progress(url: str | None, use: ToolUse) -> None:
    """Canli arac akisi: .NET'e tek POST, 2 s zaman asimi, hata yutulur (akis gorunurluk icindir, turu bozmaz)."""
    if not url:
        return
    try:
        async with httpx.AsyncClient(timeout=2.0) as client:
            await client.post(url, json=use.model_dump())
    except Exception:  # noqa: BLE001 — bildirim basarisizligi turu etkilemez
        pass


class AnthropicProvider:
    name = PROVIDER_NAME

    def __init__(self, cli_path: str | None = None) -> None:
        self._cli_path = cli_path
        self._auth_cache: tuple[float, AuthStatus] | None = None
        self._limits_cache: tuple[float, ProviderLimits] | None = None
        self._limits_backoff_until = 0.0
        self._limits_last_good: ProviderLimits | None = self._load_last_good()

    # -- yardimcilar -------------------------------------------------------

    @property
    def cli(self) -> str | None:
        return self._cli_path or find_claude_cli()

    def _options(self, request: TurnRequest, prompt_file: str | None = None) -> ClaudeAgentOptions:
        tools = list(request.tools or [])
        opts: dict[str, Any] = {
            "model": request.model,
            # Mod .NET'ten gelir (adim turune gore), burasi yalniz SDK sekline esler:
            #   replace     -> duz string: Claude Code'un kendi kilavuzu SILINIR (plan ureten adimlar; preset
            #                  altinda buyuk Spec semasi doldurulamiyordu -- 5 denemede 'rules'/'tasks' eksik).
            #   claude_code -> preset + append: kilavuz korunur (yurutme adimlari; kilavuzsuz 55 ic tur,
            #                  Claude Code 11-16). Preset her rolde ayni -> roller arasi ortak onbellek on eki.
            "system_prompt": (
                {"type": "preset", "preset": "claude_code", "append": request.system_prompt}
                if request.system_prompt_mode == "claude_code"
                else request.system_prompt
            ),
            "effort": request.reasoning_effort,
            "cli_path": self.cli,
        }
        if prompt_file:
            # Uzun istem komut satirina sigmaz (bkz. PROMPT_FILE_THRESHOLD): ayni metin dosyadan okunur.
            if request.system_prompt_mode == "claude_code":
                opts["system_prompt"] = {"type": "preset", "preset": "claude_code"}
                opts["extra_args"] = {"append-system-prompt-file": prompt_file}
            else:
                opts["system_prompt"] = {"type": "file", "path": prompt_file}
        if tools:
            # Aracli tur (kullanici karari 2026-09-19: developer/testci dosyayi kendisi yazar, testi kendisi kosar).
            # Izin listesi ve dizin .NET'ten gelir; runtime secmez. Sunucu etkilesimsizdir, izin sorusu soracak
            # kimse yok: her arac cagrisi `_guard` ile karar bulur — dosya yazma yalniz cwd altinda, gerisi izinli.
            # `allowed_tools` VERILMEZ: verilirse SDK araci geri cagriyi sormadan onaylar (CanUseToolShadowedWarning) ve
            # yazma siniri devre disi kalir. Arac kumesi `tools`, her cagrinin karari `_guard`.
            opts["tools"] = tools
            opts["can_use_tool"] = self._guard(request.cwd)
            opts["max_turns"] = request.max_turns or 80
            if request.cwd:
                opts["cwd"] = request.cwd
        else:
            # Yerlesik arac tanimlari prompt'a girmesin: 23k → 4.5k token / cagri (olculdu, 2026-09-19).
            opts["allowed_tools"] = []
            opts["tools"] = []
            # Yapisal cikti (json_schema) ikinci bir tur ister; 1 ile "Reached maximum number of turns" gelir.
            opts["max_turns"] = request.max_turns or 4
        if request.schema_ is not None:
            opts["output_format"] = {"type": "json_schema", "schema": request.schema_}
        key = credentials.api_key(PROVIDER_NAME)
        if key:
            # Kayitli API anahtari varsa CLI onu kullanir: fatura Anthropic Console'a, Claude Code oturumu devre disi.
            opts["env"] = {"ANTHROPIC_API_KEY": key}
        return ClaudeAgentOptions(**opts)

    #: Dosya degistiren araclar: hedef yol cwd disindaysa reddedilir.
    WRITE_TOOLS = frozenset({"Write", "Edit", "MultiEdit", "NotebookEdit"})

    #: Bash komutunda mutlak yol adaylari: `C:\...`, `C:/...`, `/...`, `~`. Dizin disi yol → ret.
    _ABS_PATH = re.compile(r"""(?<![\w.-])(?:[A-Za-z]:[\\/][^\s"'`;&|<>)]*|/[^\s"'`;&|<>)]+|~(?:[\\/][^\s"'`;&|<>)]*)?)""")
    #: Kok disina cikamayan ama gorunumde mutlak olan yollar (git bash / posix aygitlari).
    _BASH_ALLOW_PREFIXES = ("/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/tmp")

    @classmethod
    def _guard(cls, cwd: str | None):
        """Arac izin karari (SDK `can_use_tool`). Is kurali degil, sinir: dosya yazma ve Bash yalniz verilen dizinde."""
        root = Path(cwd).resolve() if cwd else None

        def inside(raw: str) -> bool:
            try:
                target = (root / raw).resolve() if not Path(raw).is_absolute() else Path(raw).resolve()
            except (OSError, ValueError):
                return False
            return root == target or root in target.parents

        async def decide(tool: str, tool_input: dict[str, Any], _ctx: ToolPermissionContext):
            if root is None:
                return PermissionResultAllow()
            if tool in cls.WRITE_TOOLS:
                raw = tool_input.get("file_path") or tool_input.get("notebook_path") or ""
                if not inside(raw):
                    return PermissionResultDeny(message=f"Bu dizinin disina yazilamaz: {raw}. Yalniz {root} altinda calis.")
            elif tool == "Bash":
                # Bash her yere ulasabilir (cat, rm, > yonlendirme): komuttaki mutlak yollar ve `..` denetlenir.
                cmd = str(tool_input.get("command") or "")
                if ".." in cmd.replace("...", ""):
                    return PermissionResultDeny(message=f"Komutta '..' kullanilamaz; yollar {root} altinda ve goreli olsun.")
                for m in cls._ABS_PATH.finditer(cmd):
                    raw = m.group(0)
                    if raw.startswith(cls._BASH_ALLOW_PREFIXES):
                        continue
                    if raw.startswith("~") or not inside(raw):
                        return PermissionResultDeny(message=f"Komut bu dizinin disina cikiyor: {raw}. Yalniz {root} altinda calis.")
            return PermissionResultAllow()

        return decide

    @staticmethod
    async def _stream(prompt: str):
        """`can_use_tool` akis modu ister: tek kullanici mesaji uretilir."""
        yield {
            "type": "user",
            "message": {"role": "user", "content": prompt},
            "parent_tool_use_id": None,
            "session_id": "default",
        }

    @staticmethod
    def _tool_target(block: ToolUseBlock) -> str | None:
        inp = block.input or {}
        for key in ("file_path", "path", "command", "pattern", "notebook_path", "url"):
            v = inp.get(key)
            if isinstance(v, str) and v:
                return v[:300]
        return None

    @staticmethod
    def _prompt(request: TurnRequest) -> str:
        return "\n\n".join(
            (m.content if m.role == "user" else f"[önceki yanıt]\n{m.content}")
            for m in request.messages
        )

    # -- arayuz ------------------------------------------------------------

    async def complete(self, request: TurnRequest) -> TurnResponse:
        parts: list[str] = []
        structured: Any = None
        cost: float | None = None
        usage_raw: dict[str, Any] = {}
        tool_uses: list[ToolUse] = []
        turns = 1
        started = time.monotonic()

        prompt_file = _write_prompt_file(request.system_prompt)
        try:
            prompt_text = self._prompt(request)
            prompt: Any = self._stream(prompt_text) if request.tools else prompt_text
            async for msg in sdk.query(prompt=prompt, options=self._options(request, prompt_file)):
                if isinstance(msg, AssistantMessage):
                    if msg.error:
                        err_text = f"Claude hatası: {msg.error}"
                        if _auth_failed(err_text):
                            raise _error(503, "runtime.not_logged_in", err_text)
                        if _limit_reached(err_text):
                            raise _error(503, "runtime.provider_limit", err_text)
                        raise _error(502, "runtime.provider_error", err_text)
                    for block in msg.content:
                        if isinstance(block, TextBlock):
                            parts.append(block.text)
                        elif isinstance(block, ToolUseBlock):
                            use = ToolUse(tool=block.name, target=self._tool_target(block))
                            tool_uses.append(use)
                            await _report_progress(request.progress_url, use)
                elif isinstance(msg, ResultMessage):
                    if msg.is_error:
                        detail = "; ".join(msg.errors or []) or msg.result or "bilinmiyor"
                        if _auth_failed(detail):
                            raise _error(503, "runtime.not_logged_in", detail)
                        if _limit_reached(detail):
                            raise _error(503, "runtime.provider_limit", detail)
                        raise _error(502, "runtime.provider_error", detail)
                    structured = msg.structured_output
                    cost = msg.total_cost_usd
                    usage_raw = msg.usage or {}
                    turns = int(msg.num_turns or 1)
                    if msg.result and not parts:
                        parts.append(msg.result)
        except HTTPException:
            raise
        except ClaudeSDKError as exc:
            raise _classify(exc) from exc
        finally:
            if prompt_file:
                Path(prompt_file).unlink(missing_ok=True)

        return TurnResponse(
            text="\n".join(parts).strip(),
            structured=structured,
            provider=PROVIDER_NAME,
            model=request.model,
            destination=DESTINATION_OF[PROVIDER_NAME],
            usage=Usage(
                # Girdi = dogrudan + onbellege yazilan + onbellekten okunan: kullanici "kac token gitti" diye bakar.
                input_tokens=int(usage_raw.get("input_tokens") or 0)
                + int(usage_raw.get("cache_creation_input_tokens") or 0)
                + int(usage_raw.get("cache_read_input_tokens") or 0),
                output_tokens=int(usage_raw.get("output_tokens") or 0),
                # Kirilim ayrica tasinir: toplam tek basina "baglam bosa mi gitti" sorusunu cevaplamaz.
                # Ajan araci dongusunde toplam her turda buyur ama buyuyen kismin cogu onbellekten okunur.
                cache_read_tokens=int(usage_raw.get("cache_read_input_tokens") or 0),
                cache_write_tokens=int(usage_raw.get("cache_creation_input_tokens") or 0),
            ),
            cost_usd=cost,
            duration_s=round(time.monotonic() - started, 3),
            attempts=1,
            tool_uses=tool_uses,
            turns=turns,
        )

    def auth(self, refresh: bool = False) -> AuthStatus:
        """`refresh=True` onbellegi atlar: kullanici `claude login` sonrasi "Yeniden kontrol et" dedi."""
        now = time.monotonic()
        if not refresh and self._auth_cache and now - self._auth_cache[0] < AUTH_CACHE_TTL_S:
            return self._auth_cache[1]
        status = self._probe_auth()
        self._auth_cache = (now, status)
        return status

    def _probe_auth(self) -> AuthStatus:
        key = credentials.api_key(PROVIDER_NAME)
        if key:
            src = "ortam değişkeni" if credentials.key_source(PROVIDER_NAME) == "env" else "kayıtlı"
            return AuthStatus(provider=PROVIDER_NAME, logged_in=True, account=credentials.mask(key), method="apikey",
                              detail=f"API anahtarı ({src}): çağrılar Anthropic Console faturasına yazılır.")
        cli = self.cli
        if not cli or not Path(cli).is_file():
            where = cli or "CLAUDE_CLI_PATH ve %APPDATA%\\Claude\\claude-code boş"
            return AuthStatus(
                provider=PROVIDER_NAME,
                logged_in=False,
                detail=f"claude.exe bulunamadı: {where}",
            )
        try:
            proc = subprocess.run(
                [cli, "auth", "status"], capture_output=True, text=True, timeout=20
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            return AuthStatus(
                provider=PROVIDER_NAME,
                logged_in=False,
                detail=f"claude auth status çalışmadı: {exc}",
            )
        try:
            data = json.loads(proc.stdout or "{}")
        except json.JSONDecodeError:
            data = {}
        logged_in = bool(data.get("loggedIn", False))
        account = data.get("email") or data.get("account") or data.get("accountEmail")
        if isinstance(account, dict):
            account = account.get("email") or account.get("name")
        return AuthStatus(
            provider=PROVIDER_NAME,
            logged_in=logged_in,
            account=str(account) if account else None,
            method="session" if logged_in else None,
            detail=DETAIL_LOGGED_IN if logged_in else DETAIL_LOGGED_OUT,
        )

    # -- kimlik: giris / cikis --------------------------------------------

    def login(self, request: LoginRequest) -> LoginStarted:
        """`claude auth login` yeni bir konsol penceresinde acilir; tarayicida onay kullanicinin isidir.

        Kimlik bilgisi (sifre, token) bu surecten GECMEZ: CLI kendi OAuth akisini yurutur.
        `apikey` modu istisna: anahtar dogrulanir, kullanici profiline yazilir, yanita yazilmaz.
        """
        if request.mode == "apikey":
            return self._login_api_key(request.api_key)
        cli = self.cli
        if not cli or not Path(cli).is_file():
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail="claude.exe bulunamadı; CLAUDE_CLI_PATH verin.")
        args = [cli, "auth", "login"]
        if request.mode == "console":
            args.append("--console")
        if request.email:
            args.extend(["--email", request.email])
        flags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0)
        try:
            subprocess.Popen(args, creationflags=flags, close_fds=True)  # noqa: S603 — sabit argumanlar
        except OSError as exc:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"giriş başlatılamadı: {exc}")
        self._auth_cache = None
        return LoginStarted(
            provider=PROVIDER_NAME,
            started=True,
            detail="Konsol penceresi açıldı; tarayıcıda Anthropic hesabınla onayla, sonra 'Yeniden kontrol et'.",
        )

    def _login_api_key(self, key: str | None) -> LoginStarted:
        key = (key or "").strip()
        if not key:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail="API anahtarı boş.")
        try:
            r = httpx.get(f"{API_BASE}/v1/models", headers={"x-api-key": key, "anthropic-version": "2023-06-01"}, timeout=15)
        except httpx.HTTPError as exc:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"Anthropic'e ulaşılamadı, anahtar doğrulanamadı: {exc.__class__.__name__}")
        if r.status_code == 401:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail="Anahtar reddedildi (401); kaydedilmedi.")
        if r.status_code >= 400:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"Doğrulama HTTP {r.status_code}; kaydedilmedi.")
        credentials.store_api_key(PROVIDER_NAME, key)
        self._auth_cache = None
        self._limits_cache = None
        return LoginStarted(provider=PROVIDER_NAME, started=True, detail=f"API anahtarı doğrulandı ve kaydedildi ({credentials.mask(key)}). Claude Code oturumu artık kullanılmaz; anahtarı silince oturuma dönülür.")

    def logout(self) -> AuthStatus:
        """Kayitli API anahtari varsa once o silinir (oturuma donulur); yoksa CLI oturumu kapatilir."""
        source = credentials.key_source(PROVIDER_NAME)
        if source == "env":
            st = self.auth(refresh=True)
            return st.model_copy(update={"detail": "API anahtarı ortam değişkeninden (ANTHROPIC_API_KEY) geliyor; buradan silinemez."})
        if source == "file":
            credentials.delete_api_key(PROVIDER_NAME)
            self._auth_cache = None
            self._limits_cache = None
            return self.auth(refresh=True)
        cli = self.cli
        if cli and Path(cli).is_file():
            try:
                subprocess.run([cli, "auth", "logout"], capture_output=True, text=True, timeout=30)
            except (OSError, subprocess.TimeoutExpired):
                pass
        self._auth_cache = None
        return self.auth(refresh=True)

    # -- kalan kullanim ------------------------------------------------------

    @staticmethod
    def _load_last_good() -> ProviderLimits | None:
        try:
            data = json.loads(_limits_cache_file().read_text(encoding="utf-8"))
            item = ProviderLimits.model_validate(data)
            return item if item.available and item.limits else None
        except (OSError, ValueError):
            return None

    @staticmethod
    def _save_last_good(item: ProviderLimits) -> None:
        try:
            f = _limits_cache_file()
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text(item.model_dump_json(by_alias=True), encoding="utf-8")
        except OSError:
            pass  # onbellek kaybi kritik degil

    def limits(self, refresh: bool = False) -> ProviderLimits:
        now = time.monotonic()
        if not refresh and self._limits_cache and now - self._limits_cache[0] < LIMITS_CACHE_TTL_S:
            return self._limits_cache[1]
        if now < self._limits_backoff_until:
            # 429 sonrasi: uc yeniden sorulmaz, son iyi deger (varsa) notla gosterilir.
            return self._with_last_good(ProviderLimits(provider=PROVIDER_NAME, available=False, detail="kullanım ucu sık soruldu (429); bekleniyor"), now)
        try:
            result = self._probe_limits()
        except Exception as exc:  # noqa: BLE001 — belgesiz uc; sekli degisirse ust bar 500 degil nedeni gormeli
            result = ProviderLimits(
                provider=PROVIDER_NAME,
                available=False,
                detail=f"kota yanıtı ayrıştırılamadı: {type(exc).__name__}: {exc}"[:240],
            )
        if "429" in result.detail:
            self._limits_backoff_until = now + LIMITS_BACKOFF_429_S
        if result.available and result.limits:
            self._limits_last_good = result
            self._save_last_good(result)
            self._limits_cache = (now, result)
            return result
        return self._with_last_good(result, now)

    def _with_last_good(self, failed: ProviderLimits, now: float) -> ProviderLimits:
        """Uc cevap vermedi (429, ag, oturum): son iyi deger notla; ilk kez ve hic yoksa oldugu gibi."""
        result = failed
        # API anahtari modunda kota yok: son iyi degeri gostermek yanlis olur.
        if self._limits_last_good is not None and "API anahtarı" not in failed.detail:
            result = self._limits_last_good.model_copy(update={"detail": f"son bilinen değer · {failed.detail}"})
        self._limits_cache = (now, result)
        return result

    def _probe_limits(self) -> ProviderLimits:
        def unavailable(detail: str) -> ProviderLimits:
            return ProviderLimits(provider=PROVIDER_NAME, available=False, detail=detail)

        if credentials.api_key(PROVIDER_NAME):
            return unavailable("API anahtarı: kota penceresi yok; kullanım Anthropic Console faturasına yazılır.")
        try:
            data = json.loads(_credentials_path().read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return unavailable("Claude Code oturum dosyası okunamadı; giriş yapılmamış olabilir.")
        oauth = data.get("claudeAiOauth") or {}
        token = oauth.get("accessToken")
        if not token:
            return unavailable("oturum yok")
        try:
            r = httpx.get(
                USAGE_URL,
                headers={"Authorization": f"Bearer {token}", "anthropic-beta": "oauth-2025-04-20", "Accept": "application/json"},
                timeout=15,
            )
        except httpx.HTTPError as exc:
            return unavailable(f"kullanım ucu yanıt vermedi: {exc.__class__.__name__}")
        if r.status_code == 401:
            return unavailable("oturum süresi dolmuş; bir model çağrısı ya da yeniden giriş tazeler.")
        if r.status_code == 429:
            return unavailable("kullanım ucu sık soruldu (429); biraz sonra yeniden")
        if r.status_code != 200:
            return unavailable(f"kullanım ucu HTTP {r.status_code}")
        try:
            body = r.json()
        except ValueError:
            return unavailable("kullanım ucu JSON dönmedi")

        items: list[UsageLimit] = []
        for lim in body.get("limits") or []:
            if not isinstance(lim, dict):
                continue
            items.append(UsageLimit(
                kind=str(lim.get("kind") or ""),
                group=_opt_str(lim.get("group")),
                percent=_pct(lim.get("percent")),
                severity=_opt_str(lim.get("severity")),
                resets_at=_iso(lim.get("resets_at")),
                scope=_scope_name(lim.get("scope")),
                is_active=bool(lim.get("is_active", True)),
            ))
        if not items:
            for key, kind in (("five_hour", "session"), ("seven_day", "weekly_all"), ("seven_day_opus", "weekly_opus"), ("seven_day_sonnet", "weekly_sonnet")):
                w = body.get(key)
                if isinstance(w, dict) and w.get("utilization") is not None:
                    items.append(UsageLimit(kind=kind, percent=_pct(w["utilization"]), resets_at=_iso(w.get("resets_at"))))
        return ProviderLimits(
            provider=PROVIDER_NAME,
            available=True,
            subscription=_opt_str(oauth.get("subscriptionType")),
            fetched_at=datetime.now(timezone.utc).isoformat(),
            limits=items,
        )

    def models(self) -> list[ModelInfo]:
        status = self.auth()
        return [
            ModelInfo(
                provider=PROVIDER_NAME,
                model=m,
                reachable=status.logged_in,
                detail=status.detail,
            )
            for m in ANTHROPIC_MODELS
        ]
