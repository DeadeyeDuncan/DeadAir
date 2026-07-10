import json
import os
from pathlib import Path
import httpx
import numpy as np
import pytest
from asr_sidecar.engines.gpu_whispercpp import GpuEngine, GpuEngineError


def test_missing_exe_raises(tmp_path):
    with pytest.raises(GpuEngineError):
        GpuEngine(server_exe=str(tmp_path / "nope.exe"),
                  model_path=str(tmp_path / "nope.bin"))


def test_transcribe_posts_wav_and_parses_json():
    def handler(request: httpx.Request) -> httpx.Response:
        assert b"audio.wav" in request.read()  # multipart contains our file
        return httpx.Response(200, json={"text": " hello world "})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    text = eng.transcribe(np.zeros(16000, dtype=np.float32))
    assert text == "hello world"


def test_close_closes_http_client():
    eng = GpuEngine(server_exe="", model_path="", spawn=False)
    eng.close()
    assert eng._client.is_closed


def test_transcribe_respawns_dead_server_then_succeeds(monkeypatch):
    # The whisper-server crashes on inference (WinError 10061 on connect).
    # A managed engine must respawn it once and retry, not wedge forever.
    calls = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        if calls["n"] == 1:
            raise httpx.ConnectError("[WinError 10061] actively refused")
        return httpx.Response(200, json={"text": " recovered "})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    eng._manage_proc = True  # pretend we own the server process
    respawns = {"n": 0}
    monkeypatch.setattr(eng, "_respawn",
                        lambda: respawns.__setitem__("n", respawns["n"] + 1))

    text = eng.transcribe(np.zeros(16000, dtype=np.float32))

    assert text == "recovered"
    assert respawns["n"] == 1  # crashed server respawned exactly once


def test_transcribe_raises_with_server_log_when_recovery_fails(monkeypatch):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("[WinError 10061] actively refused")

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    eng._manage_proc = True
    eng._server_log.append("ggml_vulkan: Device lost")  # captured crash output
    monkeypatch.setattr(eng, "_respawn", lambda: None)

    with pytest.raises(GpuEngineError) as ei:
        eng.transcribe(np.zeros(16000, dtype=np.float32))

    assert "Device lost" in str(ei.value)  # crash reason surfaced, not hidden


def test_transcribe_no_respawn_when_process_not_managed():
    # spawn=False means we didn't launch the server; we cannot recover it.
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("[WinError 10061] actively refused")

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    with pytest.raises(GpuEngineError):
        eng.transcribe(np.zeros(16000, dtype=np.float32))


def test_try_partial_returns_text_on_success():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"text": " interim words "})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    assert eng.try_partial(np.zeros(16000, dtype=np.float32)) == "interim words"


def test_try_partial_bounds_request_timeout():
    # The partial POST must carry a bounded timeout so a hung server can't hold
    # the shared server_lock (and stall the final decode) for the client's full
    # 120s default.
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["timeout"] = request.extensions.get("timeout")
        return httpx.Response(200, json={"text": "ok"})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    eng.try_partial(np.zeros(16000, dtype=np.float32))
    assert seen["timeout"] is not None  # fail clearly if httpx drops the extension
    assert seen["timeout"]["read"] == GpuEngine.partial_timeout_s


def test_try_partial_swallows_failure_without_respawn(monkeypatch):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("[WinError 10061] actively refused")

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    eng._manage_proc = True
    called = {"respawn": 0}
    monkeypatch.setattr(eng, "_respawn",
                        lambda: called.__setitem__("respawn", 1))
    assert eng.try_partial(np.zeros(16000, dtype=np.float32)) is None
    assert called["respawn"] == 0  # partials must NEVER respawn the server


def test_spawn_fails_loudly_when_port_already_answers(monkeypatch):
    # An orphaned whisper-server squatting the port must not be silently
    # adopted: the fresh child would bind-fail and die unnoticed while
    # _wait_ready gets 200s from the orphan (possibly serving a wrong model).
    def orphan(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(orphan))
    eng._manage_proc = True
    popened = {"n": 0}
    monkeypatch.setattr(
        "asr_sidecar.engines.gpu_whispercpp.subprocess.Popen",
        lambda *a, **k: popened.__setitem__("n", popened["n"] + 1))

    with pytest.raises(GpuEngineError, match="already in use"):
        eng._spawn()
    assert popened["n"] == 0     # refused before launching a doomed child


# Prefer the DEADAIR_* names (post-rename); accept the legacy LOCALFLOW_* ones.
_WHISPER_SERVER = (os.environ.get("DEADAIR_WHISPER_SERVER")
                   or os.environ.get("LOCALFLOW_WHISPER_SERVER"))
_WHISPER_MODEL = (os.environ.get("DEADAIR_WHISPER_MODEL")
                  or os.environ.get("LOCALFLOW_WHISPER_MODEL"))


@pytest.mark.integration
@pytest.mark.skipif(not _WHISPER_SERVER,
                    reason="set DEADAIR_WHISPER_SERVER + DEADAIR_WHISPER_MODEL")
def test_real_server_transcribes_jfk():
    from asr_sidecar.audio import load_wav
    eng = GpuEngine(server_exe=_WHISPER_SERVER,
                    model_path=_WHISPER_MODEL)
    try:
        text = eng.transcribe(load_wav(
            str(Path(__file__).parent / "fixtures" / "jfk.wav")))
        assert "country" in text.lower()
    finally:
        eng.close()
