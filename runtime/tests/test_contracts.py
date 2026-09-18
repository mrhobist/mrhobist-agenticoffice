"""Sinir sozlesmesinin testleri.

Bu testler Python'un ne OLMADIGINI da korur: sozlesmeye gorev/faz alani
eklenirse buradaki alan listesi testi kirilir.
"""

from app.contracts import DESTINATION_OF, TurnRequest, TurnResponse


def test_istek_camelcase_json_ile_kurulur():
    r = TurnRequest.model_validate({
        "systemPrompt": "Sen bir developer'sin.",
        "messages": [{"role": "user", "content": "slugify yaz"}],
        "provider": "nvidia",
        "model": "z-ai/glm-5.3",
    })
    assert r.system_prompt.startswith("Sen")
    assert r.reasoning_effort == "low", "NVIDIA icin varsayilan dusuk olmali"
    assert r.max_tokens == 8192


def test_yanit_camelcase_doner():
    body = TurnResponse(
        text="ok", provider="nvidia", model="m", destination="nvidia"
    ).model_dump(by_alias=True)
    assert "costUsd" in body and "durationS" in body


def test_saglayici_hedef_eslemesi():
    assert DESTINATION_OF["ollama"] == "local"
    assert DESTINATION_OF["anthropic"] == "anthropic"
    assert DESTINATION_OF["nvidia"] == "nvidia"


def test_sozlesmede_is_mantigi_alani_yok():
    """CLAUDE.md §1: Python gorev, faz, tur kavramlarini bilmez."""
    yasakli = {"task", "phase", "stage", "round", "workflow", "run_id", "runId", "agent"}
    alanlar = set(TurnRequest.model_fields) | set(TurnResponse.model_fields)
    assert not (alanlar & yasakli), f"sinira is mantigi sizdi: {alanlar & yasakli}"
