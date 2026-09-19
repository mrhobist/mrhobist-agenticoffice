"""Anthropic adaptoru: SDK sahte, CLI sahte. Gercek cagri yapilmaz."""

from __future__ import annotations

import json
import subprocess
from types import SimpleNamespace

import claude_agent_sdk
import pytest
from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock
from fastapi.testclient import TestClient

from app import main
from app.contracts import TurnRequest
from app.providers import anthropic as mod
from app.providers.anthropic import ANTHROPIC_MODELS, AnthropicProvider

FAKE_CLI = "C:/fake/claude.exe"


def _result(**kw) -> ResultMessage:
    base = dict(
        subtype="success", duration_ms=10, duration_api_ms=8, is_error=False,
        num_turns=1, session_id="s",
    )
    base.update(kw)
    return ResultMessage(**base)


def _fake_query(captured: dict, *, messages):
    async def fake(*, prompt, options=None, transport=None):
        captured["prompt"] = prompt
        captured["options"] = options
        for m in messages:
            yield m
    return fake


def _fake_auth_run(payload: dict):
    def fake(cmd, **kw):
        assert cmd[1:] == ["auth", "status"]
        return SimpleNamespace(returncode=0, stdout=json.dumps(payload), stderr="")
    return fake


@pytest.fixture
def cli_present(monkeypatch):
    monkeypatch.setattr(mod, "find_claude_cli", lambda: FAKE_CLI)
    monkeypatch.setattr(mod.Path, "is_file", lambda self: True)


