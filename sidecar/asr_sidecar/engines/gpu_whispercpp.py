import os
import subprocess
import time
import numpy as np
import httpx
from .base import AsrEngine
from ..audio import to_wav_bytes


class GpuEngineError(RuntimeError):
    pass


class GpuEngine(AsrEngine):
    """Client for a whisper.cpp `whisper-server` (Vulkan build) subprocess."""
    name = "gpu"

    def __init__(self, server_exe: str, model_path: str, port: int = 8910,
                 startup_timeout: int = 60, spawn: bool = True, transport=None):
        self._url = f"http://127.0.0.1:{port}"
        self._proc = None
        if spawn:
            if not server_exe or not os.path.exists(server_exe):
                raise GpuEngineError(f"whisper-server not found: {server_exe!r}")
            if not model_path or not os.path.exists(model_path):
                raise GpuEngineError(f"model not found: {model_path!r}")
        self._client = httpx.Client(transport=transport, timeout=120)
        if spawn:
            self._proc = subprocess.Popen(
                [server_exe, "-m", model_path, "--host", "127.0.0.1",
                 "--port", str(port)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self._wait_ready(startup_timeout)

    def _wait_ready(self, timeout: int) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self._proc.poll() is not None:
                raise GpuEngineError("whisper-server exited during startup "
                                     "(no Vulkan device? bad model?)")
            try:
                self._client.get(self._url + "/", timeout=2)
                return  # any HTTP response means the server is up
            except httpx.TransportError:
                time.sleep(0.5)
        self.close()
        raise GpuEngineError(f"whisper-server not ready after {timeout}s")

    def transcribe(self, audio: np.ndarray, initial_prompt: str = "") -> str:
        data = {"temperature": "0.0", "response_format": "json"}
        if initial_prompt:
            data["prompt"] = initial_prompt
        r = self._client.post(
            self._url + "/inference",
            files={"file": ("audio.wav", to_wav_bytes(audio), "audio/wav")},
            data=data)
        r.raise_for_status()
        return r.json()["text"].strip()

    def close(self) -> None:
        if self._proc and self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self._proc.kill()
        self._client.close()
