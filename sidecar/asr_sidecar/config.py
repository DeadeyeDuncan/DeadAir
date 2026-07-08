import dataclasses
from dataclasses import dataclass, field


@dataclass
class SidecarConfig:
    engine: str = "auto"            # auto | gpu | cpu
    model: str = "large-v3-turbo"   # GPU model label (informational)
    cpu_model: str = "small"        # faster-whisper size
    mic: str = "default"
    dictionary: list = field(default_factory=list)
    gpu_server_exe: str = ""        # path to whisper-server.exe
    gpu_model_path: str = ""        # path to ggml-*.bin
    gpu_port: int = 8910
    partials: bool = True           # live interim partials (GPU only)
    partial_interval_ms: int = 600  # re-decode cadence while recording
    partial_min_ms: int = 700       # min audio before the first partial
    partial_window_s: int = 30      # cap re-decode to the last N seconds

    @classmethod
    def from_cmd(cls, cmd: dict) -> "SidecarConfig":
        known = {f.name for f in dataclasses.fields(cls)}
        return cls(**{k: v for k, v in cmd.items() if k in known})
