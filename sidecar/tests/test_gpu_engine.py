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


@pytest.mark.integration
@pytest.mark.skipif(not os.environ.get("LOCALFLOW_WHISPER_SERVER"),
                    reason="set LOCALFLOW_WHISPER_SERVER + LOCALFLOW_WHISPER_MODEL")
def test_real_server_transcribes_jfk():
    from asr_sidecar.audio import load_wav
    eng = GpuEngine(server_exe=os.environ["LOCALFLOW_WHISPER_SERVER"],
                    model_path=os.environ["LOCALFLOW_WHISPER_MODEL"])
    try:
        text = eng.transcribe(load_wav(
            str(Path(__file__).parent / "fixtures" / "jfk.wav")))
        assert "country" in text.lower()
    finally:
        eng.close()
