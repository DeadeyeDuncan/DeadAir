"""Runtime GPU-death fallback: if the whisper-server crashes and the engine's
own respawn/retry can't recover, the sidecar must drop to CPU (unless the user
pinned engine=gpu) so a dictation's words are never lost."""
import threading
import numpy as np
import asr_sidecar.__main__ as main_mod
from asr_sidecar.config import SidecarConfig
from asr_sidecar.engines.gpu_whispercpp import GpuEngineError


class _DeadGpu:
    name = "gpu"

    def __init__(self):
        self.closed = False

    def transcribe(self, audio, initial_prompt=""):
        raise GpuEngineError("whisper-server crashed and could not recover")

    def close(self):
        self.closed = True


class _FakeCpu:
    name = "cpu"

    def transcribe(self, audio, initial_prompt=""):
        return "cpu transcript"

    def close(self):
        pass


def _patch_common(monkeypatch, events):
    monkeypatch.setattr(main_mod, "emit", events.append)
    monkeypatch.setattr(main_mod, "extract_speech", lambda a: a)  # skip VAD
    monkeypatch.setattr(main_mod, "CpuEngine", lambda model_size: _FakeCpu())


def test_finish_falls_back_to_cpu_when_gpu_engine_dies(monkeypatch):
    events = []
    _patch_common(monkeypatch, events)
    gpu = _DeadGpu()
    cfg = SidecarConfig(engine="auto", cpu_model="small")

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu,
                                  threading.Lock())

    assert new_engine.name == "cpu"      # swapped so next utterance skips GPU
    assert gpu.closed                    # dead GPU engine released
    kinds = [e.get("event") for e in events]
    assert "degraded" in kinds           # user told GPU dropped to CPU
    finals = [e for e in events if e.get("event") == "final"]
    assert finals and finals[0]["text"] == "cpu transcript"  # words survived


def test_finish_survives_cpu_engine_ctor_failure(monkeypatch):
    # If the CPU fallback engine can't even be constructed (model size not in
    # the local cache + offline, or a typo'd size), the exception must not
    # escape _finish, the still-respawnable GPU engine must stay bound (and
    # unclosed), and the host must not be told "degraded" (it would believe
    # the sidecar is on CPU when it is not).
    events = []
    monkeypatch.setattr(main_mod, "emit", events.append)
    monkeypatch.setattr(main_mod, "extract_speech", lambda a: a)

    def boom(model_size):
        raise RuntimeError("model not cached")
    monkeypatch.setattr(main_mod, "CpuEngine", boom)
    gpu = _DeadGpu()
    cfg = SidecarConfig(engine="auto", cpu_model="small")

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu,
                                  threading.Lock())

    assert new_engine is gpu             # kept: its respawn self-heal may recover
    assert not gpu.closed                # never closed before a fallback is secured
    assert any(e.get("event") == "error" and e.get("where") == "asr"
               for e in events)
    assert not any(e.get("event") == "degraded" for e in events)


def test_finish_emits_error_when_cpu_fallback_transcribe_fails(monkeypatch):
    # Last rung of the words-never-lost ladder: fallback engine constructed but
    # its transcribe raises — emit an asr error and hand back the CPU engine
    # (the GPU one is closed and dead).
    events = []
    monkeypatch.setattr(main_mod, "emit", events.append)
    monkeypatch.setattr(main_mod, "extract_speech", lambda a: a)

    class _FailingCpu:
        name = "cpu"

        def transcribe(self, audio, initial_prompt=""):
            raise RuntimeError("cpu decode failed")

        def close(self):
            pass

    cpu = _FailingCpu()
    monkeypatch.setattr(main_mod, "CpuEngine", lambda model_size: cpu)
    gpu = _DeadGpu()
    cfg = SidecarConfig(engine="auto", cpu_model="small")

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu,
                                  threading.Lock())

    assert new_engine is cpu
    assert gpu.closed
    assert any(e.get("event") == "error" and e.get("where") == "asr"
               for e in events)
    assert not any(e.get("event") == "final" for e in events)


def test_finish_holds_shared_lock_during_transcribe(monkeypatch):
    # Invariant: single-flight — the final decode must run under the shared
    # server_lock (partials pause while it holds the lock).
    events = []
    monkeypatch.setattr(main_mod, "emit", events.append)
    monkeypatch.setattr(main_mod, "extract_speech", lambda a: a)
    lock = threading.Lock()
    held = {}

    class _LockProbe:
        name = "cpu"

        def transcribe(self, audio, initial_prompt=""):
            held["locked"] = lock.locked()
            return "text"

    main_mod._finish(np.ones(16000, dtype=np.float32), SidecarConfig(),
                     _LockProbe(), lock)
    assert held["locked"] is True


def test_finish_gpu_forced_does_not_fall_back(monkeypatch):
    events = []
    _patch_common(monkeypatch, events)
    gpu = _DeadGpu()
    cfg = SidecarConfig(engine="gpu")    # user pinned GPU: no silent CPU swap

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu,
                                  threading.Lock())

    assert new_engine is gpu             # not swapped
    assert any(e.get("event") == "error" and e.get("where") == "asr"
               for e in events)
