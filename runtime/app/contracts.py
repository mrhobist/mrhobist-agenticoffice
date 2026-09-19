"""Sinir sozlesmesi.

Bu dosya Python tarafinin TAMAMINI tanimlar. Buraya gorev, faz, tur sayaci gibi
bir alan eklemek, is mantigini Python'a sizdirmak demektir (bkz. ../CLAUDE.md §1).
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field

Provider = Literal["nvidia", "anthropic", "ollama"]

#: Icerigin fiilen ulastigi yer. .NET tarafi bunu denetim kaydina yazar.
Destination = Literal["local", "anthropic", "nvidia"]

DESTINATION_OF: dict[str, Destination] = {
    "ollama": "local",
    "anthropic": "anthropic",
    "nvidia": "nvidia",
}


class Message(BaseModel):
    role: Literal["user", "assistant"]
    content: str


class TurnRequest(BaseModel):
    """Bir LLM cagrisinin tamami. Gecmis `messages` ile gelir; sunucu hicbir sey hatirlamaz."""

    system_prompt: str = Field(alias="systemPrompt")
    messages: list[Message]
    provider: Provider
    model: str
    #: Verilirse saglayicinin yapisal cikti mekanizmasi kullanilir.
    schema_: dict[str, Any] | None = Field(default=None, alias="schema")
    max_tokens: int = Field(default=8192, alias="maxTokens")
    #: NVIDIA katalogundaki modeller "thinking" modeli; dusuk tutulmazsa
    #: butcenin tamamini akil yurutmeye harciyorlar (bkz. docs/LESSONS.md).
    reasoning_effort: Literal["low", "medium", "high", "max"] | None = Field(
        default="low", alias="reasoningEffort"
    )

    model_config = {"populate_by_name": True}


class Usage(BaseModel):
    input_tokens: int = Field(default=0, alias="inputTokens")
    output_tokens: int = Field(default=0, alias="outputTokens")
    reasoning_chars: int = Field(default=0, alias="reasoningChars")

    model_config = {"populate_by_name": True}


class TurnResponse(BaseModel):
    text: str
    structured: Any | None = None
    provider: Provider
    model: str
    destination: Destination
    usage: Usage = Usage()
    cost_usd: float | None = Field(default=None, alias="costUsd")
    duration_s: float = Field(default=0.0, alias="durationS")
    attempts: int = 1

    model_config = {"populate_by_name": True}


class ModelInfo(BaseModel):
    provider: Provider
    model: str
    #: Katalogda gorunmek erisilebilir olmak DEGILDIR; bu alan fiilen
    #: cagirarak dogrulanir (bkz. docs/LESSONS.md).
    reachable: bool
    detail: str = ""


class AuthStatus(BaseModel):
    """Saglayicinin kimlik durumu. Bu bir is durumu DEGIL, oturum bilgisidir."""

    provider: Provider
    logged_in: bool = Field(alias="loggedIn")
    account: str | None = None
    detail: str = ""

    model_config = {"populate_by_name": True}


class LoginRequest(BaseModel):
    """Saglayici oturumunu baslat. `claudeai` = Claude aboneligi, `console` = Anthropic Console (API faturasi)."""

    provider: Provider
    mode: Literal["claudeai", "console"] = "claudeai"
    email: str | None = None


class LoginStarted(BaseModel):
    """Giris akisi kullanicinin makinesinde basladi (yeni konsol penceresi + tarayici). Tamamlanmasi kullanicida."""

    provider: Provider
    started: bool
    detail: str = ""


class LogoutRequest(BaseModel):
    provider: Provider


class UsageLimit(BaseModel):
    """Saglayicinin bildirdigi tek bir kota penceresi (5 saat, hafta, modele ozel...). `percent` = kullanilan yuzde."""

    kind: str
    group: str | None = None
    percent: float = 0.0
    severity: str | None = None
    resets_at: str | None = Field(default=None, alias="resetsAt")
    scope: str | None = None
    is_active: bool = Field(default=True, alias="isActive")

    model_config = {"populate_by_name": True}


class ProviderLimits(BaseModel):
    """Bir saglayicinin kalan kullanimi. Token bu modelden GECMEZ; yalniz yuzdeler ve sifirlanma zamanlari."""

    provider: Provider
    available: bool
    detail: str = ""
    subscription: str | None = None
    fetched_at: str | None = Field(default=None, alias="fetchedAt")
    limits: list[UsageLimit] = []

    model_config = {"populate_by_name": True}
