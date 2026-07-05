import numpy as np
from asr_sidecar.capture import MicCapture


def test_frames_accumulate_and_stop_concatenates():
    cap = MicCapture()
    cap._recording = True  # simulate start without opening a real stream
    cap._on_frames(np.ones(160, dtype=np.float32))
    cap._on_frames(np.zeros(160, dtype=np.float32))
    audio = cap.stop()
    assert len(audio) == 320 and audio[0] == 1.0 and audio[-1] == 0.0


def test_cancel_discards():
    cap = MicCapture()
    cap._recording = True
    cap._on_frames(np.ones(160, dtype=np.float32))
    cap.cancel()
    assert len(cap.stop()) == 0
