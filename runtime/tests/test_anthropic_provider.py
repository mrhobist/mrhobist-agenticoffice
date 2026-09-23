"""Anthropic adaptoru: SDK sahte, CLI sahte. Gercek cagri yapilmaz."""

from __future__ import annotations

import json
import subprocess
from types import SimpleNamespace

import claude_agent_sdk
import pytest
from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock
from fastapi.testclient import TestClient

import httpx
import respx

from app import credentials, main
from app.contracts import TurnRequest
from app.providers import anthropic as mod
from app.providers import openai as openai_mod
from app.providers.anthropic import ANTHROPIC_MODELS, AnthropicProvider

FAKE_CLI = "C:/fake/claude.exe"


@pytest.fixture(autouse=True)
def isolated_credentials(monkeypatch, tmp_path):
    """Testler kullanicinin gercek anahtar dosyasina, ortam degiskenine ve Codex CLI'ya DOKUNMAZ."""
    monkeypatch.setenv("AITEAM_CREDENTIALS_FILE", str(tmp_path / "credentials.json"))
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    monkeypatch.setattr(openai_mod, "find_codex_cli", lambda: None)
    main.PROVIDERS["openai"]._auth_cache = None


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
        assert cmd[1:] == ["auth", "status"], "yalniz claude sorgulanir; codex bu testlerde yok"
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


async def test_sistem_promptu_modu_sdk_sekline_eslenir(monkeypatch, cli_present):
    """Mod .NET'in karari; runtime yalniz esler. `claude_code` -> preset + append (kilavuz korunur),
    `replace` (varsayilan) -> duz string. Olculdu 2026-09-21: kilavuz yurutme adiminda 55 -> ~16 ic tur
    kazandiriyor, ama plan ureten adimda buyuk semayi bozuyor -- o yuzden ikisi de gerekli."""
    async def call(mode):
        captured: dict = {}
        monkeypatch.setattr(claude_agent_sdk, "query", _fake_query(captured, messages=[_result(result="ok", usage={})]))
        req = TurnRequest(system_prompt="Sen bir DEVELOPER'sin.", messages=[{"role": "user", "content": "x"}],
                          provider="anthropic", model="claude-sonnet-5", reasoning_effort="high",
                          system_prompt_mode=mode)
        await AnthropicProvider().complete(req)
        return captured["options"].system_prompt

    preset = await call("claude_code")
    assert preset == {"type": "preset", "preset": "claude_code", "append": "Sen bir DEVELOPER'sin."}

    plain = await call("replace")
    assert plain == "Sen bir DEVELOPER'sin."

    # Alan verilmezse eski davranis: duz string.
    default_req = TurnRequest(system_prompt="s", messages=[{"role": "user", "content": "x"}], provider="anthropic", model="m")
    assert default_req.system_prompt_mode == "replace"


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
    assert [a["provider"] for a in auth] == ["anthropic", "openai"], "tum bagli saglayicilar"
    assert auth[0] == {"provider": "anthropic", "loggedIn": True, "account": "a@b.c",
                       "detail": mod.DETAIL_LOGGED_IN, "method": "session"}

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
    assert body["usage"] == {"inputTokens": 1, "outputTokens": 2, "reasoningChars": 0,
                             "cacheReadTokens": 0, "cacheWriteTokens": 0}
    assert body["destination"] == "anthropic"


def test_onbellek_kirilimi_tasinir(monkeypatch, cli_present):
    """inputTokens TOPLAMDIR; kirilim ayrica tasinir, yoksa "baglam bosa mi gitti" sorusu cevapsiz kalir."""
    monkeypatch.setattr(claude_agent_sdk, "query", _fake_query({}, messages=[
        AssistantMessage(content=[TextBlock(text="ok")], model="m"),
        _result(total_cost_usd=0.5, usage={
            "input_tokens": 10,
            "cache_creation_input_tokens": 200,
            "cache_read_input_tokens": 3000,
            "output_tokens": 5,
        }),
    ]))
    client = TestClient(main.app)
    body = client.post("/v1/turn", json={
        "systemPrompt": "s", "messages": [{"role": "user", "content": "x"}],
        "provider": "anthropic", "model": "claude-haiku-4-5-20251001",
    }).json()

    usage = body["usage"]
    # Toplam uc parcanin toplamidir; parcalar da ayri ayri gorulur.
    assert usage["inputTokens"] == 3210
    assert usage["cacheReadTokens"] == 3000
    assert usage["cacheWriteTokens"] == 200
    assert usage["outputTokens"] == 5


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
    provider._limits_last_good = None
    provider._limits_backoff_until = 0.0
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

    # Govde hic beklenmedik (liste): 500 degil; son iyi deger notla gosterilir (bar bos kalmaz), neden detail'de.
    monkeypatch.setattr(mod.httpx, "get", lambda *a, **k: _Resp(200, []))
    provider._limits_cache = None
    r = client.get("/v1/limits", params={"refresh": "true"})
    assert r.status_code == 200
    body = r.json()[0]
    assert body["available"] is True and body["detail"].startswith("son bilinen değer") and "AttributeError" in body["detail"]
    assert [l["kind"] for l in body["limits"]] == ["session"]
    # Son iyi deger yoksa: available=False + neden.
    provider._limits_cache = None
    provider._limits_last_good = None
    body = client.get("/v1/limits", params={"refresh": "true"}).json()[0]
    assert body["available"] is False and "AttributeError" in body["detail"]
    provider._limits_cache = None


