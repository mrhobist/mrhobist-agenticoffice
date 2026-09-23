"""OpenAI saglayicisi — iki kimlik yolu, tek sozlesme.

1. **ChatGPT aboneligi** (varsayilan): makinedeki Codex CLI (`codex`) oturumu. Anthropic'teki Claude Code
   oturumunun karsiligi: kimlik CLI'nin kendi dosyasinda, giris `codex login` ile tarayicida, kota
   abonelik penceresinden duser. Tur `codex exec --json` ile kosar; aracli adimlarda (tools verilmisse)
   Codex kendi araclariyla (komut, dosya degisikligi) `cwd` icinde calisir, `workspace-write` sandbox'i
   dizin disina yazmayi keser.
2. **API anahtari**: OpenAI Responses API dogrudan (httpx). Anahtar `credentials` deposunda; fatura OpenAI'ye.
   Bu yolda ajan dongusu yoktur (runtime arac uygulamaz, CLAUDE.md §1): aracli istek 501 doner.

Oncelik: kayitli API anahtari > Codex oturumu (anahtar acikca girilmis bir tercihtir; silinince oturuma doner).
Burada is mantigi YOKTUR: istek geldigi gibi tek bir cagriya cevrilir.
"""

from __future__ import annotations

import asyncio
import json
import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

import httpx
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
    TurnRequest,
    TurnResponse,
    Usage,
)

PROVIDER_NAME = "openai"

#: Varsayilan katalog; `OPENAI_MODELS="a,b,c"` ile degistirilir. Katalogda gorunmek erisilebilir olmak degildir.
DEFAULT_MODELS: list[str] = [
    "gpt-5.2",
    "gpt-5.2-codex",
    "gpt-5.1",
    "gpt-5.1-codex",
    "gpt-5.1-codex-mini",
    "gpt-5-mini",
]

#: Esdegeri maliyet icin liste fiyati (USD / 1M token: girdi, cikti). Yaklasiktir; bilinmeyen model → maliyet None.
#: Abonelikle ucret kesilmez; rakam karsilastirma icindir (docs/DOMAIN.md → Butce ve limit).
PRICE_PER_M: dict[str, tuple[float, float]] = {
    "gpt-5.2": (1.75, 14.0),
    "gpt-5.2-codex": (1.75, 14.0),
    "gpt-5.1": (1.25, 10.0),
    "gpt-5.1-codex": (1.25, 10.0),
    "gpt-5.1-codex-mini": (0.25, 2.0),
    "gpt-5-mini": (0.25, 2.0),
    "gpt-5-nano": (0.05, 0.4),
}

#: Bizim efor adlari → Codex/Responses `reasoning.effort`.
EFFORT_MAP: dict[str, str] = {"low": "low", "medium": "medium", "high": "high", "max": "xhigh"}
#: Responses API'de `xhigh` her modelde yok; API yolunda `max` → `high` (varsayimla ilerlenir).
API_EFFORT_MAP: dict[str, str] = {**EFFORT_MAP, "max": "high"}

AUTH_CACHE_TTL_S = 60.0
API_BASE = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
#: Tek istek icin ust sinir. Codex araclı turlarda dakikalar surebilir.
TURN_TIMEOUT_S = 900.0

INSTALL_HINT = "Codex CLI yok: `npm i -g @openai/codex` kur ya da CODEX_CLI_PATH ver."


def _error(status: int, code: str, message: str) -> HTTPException:
    return HTTPException(status_code=status, detail={"errorCode": code, "message": message})


def _auth_failed(text: str) -> bool:
    t = text.lower()
    return any(k in t for k in ("not logged in", "login required", "unauthorized", "invalid api key", "incorrect api key", "401", "please run `codex login`", "run codex login"))


