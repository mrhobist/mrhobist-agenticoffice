"""FastAPI girisi.

Yalniz loopback dinlenir. Burada is mantigi YOKTUR: uclar istegi ilgili
saglayici adaptorune verir ve sonucu oldugu gibi doner.
"""

from __future__ import annotations

from fastapi import FastAPI, HTTPException

from .contracts import ModelInfo, TurnRequest, TurnResponse

app = FastAPI(
    title="MrHobist.AITeam runtime",
    version="0.1.0",
    description="LLM cagri katmani. Durumsuz.",
)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/v1/turn", response_model=TurnResponse, response_model_by_alias=True)
async def run_turn(request: TurnRequest) -> TurnResponse:
    # Faz 3: saglayici adaptorleri baglanacak.
    raise HTTPException(status_code=501, detail="Faz 3'te baglanacak")


@app.get("/v1/models", response_model=list[ModelInfo], response_model_by_alias=True)
async def list_models(provider: str | None = None) -> list[ModelInfo]:
    # Faz 3: katalog yoklamasi baglanacak.
    raise HTTPException(status_code=501, detail="Faz 3'te baglanacak")