# ---------------------------------------------------------------- API anahtari (Console faturasi)

@respx.mock
def test_apikey_girisi_dogrular_saklar_ve_oturumun_onune_gecer(monkeypatch, cli_present):
    """Anahtar kaydedilince kimlik 'apikey' olur, SDK'ya ANTHROPIC_API_KEY ortamla gider, kota penceresi yoktur."""
    respx.get(f"{mod.API_BASE}/v1/models").mock(return_value=httpx.Response(200, json={"data": []}))
    p = AnthropicProvider()

    r = p.login(mod.LoginRequest(provider="anthropic", mode="apikey", apiKey="sk-ant-test-1234wxyz"))

    assert r.started and "wxyz" in r.detail and "sk-ant-test-1234wxyz" not in r.detail
    st = p.auth(refresh=True)
    assert st.logged_in and st.method == "apikey" and st.account == "sk-…wxyz"
    opts = p._options(TurnRequest.model_validate({"systemPrompt": "s", "messages": [], "provider": "anthropic", "model": "m"}))
    assert opts.env["ANTHROPIC_API_KEY"] == "sk-ant-test-1234wxyz"
    lim = p.limits(refresh=True)
    assert not lim.available and "fatura" in lim.detail

    # cikis anahtari siler ve CLI oturumuna doner
    monkeypatch.setattr(subprocess, "run", _fake_auth_run({"loggedIn": True, "email": "a@b.c"}))
    st = p.logout()
    assert st.method == "session" and st.account == "a@b.c" and credentials.api_key("anthropic") is None


@respx.mock
def test_apikey_reddedilirse_saklanmaz(cli_present):
    respx.get(f"{mod.API_BASE}/v1/models").mock(return_value=httpx.Response(401, json={"error": {"message": "bad"}}))
    r = AnthropicProvider().login(mod.LoginRequest(provider="anthropic", mode="apikey", apiKey="sk-ant-bad"))
    assert not r.started and credentials.api_key("anthropic") is None


def test_uzun_istem_komut_satiri_yerine_dosyadan_verilir(cli_present):
    """2026-09-23: ~24 KB istem + sema Windows komut satirini asti, surec 'Access is denied' ile hic baslamadi."""
    assert mod._write_prompt_file("kisa") is None
    long_text = "ğ" * (mod.PROMPT_FILE_THRESHOLD + 1)
    path = mod._write_prompt_file(long_text)
    try:
        with open(path, encoding="utf-8") as f:
            assert f.read() == long_text
        base = {"systemPrompt": long_text, "messages": [], "provider": "anthropic", "model": "m"}
        replace = AnthropicProvider()._options(TurnRequest.model_validate(base), path)
        assert replace.system_prompt == {"type": "file", "path": path}
        preset = AnthropicProvider()._options(TurnRequest.model_validate({**base, "systemPromptMode": "claude_code"}), path)
        assert preset.system_prompt == {"type": "preset", "preset": "claude_code"}
        assert preset.extra_args == {"append-system-prompt-file": path}
    finally:
        import os
        os.unlink(path)


def test_apikey_yokken_options_env_tasimaz(cli_present):
    opts = AnthropicProvider()._options(TurnRequest.model_validate({"systemPrompt": "s", "messages": [], "provider": "anthropic", "model": "m"}))
    assert "ANTHROPIC_API_KEY" not in opts.env


def test_alt_surec_kullanici_ortamini_devralmaz(cli_present):
    """2026-09-23: CLI kullanicinin ayarlarini ve claude.ai MCP baglayicilarini devraliyordu (~32K token/cagri, e-posta sizintisi)."""
    opts = AnthropicProvider()._options(TurnRequest.model_validate({"systemPrompt": "s", "messages": [], "provider": "anthropic", "model": "m", "tools": ["Read"], "cwd": "."}))
    assert opts.setting_sources == [] and opts.strict_mcp_config is True
    assert opts.env["ENABLE_CLAUDEAI_MCP_SERVERS"] == "false"


