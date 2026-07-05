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
    monkeypatch.setattr(main_mod, "LevelEmitter", lambda emit: type(
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
