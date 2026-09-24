"""Anthropic saglayicisi — Claude Agent SDK uzerinden.

Ayri API anahtari yoktur: SDK, makinedeki `claude.exe` (Claude Code) oturumunu
kullanir. Farkli Windows kullanicilari farkli oturumdur. Yapisal cikti
`output_format={"type": "json_schema", ...}` ile alinir ve
`ResultMessage.structured_output` uzerinden okunur.

Burada is mantigi YOKTUR: istek geldigi gibi tek bir cagriya cevrilir.
"""

from __future__ import annotations

import contextlib
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
    ThinkingBlock,
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
    LocalUsage,
    McpServerConfig,
    ProgressEvent,
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


def _parse_ts(v: Any) -> float | None:
    """Oturum kaydindaki ISO zaman (`...Z`) -> epoch saniye; okunamazsa None."""
    if not isinstance(v, str) or not v:
        return None
    try:
        return datetime.fromisoformat(v.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


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


#: Canli akista tek metin/dusunce parcasinin ust siniri (karakter). Tam metin tur sonunda gunluge zaten yazilir.
PROGRESS_TEXT_MAX = 4000


async def _report_progress(client: httpx.AsyncClient | None, url: str | None, event: ProgressEvent) -> None:
    """Canli akis: .NET'e tek POST, 2 s zaman asimi, hata yutulur (akis gorunurluk icindir, turu bozmaz)."""
    if not url or client is None:
        return
    try:
        await client.post(url, json=event.model_dump(by_alias=True, exclude_none=True))
    except Exception:  # noqa: BLE001 — bildirim basarisizligi turu etkilemez
        pass


def _block_chars(block: Any) -> int:
    """Bir icerik blogunun uretilen karakter sayisi (cikti tahmini icin; .NET boler)."""
    if isinstance(block, TextBlock):
        return len(block.text or "")
    if isinstance(block, ThinkingBlock):
        return len(block.thinking or "")
    if isinstance(block, ToolUseBlock):
        return len(json.dumps(block.input or {}, ensure_ascii=False))
    return 0


def _sdk_mcp(cfg: McpServerConfig) -> dict[str, Any]:
    """Sozlesmedeki baglanti -> SDK bicimi (McpStdioServerConfig / McpHttpServerConfig / McpSSEServerConfig). Bos alan yazilmaz."""
    if cfg.type == "stdio":
        out: dict[str, Any] = {"type": "stdio", "command": cfg.command or ""}
        if cfg.args:
            out["args"] = list(cfg.args)
        if cfg.env:
            out["env"] = dict(cfg.env)
        return out
    out = {"type": cfg.type, "url": cfg.url or ""}
    if cfg.headers:
        out["headers"] = dict(cfg.headers)
    return out


def _usage_of(raw: dict[str, Any] | None) -> Usage:
    """SDK kullanim sozlugu -> sozlesme. Girdi = dogrudan + onbellege yazilan + onbellekten okunan.
    Yazmanin omur kirilimi `cache_creation.ephemeral_5m_input_tokens`'ta; yoksa 0 (fiyat 1 sa varsayar)."""
    raw = raw or {}
    read = int(raw.get("cache_read_input_tokens") or 0)
    write = int(raw.get("cache_creation_input_tokens") or 0)
    split = raw.get("cache_creation")
    short = int(split.get("ephemeral_5m_input_tokens") or 0) if isinstance(split, dict) else 0
    return Usage(
        input_tokens=int(raw.get("input_tokens") or 0) + read + write,
        output_tokens=int(raw.get("output_tokens") or 0),
        cache_read_tokens=read,
        cache_write_tokens=write,
        cache_write_5m_tokens=min(short, write),
    )


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
            # Alt surec kullanicinin Claude Code ortamini DEVRALMAZ (2026-09-23 olcumu): ayar/CLAUDE.md/hook
            # kaynaklari ve claude.ai MCP baglayicilari (Docs, Figma...) kapali. Onceden cagri basina ~32K token MCP
            # semasi geliyor, her gorevde ~47K yeniden onbellege yaziliyordu (~%18 maliyet); kullanicinin e-postasi ve
            # commit imza kurali da ajanin baglamina siziyordu. Ajan hedef projenin CLAUDE.md'sini kendi araciyla okur.
            "setting_sources": [],
            "strict_mcp_config": True,
            # Otomatik hafiza da kapali (2026-09-24 olcumu): acikken ajan `~/.claude/projects/<cwd>/memory/` altina 9 turda
            # yazdi/okudu. CLI kendi hafiza dizinini izin sormadan onayliyor, `_guard`'a hic ugramiyor: "yazma yalniz cwd"
            # sinirini deliyor ve veritabani disinda gizli, calismadan calismaya tasinan durum biriktiriyordu.
            "env": {"ENABLE_CLAUDEAI_MCP_SERVERS": "false", "CLAUDE_CODE_DISABLE_AUTO_MEMORY": "1"},
        }
        if request.cache_ttl == "5m":
            # Onbellek omru .NET'in secimi (Ayarlar); burasi yalniz CLI'nin degiskenine esler. 5 dk yazma 1,25x, 1 sa 2x.
            opts["env"]["FORCE_PROMPT_CACHING_5M"] = "1"
        elif request.cache_ttl == "1h":
            opts["env"]["ENABLE_PROMPT_CACHING_1H"] = "1"
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
            mcp_allow = {k: (set(v.tools) if v.tools is not None else None) for k, v in (request.mcp_servers or {}).items()}
            opts["can_use_tool"] = self._guard(request.cwd, request.read_dirs, mcp_allow)
            if request.disallowed_tools:
                # Secilmeyen MCP araclari modele hic sunulmaz (semalari baglama girmez); `_guard` izin listesini ayrica uygular.
                opts["disallowed_tools"] = list(request.disallowed_tools)
            opts["max_turns"] = request.max_turns or 80
            if request.cwd:
                opts["cwd"] = request.cwd
            if request.read_dirs:
                # Is ekleri cwd disinda: Claude Code okumayi bu dizinlerde de serbest birakir. Yazma siniri `_guard`'da, cwd'de kalir.
                opts["add_dirs"] = list(request.read_dirs)
            if request.mcp_servers:
                # Ajanin MCP sunuculari (.NET secti). `strict_mcp_config` acik: kullanicinin kendi MCP'leri DEGIL yalniz bunlar yuklenir.
                # Araclar `mcp__{anahtar}__{arac}` adini alir; izin karari yine `_guard` (dosya yazmayan arac serbest).
                opts["mcp_servers"] = {key: _sdk_mcp(cfg) for key, cfg in request.mcp_servers.items()}
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
            opts["env"] = {**opts["env"], "ANTHROPIC_API_KEY": key}
        return ClaudeAgentOptions(**opts)

    #: Dosya degistiren araclar: hedef yol cwd disindaysa reddedilir.
    WRITE_TOOLS = frozenset({"Write", "Edit", "MultiEdit", "NotebookEdit"})

    #: Bash komutunda mutlak yol adaylari: `C:\...`, `C:/...`, `/...`, `~`. Dizin disi yol → ret.
    _ABS_PATH = re.compile(r"""(?<![\w.-])(?:[A-Za-z]:[\\/][^\s"'`;&|<>)]*|/[^\s"'`;&|<>)]+|~(?:[\\/][^\s"'`;&|<>)]*)?)""")
    #: Kok disina cikamayan ama gorunumde mutlak olan yollar (git bash / posix aygitlari).
    _BASH_ALLOW_PREFIXES = ("/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/tmp")

    @classmethod
    def _guard(cls, cwd: str | None, read_dirs: list[str] | None = None, mcp_allow: dict[str, set[str] | None] | None = None):
        """Arac izin karari (SDK `can_use_tool`). Is kurali degil, sinir: dosya yazma yalniz verilen dizinde; Bash verilen
        dizinde ve .NET'in actigi okuma dizinlerinde (is ekleri: ornegin bir resmi projeye kopyalamak)."""
        root = Path(cwd).resolve() if cwd else None
        extra = [Path(d).resolve() for d in (read_dirs or [])]

        def under(raw: str, base: Path) -> bool:
            try:
                target = (base / raw).resolve() if not Path(raw).is_absolute() else Path(raw).resolve()
            except (OSError, ValueError):
                return False
            return base == target or base in target.parents

        def inside(raw: str) -> bool:
            return under(raw, root)

        def readable(raw: str) -> bool:
            return inside(raw) or any(under(raw, d) for d in extra if Path(raw).is_absolute())

        def mcp_denied(tool: str) -> str | None:
            """MCP aracinin izin listesi disinda olup olmadigi. Sunucu anahtari `__` icerebilir: en uzun eslesen onek."""
            if not tool.startswith("mcp__") or not mcp_allow:
                return None
            rest = tool[len("mcp__"):]
            keys = sorted((k for k in mcp_allow if rest.startswith(k + "__")), key=len, reverse=True)
            if not keys:
                return None
            allowed = mcp_allow[keys[0]]
            name = rest[len(keys[0]) + 2:]
            if allowed is not None and name not in allowed:
                return f"'{name}' araci bu ajana acilmadi ({keys[0]} sunucusunda secili degil)."
            return None

        async def decide(tool: str, tool_input: dict[str, Any], _ctx: ToolPermissionContext):
            denied = mcp_denied(tool)
            if denied:
                return PermissionResultDeny(message=denied)
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
                    if raw.startswith("~") or not readable(raw):
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
        # Tur basina tek istemci (bildirim sik: her arac, metin, kullanim). Akis yoksa acilmaz.
        client = httpx.AsyncClient(timeout=2.0) if request.progress_url else None
        # Ayni API mesaji blok basina tekrar gelir (ayni message_id, ayni kullanim): degismeyen kullanim yeniden bildirilmez.
        sent_usage: dict[str, tuple[Usage, int]] = {}
        chars: dict[str, int] = {}
        # Tek API cagrisinin girdisi = o anki baglam; turun tepesi olcu olarak doner (is kurali degil, sayim).
        peak = 0
        short_by_msg: dict[str, int] = {}
        stream: Any = None
        try:
            prompt_text = self._prompt(request)
            prompt: Any = self._stream(prompt_text) if request.tools else prompt_text
            stream = sdk.query(prompt=prompt, options=self._options(request, prompt_file))
            async for msg in stream:
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
                            if block.text.strip():
                                await _report_progress(client, request.progress_url, ProgressEvent(kind="text", text=block.text[:PROGRESS_TEXT_MAX]))
                        elif isinstance(block, ThinkingBlock):
                            if (block.thinking or "").strip():
                                await _report_progress(client, request.progress_url, ProgressEvent(kind="thinking", text=block.thinking[:PROGRESS_TEXT_MAX]))
                        elif isinstance(block, ToolUseBlock):
                            use = ToolUse(tool=block.name, target=self._tool_target(block))
                            tool_uses.append(use)
                            await _report_progress(client, request.progress_url, ProgressEvent(kind="tool", tool=use.tool, target=use.target))
                    if msg.usage and msg.message_id:
                        chars[msg.message_id] = chars.get(msg.message_id, 0) + sum(_block_chars(b) for b in msg.content)
                        state = (_usage_of(msg.usage), chars[msg.message_id])
                        peak = max(peak, state[0].input_tokens)
                        short_by_msg[msg.message_id] = state[0].cache_write_5m_tokens
                        if sent_usage.get(msg.message_id) != state:
                            sent_usage[msg.message_id] = state
                            await _report_progress(client, request.progress_url, ProgressEvent(kind="usage", message_id=msg.message_id, usage=state[0], chars=state[1]))
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
            if stream is not None:
                # Tur iptal edildiyse (istemci koptu) akis HEMEN kapatilir: SDK alt sureci (claude.exe) bununla durur.
                # Kapatilmazsa uretec yalniz cop toplayicida kapanir ve ajan kimse beklemeden calismaya devam eder.
                with contextlib.suppress(Exception):
                    await stream.aclose()
            if prompt_file:
                Path(prompt_file).unlink(missing_ok=True)
            if client is not None:
                await client.aclose()

        usage = _usage_of(usage_raw)
        if usage.cache_write_tokens and not usage.cache_write_5m_tokens and short_by_msg:
            # Sonuc toplaminda kirilim yoksa mesajlardan toplanir (ayni mesajin son degeri).
            usage.cache_write_5m_tokens = min(sum(short_by_msg.values()), usage.cache_write_tokens)
        usage.peak_context_tokens = peak
        return TurnResponse(
            text="\n".join(parts).strip(),
            structured=structured,
            provider=PROVIDER_NAME,
            model=request.model,
            destination=DESTINATION_OF[PROVIDER_NAME],
            # Girdi = dogrudan + onbellege yazilan + onbellekten okunan: kullanici "kac token gitti" diye bakar.
            # Kirilim ayrica tasinir: toplam tek basina "baglam bosa mi gitti" sorusunu cevaplamaz.
            usage=usage,
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

    def local_usage(self, since: datetime, until: datetime | None = None) -> list[LocalUsage]:
        """Kim ne harcadi: CLI'nin makinedeki oturum kayitlari (`~/.claude/projects/**/*.jsonl`) taranir, `[since, until)`
        araligindaki asistan mesajlarinin kullanimi (kaynak, klasor, model) basina toplanir. Her cagri (ofis ajani da,
        etkilesimli oturum da) buraya yazilir; `entrypoint` kaynagi soyler. Ayni mesaj blok basina tekrar yazilir: mesaj
        kimligiyle tekillenir. Durum tutulmaz, dosya yazilmaz; kayit okunamazsa o satir atlanir."""
        base = Path(os.environ.get("CLAUDE_CONFIG_DIR") or str(Path.home() / ".claude")) / "projects"
        if not base.is_dir():
            return []
        lo = since.timestamp()
        hi = until.timestamp() if until else None
        seen: dict[str, tuple[str, str, str, dict[str, Any]]] = {}
        for f in base.rglob("*.jsonl"):
            try:
                if f.stat().st_mtime < lo:
                    continue
                project = f.relative_to(base).parts[0]
                with f.open(encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if '"assistant"' not in line or '"usage"' not in line:
                            continue
                        try:
                            o = json.loads(line)
                        except ValueError:
                            continue
                        msg = o.get("message") if o.get("type") == "assistant" else None
                        if not isinstance(msg, dict) or not isinstance(msg.get("usage"), dict):
                            continue
                        ts = _parse_ts(o.get("timestamp"))
                        if ts is None or ts < lo or (hi is not None and ts >= hi):
                            continue
                        key = str(msg.get("id") or o.get("requestId") or o.get("uuid"))
                        seen[key] = (str(o.get("entrypoint") or "bilinmiyor"), project, str(msg.get("model") or "?"), msg["usage"])
            except OSError:
                continue

        groups: dict[tuple[str, str, str], LocalUsage] = {}
        for source, project, model, raw in seen.values():
            if model.startswith("<"):  # "<synthetic>": CLI'nin kendi urettigi, API'ye gitmeyen mesaj
                continue
            g = groups.setdefault((source, project, model), LocalUsage(source=source, project=project, model=model))
            u = _usage_of(raw)
            g.messages += 1
            g.input_tokens += u.input_tokens
            g.output_tokens += u.output_tokens
            g.cache_read_tokens += u.cache_read_tokens
            g.cache_write_tokens += u.cache_write_tokens
            g.cache_write_5m_tokens += u.cache_write_5m_tokens
        return sorted(groups.values(), key=lambda g: (g.source, g.project, g.model))

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
