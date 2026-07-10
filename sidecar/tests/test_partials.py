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


def test_stop_during_inflight_partial_suppresses_emit():
    # A stop() that lands WHILE try_partial is running must suppress the late
    # emit via the post-lock stop-guard (not just the pre-work guard).
    cap, events = FakeCap(), []
    holder = {}

    class StoppingEngine:
        def try_partial(self, audio, prompt=""):
            holder["loop"].stop()      # stop lands mid-decode
            return "late text"

    cap.n = 3200
    loop = PartialLoop(cap, StoppingEngine(), events.append, threading.Lock(),
                       min_ms=100, window_s=30, sr=16000)
    holder["loop"] = loop
    assert loop.tick() is False        # second guard suppresses the emit
    assert events == []


def test_tick_isolates_snapshot_failure():
    # Invariant: partials are best-effort — a snapshot failure logs to stderr
    # only, never emits an error event, never escapes the tick.
    class BoomCap:
        def snapshot(self):
            raise RuntimeError("mic backend died")

    events = []
    loop = PartialLoop(BoomCap(), FakeEngine(), events.append, threading.Lock(),
                       min_ms=100, window_s=30, sr=16000)
    assert loop.tick() is False
    assert events == []


def test_tick_holds_shared_lock_during_try_partial():
    # Invariant: single-flight — tick must hold the shared server_lock while
    # try_partial runs so a partial can never overlap the final decode.
    lock = threading.Lock()
    held = {}

    class LockProbeEngine:
        def try_partial(self, audio, prompt=""):
            held["locked"] = lock.locked()
            return "x"

    cap, events = FakeCap(), []
    cap.n = 3200
    loop = PartialLoop(cap, LockProbeEngine(), events.append, lock,
                       min_ms=100, window_s=30, sr=16000)
    assert loop.tick() is True
    assert held["locked"] is True


def test_nonpositive_partial_config_clamped():
    # Host-supplied config is untrusted: interval_ms<=0 must not busy-spin the
    # loop thread and min_ms/window_s<=0 must not zero out the guards.
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = PartialLoop(cap, engine, events.append, threading.Lock(),
                       min_ms=0, window_s=0, sr=16000)
    assert loop._min_samples >= 1
    assert loop._window_samples >= 1
    loop.start(interval_ms=0)
    try:
        assert loop._interval_s >= 0.05
    finally:
        loop.stop()


def test_window_cap_applies_with_zero_window():
    # window_s=0 must not silently disable the cap: audio[-0:] selects the
    # WHOLE buffer, defeating the re-decode window.
    seen = {}

    class LenEngine:
        def try_partial(self, audio, prompt=""):
            seen["n"] = len(audio)
            return "x"

    cap = FakeCap()
    cap.n = 3200
    loop = PartialLoop(cap, LenEngine(), [].append, threading.Lock(),
                       min_ms=100, window_s=0, sr=16000)
    loop.tick()
    assert seen["n"] == 1            # clamped to a 1-sample window, not 3200


def test_partial_loop_not_started_when_partials_disabled():
    # The other half of the invariant-4 guard: cfg.partials=False must win
    # even on a GPU engine.
    from asr_sidecar.__main__ import _maybe_start_partials
    from asr_sidecar.config import SidecarConfig

    class G:
        name = "gpu"
        def try_partial(self, a, p=""):
            return "x"

    started = _maybe_start_partials(SidecarConfig(partials=False), G(),
                                    FakeCap(), lambda e: None,
                                    threading.Lock())
    assert started is None


def test_partial_loop_only_starts_for_gpu_engine():
    # Rehearses the main-loop guard without real audio/model: a cpu-named
    # engine must never be wrapped in a PartialLoop by _maybe_start_partials.
    from asr_sidecar.__main__ import _maybe_start_partials
    from asr_sidecar.config import SidecarConfig

    class E:
        name = "cpu"
    started = _maybe_start_partials(SidecarConfig(engine="cpu"),
                                    E(), FakeCap(), lambda e: None,
                                    threading.Lock())
    assert started is None            # no loop for cpu

    class G(E):
        name = "gpu"
        def try_partial(self, a, p=""):
            return "x"
    loop = _maybe_start_partials(SidecarConfig(), G(), FakeCap(),
                                 lambda e: None, threading.Lock())
    assert loop is not None
    loop.stop()
