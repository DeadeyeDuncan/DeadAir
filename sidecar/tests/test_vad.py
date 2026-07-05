import numpy as np
from pathlib import Path
from asr_sidecar.audio import load_wav
from asr_sidecar.vad import extract_speech

FIXTURES = Path(__file__).parent / "fixtures"


def test_silence_returns_none():
    assert extract_speech(np.zeros(16000 * 2, dtype=np.float32)) is None


def test_speech_wav_returns_trimmed_audio():
    audio = load_wav(str(FIXTURES / "jfk.wav"))
    speech = extract_speech(audio)
    assert speech is not None
    assert 0 < len(speech) <= len(audio)
