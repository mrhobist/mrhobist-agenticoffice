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


class McpServerConfig(BaseModel):
    """Bir MCP sunucusuna baglanti (Agent SDK bicimi). Hangi ajanin hangisini alacagi .NET'in karari; burasi yalniz iletir.
    `stdio`: command/args/env · `http` | `sse`: url/headers. Degerler sir tasiyabilir: gunluge ve yanita yazilmaz."""

    type: Literal["stdio", "http", "sse"] = "stdio"
    command: str | None = None
    args: list[str] = Field(default_factory=list)
    env: dict[str, str] = Field(default_factory=dict)
    url: str | None = None
    headers: dict[str, str] = Field(default_factory=dict)


class McpToolInfo(BaseModel):
    name: str
    description: str | None = None


class McpProbeResult(BaseModel):
    """Baglanti denemesi: sunucu acildi mi, hangi araclari sunuyor. Durum degil, anlik olcum; hata da sonuctur (ok=false)."""

    ok: bool
    detail: str = ""
    tools: list[McpToolInfo] = Field(default_factory=list)
    server_name: str | None = Field(default=None, alias="serverName")
    server_version: str | None = Field(default=None, alias="serverVersion")

    model_config = {"populate_by_name": True}


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
    #: Sistem promptu SDK'ya nasil verilir: `replace` = duz string (Claude Code'un kilavuzu silinir),
    #: `claude_code` = preset korunur, bizimki sonuna eklenir. KARAR .NET'in (adim turune gore); burasi yalniz esler.
    system_prompt_mode: Literal["replace", "claude_code"] = Field(default="replace", alias="systemPromptMode")
    #: Ajanin kullanabilecegi araclar (Read, Write, Edit, Bash, Glob, Grep...). Bos/None = arac yok.
    #: Hangi ajanin hangi araci alacagi .NET'in karari; burasi yalniz iletir.
    tools: list[str] | None = None
    #: Araclarin calisacagi dizin (projenin hedef dizini). .NET verir; runtime hicbir yolu kendisi secmez.
    cwd: str | None = None
    #: Ajan dongusunun en fazla tur sayisi. None = araclara gore varsayilan.
    max_turns: int | None = Field(default=None, alias="maxTurns")
    #: Canli akis: tur surerken buraya `ProgressEvent` POST edilir (loopback, tek kullanimlik belirtecli adres) --
    #: arac cagrisi, ajanin metni/dusuncesi, mesaj basina kullanim. .NET verir; runtime yalniz bildirir, cevabi
    #: beklemez, hata yutulur. None = akis yok.
    progress_url: str | None = Field(default=None, alias="progressUrl")
    #: Ajana acilan MCP sunuculari (anahtar -> baglanti). Yalniz aracli turda anlamli; araclari `mcp__{anahtar}__{arac}` adini alir.
    #: Hangi ajanin hangisini aldigi .NET'in karari (ajan md'si `mcp`); None = yok.
    mcp_servers: dict[str, McpServerConfig] | None = Field(default=None, alias="mcpServers")
    #: cwd DISINDA okunabilecek dizinler (is ekleri). Yazma araclari yine yalniz cwd'de; Bash bu dizinlerdeki yollari
    #: kullanabilir (ornegin bir resmi projeye kopyalamak). .NET verir; runtime hicbir yolu kendisi secmez.
    read_dirs: list[str] | None = Field(default=None, alias="readDirs")

    model_config = {"populate_by_name": True}


class ToolUse(BaseModel):
    """Ajanin bir arac cagrisi: yalniz ad ve hedef (dosya yolu / komut). .NET denetim kaydina yazar."""

    tool: str
    target: str | None = None


class Usage(BaseModel):
    #: Toplam girdi: dogrudan + onbellege yazilan + onbellekten okunan.
    input_tokens: int = Field(default=0, alias="inputTokens")
    output_tokens: int = Field(default=0, alias="outputTokens")
    reasoning_chars: int = Field(default=0, alias="reasoningChars")
    #: Kirilim: `input_tokens` icindeki onbellekten OKUNAN pay (ucuz) ve onbellege YAZILAN pay (pahali).
    #: Ikisi de 0 ise ya saglayici onbellek kullanmiyor ya da bildirmiyordur -- "olculemedi" demektir.
    cache_read_tokens: int = Field(default=0, alias="cacheReadTokens")
    cache_write_tokens: int = Field(default=0, alias="cacheWriteTokens")

    model_config = {"populate_by_name": True}


class ProgressEvent(BaseModel):
    """Tur sirasindaki tek bildirim. `kind`: tool (arac cagrisi) · text (ajanin yazdigi) · thinking (dusunce ozeti) ·
    usage (bir API mesajinin kullanimi; ayni `message_id` icin son deger gecerlidir -- tur kesilirse maliyet bundan
    kurtarilir). Yeni bir tur maliyet dogurmaz: akista zaten uretilen icerik iletilir."""

    kind: Literal["tool", "text", "thinking", "usage"] = "tool"
    tool: str | None = None
    target: str | None = None
    text: str | None = None
    message_id: str | None = Field(default=None, alias="messageId")
    usage: Usage | None = None
    #: O API mesajinda o ana kadar uretilen icerigin karakter sayisi (metin + dusunce + arac girdisi). Akistaki
    #: `usage.output_tokens` mesajin basindaki degerdir; kesilen turun ciktisini .NET bundan tahmin eder.
    chars: int | None = None

    model_config = {"populate_by_name": True}


class LocalUsage(BaseModel):
    """Makinedeki CLI oturum kayitlarindan toplanan kullanim (kim ne harcadi). `source` CLI'nin giris noktasidir
    (ofis ajani `sdk-py`; etkilesimli oturum `cli`, `claude-desktop`...), `project` kaydin klasoru."""

    source: str
    project: str
    model: str
    messages: int = 0
    input_tokens: int = Field(default=0, alias="inputTokens")
    output_tokens: int = Field(default=0, alias="outputTokens")
    cache_read_tokens: int = Field(default=0, alias="cacheReadTokens")
    cache_write_tokens: int = Field(default=0, alias="cacheWriteTokens")

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
