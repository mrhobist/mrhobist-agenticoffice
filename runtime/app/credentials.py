"""API anahtari deposu — is durumu DEGIL, kimlik bilgisi.

Anthropic'in varsayilan kimligi Claude Code oturumu, OpenAI'ninki Codex CLI oturumudur; ikisini de CLI kendi
dosyasinda tutar. Kullanici bir API anahtari girerse burada saklanir: kullanici profilinde
(`%USERPROFILE%\\.mrhobist-aiteam\\credentials.json`), depo DISINDA, git'e girmez, Windows kullanicisina baglidir
(CLI oturumlariyla ayni davranis). Anahtar hicbir HTTP yanitina ve gunluge yazilmaz; UI yalniz maskeli sonunu gorur.

Oncelik: ortam degiskeni (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`) > dosya. Ortamdan gelen anahtar UI'dan silinemez.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

ENV_VAR: dict[str, str] = {"anthropic": "ANTHROPIC_API_KEY", "openai": "OPENAI_API_KEY"}


def credentials_path() -> Path:
    override = os.environ.get("AITEAM_CREDENTIALS_FILE")
    return Path(override) if override else Path.home() / ".mrhobist-aiteam" / "credentials.json"


def _read() -> dict[str, str]:
    try:
        data = json.loads(credentials_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    keys = data.get("apiKeys") if isinstance(data, dict) else None
    return {k: v for k, v in (keys or {}).items() if isinstance(k, str) and isinstance(v, str) and v}


def _write(keys: dict[str, str]) -> None:
    path = credentials_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps({"apiKeys": keys}, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def key_source(provider: str) -> str | None:
    """`env` | `file` | None."""
    if os.environ.get(ENV_VAR.get(provider, "")):
        return "env"
    return "file" if _read().get(provider) else None


def api_key(provider: str) -> str | None:
    env = os.environ.get(ENV_VAR.get(provider, ""))
    if env:
        return env
    return _read().get(provider)


def store_api_key(provider: str, key: str) -> None:
    keys = _read()
    keys[provider] = key.strip()
    _write(keys)


def delete_api_key(provider: str) -> bool:
    """Dosyadaki anahtari siler. Ortamdan geliyorsa silemez → False."""
    keys = _read()
    if provider not in keys:
        return False
    del keys[provider]
    _write(keys)
    return True


def mask(key: str) -> str:
    """Kullaniciya gosterilecek tek sey: `sk-…ab12`."""
    tail = key[-4:] if len(key) >= 8 else ""
    return f"{key[:3]}…{tail}"
