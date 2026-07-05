from ..config import SidecarConfig
from .base import AsrEngine
from .cpu_fasterwhisper import CpuEngine
from .gpu_whispercpp import GpuEngine, GpuEngineError

__all__ = ["AsrEngine", "CpuEngine", "GpuEngine", "GpuEngineError",
           "create_engine"]


def create_engine(cfg: SidecarConfig, emit_fn) -> AsrEngine:
    if cfg.engine == "cpu":
        return CpuEngine(model_size=cfg.cpu_model)
    try:
        return GpuEngine(server_exe=cfg.gpu_server_exe,
                         model_path=cfg.gpu_model_path, port=cfg.gpu_port)
    except Exception as e:
        if cfg.engine == "gpu":
            raise
        emit_fn({"event": "degraded", "engine": "cpu", "reason": str(e)})
        return CpuEngine(model_size=cfg.cpu_model)
