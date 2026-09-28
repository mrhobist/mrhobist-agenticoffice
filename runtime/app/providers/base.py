"""Saglayici arayuzu. Her adaptor bu uc cagriyi karsilar, baska bir sey bilmez."""

from __future__ import annotations

from datetime import datetime
from typing import Protocol

from ..contracts import AuthStatus, LocalUsage, LoginRequest, LoginStarted, ModelInfo, ProviderLimits, TurnRequest, TurnResponse


class LlmProvider(Protocol):
    name: str

    async def complete(self, request: TurnRequest) -> TurnResponse:
        """Tek LLM cagrisi. Gecmis `request.messages` icinde gelir."""
        ...

    def models(self) -> list[ModelInfo]:
        """Cagirilabilir modeller."""
        ...

    def auth(self, refresh: bool = False) -> AuthStatus:
        """Kimlik durumu (giris var mi). `refresh` onbellegi atlar."""
        ...

    def login(self, request: LoginRequest) -> LoginStarted:
        """Giris akisini kullanicinin makinesinde baslatir; kimlik bilgisi buradan gecmez."""
        ...

    def logout(self) -> AuthStatus:
        """Oturumu kapatir ve guncel durumu doner."""
        ...

    def limits(self, refresh: bool = False) -> ProviderLimits:
        """Kalan kullanim (kota pencereleri). Saglayici vermiyorsa `available=False` + neden."""
        ...

    def local_usage(self, since: datetime, until: datetime | None = None) -> list[LocalUsage]:
        """Makinedeki CLI oturum kayitlarindan kaynak/klasor/model basina kullanim; saglayici vermiyorsa bos."""
        ...
