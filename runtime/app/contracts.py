"""Sinir sozlesmesi.

Bu dosya Python tarafinin TAMAMINI tanimlar. Buraya gorev, faz, tur sayaci gibi
bir alan eklemek, is mantigini Python'a sizdirmak demektir (bkz. ../CLAUDE.md §1).
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field

Provider = Literal["nvidia", "anthropic", "ollama", "openai"]

#: Icerigin fiilen ulastigi yer. .NET tarafi bunu denetim kaydina yazar.
Destination = Literal["local", "anthropic", "nvidia", "openai"]

DESTINATION_OF: dict[str, Destination] = {
    "ollama": "local",
    "anthropic": "anthropic",
    "nvidia": "nvidia",
    "openai": "openai",
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
    #: Ajanin kullanabilecegi araclar (Read, Write, Edit, Bash, Glob, Grep...). Bos/None = arac yok.
    #: Hangi ajanin hangi araci alacagi .NET'in karari; burasi yalniz iletir.
    tools: list[str] | None = None
    #: Araclarin calisacagi dizin (projenin hedef dizini). .NET verir; runtime hicbir yolu kendisi secmez.
    cwd: str | None = None
    #: Ajan dongusunun en fazla tur sayisi. None = araclara gore varsayilan.
    max_turns: int | None = Field(default=None, alias="maxTurns")
    #: Canli arac akisi: her arac cagrisinda buraya `{tool, target}` POST edilir (loopback, tek kullanimlik belirtecli adres).
    #: .NET verir; runtime yalniz bildirir, cevabi beklemez, hata yutulur. None = akis yok.
    progress_url: str | None = Field(default=None, alias="progressUrl")

    model_config = {"populate_by_name": True}


class ToolUse(BaseModel):
    """Ajanin bir arac cagrisi: yalniz ad ve hedef (dosya yolu / komut). .NET denetim kaydina yazar."""

    tool: str
    target: str | None = None


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
    #: Ajan dongusundeki arac cagrilari (sirali). Arac yoksa bos.
    tool_uses: list[ToolUse] = Field(default_factory=list, alias="toolUses")
    #: Ajan dongusunun tur sayisi (saglayici bildirirse).
    turns: int = 1

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
    #: Kimligin kaynagi: `session` (CLI oturumu: Claude Code / Codex) | `apikey` (kayitli API anahtari) | None (giris yok).
    method: str | None = None

    model_config = {"populate_by_name": True}


class LoginRequest(BaseModel):
    """Saglayici oturumunu baslat.

    Anthropic: `claudeai` = Claude aboneligi, `console` = Anthropic Console (API faturasi), `apikey` = API anahtari.
    OpenAI:    `chatgpt` = ChatGPT aboneligi (Codex CLI oturumu), `apikey` = API anahtari.
    `apiKey` yalniz `apikey` modunda gelir; dogrulanir, kullanici profiline yazilir, HICBIR yanita ve gunluge yazilmaz.
    """

    provider: Provider
    mode: Literal["claudeai", "console", "chatgpt", "apikey"] = "claudeai"
    email: str | None = None
    api_key: str | None = Field(default=None, alias="apiKey")

    model_config = {"populate_by_name": True}


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
