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
    ResultMessage,
    TextBlock,
)
from fastapi import HTTPException

from ..contracts import (
    DESTINATION_OF,
    AuthStatus,
    LoginRequest,
    LoginStarted,
    ModelInfo,
    ProviderLimits,
    UsageLimit,
    TurnRequest,
    TurnResponse,
    Usage,
)

PROVIDER_NAME = "anthropic"

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
#: Claude Code'un kendi /usage ekraninin okudugu uc; oturum belirteci CLI'nin dosyasindan alinir, hicbir yere yazilmaz.
USAGE_URL = "https://api.anthropic.com/api/oauth/usage"


def _credentials_path() -> Path:
    base = os.environ.get("CLAUDE_CONFIG_DIR") or str(Path.home() / ".claude")
    return Path(base) / ".credentials.json"


DETAIL_LOGGED_IN = "Claude Code oturumu: giriş var"


def _opt_str(v: Any) -> str | None:
    """Kota yanitindaki serbest alanlar: str degilse None (pydantic 'str | None' sayiyi reddeder)."""
    return v if isinstance(v, str) and v else None


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


def _error(status: int, code: str, message: str) -> HTTPException:
    return HTTPException(status_code=status, detail={"errorCode": code, "message": message})


def _classify(exc: Exception) -> HTTPException:
    text = str(exc)
    if isinstance(exc, CLINotFoundError):
        return _error(503, "runtime.cli_missing", f"claude.exe bulunamadı: {text}")
    if _auth_failed(text):
        return _error(503, "runtime.not_logged_in", text)
    return _error(502, "runtime.provider_error", text)


class AnthropicProvider:
    name = PROVIDER_NAME

    def __init__(self, cli_path: str | None = None) -> None:
        self._cli_path = cli_path
        self._auth_cache: tuple[float, AuthStatus] | None = None
        self._limits_cache: tuple[float, ProviderLimits] | None = None

    # -- yardimcilar -------------------------------------------------------

    @property
    def cli(self) -> str | None:
        return self._cli_path or find_claude_cli()

    def _options(self, request: TurnRequest) -> ClaudeAgentOptions:
        opts: dict[str, Any] = {
            "model": request.model,
            "system_prompt": request.system_prompt,
            "effort": request.reasoning_effort,
            "allowed_tools": [],
            # Yerlesik arac tanimlari prompt'a girmesin: 23k → 4.5k token / cagri (olculdu, 2026-09-19). Ajanlar arac kullanmaz.
            "tools": [],
            # Yapisal cikti (json_schema) ikinci bir tur ister; 1 ile "Reached maximum number of turns" gelir.
            "max_turns": 4,
            "cli_path": self.cli,
        }
        if request.schema_ is not None:
            opts["output_format"] = {"type": "json_schema", "schema": request.schema_}
        return ClaudeAgentOptions(**opts)

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
        started = time.monotonic()

        try:
            async for msg in sdk.query(prompt=self._prompt(request), options=self._options(request)):
                if isinstance(msg, AssistantMessage):
                    if msg.error:
                        err_text = f"Claude hatası: {msg.error}"
                        if _auth_failed(err_text):
                            raise _error(503, "runtime.not_logged_in", err_text)
                        raise _error(502, "runtime.provider_error", err_text)
                    for block in msg.content:
                        if isinstance(block, TextBlock):
                            parts.append(block.text)
                elif isinstance(msg, ResultMessage):
                    if msg.is_error:
                        detail = "; ".join(msg.errors or []) or msg.result or "bilinmiyor"
                        if _auth_failed(detail):
                            raise _error(503, "runtime.not_logged_in", detail)
                        raise _error(502, "runtime.provider_error", detail)
                    structured = msg.structured_output
                    cost = msg.total_cost_usd
                    usage_raw = msg.usage or {}
                    if msg.result and not parts:
                        parts.append(msg.result)
        except HTTPException:
            raise
        except ClaudeSDKError as exc:
            raise _classify(exc) from exc

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
            ),
            cost_usd=cost,
            duration_s=round(time.monotonic() - started, 3),
            attempts=1,
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
            detail=DETAIL_LOGGED_IN if logged_in else DETAIL_LOGGED_OUT,
        )

    # -- kimlik: giris / cikis --------------------------------------------

    def login(self, request: LoginRequest) -> LoginStarted:
        """`claude auth login` yeni bir konsol penceresinde acilir; tarayicida onay kullanicinin isidir.

        Kimlik bilgisi (sifre, token) bu surecten GECMEZ: CLI kendi OAuth akisini yurutur.
        """
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

    def logout(self) -> AuthStatus:
        cli = self.cli
        if cli and Path(cli).is_file():
            try:
                subprocess.run([cli, "auth", "logout"], capture_output=True, text=True, timeout=30)
            except (OSError, subprocess.TimeoutExpired):
                pass
        self._auth_cache = None
        return self.auth(refresh=True)

    # -- kalan kullanim ------------------------------------------------------

    def limits(self, refresh: bool = False) -> ProviderLimits:
        now = time.monotonic()
        if not refresh and self._limits_cache and now - self._limits_cache[0] < LIMITS_CACHE_TTL_S:
            return self._limits_cache[1]
        try:
            result = self._probe_limits()
        except Exception as exc:  # noqa: BLE001 — belgesiz uc; sekli degisirse ust bar 500 degil nedeni gormeli
            result = ProviderLimits(
                provider=PROVIDER_NAME,
                available=False,
                detail=f"kota yanıtı ayrıştırılamadı: {type(exc).__name__}: {exc}"[:240],
            )
        if not result.available and self._limits_cache and self._limits_cache[1].available:
            # Uc gecici olarak cevap vermedi (429 vb.): son iyi degeri notla goster, bari bos kalmasin.
            last = self._limits_cache[1]
            result = last.model_copy(update={"detail": f"son bilinen değer ({result.detail})"})
        self._limits_cache = (now, result)
        return result

    def _probe_limits(self) -> ProviderLimits:
        def unavailable(detail: str) -> ProviderLimits:
            return ProviderLimits(provider=PROVIDER_NAME, available=False, detail=detail)

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
                scope=_opt_str(lim.get("scope")),
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
