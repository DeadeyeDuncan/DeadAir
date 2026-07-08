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


def test_start_twice_closes_first_stream(monkeypatch):
    import asr_sidecar.capture as capture_mod

    class FakeStream:
        instances = []
        def __init__(self, **kw):
            self.stopped = False
            self.closed = False
            FakeStream.instances.append(self)
        def start(self): pass
        def stop(self): self.stopped = True
        def close(self): self.closed = True

    import sounddevice as sd
    monkeypatch.setattr(sd, "InputStream", FakeStream)
    cap = capture_mod.MicCapture()
    cap.start()
    cap.start()
    assert len(FakeStream.instances) == 2
    assert FakeStream.instances[0].stopped and FakeStream.instances[0].closed
    cap.cancel()


def test_snapshot_is_nondestructive_and_grows():
    cap = MicCapture()
    cap._recording = True
    assert cap.snapshot().shape == (0,)          # nothing yet
    cap._on_frames(np.ones(160, dtype=np.float32))
    assert cap.snapshot().shape == (160,)         # sees first block
    cap._on_frames(np.ones(160, dtype=np.float32))
    assert cap.snapshot().shape == (320,)         # grows, non-destructive
    assert cap.stop().shape == (320,)             # frames still intact after peeks