def test_scope_nesneden_model_adi_cikarilir():
    """
    Ust uc kapsami NESNE olarak verir: {"model": {"display_name": "Fable", ...}, "surface": ...}.
    Duz string bekleyen okuyucu bunu None'a dusuruyordu; modele ozel haftalik pencere UI'da
    "haftalik" diye etiketlenip normal haftalik pencereden ayirt edilemiyordu (kullanici 2026-09-20).
    """
    assert mod._scope_name({"model": {"display_name": "Fable", "id": None}, "surface": None}) == "Fable"
    # display_name yoksa id'ye duser
    assert mod._scope_name({"model": {"display_name": None, "id": "claude-opus-5"}}) == "claude-opus-5"
    # model yoksa surface
    assert mod._scope_name({"model": None, "surface": "cowork"}) == "cowork"
    # eski duz string bicimi korunur
    assert mod._scope_name("Sonnet") == "Sonnet"
    # bos/bilinmeyen bicimler None
    assert mod._scope_name(None) is None
    assert mod._scope_name({}) is None
    assert mod._scope_name({"model": {}}) is None
    assert mod._scope_name(7) is None


def test_kota_reddi_ayri_kodla_siniflandirilir():
    """
    Saglayici cagri SIRASINDA kota reddi verirse bu `runtime.provider_limit` olmali, `provider_error` degil.
    Ayrimi .NET okuyor: provider_error -> calisma Failed (kullanici elle yeniden dener), provider_limit ->
    Paused + ResumeAt (pencere sifirlaninca kendiliginden surer). Yanlis siniflandirma sessizce kaybolan
    is demektir (2026-09-22).
    """
    assert mod._limit_reached("Claude hatası: usage limit reached")
    assert mod._limit_reached("rate_limit_error")
    assert mod._limit_reached("HTTP 429 Too Many Requests")
    assert mod._limit_reached("quota exceeded for this window")
    # Kota disi hatalar limit SAYILMAZ: yoksa gercek hata sessizce beklemeye donerdi.
    assert not mod._limit_reached("schema validation failed")
    assert not mod._limit_reached("Not logged in")

    # Siniflandirma ucun kodunu degistiriyor.
    assert mod._classify(RuntimeError("usage limit reached")).detail["errorCode"] == "runtime.provider_limit"
    assert mod._classify(RuntimeError("bilinmeyen patlama")).detail["errorCode"] == "runtime.provider_error"
    # Giris hatasi limitten ONCE bakilir: ikisi de gecerliyse kok sebep giristir.
    assert mod._classify(RuntimeError("Not logged in")).detail["errorCode"] == "runtime.not_logged_in"


# -- canli akis, kesilen tur, kim ne harcadi (2026-09-23) ----------------------------------------------------------


@respx.mock
async def test_canli_akis_metin_dusunce_arac_ve_kullanimi_bildirir(monkeypatch, cli_present):
    """Tur surerken metin, dusunce, arac ve mesaj basina kullanim .NET'e gider; ayni kullanim ikinci kez gitmez."""
    from claude_agent_sdk import ThinkingBlock, ToolUseBlock

    sent: list[dict] = []
    respx.post("http://127.0.0.1:5080/api/v1/progress/tok").mock(
        side_effect=lambda req: (sent.append(json.loads(req.content)), httpx.Response(204))[1])
    usage = {"input_tokens": 2, "cache_creation_input_tokens": 100, "cache_read_input_tokens": 900, "output_tokens": 7}
    monkeypatch.setattr(claude_agent_sdk, "query", _fake_query({}, messages=[
        AssistantMessage(content=[ThinkingBlock(thinking="once dizine bakayim", signature="x")], model="m", message_id="m1", usage=usage),
        AssistantMessage(content=[ToolUseBlock(id="u1", name="Read", input={"file_path": "a.cs"})], model="m", message_id="m1", usage=usage),
        AssistantMessage(content=[TextBlock(text="bitti")], model="m", message_id="m2", usage={**usage, "output_tokens": 3}),
        _result(result="bitti", usage={}),
    ]))
    req = TurnRequest(systemPrompt="s", messages=[{"role": "user", "content": "x"}], provider="anthropic", model="m",
                      tools=["Read"], progressUrl="http://127.0.0.1:5080/api/v1/progress/tok")
    await AnthropicProvider().complete(req)

    # m1 ikinci blokta ayni kullanimla gelir ama icerik buyudu (arac girdisi): yeniden bildirilir.
    assert [e["kind"] for e in sent] == ["thinking", "usage", "tool", "usage", "text", "usage"]
    assert sent[0]["text"] == "once dizine bakayim"
    assert sent[1] == {"kind": "usage", "messageId": "m1", "chars": 19, "usage": {"inputTokens": 1002, "outputTokens": 7, "reasoningChars": 0, "cacheReadTokens": 900, "cacheWriteTokens": 100}}
    assert sent[2]["tool"] == "Read" and sent[2]["target"] == "a.cs"
    assert sent[3]["chars"] == 19 + len(json.dumps({"file_path": "a.cs"}))
    assert sent[5]["messageId"] == "m2" and sent[5]["chars"] == 5


