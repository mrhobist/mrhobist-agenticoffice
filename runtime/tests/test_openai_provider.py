"""OpenAI adaptoru: Codex CLI sahte, Responses API sahte (respx). Gercek cagri yapilmaz, gercek anahtar okunmaz."""

from __future__ import annotations

import asyncio
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from app import credentials, main
from app.contracts import TurnRequest
from app.providers import openai as mod
from app.providers.openai import OpenAiProvider

FAKE_CLI = "C:/fake/codex.cmd"


@pytest.fixture(autouse=True)
def isolated_credentials(monkeypatch, tmp_path):
    """Testler kullanicinin gercek anahtar dosyasina ve ortam degiskenine DOKUNMAZ."""
    monkeypatch.setenv("AITEAM_CREDENTIALS_FILE", str(tmp_path / "credentials.json"))
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    monkeypatch.delenv("OPENAI_MODELS", raising=False)


@pytest.fixture
def cli_present(monkeypatch):
    monkeypatch.setattr(mod, "find_codex_cli", lambda: FAKE_CLI)
    monkeypatch.setattr(mod.Path, "is_file", lambda self: True)


def _req(**over) -> TurnRequest:
    base = {
        "systemPrompt": "sen analistsin",
        "messages": [{"role": "user", "content": "merhaba"}, {"role": "assistant", "content": "eski"}, {"role": "user", "content": "devam"}],
        "provider": "openai",
        "model": "gpt-5.2",
        "reasoningEffort": "low",
    }
    base.update(over)
    return TurnRequest.model_validate(base)


class _FakeProc:
    """asyncio alt sureci: verilen JSONL'i stdout'a yazar, son mesaj dosyasini doldurur."""

    def __init__(self, captured: dict, events: list[dict], *, returncode=0, stderr="", last_message: str | None = None):
        self._events = events
        self._captured = captured
        self.returncode = returncode
        self._stderr = stderr
        self._last = last_message

    async def communicate(self, stdin_bytes: bytes):
        self._captured["stdin"] = stdin_bytes.decode("utf-8")
        args = self._captured["args"]
        out_file = Path(args[args.index("-o") + 1])
        text = self._last
        if text is None:
            text = "\n".join(e["item"]["text"] for e in self._events if e.get("type") == "item.completed" and e["item"].get("type") == "agent_message")
        out_file.write_text(text, encoding="utf-8")
        return ("\n".join(json.dumps(e) for e in self._events)).encode("utf-8"), self._stderr.encode("utf-8")


def _fake_exec(captured: dict, events: list[dict], **kw):
    async def fake(*args, **_):
        captured["args"] = list(args)
        return _FakeProc(captured, events, **kw)
    return fake


EVENTS_OK = [
    {"type": "thread.started", "thread_id": "t1"},
    {"type": "turn.started"},
    {"type": "item.completed", "item": {"id": "i1", "type": "command_execution", "command": "dir", "exit_code": 0, "status": "completed"}},
    {"type": "item.completed", "item": {"id": "i2", "type": "file_change", "changes": [{"path": "src/a.py", "kind": "add"}]}},
    {"type": "item.completed", "item": {"id": "i3", "type": "agent_message", "text": '{"ok": true}'}},
    {"type": "turn.completed", "usage": {"input_tokens": 1000, "cached_input_tokens": 200, "output_tokens": 500}},
]


# ---------------------------------------------------------------- Codex yolu (ChatGPT aboneligi)

