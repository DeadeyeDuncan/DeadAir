"""Runtime GPU-death fallback: if the whisper-server crashes and the engine's
own respawn/retry can't recover, the sidecar must drop to CPU (unless the user
pinned engine=gpu) so a dictation's words are never lost."""
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

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu)

    assert new_engine.name == "cpu"      # swapped so next utterance skips GPU
    assert gpu.closed                    # dead GPU engine released
    kinds = [e.get("event") for e in events]
    assert "degraded" in kinds           # user told GPU dropped to CPU
    finals = [e for e in events if e.get("event") == "final"]
    assert finals and finals[0]["text"] == "cpu transcript"  # words survived


def test_finish_gpu_forced_does_not_fall_back(monkeypatch):
    events = []
    _patch_common(monkeypatch, events)
    gpu = _DeadGpu()
    cfg = SidecarConfig(engine="gpu")    # user pinned GPU: no silent CPU swap

    new_engine = main_mod._finish(np.ones(16000, dtype=np.float32), cfg, gpu)

    assert new_engine is gpu             # not swapped
    assert any(e.get("event") == "error" and e.get("where") == "asr"
               for e in events)
