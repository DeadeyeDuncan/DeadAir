import pytest
from asr_sidecar.config import SidecarConfig
from asr_sidecar.engines import create_engine


def test_cpu_explicit(monkeypatch):
    monkeypatch.setattr("asr_sidecar.engines.CpuEngine",
                        lambda model_size: type("F", (), {"name": "cpu"})())
    eng = create_engine(SidecarConfig(engine="cpu"), emit_fn=lambda e: None)
    assert eng.name == "cpu"


def test_auto_falls_back_and_emits_degraded(monkeypatch):
    def boom(**kw):
        raise RuntimeError("no vulkan")
    monkeypatch.setattr("asr_sidecar.engines.GpuEngine", boom)
    monkeypatch.setattr("asr_sidecar.engines.CpuEngine",
                        lambda model_size: type("F", (), {"name": "cpu"})())
    events = []
    eng = create_engine(SidecarConfig(engine="auto"), emit_fn=events.append)
    assert eng.name == "cpu"
    assert events == [{"event": "degraded", "engine": "cpu",
                       "reason": "no vulkan"}]


def test_gpu_explicit_failure_raises(monkeypatch):
    def boom(**kw):
        raise RuntimeError("no vulkan")
    monkeypatch.setattr("asr_sidecar.engines.GpuEngine", boom)
    with pytest.raises(RuntimeError):
        create_engine(SidecarConfig(engine="gpu"), emit_fn=lambda e: None)