async def test_turn_sdk_ciktisini_sozlesmeye_esler(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(claude_agent_sdk, "query", _fake_query(captured, messages=[
        AssistantMessage(content=[TextBlock(text="merhaba"), TextBlock(text="dünya")], model="m"),
        _result(
            total_cost_usd=0.0123,
            usage={"input_tokens": 11, "output_tokens": 7, "cache_read_input_tokens": 500},
            structured_output={"ok": True},
        ),
    ]))
    req = TurnRequest.model_validate({
        "systemPrompt": "sys",
        "messages": [
            {"role": "user", "content": "soru"},
            {"role": "assistant", "content": "eski"},
            {"role": "user", "content": "devam"},
        ],
        "provider": "anthropic",
        "model": "claude-opus-5",
        "schema": {"type": "object"},
        "reasoningEffort": "high",
    })

    resp = await AnthropicProvider().complete(req)

    assert resp.text == "merhaba\ndünya"
    assert resp.structured == {"ok": True}
    assert resp.usage.input_tokens == 511, "girdi = dogrudan + onbellek yazma + onbellek okuma" and resp.usage.output_tokens == 7
    assert resp.cost_usd == 0.0123
    assert resp.destination == "anthropic" and resp.attempts == 1
    assert captured["prompt"] == "soru\n\n[önceki yanıt]\neski\n\ndevam"
    opts = captured["options"]
    assert opts.output_format == {"type": "json_schema", "schema": {"type": "object"}}
    assert opts.effort == "high"
    assert opts.model == "claude-opus-5" and opts.max_turns >= 2, "json_schema ciktisi ikinci tur ister"
    assert opts.allowed_tools == [] and opts.cli_path == FAKE_CLI


async def test_turn_schema_yoksa_output_format_yok(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(claude_agent_sdk, "query", _fake_query(captured, messages=[
        _result(result="düz metin", usage={}),
    ]))
    req = TurnRequest(system_prompt="s", messages=[{"role": "user", "content": "x"}],
                      provider="anthropic", model="claude-sonnet-5", reasoning_effort=None)
    resp = await AnthropicProvider().complete(req)
    assert resp.text == "düz metin"
    assert captured["options"].output_format is None
    assert captured["options"].effort is None


async def test_turn_giris_yoksa_503(monkeypatch, cli_present):
    async def fake(*, prompt, options=None, transport=None):
        raise claude_agent_sdk.ProcessError("Not logged in", exit_code=1)
        yield  # async generator olsun diye; erisilmez

    monkeypatch.setattr(claude_agent_sdk, "query", fake)
    req = TurnRequest(system_prompt="s", messages=[{"role": "user", "content": "x"}],
                      provider="anthropic", model="claude-opus-5")
    with pytest.raises(mod.HTTPException) as ei:
        await AnthropicProvider().complete(req)
    assert ei.value.status_code == 503
    assert ei.value.detail["errorCode"] == "runtime.not_logged_in"


async def test_turn_cli_yoksa_503(monkeypatch, cli_present):
    async def fake(*, prompt, options=None, transport=None):
        raise claude_agent_sdk.CLINotFoundError("Claude Code not found", cli_path=FAKE_CLI)
        yield

    monkeypatch.setattr(claude_agent_sdk, "query", fake)
    req = TurnRequest(system_prompt="s", messages=[{"role": "user", "content": "x"}],
                      provider="anthropic", model="claude-opus-5")
    with pytest.raises(mod.HTTPException) as ei:
        await AnthropicProvider().complete(req)
    assert ei.value.status_code == 503
    assert ei.value.detail["errorCode"] == "runtime.cli_missing"


def test_auth_status_json_parse(monkeypatch, cli_present):
    monkeypatch.setattr(subprocess, "run", _fake_auth_run(
        {"loggedIn": True, "authMethod": "claude.ai", "email": "kisi@ornek.com"}
    ))
    st = AnthropicProvider().auth()
    assert st.logged_in is True
    assert st.account == "kisi@ornek.com"
    assert st.provider == "anthropic"
    assert st.model_dump(by_alias=True)["loggedIn"] is True


def test_auth_giris_yok_ve_onbellek(monkeypatch, cli_present):
    calls = {"n": 0}
    real = _fake_auth_run({"loggedIn": False, "authMethod": "none"})

    def counting(cmd, **kw):
        calls["n"] += 1
        return real(cmd, **kw)

    monkeypatch.setattr(subprocess, "run", counting)
    p = AnthropicProvider()
    first = p.auth()
    second = p.auth()
    assert first.logged_in is False and "claude login" in first.detail
    assert calls["n"] == 1, "60 sn icinde ikinci cagri onbellekten donmeli"
    assert second == first


def test_auth_cli_yoksa(monkeypatch):
    monkeypatch.setattr(mod, "find_claude_cli", lambda: None)
    st = AnthropicProvider().auth()
    assert st.logged_in is False
    assert "claude.exe bulunamadı" in st.detail


def test_models_dort_model_reachable_auth_bagli(monkeypatch, cli_present):
    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": False}))
    models = AnthropicProvider().models()
    assert [m.model for m in models] == ANTHROPIC_MODELS and len(models) == 4
    assert all(m.reachable is False for m in models)

    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": True}))
    models = AnthropicProvider().models()
    assert all(m.reachable is True for m in models)
    assert all(m.detail == mod.DETAIL_LOGGED_IN for m in models)


def test_http_nvidia_501():
    client = TestClient(main.app)
    r = client.post("/v1/turn", json={
        "systemPrompt": "s", "messages": [{"role": "user", "content": "x"}],
        "provider": "nvidia", "model": "z-ai/glm-5.3",
    })
    assert r.status_code == 501
    assert r.json()["detail"]["errorCode"] == "runtime.provider_unsupported"
    assert client.get("/v1/models", params={"provider": "ollama"}).status_code == 501


def test_http_auth_ve_models(monkeypatch, cli_present):
    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": True, "email": "a@b.c"}))
    main.PROVIDERS["anthropic"]._auth_cache = None
    client = TestClient(main.app)

    auth = client.get("/v1/auth").json()
    assert auth == [{"provider": "anthropic", "loggedIn": True, "account": "a@b.c",
                     "detail": mod.DETAIL_LOGGED_IN}]

    models = client.get("/v1/models", params={"provider": "anthropic"}).json()
    assert len(models) == 4 and all(m["reachable"] for m in models)
    assert client.get("/health").json() == {"status": "ok"}
    main.PROVIDERS["anthropic"]._auth_cache = None


def test_http_turn_anthropic_yonlenir(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(claude_agent_sdk, "query", _fake_query(captured, messages=[
        AssistantMessage(content=[TextBlock(text="ok")], model="m"),
        _result(total_cost_usd=0.5, usage={"input_tokens": 1, "output_tokens": 2}),
    ]))
    client = TestClient(main.app)
    r = client.post("/v1/turn", json={
        "systemPrompt": "s", "messages": [{"role": "user", "content": "x"}],
        "provider": "anthropic", "model": "claude-haiku-4-5-20251001",
    })
    assert r.status_code == 200
    body = r.json()
    assert body["text"] == "ok" and body["costUsd"] == 0.5
    assert body["usage"] == {"inputTokens": 1, "outputTokens": 2, "reasoningChars": 0}
    assert body["destination"] == "anthropic"


def test_auth_refresh_onbellegi_atlar(monkeypatch, cli_present):
    """Kullanici `claude login` sonrasi "Yeniden kontrol et" dedi: refresh=true eski cevabi kullanmaz."""
    provider = main.PROVIDERS["anthropic"]
    provider._auth_cache = None
    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": False}))
    client = TestClient(main.app)
    assert client.get("/v1/auth").json()[0]["loggedIn"] is False

    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": True, "email": "a@b.c"}))
    assert client.get("/v1/auth").json()[0]["loggedIn"] is False, "onbellek: ayni cevap"
    assert client.get("/v1/auth", params={"refresh": "true"}).json()[0]["loggedIn"] is True
    provider._auth_cache = None


def test_login_konsolda_baslatilir_ve_onbellegi_sifirlar(monkeypatch, cli_present):
    """Giris akisi Popen ile ayri konsolda acilir; sifre/token runtime'dan gecmez."""
    provider = main.PROVIDERS["anthropic"]
    calls: list[list[str]] = []

    class FakePopen:
        def __init__(self, args, **kwargs):
            calls.append(list(args))

    monkeypatch.setattr(subprocess, "Popen", FakePopen)
    provider._auth_cache = (1e12, None)  # eski onbellek sifirlanmali
    client = TestClient(main.app)
    r = client.post("/v1/auth/login", json={"provider": "anthropic", "mode": "console", "email": "a@b.c"})
    assert r.status_code == 200 and r.json()["started"] is True
    assert calls and calls[0][1:] == ["auth", "login", "--console", "--email", "a@b.c"]
    assert provider._auth_cache is None


def test_logout_cli_cagirir_ve_taze_durum_doner(monkeypatch, cli_present):
    provider = main.PROVIDERS["anthropic"]
    seen: list[list[str]] = []
    real_fake = _fake_auth_run({"loggedIn": False})

    def run(args, **kwargs):
        seen.append(list(args))
        if list(args[1:3]) == ["auth", "logout"]:
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        return real_fake(args, **kwargs)

    monkeypatch.setattr(subprocess, "run", run)
    provider._auth_cache = None
    client = TestClient(main.app)
    r = client.post("/v1/auth/logout", json={"provider": "anthropic"})
    assert r.status_code == 200 and r.json()["loggedIn"] is False
    assert any(a[1:3] == ["auth", "logout"] for a in seen)
    provider._auth_cache = None


class _Resp:
    def __init__(self, status_code: int, payload):
        self.status_code = status_code
        self._payload = payload

    def json(self):
        return self._payload


def _creds(tmp_path, monkeypatch, token):
    f = tmp_path / ".credentials.json"
    f.write_text(json.dumps({"claudeAiOauth": {"accessToken": token, "subscriptionType": "team"}} if token else {}), encoding="utf-8")
    monkeypatch.setattr(mod, "_credentials_path", lambda: f)


def test_limits_kota_pencereleri_eslenir(tmp_path, monkeypatch):
    _creds(tmp_path, monkeypatch, "tok-123")
    seen = {}

    def fake_get(url, headers=None, timeout=None):
        seen["url"] = url
        seen["auth"] = headers["Authorization"]
        return _Resp(200, {"limits": [
            {"kind": "session", "group": "session", "percent": 81, "severity": "warning", "resets_at": "2026-09-19T16:30:00+00:00", "scope": None, "is_active": True},
            {"kind": "weekly_all", "group": "weekly", "percent": 20, "severity": "ok", "resets_at": "2026-09-20T09:00:00+00:00", "scope": None, "is_active": True},
        ]})

    monkeypatch.setattr(mod.httpx, "get", fake_get)
    provider = main.PROVIDERS["anthropic"]
    provider._limits_cache = None
    client = TestClient(main.app)
    body = client.get("/v1/limits").json()
    assert seen["url"] == mod.USAGE_URL and seen["auth"] == "Bearer tok-123"
    assert body[0]["available"] is True and body[0]["subscription"] == "team"
    assert [l["kind"] for l in body[0]["limits"]] == ["session", "weekly_all"]
    assert body[0]["limits"][0]["percent"] == 81 and body[0]["limits"][0]["resetsAt"].startswith("2026-09-19")
    assert "tok-123" not in json.dumps(body), "belirtec yanita sizmamali"
    provider._limits_cache = None


def test_limits_oturum_yoksa_ve_401de_kullanilamaz(tmp_path, monkeypatch):
    provider = main.PROVIDERS["anthropic"]
    _creds(tmp_path, monkeypatch, None)
    provider._limits_cache = None
    client = TestClient(main.app)
    assert client.get("/v1/limits").json()[0]["available"] is False

    _creds(tmp_path, monkeypatch, "tok")
    monkeypatch.setattr(mod.httpx, "get", lambda *a, **k: _Resp(401, {}))
    body = client.get("/v1/limits", params={"refresh": "true"}).json()[0]
    assert body["available"] is False and "dolmu" in body["detail"]
    provider._limits_cache = None


def test_limits_epoch_resets_at_cevrilir_ve_beklenmedik_govde_500_vermez(tmp_path, monkeypatch):
    _creds(tmp_path, monkeypatch, "tok")
    provider = main.PROVIDERS["anthropic"]
    client = TestClient(main.app)

    # Claude Code'un /usage sekli: pencere basina {utilization, resets_at}; resets_at epoch gelirse ISO'ya cevrilir.
    monkeypatch.setattr(mod.httpx, "get", lambda *a, **k: _Resp(200, {"five_hour": {"utilization": 42, "resets_at": 1789840000}, "seven_day": None}))
    provider._limits_cache = None
    body = client.get("/v1/limits").json()[0]
    assert body["available"] is True
    assert [l["kind"] for l in body["limits"]] == ["session"]
    assert body["limits"][0]["percent"] == 42 and body["limits"][0]["resetsAt"].startswith("2026-")

    # Govde hic beklenmedik (liste): 500 degil, available=False + neden. Ust bar nedeni gosterir.
    monkeypatch.setattr(mod.httpx, "get", lambda *a, **k: _Resp(200, []))
    provider._limits_cache = None
    r = client.get("/v1/limits", params={"refresh": "true"})
    assert r.status_code == 200
    body = r.json()[0]
    assert body["available"] is False and "AttributeError" in body["detail"]
    provider._limits_cache = None
