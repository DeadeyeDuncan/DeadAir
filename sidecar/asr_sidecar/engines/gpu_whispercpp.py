import os
import subprocess
import threading
import time
from collections import deque
import numpy as np
import httpx
from .base import AsrEngine
from ..audio import to_wav_bytes


class GpuEngineError(RuntimeError):
    pass


class GpuEngine(AsrEngine):
    """Client for a whisper.cpp `whisper-server` (Vulkan build) subprocess.

    The Vulkan server can crash sporadically at inference time (AMD driver
    device-lost / TDR). It is a grandchild of the C# host, so nothing above
    restarts it — a dead server would otherwise make every later dictation
    fail with ``[WinError 10061] ... actively refused`` until the whole app
    is restarted. So a managed engine (one we spawned) detects a dead server
    on transcribe, respawns it once, and retries. Server stderr is captured
    so the crash reason is visible instead of swallowed into DEVNULL.
    """
    name = "gpu"

    # A live partial is worthless if it takes longer than a normal utterance;
    # bounding its POST also caps how long a hung/crashing server can hold the
    # shared server_lock and stall the authoritative final decode (fast-follow
    # to the Phase 1 PartialLoop.stop() join-timeout finding).
    partial_timeout_s: float = 8.0

    def __init__(self, server_exe: str, model_path: str, port: int = 8910,
                 startup_timeout: int = 60, spawn: bool = True, transport=None):
        self._server_exe = server_exe
        self._model_path = model_path
        self._port = port
        self._startup_timeout = startup_timeout
        self._url = f"http://127.0.0.1:{port}"
        self._proc = None
        self._reader = None
        self._server_log: deque[str] = deque(maxlen=50)
        self._manage_proc = spawn
        if spawn:
            if not server_exe or not os.path.exists(server_exe):
                raise GpuEngineError(f"whisper-server not found: {server_exe!r}")
            if not model_path or not os.path.exists(model_path):
                raise GpuEngineError(f"model not found: {model_path!r}")
        self._client = httpx.Client(transport=transport, timeout=120)
        if spawn:
            try:
                self._spawn()
            except Exception:
                self._client.close()  # ctor failed: don't leak the client
                raise

    # -- process lifecycle --------------------------------------------------

    def _spawn(self) -> None:
        """Launch (or relaunch) whisper-server and block until it answers."""
        self._terminate_proc()
        self._assert_port_free()
        self._proc = subprocess.Popen(
            [self._server_exe, "-m", self._model_path, "--host", "127.0.0.1",
             "--port", str(self._port)],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self._reader = threading.Thread(
            target=self._drain_stderr, args=(self._proc,), daemon=True)
        self._reader.start()
        self._wait_ready(self._startup_timeout)

    def _respawn(self) -> None:
        """Recover from a crashed server (used by transcribe self-heal)."""
        self._spawn()

    def _drain_stderr(self, proc: subprocess.Popen) -> None:
        try:
            for line in proc.stderr:  # blocks until the pipe closes
                self._server_log.append(line.rstrip())
        except Exception:
            pass  # pipe closed / process gone

    def _terminate_proc(self) -> None:
        if self._proc and self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self._proc.kill()
                # Reap it — an unreaped corpse could still hold the port and
                # trip the follow-up _assert_port_free probe.
                try:
                    self._proc.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    pass
        self._proc = None

    def _assert_port_free(self) -> None:
        """An orphaned whisper-server squatting the port would answer
        _wait_ready while our fresh child bind-fails and dies unnoticed
        (whisper.cpp loads the model before binding) — refuse loudly."""
        try:
            self._client.get(self._url + "/", timeout=2)
        except httpx.TransportError:
            return  # nothing listening — safe to bind
        raise GpuEngineError(
            f"port {self._port} already in use (stale whisper-server?) — "
            "kill the orphaned process or change gpu_port")

    def _wait_ready(self, timeout: int) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self._proc.poll() is not None:
                raise GpuEngineError(
                    "whisper-server exited during startup "
                    "(no Vulkan device? bad model?)\n" + self.server_log_tail())
            try:
                self._client.get(self._url + "/", timeout=2)
                return  # any HTTP response means the server is up
            except httpx.TransportError:
                time.sleep(0.5)
        self.close()
        raise GpuEngineError(f"whisper-server not ready after {timeout}s")

    def server_log_tail(self, n: int = 12) -> str:
        return "\n".join(list(self._server_log)[-n:])

    # -- inference ----------------------------------------------------------

    def _post(self, audio: np.ndarray, initial_prompt: str,
              timeout: float | None = None) -> str:
        data = {"temperature": "0.0", "response_format": "json"}
        if initial_prompt:
            data["prompt"] = initial_prompt
        # timeout=None -> use the client default (120s); a value overrides it.
        kwargs = {"timeout": timeout} if timeout is not None else {}
        r = self._client.post(
            self._url + "/inference",
            files={"file": ("audio.wav", to_wav_bytes(audio), "audio/wav")},
            data=data, **kwargs)
        r.raise_for_status()
        return r.json()["text"].strip()

    def transcribe(self, audio: np.ndarray, initial_prompt: str = "") -> str:
        try:
            return self._post(audio, initial_prompt)
        except httpx.TransportError as e:
            # Connection-level failure => the server process is gone/wedged
            # (the classic WinError 10061). A 500/HTTPStatusError is NOT
            # caught here: that means the server is alive but choked on the
            # input, and respawning would only hide it.
            if not self._manage_proc:
                raise GpuEngineError(
                    f"whisper-server unreachable: {e}") from e
            self._respawn()
            try:
                return self._post(audio, initial_prompt)
            except httpx.TransportError as e2:
                raise GpuEngineError(
                    "whisper-server crashed and could not recover: "
                    f"{e2}\n{self.server_log_tail()}") from e2

    def try_partial(self, audio: np.ndarray, initial_prompt: str = "") -> str | None:
        """Best-effort interim decode for the live pill. Returns None on any
        failure — never respawns, never raises — so a crashy partial can't
        wedge or delay the authoritative final decode (which keeps self-heal)."""
        try:
            return self._post(audio, initial_prompt,
                              timeout=self.partial_timeout_s)
        except Exception:
            return None

    def close(self) -> None:
        self._terminate_proc()
        self._client.close()
