"""MCP ve is ekleri: SDK'ya eslenme, okuma dizini siniri, baglanti denemesi (gercek stdio sunucusu)."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest
from fastapi import HTTPException
from fastapi.testclient import TestClient

from app import main, mcp_probe
from app.contracts import McpServerConfig, TurnRequest
from app.providers import anthropic as mod
from app.providers import openai as openai_mod
from app.providers.anthropic import AnthropicProvider

ECHO_SERVER = Path(__file__).parent / "fixtures" / "echo_mcp.py"


def _req(**kw) -> TurnRequest:
    base = {"systemPrompt": "s", "messages": [], "provider": "anthropic", "model": "m"}
    base.update(kw)
    return TurnRequest.model_validate(base)


@pytest.fixture
def cli_present(monkeypatch):
    monkeypatch.setattr(mod, "find_claude_cli", lambda: "C:/fake/claude.exe")


def test_mcp_sunuculari_sdk_bicimine_eslenir(cli_present, tmp_path):
    req = _req(
        tools=["Read"], cwd=str(tmp_path),
        mcpServers={
            "gh": {"type": "stdio", "command": "npx", "args": ["-y", "srv"], "env": {"TOKEN": "x"}},
            "docs": {"type": "http", "url": "http://127.0.0.1:9/mcp", "headers": {"Authorization": "Bearer y"}},
            "eski": {"type": "sse", "url": "http://127.0.0.1:9/sse"},
        },
        readDirs=[str(tmp_path / "ekler")],
    )
    opts = AnthropicProvider()._options(req)
    assert opts.mcp_servers == {
        "gh": {"type": "stdio", "command": "npx", "args": ["-y", "srv"], "env": {"TOKEN": "x"}},
        "docs": {"type": "http", "url": "http://127.0.0.1:9/mcp", "headers": {"Authorization": "Bearer y"}},
        "eski": {"type": "sse", "url": "http://127.0.0.1:9/sse"},
    }
    assert opts.strict_mcp_config is True  # kullanicinin kendi MCP'leri yuklenmez, yalniz bunlar
    assert opts.add_dirs == [str(tmp_path / "ekler")]


def test_aracsiz_turda_mcp_ve_okuma_dizini_verilmez(cli_present, tmp_path):
    opts = AnthropicProvider()._options(_req(mcpServers={"gh": {"command": "npx"}}, readDirs=[str(tmp_path)]))
    assert not opts.mcp_servers
    assert not opts.add_dirs


async def test_okuma_dizini_bashte_kullanilir_ama_yazilamaz(tmp_path):
    root, att = tmp_path / "proje", tmp_path / "ekler"
    root.mkdir()
    att.mkdir()
    guard = AnthropicProvider._guard(str(root), [str(att)])
    logo = att / "logo.png"

    allowed = await guard("Bash", {"command": f'cp "{logo}" public/logo.png'}, None)
    assert type(allowed).__name__ == "PermissionResultAllow"

    write = await guard("Write", {"file_path": str(att / "x.txt")}, None)
    assert type(write).__name__ == "PermissionResultDeny"

    other = await guard("Bash", {"command": f'cat "{tmp_path / "baska" / "gizli.txt"}"'}, None)
    assert type(other).__name__ == "PermissionResultDeny"

    # MCP araci dosya yazmaz: serbest
    mcp_tool = await guard("mcp__gh__create_issue", {"title": "t"}, None)
    assert type(mcp_tool).__name__ == "PermissionResultAllow"


def test_secilmeyen_araclar_disallowed_olarak_gider_ve_sdkya_izin_listesi_sizmaz(cli_present, tmp_path):
    req = _req(
        tools=["Read"], cwd=str(tmp_path),
        mcpServers={"gh": {"type": "http", "url": "https://x/mcp", "tools": ["get_issue"]}},
        disallowedTools=["mcp__gh__delete_repo"],
    )
    opts = AnthropicProvider()._options(req)
    assert opts.disallowed_tools == ["mcp__gh__delete_repo"]
    assert "tools" not in opts.mcp_servers["gh"]


async def test_izin_listesi_disindaki_mcp_araci_reddedilir(tmp_path):
    guard = AnthropicProvider._guard(str(tmp_path), None, {"gh": {"get_issue"}, "my__srv": None})
    ok = await guard("mcp__gh__get_issue", {}, None)
    assert type(ok).__name__ == "PermissionResultAllow"
    denied = await guard("mcp__gh__delete_repo", {}, None)
    assert type(denied).__name__ == "PermissionResultDeny"
    # "__" iceren anahtar: en uzun onek; izin listesi yok = hepsi
    anything = await guard("mcp__my__srv__whatever", {}, None)
    assert type(anything).__name__ == "PermissionResultAllow"


async def test_openai_mcp_istenirse_501(monkeypatch):
    monkeypatch.setattr(openai_mod, "find_codex_cli", lambda: None)
    with pytest.raises(HTTPException) as err:
        await openai_mod.OpenAiProvider().complete(_req(provider="openai", mcpServers={"gh": {"command": "npx"}}))
    assert err.value.status_code == 501
    assert err.value.detail["errorCode"] == "runtime.mcp_unsupported"


async def test_probe_gercek_stdio_sunucusunun_araclarini_listeler():
    result = await mcp_probe.probe(McpServerConfig(type="stdio", command=sys.executable, args=[str(ECHO_SERVER)]))
    assert result.ok, result.detail
    assert [t.name for t in result.tools] == ["echo"]
    assert result.tools[0].description and "metni" in result.tools[0].description
    assert result.server_name == "echo-test"


async def test_probe_acilmayan_sunucu_hata_degil_sonuc():
    result = await mcp_probe.probe(McpServerConfig(type="stdio", command="boyle-bir-komut-yok-aiteam"), timeout_s=15)
    assert not result.ok
    assert result.detail


async def test_probe_kapali_adres_sonuc_doner():
    result = await mcp_probe.probe(McpServerConfig(type="http", url="http://127.0.0.1:9/mcp"), timeout_s=10)
    assert not result.ok
    assert result.detail


def test_http_probe_ucu_takma_adlarla_doner():
    client = TestClient(main.app)
    r = client.post("/v1/mcp/probe", json={"type": "stdio", "command": sys.executable, "args": [str(ECHO_SERVER)]})
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is True
    assert body["serverName"] == "echo-test"
    assert body["tools"][0]["name"] == "echo"
