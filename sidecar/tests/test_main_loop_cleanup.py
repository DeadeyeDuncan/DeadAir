"""Promoted finding: the __main__ command loop must always close the engine
(and cancel any in-flight capture) even if the loop itself raises — e.g. a
stdin read failure from read_commands(), not just a per-command exception
(which is already caught and turned into an "error" event)."""
import asr_sidecar.__main__ as main_mod


class _FakeEngine:
    name = "cpu"

    def __init__(self):
        self.closed = False

    def close(self):
        self.closed = True


class _FakeCapture:
    def __init__(self):
        self.cancelled = False

    def cancel(self):
        self.cancelled = True

    def start(self):
        pass

    def stop(self):
        import numpy as np
        return np.zeros(0, dtype=np.float32)


def test_engine_closed_when_command_loop_itself_raises(monkeypatch):
    fake_engine = _FakeEngine()
    fake_capture = _FakeCapture()

    monkeypatch.setattr(main_mod, "create_engine", lambda cfg, emit: fake_engine)
    monkeypatch.setattr(main_mod, "MicCapture", lambda mic: fake_capture)
    monkeypatch.setattr(main_mod, "WaveformEmitter", lambda emit: type(
        "L", (), {"on_block": None})())
    monkeypatch.setattr(main_mod, "emit", lambda obj: None)

    def commands():
        yield {"cmd": "config", "engine": "cpu"}
        raise RuntimeError("stdin pipe broke")

    monkeypatch.setattr(main_mod, "read_commands", commands)

    try:
        main_mod.main()
        assert False, "expected the loop's own exception to propagate"
    except RuntimeError:
        pass

    assert fake_engine.closed, "engine.close() must run even when the loop itself raises"
    assert fake_capture.cancelled, "cap.cancel() must run even when the loop itself raises"


def test_failed_reconfig_does_not_leave_closed_engine_bound(monkeypatch):
    # Settings-save hot-reload path: the config handler closes the old engine
    # before create_engine. If create_engine raises, the loop must NOT keep
    # dictating against the closed engine — start/stop must answer with a
    # clear "not configured" error until a config succeeds.
    events = []
    first = _FakeEngine()
    engines = [first]

    def make_engine(cfg, emit):
        if engines:
            return engines.pop(0)
        raise RuntimeError("bad gpu path")

    monkeypatch.setattr(main_mod, "create_engine", make_engine)
    monkeypatch.setattr(main_mod, "MicCapture", lambda mic: _FakeCapture())
    monkeypatch.setattr(main_mod, "WaveformEmitter", lambda emit: type(
        "L", (), {"on_block": None})())
    monkeypatch.setattr(main_mod, "emit", events.append)

    def commands():
        yield {"cmd": "config", "engine": "cpu"}
        yield {"cmd": "config", "engine": "gpu"}   # create_engine raises
        yield {"cmd": "start"}                     # must not touch dead engine
        yield {"cmd": "stop"}
        yield {"cmd": "shutdown"}

    monkeypatch.setattr(main_mod, "read_commands", commands)
    main_mod.main()

    assert first.closed
    errs = [e.get("message", "") for e in events if e.get("event") == "error"]
    assert any("bad gpu path" in m for m in errs)
    assert sum("not configured" in m for m in errs) == 2  # start AND stop


def test_failed_reconfig_mid_recording_cancels_capture(monkeypatch):
    # A settings save can land while the mic is hot. If the new config fails
    # in create_engine, the old capture must already be cancelled — otherwise
    # the InputStream stays open (hot mic, unbounded buffer) until shutdown,
    # because the stop guard no longer reaches cap.stop().
    events = []
    caps = []

    class _Cap(_FakeCapture):
        def __init__(self):
            super().__init__()
            caps.append(self)

    first = _FakeEngine()
    engines = [first]

    def make_engine(cfg, emit):
        if engines:
            return engines.pop(0)
        raise RuntimeError("bad gpu path")

    snap = {}

    def emit_probe(e):
        if e.get("event") == "error" and "bad gpu path" in e.get("message", ""):
            snap["cancelled_when_config_failed"] = caps[0].cancelled
        events.append(e)

    monkeypatch.setattr(main_mod, "create_engine", make_engine)
    monkeypatch.setattr(main_mod, "MicCapture", lambda mic: _Cap())
    monkeypatch.setattr(main_mod, "WaveformEmitter", lambda emit: type(
        "L", (), {"on_block": None})())
    monkeypatch.setattr(main_mod, "emit", emit_probe)

    def commands():
        yield {"cmd": "config", "engine": "cpu"}
        yield {"cmd": "start"}                     # mic hot
        yield {"cmd": "config", "engine": "gpu"}   # create_engine raises
        yield {"cmd": "shutdown"}

    monkeypatch.setattr(main_mod, "read_commands", commands)
    main_mod.main()

    assert snap["cancelled_when_config_failed"] is True


def test_second_start_stops_prior_partial_loop(monkeypatch):
    # A back-to-back start (no intervening stop) must not orphan the prior
    # PartialLoop — the start branch must stop any existing loop first.
    import numpy as np

    class _GpuEngine:
        name = "gpu"
        def close(self):
            pass

    class _Cap:
        def cancel(self):
            pass
        def start(self):
            pass
        def stop(self):
            return np.zeros(0, dtype=np.float32)

    loops = []

    class _FakeLoop:
        def __init__(self, *a, **k):
            self.stopped = False
            loops.append(self)
        def start(self, interval_ms=600):
            pass
        def stop(self):
            self.stopped = True

    monkeypatch.setattr(main_mod, "create_engine", lambda cfg, emit: _GpuEngine())
    monkeypatch.setattr(main_mod, "MicCapture", lambda mic: _Cap())
    monkeypatch.setattr(main_mod, "WaveformEmitter",
                        lambda emit: type("W", (), {"on_block": None})())
    monkeypatch.setattr(main_mod, "PartialLoop", _FakeLoop)
    monkeypatch.setattr(main_mod, "emit", lambda obj: None)

    def commands():
        yield {"cmd": "config", "engine": "gpu"}
        yield {"cmd": "start"}
        yield {"cmd": "start"}
        yield {"cmd": "shutdown"}

    monkeypatch.setattr(main_mod, "read_commands", commands)
    main_mod.main()

    assert len(loops) == 2, "each start should create a PartialLoop"
    assert loops[0].stopped, "the first loop must be stopped by the second start"