async def test_codex_turu_sozlesmeye_esler(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(asyncio, "create_subprocess_exec", _fake_exec(captured, EVENTS_OK))

    resp = await OpenAiProvider().complete(_req(schema={"type": "object"}, tools=["Bash", "Write"], cwd="C:/proj", reasoningEffort="max"))

    assert resp.provider == "openai" and resp.destination == "openai"
    assert resp.structured == {"ok": True} and resp.text == '{"ok": true}'
    assert resp.usage.input_tokens == 1000 and resp.usage.output_tokens == 500
    assert resp.cost_usd == pytest.approx((1000 * 1.75 + 500 * 14.0) / 1_000_000)
    assert [(t.tool, t.target) for t in resp.tool_uses] == [("Bash", "dir"), ("Edit", "src/a.py")]
    assert resp.turns == 3

    args = captured["args"]
    assert args[0] == FAKE_CLI and args[1:3] == ["exec", "--json"]
    assert "--skip-git-repo-check" in args and args[-1] == "-"
    assert args[args.index("-m") + 1] == "gpt-5.2"
    assert args[args.index("-C") + 1] == "C:/proj"
    assert args[args.index("--sandbox") + 1] == "workspace-write", "aracli tur: dizin icine yazabilir"
    assert 'model_reasoning_effort="xhigh"' in args
    assert "--output-schema" in args
    assert captured["stdin"].startswith("# Sistem talimatı\n\nsen analistsin")
    assert "[önceki yanıt]\neski" in captured["stdin"]


async def test_codex_aracsiz_tur_salt_okunur_ve_bos_dizin(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(asyncio, "create_subprocess_exec", _fake_exec(captured, [
        {"type": "item.completed", "item": {"type": "agent_message", "text": "selam"}},
        {"type": "turn.completed", "usage": {"input_tokens": 1, "output_tokens": 1}},
    ]))

    resp = await OpenAiProvider().complete(_req())

    args = captured["args"]
    assert args[args.index("--sandbox") + 1] == "read-only"
    assert args[args.index("-C") + 1].endswith("empty")
    assert "--output-schema" not in args
    assert resp.text == "selam" and resp.structured is None and resp.tool_uses == [] and resp.turns == 1


async def test_codex_giris_yoksa_503(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(asyncio, "create_subprocess_exec", _fake_exec(captured, [], returncode=1, stderr="Error: Not logged in. Run `codex login`."))

    with pytest.raises(mod.HTTPException) as exc:
        await OpenAiProvider().complete(_req())
    assert exc.value.status_code == 503 and exc.value.detail["errorCode"] == "runtime.not_logged_in"


async def test_codex_tur_hatasi_502(monkeypatch, cli_present):
    captured: dict = {}
    monkeypatch.setattr(asyncio, "create_subprocess_exec", _fake_exec(captured, [
        {"type": "turn.failed", "error": {"message": "model overloaded"}},
    ], returncode=0, last_message=""))

    with pytest.raises(mod.HTTPException) as exc:
        await OpenAiProvider().complete(_req())
    assert exc.value.status_code == 502 and "model overloaded" in exc.value.detail["message"]


async def test_codex_yoksa_cli_missing(monkeypatch):
    monkeypatch.setattr(mod, "find_codex_cli", lambda: None)
    with pytest.raises(mod.HTTPException) as exc:
        await OpenAiProvider().complete(_req())
    assert exc.value.status_code == 503 and exc.value.detail["errorCode"] == "runtime.cli_missing"


def test_auth_codex_durumu(monkeypatch, cli_present):
    def fake_run(cmd, **kw):
        assert cmd[1:] == ["login", "status"]
        return SimpleNamespace(returncode=0, stdout="Logged in using ChatGPT\n", stderr="")
    monkeypatch.setattr(subprocess, "run", fake_run)

    st = OpenAiProvider().auth(refresh=True)
    assert st.logged_in and st.method == "session" and "ChatGPT" in st.detail


def test_auth_codex_giris_yok(monkeypatch, cli_present):
    monkeypatch.setattr(subprocess, "run", lambda cmd, **kw: SimpleNamespace(returncode=1, stdout="Not logged in\n", stderr=""))
    st = OpenAiProvider().auth(refresh=True)
    assert not st.logged_in and st.method is None


def test_login_chatgpt_yeni_konsol_acar(monkeypatch, cli_present):
    popen: dict = {}
    monkeypatch.setattr(subprocess, "Popen", lambda args, **kw: popen.update(args=args, kw=kw))
    r = main.PROVIDERS["openai"].login(mod.LoginRequest(provider="openai", mode="chatgpt"))
    assert r.started and popen["args"] == [FAKE_CLI, "login"]


def test_login_yanlis_mod(cli_present):
    r = OpenAiProvider().login(mod.LoginRequest(provider="openai", mode="claudeai"))
    assert not r.started


# ---------------------------------------------------------------- API anahtari yolu

@respx.mock
def test_login_apikey_dogrular_ve_saklar(monkeypatch):
    respx.get(f"{mod.API_BASE}/models").mock(return_value=httpx.Response(200, json={"data": []}))
    p = OpenAiProvider()

    r = p.login(mod.LoginRequest(provider="openai", mode="apikey", apiKey="sk-test-1234abcd"))

    assert r.started and "abcd" in r.detail and "sk-test-1234abcd" not in r.detail
    assert credentials.api_key("openai") == "sk-test-1234abcd"
    assert credentials.credentials_path().is_file()
    st = p.auth(refresh=True)
    assert st.logged_in and st.method == "apikey" and st.account == "sk-…abcd"
    lim = p.limits()
    assert not lim.available and "fatura" in lim.detail

    # cikis: anahtar silinir, oturuma donulur (codex yok → giris yok)
    monkeypatch.setattr(mod, "find_codex_cli", lambda: None)
    st = p.logout()
    assert not st.logged_in and credentials.api_key("openai") is None


@respx.mock
def test_login_apikey_reddedilirse_saklamaz():
    respx.get(f"{mod.API_BASE}/models").mock(return_value=httpx.Response(401, json={"error": {"message": "bad"}}))
    r = OpenAiProvider().login(mod.LoginRequest(provider="openai", mode="apikey", apiKey="sk-bad"))
    assert not r.started and credentials.api_key("openai") is None


def test_login_apikey_bos():
    r = OpenAiProvider().login(mod.LoginRequest(provider="openai", mode="apikey", apiKey="  "))
    assert not r.started


def test_ortam_anahtari_silinemez(monkeypatch):
    monkeypatch.setenv("OPENAI_API_KEY", "sk-env-9999zzzz")
    p = OpenAiProvider()
    assert p.auth(refresh=True).method == "apikey"
    st = p.logout()
    assert st.logged_in and "ortam değişkeni" in st.detail


@respx.mock
async def test_api_turu_responses_sozlesmesi():
    credentials.store_api_key("openai", "sk-test-1234abcd")
    route = respx.post(f"{mod.API_BASE}/responses").mock(return_value=httpx.Response(200, json={
        "status": "completed",
        "output": [
            {"type": "reasoning", "summary": []},
            {"type": "message", "content": [{"type": "output_text", "text": '{"summary": "tamam"}'}]},
        ],
        "usage": {"input_tokens": 2000, "output_tokens": 100},
    }))

    resp = await OpenAiProvider().complete(_req(schema={"type": "object"}, reasoningEffort="max"))

    body = json.loads(route.calls[0].request.content)
    assert route.calls[0].request.headers["authorization"] == "Bearer sk-test-1234abcd"
    assert body["instructions"] == "sen analistsin" and body["model"] == "gpt-5.2"
    assert body["input"][1] == {"role": "assistant", "content": "eski"}
    assert body["reasoning"] == {"effort": "high"}, "API yolunda max → high"
    assert body["text"]["format"]["type"] == "json_schema"
    assert resp.structured == {"summary": "tamam"} and resp.usage.input_tokens == 2000
    assert resp.cost_usd == pytest.approx((2000 * 1.75 + 100 * 14.0) / 1_000_000)
    assert resp.tool_uses == []


@respx.mock
async def test_api_turu_401_not_logged_in():
    credentials.store_api_key("openai", "sk-test-1234abcd")
    respx.post(f"{mod.API_BASE}/responses").mock(return_value=httpx.Response(401, json={"error": {"message": "Incorrect API key"}}))
    with pytest.raises(mod.HTTPException) as exc:
        await OpenAiProvider().complete(_req())
    assert exc.value.status_code == 503 and exc.value.detail["errorCode"] == "runtime.not_logged_in"


async def test_api_turu_aracli_istek_501():
    credentials.store_api_key("openai", "sk-test-1234abcd")
    with pytest.raises(mod.HTTPException) as exc:
        await OpenAiProvider().complete(_req(tools=["Bash"], cwd="C:/proj"))
    assert exc.value.status_code == 501 and exc.value.detail["errorCode"] == "runtime.tools_unsupported"


# ---------------------------------------------------------------- katalog ve uclar

def test_katalog_env_ile_degisir(monkeypatch):
    monkeypatch.setenv("OPENAI_MODELS", "gpt-x, gpt-y")
    monkeypatch.setattr(mod, "find_codex_cli", lambda: None)
    models = OpenAiProvider().models()
    assert [m.model for m in models] == ["gpt-x", "gpt-y"] and not models[0].reachable


def test_uclar_openai_saglayicisini_taniyor(monkeypatch):
    monkeypatch.setattr(mod, "find_codex_cli", lambda: None)
    client = TestClient(main.app)

    auth = client.get("/v1/auth", params={"provider": "openai"}).json()
    assert auth[0]["provider"] == "openai" and auth[0]["loggedIn"] is False and "npm i -g @openai/codex" in auth[0]["detail"]

    limits = client.get("/v1/limits", params={"provider": "openai"}).json()
    assert limits[0]["available"] is False

    r = client.post("/v1/auth/login", json={"provider": "openai", "mode": "chatgpt"})
    assert r.status_code == 200 and r.json()["started"] is False


def test_maliyet_bilinmeyen_modelde_none():
    assert mod.estimate_cost("bilinmeyen", 10, 10) is None
    assert mod.estimate_cost("gpt-5-nano", 1_000_000, 0) == pytest.approx(0.05)
