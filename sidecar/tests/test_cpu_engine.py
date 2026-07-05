from pathlib import Path
import pytest
from asr_sidecar.audio import load_wav
from asr_sidecar.engines.cpu_fasterwhisper import CpuEngine

FIXTURES = Path(__file__).parent / "fixtures"


@pytest.mark.slow  # downloads the 'tiny' model (~75 MB) on first run
def test_cpu_engine_transcribes_jfk():
    eng = CpuEngine(model_size="tiny")
    text = eng.transcribe(load_wav(str(FIXTURES / "jfk.wav")))
    assert "country" in text.lower()
