"""FastAPI girisi.

Yalniz loopback dinlenir. Burada is mantigi YOKTUR: uclar istegi ilgili
saglayici adaptorune verir ve sonucu oldugu gibi doner.
"""

from __future__ import annotations

from fastapi import FastAPI, HTTPException

from .contracts import AuthStatus, LoginRequest, LoginStarted, LogoutRequest, ModelInfo, ProviderLimits, TurnRequest, TurnResponse
from .providers.anthropic import AnthropicProvider
from .providers.base import LlmProvider

app = FastAPI(
    title="MrHobist.AITeam runtime",
    version="0.1.0",
    description="LLM cagri katmani. Durumsuz.",
)

#: Bugun yalniz anthropic. NVIDIA / Ollama eklenince buraya girer.
PROVIDERS: dict[str, LlmProvider] = {"anthropic": AnthropicProvider()}


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


@app.post("/v1/turn", response_model=TurnResponse, response_model_by_alias=True)
async def run_turn(request: TurnRequest) -> TurnResponse:
    return await _provider(request.provider).complete(request)


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