def find_codex_cli() -> str | None:
    """`codex` yolu: env → PATH → %APPDATA%\\npm\\codex.cmd → None."""
    env_path = os.environ.get("CODEX_CLI_PATH")
    if env_path:
        return env_path
    found = shutil.which("codex")
    if found:
        return found
    appdata = os.environ.get("APPDATA")
    if appdata:
        for name in ("codex.cmd", "codex.exe", "codex"):
            candidate = Path(appdata) / "npm" / name
            if candidate.is_file():
                return str(candidate)
    return None


def catalog() -> list[str]:
    raw = os.environ.get("OPENAI_MODELS")
    if raw:
        return [m.strip() for m in raw.split(",") if m.strip()]
    return list(DEFAULT_MODELS)


def estimate_cost(model: str, input_tokens: int, output_tokens: int) -> float | None:
    price = PRICE_PER_M.get(model)
    if price is None:
        return None
    return round((input_tokens * price[0] + output_tokens * price[1]) / 1_000_000, 6)


class OpenAiProvider:
    name = PROVIDER_NAME

    def __init__(self, cli_path: str | None = None) -> None:
        self._cli_path = cli_path
        self._auth_cache: tuple[float, AuthStatus] | None = None

    @property
    def cli(self) -> str | None:
        return self._cli_path or find_codex_cli()

    # -- prompt --------------------------------------------------------------

    @staticmethod
    def _prompt(request: TurnRequest) -> str:
        """Codex sistem promptu almaz: talimat + gecmis tek metinde gider (Anthropic adaptoruyle ayni gecmis bicimi)."""
        history = "\n\n".join(
            (m.content if m.role == "user" else f"[önceki yanıt]\n{m.content}")
            for m in request.messages
        )
        return f"# Sistem talimatı\n\n{request.system_prompt.strip()}\n\n# Görev\n\n{history}"

    # -- arayuz --------------------------------------------------------------

    async def complete(self, request: TurnRequest) -> TurnResponse:
        if credentials.api_key(PROVIDER_NAME):
            return await self._complete_api(request)
        return await self._complete_codex(request)

    # -- yol 2: Responses API -------------------------------------------------

    async def _complete_api(self, request: TurnRequest) -> TurnResponse:
        if request.tools:
            raise _error(501, "runtime.tools_unsupported",
                         "OpenAI API anahtarı yolunda araçlı adım yok; developer/testçi için ChatGPT (Codex) oturumu kullan.")
        key = credentials.api_key(PROVIDER_NAME) or ""
        payload: dict[str, Any] = {
            "model": request.model,
            "instructions": request.system_prompt,
            "input": [{"role": m.role, "content": m.content} for m in request.messages],
            "max_output_tokens": request.max_tokens,
        }
        if request.reasoning_effort:
            payload["reasoning"] = {"effort": API_EFFORT_MAP[request.reasoning_effort]}
        if request.schema_ is not None:
            payload["text"] = {"format": {"type": "json_schema", "name": "result", "schema": request.schema_, "strict": False}}

        started = time.monotonic()
        try:
            async with httpx.AsyncClient(timeout=TURN_TIMEOUT_S) as client:
                r = await client.post(f"{API_BASE}/responses", json=payload, headers={"Authorization": f"Bearer {key}"})
        except httpx.HTTPError as exc:
            raise _error(502, "runtime.provider_error", f"OpenAI'ye ulaşılamadı: {exc.__class__.__name__}") from exc
        if r.status_code == 401:
            raise _error(503, "runtime.not_logged_in", "OpenAI API anahtarı geçersiz ya da iptal edilmiş.")
        if r.status_code != 200:
            raise _error(502, "runtime.provider_error", f"OpenAI HTTP {r.status_code}: {self._api_error_text(r)}")
        body = r.json()
        text = "\n".join(
            c.get("text", "")
            for item in body.get("output") or []
            if item.get("type") == "message"
            for c in item.get("content") or []
            if c.get("type") == "output_text"
        ).strip()
        if not text and body.get("status") == "incomplete":
            reason = (body.get("incomplete_details") or {}).get("reason") or "bilinmiyor"
            raise _error(502, "runtime.provider_error", f"OpenAI yanıtı tamamlanmadı ({reason}); max_tokens artır.")
        usage = body.get("usage") or {}
        in_tok = int(usage.get("input_tokens") or 0)
        out_tok = int(usage.get("output_tokens") or 0)
        return TurnResponse(
            text=text,
            structured=self._parse_structured(text) if request.schema_ is not None else None,
            provider=PROVIDER_NAME,
            model=request.model,
            destination=DESTINATION_OF[PROVIDER_NAME],
            usage=Usage(input_tokens=in_tok, output_tokens=out_tok),
            cost_usd=estimate_cost(request.model, in_tok, out_tok),
            duration_s=round(time.monotonic() - started, 3),
            attempts=1,
        )

    @staticmethod
    def _api_error_text(r: httpx.Response) -> str:
        try:
            return str((r.json().get("error") or {}).get("message") or r.text)[:300]
        except ValueError:
            return r.text[:300]

    @staticmethod
    def _parse_structured(text: str) -> Any | None:
        try:
            return json.loads(text)
        except ValueError:
            return None

    # -- yol 1: Codex CLI -----------------------------------------------------

    def _codex_args(self, request: TurnRequest, cwd: str, last_message: Path, schema_file: Path | None) -> list[str]:
        cli = self.cli
        if not cli or not Path(cli).is_file():
            raise _error(503, "runtime.cli_missing", INSTALL_HINT)
        args = [
            cli, "exec", "--json", "--skip-git-repo-check", "--ephemeral", "--color", "never",
            "-m", request.model,
            "-c", 'approval_policy="never"',
            "-C", cwd,
            "-o", str(last_message),
        ]
        if request.reasoning_effort:
            args += ["-c", f'model_reasoning_effort="{EFFORT_MAP[request.reasoning_effort]}"']
        # Aracli tur: Codex kendi araclariyla calisir; sandbox dizin disina yazmayi keser (Anthropic'teki _guard'in karsiligi).
        args += ["--sandbox", "workspace-write" if request.tools else "read-only"]
        if schema_file is not None:
            args += ["--output-schema", str(schema_file)]
        args.append("-")  # prompt stdin'den
        return args

    async def _complete_codex(self, request: TurnRequest) -> TurnResponse:
        started = time.monotonic()
        with tempfile.TemporaryDirectory(prefix="aiteam-codex-") as tmp:
            tmp_dir = Path(tmp)
            last_message = tmp_dir / "last.md"
            schema_file: Path | None = None
            if request.schema_ is not None:
                schema_file = tmp_dir / "schema.json"
                schema_file.write_text(json.dumps(request.schema_), encoding="utf-8")
            # cwd yoksa (aracsiz tur) bos gecici dizin: Codex runtime'in kendi dosyalarini okumasin.
            cwd = request.cwd or str(tmp_dir / "empty")
            Path(cwd).mkdir(parents=True, exist_ok=True)
            args = self._codex_args(request, cwd, last_message, schema_file)

            try:
                proc = await asyncio.create_subprocess_exec(
                    *args,
                    stdin=asyncio.subprocess.PIPE,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                    env={**os.environ, "NO_COLOR": "1"},
                )
                stdout, stderr = await asyncio.wait_for(
                    proc.communicate(self._prompt(request).encode("utf-8")), timeout=TURN_TIMEOUT_S,
                )
            except FileNotFoundError as exc:
                raise _error(503, "runtime.cli_missing", f"codex çalıştırılamadı: {exc}") from exc
            except asyncio.TimeoutError as exc:
                raise _error(502, "runtime.provider_error", f"codex {TURN_TIMEOUT_S:.0f} s içinde bitmedi.") from exc

            events = self._parse_events(stdout.decode("utf-8", errors="replace"))
            final_text = last_message.read_text(encoding="utf-8").strip() if last_message.is_file() else ""

        err_text = events["error"] or stderr.decode("utf-8", errors="replace").strip()
        if proc.returncode != 0 or events["error"]:
            detail = (err_text or f"codex çıkış kodu {proc.returncode}")[:400]
            if _auth_failed(detail):
                raise _error(503, "runtime.not_logged_in", detail)
            raise _error(502, "runtime.provider_error", detail)

        text = final_text or "\n".join(events["texts"]).strip()
        usage = events["usage"]
        in_tok = int(usage.get("input_tokens") or 0)
        out_tok = int(usage.get("output_tokens") or 0)
        return TurnResponse(
            text=text,
            structured=self._parse_structured(text) if request.schema_ is not None else None,
            provider=PROVIDER_NAME,
            model=request.model,
            destination=DESTINATION_OF[PROVIDER_NAME],
            usage=Usage(input_tokens=in_tok, output_tokens=out_tok),
            cost_usd=estimate_cost(request.model, in_tok, out_tok),
            duration_s=round(time.monotonic() - started, 3),
            attempts=1,
            tool_uses=events["tool_uses"],
            turns=1 + len(events["tool_uses"]),
        )

    @staticmethod
    def _parse_events(raw: str) -> dict[str, Any]:
        """`codex exec --json` JSONL: agent_message metinleri, arac kullanimlari, kullanim, hata."""
        texts: list[str] = []
        tool_uses: list[ToolUse] = []
        usage: dict[str, Any] = {}
        error: str | None = None
        for line in raw.splitlines():
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            kind = ev.get("type")
            if kind == "item.completed":
                item = ev.get("item") or {}
                it = item.get("type")
                if it == "agent_message":
                    texts.append(str(item.get("text") or ""))
                elif it == "command_execution":
                    tool_uses.append(ToolUse(tool="Bash", target=str(item.get("command") or "")[:300]))
                elif it == "file_change":
                    for ch in item.get("changes") or []:
                        tool_uses.append(ToolUse(tool="Edit", target=str(ch.get("path") or "")[:300]))
                elif it == "mcp_tool_call":
                    tool_uses.append(ToolUse(tool=f"mcp:{item.get('server')}/{item.get('tool')}", target=None))
                elif it == "web_search":
                    tool_uses.append(ToolUse(tool="WebSearch", target=str(item.get("query") or "")[:300]))
                elif it == "error":
                    error = str(item.get("message") or "codex hata bildirdi")
            elif kind == "turn.completed":
                usage = ev.get("usage") or usage
            elif kind == "turn.failed":
                error = str((ev.get("error") or {}).get("message") or "tur başarısız")
            elif kind == "error":
                error = str(ev.get("message") or "codex hata bildirdi")
        return {"texts": texts, "tool_uses": tool_uses, "usage": usage, "error": error}

    # -- kimlik ----------------------------------------------------------------

    def auth(self, refresh: bool = False) -> AuthStatus:
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
                              detail=f"API anahtarı ({src}): çağrılar OpenAI faturasına yazılır.")
        cli = self.cli
        if not cli or not Path(cli).is_file():
            return AuthStatus(provider=PROVIDER_NAME, logged_in=False, detail=INSTALL_HINT)
        try:
            proc = subprocess.run([cli, "login", "status"], capture_output=True, text=True, timeout=20)
        except (OSError, subprocess.TimeoutExpired) as exc:
            return AuthStatus(provider=PROVIDER_NAME, logged_in=False, detail=f"codex login status çalışmadı: {exc}")
        out = f"{proc.stdout}\n{proc.stderr}".strip()
        if proc.returncode != 0 or "not logged in" in out.lower():
            return AuthStatus(provider=PROVIDER_NAME, logged_in=False,
                              detail="giriş yok — 'ChatGPT ile giriş' düğmesi ya da terminalde `codex login` (aynı Windows kullanıcısı)")
        how = "API anahtarı" if "api key" in out.lower() else "ChatGPT hesabı"
        return AuthStatus(provider=PROVIDER_NAME, logged_in=True, method="session",
                          detail=f"Codex CLI oturumu: giriş var ({how}); kota ChatGPT aboneliğinden düşer.")

    def login(self, request: LoginRequest) -> LoginStarted:
        if request.mode == "apikey":
            return self._login_api_key(request.api_key)
        if request.mode != "chatgpt":
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"OpenAI için giriş modu 'chatgpt' ya da 'apikey' olmalı ({request.mode}).")
        cli = self.cli
        if not cli or not Path(cli).is_file():
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=INSTALL_HINT)
        flags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0)
        try:
            subprocess.Popen([cli, "login"], creationflags=flags, close_fds=True)  # noqa: S603 — sabit argumanlar
        except OSError as exc:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"giriş başlatılamadı: {exc}")
        self._auth_cache = None
        return LoginStarted(provider=PROVIDER_NAME, started=True,
                            detail="Konsol penceresi açıldı; tarayıcıda ChatGPT hesabınla onayla, sonra 'Yeniden kontrol et'.")

    def _login_api_key(self, key: str | None) -> LoginStarted:
        key = (key or "").strip()
        if not key:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail="API anahtarı boş.")
        try:
            r = httpx.get(f"{API_BASE}/models", headers={"Authorization": f"Bearer {key}"}, timeout=15)
        except httpx.HTTPError as exc:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"OpenAI'ye ulaşılamadı, anahtar doğrulanamadı: {exc.__class__.__name__}")
        if r.status_code == 401:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail="Anahtar reddedildi (401); kaydedilmedi.")
        if r.status_code >= 400:
            return LoginStarted(provider=PROVIDER_NAME, started=False, detail=f"Doğrulama HTTP {r.status_code}; kaydedilmedi.")
        credentials.store_api_key(PROVIDER_NAME, key)
        self._auth_cache = None
        return LoginStarted(provider=PROVIDER_NAME, started=True, detail=f"API anahtarı doğrulandı ve kaydedildi ({credentials.mask(key)}).")

    def logout(self) -> AuthStatus:
        source = credentials.key_source(PROVIDER_NAME)
        if source == "env":
            st = self.auth(refresh=True)
            return st.model_copy(update={"detail": "API anahtarı ortam değişkeninden (OPENAI_API_KEY) geliyor; buradan silinemez."})
        if source == "file":
            credentials.delete_api_key(PROVIDER_NAME)
            self._auth_cache = None
            return self.auth(refresh=True)
        cli = self.cli
        if cli and Path(cli).is_file():
            try:
                subprocess.run([cli, "logout"], capture_output=True, text=True, timeout=30)
            except (OSError, subprocess.TimeoutExpired):
                pass
        self._auth_cache = None
        return self.auth(refresh=True)

    # -- kalan kullanim ----------------------------------------------------------

    def limits(self, refresh: bool = False) -> ProviderLimits:
        if credentials.api_key(PROVIDER_NAME):
            return ProviderLimits(provider=PROVIDER_NAME, available=False,
                                  detail="API anahtarı: kota penceresi yok; kullanım OpenAI faturasına yazılır.")
        return ProviderLimits(provider=PROVIDER_NAME, available=False,
                              detail="Codex CLI kalan kullanımı dışa vermiyor; ChatGPT ayarlarından bakılır.")

    def local_usage(self, since: Any, until: Any = None) -> list[Any]:
        """Codex CLI oturum kayitlarindan kullanim okunmuyor: bos (kim ne harcadi raporunda yalniz ofis kaydi)."""
        return []

    def models(self) -> list[ModelInfo]:
        status = self.auth()
        return [ModelInfo(provider=PROVIDER_NAME, model=m, reachable=status.logged_in, detail=status.detail) for m in catalog()]
