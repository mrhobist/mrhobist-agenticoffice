"""FastAPI girisi.

Yalniz loopback dinlenir. Burada is mantigi YOKTUR: uclar istegi ilgili
saglayici adaptorune verir ve sonucu oldugu gibi doner.
"""

from __future__ import annotations

import asyncio
import contextlib
from datetime import datetime

from fastapi import FastAPI, HTTPException, Request

from .contracts import AuthStatus, LocalUsage, LoginRequest, LoginStarted, LogoutRequest, ModelInfo, ProviderLimits, TurnRequest, TurnResponse
from .providers.anthropic import AnthropicProvider
from .providers.openai import OpenAiProvider
from .providers.base import LlmProvider

app = FastAPI(
    title="MrHobist.AITeam runtime",
    version="0.1.0",
    description="LLM cagri katmani. Durumsuz.",
)

#: Bagli saglayicilar. NVIDIA / Ollama eklenince buraya girer.
PROVIDERS: dict[str, LlmProvider] = {"anthropic": AnthropicProvider(), "openai": OpenAiProvider()}


def _provider(name: str) -> LlmProvider:
    p = PROVIDERS.get(name)
    if p is None:
        raise HTTPException(
            status_code=501,
            detail={
                "errorCode": "runtime.provider_unsupported",
                "message": f"saglayici henuz bagli degil: {name}",
            },
        )
    return p


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


#: Istemci baglantisi bu aralikla yoklanir (saniye).
DISCONNECT_POLL_S = 1.0


@app.post("/v1/turn", response_model=TurnResponse, response_model_by_alias=True)
async def run_turn(request: TurnRequest, http: Request) -> TurnResponse:
    """Istemci (.NET) baglantiyi keserse tur da kesilir. Tasima kurali, is kurali degil: Starlette kopan istegin
    isleyicisini kendisi durdurmuyor; 2026-09-23'te zaman asimiyla kesilen tur `claude.exe`'de 18 dk daha kosup
    kayitsiz ~3 $ harcadi, "Yeniden dene" ile ayni dizinde iki ajan ayni anda calisabiliyordu."""
    task = asyncio.create_task(_provider(request.provider).complete(request))
    while True:
        done, _ = await asyncio.wait({task}, timeout=DISCONNECT_POLL_S)
        if done:
            return task.result()
        if await http.is_disconnected():
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
            raise HTTPException(status_code=499, detail={"errorCode": "runtime.client_closed", "message": "istemci baglantiyi kesti; tur durduruldu"})


@app.get("/v1/usage/local", response_model=list[LocalUsage], response_model_by_alias=True)
def local_usage(since: datetime, until: datetime | None = None, provider: str | None = None) -> list[LocalUsage]:
    """Kim ne harcadi: makinedeki CLI oturum kayitlarindan kaynak/klasor/model basina token. Fiyat/pay .NET'te."""
    targets = [_provider(provider)] if provider else list(PROVIDERS.values())
    return [u for p in targets for u in p.local_usage(since, until)]


@app.get("/v1/models", response_model=list[ModelInfo], response_model_by_alias=True)
def list_models(provider: str | None = None) -> list[ModelInfo]:
    targets = [_provider(provider)] if provider else list(PROVIDERS.values())
    return [m for p in targets for m in p.models()]


@app.get("/v1/auth", response_model=list[AuthStatus], response_model_by_alias=True)
def auth_status(provider: str | None = None, refresh: bool = False) -> list[AuthStatus]:
    targets = [_provider(provider)] if provider else list(PROVIDERS.values())
    return [p.auth(refresh=refresh) for p in targets]


@app.post("/v1/auth/login", response_model=LoginStarted, response_model_by_alias=True)
def auth_login(request: LoginRequest) -> LoginStarted:
    """Kimlik bilgisi tasimaz: CLI'nin kendi giris akisini kullanicinin makinesinde baslatir."""
    return _provider(request.provider).login(request)


@app.post("/v1/auth/logout", response_model=AuthStatus, response_model_by_alias=True)
def auth_logout(request: LogoutRequest) -> AuthStatus:
    return _provider(request.provider).logout()


@app.get("/v1/limits", response_model=list[ProviderLimits], response_model_by_alias=True)
def usage_limits(provider: str | None = None, refresh: bool = False) -> list[ProviderLimits]:
    """Kalan kullanim: saglayicinin kota pencereleri. Belirtec hicbir yanita yazilmaz."""
    targets = [_provider(provider)] if provider else list(PROVIDERS.values())
    return [p.limits(refresh=refresh) for p in targets]
