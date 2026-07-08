import threading
import numpy as np
from asr_sidecar.partials import PartialLoop


class FakeCap:
    def __init__(self):
        self.n = 0
    def snapshot(self):
        return np.zeros(self.n, dtype=np.float32)


class FakeEngine:
    def __init__(self, text="interim"):
        self.text = text
        self.calls = 0
    def try_partial(self, audio, prompt=""):
        self.calls += 1
        return self.text


def _loop(cap, engine, events):
    # min_ms=100 -> 1600 samples at 16 kHz; window big enough not to clip.
    return PartialLoop(cap, engine, events.append, threading.Lock(),
                       min_ms=100, window_s=30, sr=16000)


def test_tick_skips_below_min_audio():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 800                      # 50ms < 100ms
    assert loop.tick() is False
    assert events == [] and engine.calls == 0


def test_tick_emits_when_buffer_grows():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200                     # 200ms >= min
    assert loop.tick() is True
    assert events == [{"event": "partial", "text": "interim", "seq": 1}]


def test_tick_skips_when_no_new_audio():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    assert loop.tick() is True       # seq 1
    assert loop.tick() is False      # no growth -> no emit
    assert engine.calls == 1


def test_tick_skips_empty_partial_text():
    cap, engine, events = FakeCap(), FakeEngine(text=""), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    assert loop.tick() is False
    assert events == []


def test_stop_prevents_further_emits():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    loop.stop()                      # stopped before any tick
    assert loop.tick() is False
    assert events == []