async def test_iptal_edilen_tur_sdk_akisini_hemen_kapatir(monkeypatch, cli_present):
    """Tur iptal edilince SDK uretecinin finally'si calisir (alt surec orada durur); cop toplayiciya kalmaz."""
    import asyncio

    closed = asyncio.Event()

    async def fake(*, prompt, options=None, transport=None):
        try:
            yield AssistantMessage(content=[TextBlock(text="basladim")], model="m")
            await asyncio.sleep(3600)
        finally:
            closed.set()

    monkeypatch.setattr(claude_agent_sdk, "query", fake)
    req = TurnRequest(systemPrompt="s", messages=[{"role": "user", "content": "x"}], provider="anthropic", model="m")
    task = asyncio.create_task(AnthropicProvider().complete(req))
    await asyncio.sleep(0.05)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert closed.is_set()


async def test_istemci_koparsa_tur_durdurulur_499(monkeypatch):
    """Starlette kopan istegin isleyicisini durdurmaz: runtime kendisi yoklar ve turu iptal eder."""
    import asyncio

    cancelled = asyncio.Event()

    class Slow:
        async def complete(self, request):
            try:
                await asyncio.sleep(3600)
            except asyncio.CancelledError:
                cancelled.set()
                raise

    class Gone:
        async def is_disconnected(self):
            return True

    monkeypatch.setitem(main.PROVIDERS, "anthropic", Slow())
    monkeypatch.setattr(main, "DISCONNECT_POLL_S", 0.01)
    req = TurnRequest(systemPrompt="s", messages=[{"role": "user", "content": "x"}], provider="anthropic", model="m")
    with pytest.raises(main.HTTPException) as err:
        await main.run_turn(req, Gone())
    assert err.value.status_code == 499
    assert cancelled.is_set()


def test_yerel_kullanim_kaynak_ve_klasore_gore_toplanir_mesaj_tekillenir(tmp_path, monkeypatch):
    """Ofis ajani (sdk-py) ile etkilesimli oturum ayri toplanir; blok basina tekrar yazilan mesaj bir kez sayilir;
    aralik disi ve sentetik mesaj sayilmaz."""
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", str(tmp_path))
    proj = tmp_path / "projects"
    (proj / "C--Hedef").mkdir(parents=True)
    (proj / "C--Ofis" / "s1" / "subagents").mkdir(parents=True)
    u = {"input_tokens": 1, "cache_creation_input_tokens": 10, "cache_read_input_tokens": 100, "output_tokens": 5}

    def line(ts, ep, mid, model="claude-opus-5-5"):
        return json.dumps({"type": "assistant", "timestamp": ts, "entrypoint": ep, "message": {"id": mid, "model": model, "usage": u}}) + "\n"

    (proj / "C--Hedef" / "a.jsonl").write_text(
        line("2026-09-23T10:00:00Z", "sdk-py", "m1") + line("2026-09-23T10:00:01Z", "sdk-py", "m1")
        + line("2026-09-23T10:05:00Z", "sdk-py", "m2") + line("2026-09-22T10:00:00Z", "sdk-py", "eski")
        + line("2026-09-23T10:06:00Z", "sdk-py", "s", model="<synthetic>") + "{bozuk\n", encoding="utf-8")
    (proj / "C--Ofis" / "s1" / "subagents" / "b.jsonl").write_text(line("2026-09-23T11:00:00Z", "claude-desktop", "m3"), encoding="utf-8")

    from datetime import datetime, timezone
    out = AnthropicProvider().local_usage(datetime(2026, 9, 23, tzinfo=timezone.utc))

    by = {(g.source, g.project): g for g in out}
    assert set(by) == {("sdk-py", "C--Hedef"), ("claude-desktop", "C--Ofis")}
    office = by[("sdk-py", "C--Hedef")]
    assert office.messages == 2 and office.input_tokens == 222 and office.output_tokens == 10
    assert office.cache_read_tokens == 200 and office.cache_write_tokens == 20
    assert by[("claude-desktop", "C--Ofis")].messages == 1
