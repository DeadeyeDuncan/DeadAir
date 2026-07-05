# LocalFlow Phase 0 (MVP) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fully-local Wispr Flow clone — hold Right Ctrl, speak, release → Whisper transcribes on-device, Ollama cleans up, text lands at the cursor in any Windows app.

**Architecture:** Two processes: a C#/.NET 8 WPF host (hotkey hook, tray, settings, Ollama HTTP, text injection, orchestration) and a long-running Python ASR sidecar (mic capture, Silero VAD, whisper.cpp-Vulkan GPU engine with faster-whisper CPU fallback), talking newline-delimited JSON over stdio. Spec: `../../spec.md`.

**Tech Stack:** .NET 8 (WPF, xUnit, H.NotifyIcon), Python 3.11+ (faster-whisper, sounddevice, httpx, pytest), whisper.cpp `whisper-server` (Vulkan build), Ollama ≥ 0.12.11 (`qwen2.5:7b`).

## Global Constraints

- Repo root: `H:\DeadMind V.3\LocalFlow\`. Host code in `host/`, sidecar in `sidecar/`, binaries in `tools/` (gitignored), GGML models in `models/` (gitignored).
- Target: Windows 11, AMD RX 6800 XT (gfx1030). **No CUDA anywhere.** GPU ASR = whisper.cpp **Vulkan** only; faster-whisper is **CPU-only** (CTranslate2 has no AMD-GPU path on Windows).
- Ollama: `qwen2.5:7b`, `temperature 0.1`, `num_ctx 8192`, version ≥ **0.12.11** (older 0.12.x crashes on gfx1030).
- Cleanup skip-guard: transcripts **< 50 chars** bypass the LLM (configurable `skipGuardChars`).
- Two cleanup modes: **Faithful** (default) and **Polished** — system prompts verbatim from spec §5.
- Default hotkey: **Right Ctrl (`VK_RCONTROL` 0xA3), single key, hold-to-talk** (resolved decision: spec §6's combo example deferred to a later phase; key is configurable).
- Sidecar packaging v0: **venv** at `sidecar/.venv` (resolved decision: no PyInstaller yet).
- Text injection: clipboard-paste primary, Unicode `SendInput` fallback; **never** pynput/pyautogui/`SendKeys`. On total failure, text stays on the clipboard + toast.
- Sidecar stdout is **protocol-only** (JSON lines). All Python logging goes to **stderr** — a stray `print()` corrupts IPC.
- Words are never lost: any cleanup failure injects the **raw transcript**.
- Config: `%APPDATA%\LocalFlow\config.json` (spec §6 schema + a `sidecar` launch section added by Task 7).
- Commit after every task (repo is initialized in Task 0).

---

### Task 0: Repo init + environment de-risk

**Files:**
- Create: `.gitignore`, `README.md` (stub), `models/.gitkeep`, `tools/.gitkeep`

**Interfaces:**
- Produces: a git repo; a working `tools/whisper/whisper-server.exe` (Vulkan) + `models/ggml-large-v3-turbo.bin`; Ollama serving `qwen2.5:7b` on GPU. Later tasks assume these exact paths.

This task is mostly environment validation (spec §7). GUI steps are manual checkpoints — record pass/fail in the README stub.

- [ ] **Step 1: Init repo**

```powershell
cd "H:\DeadMind V.3\LocalFlow"; git init
```

- [ ] **Step 2: Write `.gitignore`**

```gitignore
# build output
host/**/bin/
host/**/obj/
*.user
# python
sidecar/.venv/
__pycache__/
*.pyc
.pytest_cache/
# large local assets
models/*
!models/.gitkeep
tools/*
!tools/.gitkeep
# logs
*.log
```

- [ ] **Step 3: Write README stub**

```markdown
# LocalFlow

Fully-local voice dictation for Windows (Wispr Flow clone).
Hold Right Ctrl → speak → release → cleaned text at your cursor.

Spec: docs/spec.md · Plan: docs/superpowers/plans/2026-07-05-localflow-phase0.md

## De-risk checklist (Task 0)
- [ ] Const-me WhisperDesktop live-mic transcribes on the RX 6800 XT
- [ ] `ollama ps` shows qwen2.5:7b at 100% GPU
- [ ] whisper-server (Vulkan) transcribes jfk.wav
```

- [ ] **Step 4: Validate the card with Const-me WhisperDesktop (MANUAL)**

Download `WhisperDesktop.zip` from https://github.com/Const-me/Whisper/releases, run it with any GGML model, use the live-capture screen, confirm the 6800 XT transcribes speech. This validates the GPU with zero build effort. Check the box in README.

- [ ] **Step 5: Validate Ollama on gfx1030**

```powershell
ollama --version           # must be >= 0.12.11
ollama pull qwen2.5:7b
ollama run qwen2.5:7b "Say OK"    # then, in another shell:
ollama ps
```
Expected: `ollama ps` shows `qwen2.5:7b` with `100% GPU`. If the ROCm backend fails to load, set `OLLAMA_VULKAN=1` and retry. Do **not** install the `likelovewant/ollama-for-amd` fork.

- [ ] **Step 6: Get a Vulkan whisper-server + model**

Download the **Vulkan** archive from https://github.com/lemonade-sdk/whisper.cpp-amd/releases (fallback: `jerryshell/whisper.cpp-windows-vulkan-bin`). Extract so that `tools\whisper\whisper-server.exe` exists. Then:

```powershell
curl -L -o models\ggml-large-v3-turbo.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin
curl -L -o models\jfk.wav https://github.com/ggml-org/whisper.cpp/raw/master/samples/jfk.wav
tools\whisper\whisper-server.exe -m models\ggml-large-v3-turbo.bin --host 127.0.0.1 --port 8910
# in another shell:
curl -F file=@models\jfk.wav -F response_format=json http://127.0.0.1:8910/inference
```
Expected: JSON containing `"ask not what your country can do for you"`. Watch the server log for a Vulkan/GPU device line (should name the 6800 XT, not CPU).

- [ ] **Step 7: Commit**

```powershell
git add -A; git commit -m "chore: repo init, gitignore, de-risk checklist"
```

---

### Task 1: Sidecar package + IPC module

**Files:**
- Create: `sidecar/requirements.txt`, `sidecar/requirements-dev.txt`, `sidecar/asr_sidecar/__init__.py`, `sidecar/asr_sidecar/ipc.py`
- Test: `sidecar/tests/test_ipc.py`

**Interfaces:**
- Produces: `ipc.emit(obj, stream=None)` — writes one JSON line, thread-safe, flushes. `ipc.read_commands(stream=None)` — generator yielding parsed dicts from stdin; bad JSON emits an `error` event and continues.

- [ ] **Step 1: Create venv + requirements**

`sidecar/requirements.txt`:
```
faster-whisper>=1.0.0
sounddevice>=0.4.6
numpy>=1.26
httpx>=0.27
```
`sidecar/requirements-dev.txt`:
```
-r requirements.txt
pytest>=8.0
```
```powershell
cd "H:\DeadMind V.3\LocalFlow\sidecar"
python -m venv .venv
.venv\Scripts\pip install -r requirements-dev.txt
```

- [ ] **Step 2: Write the failing test** — `sidecar/tests/test_ipc.py`:

```python
import io
import json
from asr_sidecar import ipc


def test_emit_writes_single_json_line():
    out = io.StringIO()
    ipc.emit({"event": "ready", "engine": "cpu"}, stream=out)
    lines = out.getvalue().splitlines()
    assert len(lines) == 1
    assert json.loads(lines[0]) == {"event": "ready", "engine": "cpu"}


def test_read_commands_parses_lines_and_skips_blanks():
    src = io.StringIO('{"cmd":"start"}\n\n{"cmd":"stop"}\n')
    cmds = list(ipc.read_commands(src))
    assert cmds == [{"cmd": "start"}, {"cmd": "stop"}]


def test_read_commands_bad_json_emits_error_and_continues():
    src = io.StringIO('not json\n{"cmd":"start"}\n')
    err_out = io.StringIO()
    cmds = list(ipc.read_commands(src, err_stream=err_out))
    assert cmds == [{"cmd": "start"}]
    err = json.loads(err_out.getvalue().splitlines()[0])
    assert err["event"] == "error" and err["where"] == "ipc"
```

- [ ] **Step 3: Run to verify failure**

Run: `cd sidecar; .venv\Scripts\python -m pytest tests/test_ipc.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'asr_sidecar'` (add empty `asr_sidecar/__init__.py` first if needed, then `AttributeError` on `ipc`).

- [ ] **Step 4: Implement** — `sidecar/asr_sidecar/ipc.py`:

```python
"""Newline-delimited JSON protocol. stdout is protocol-ONLY; logs go to stderr."""
import json
import sys
import threading

_emit_lock = threading.Lock()


def emit(obj: dict, stream=None) -> None:
    stream = stream if stream is not None else sys.stdout
    with _emit_lock:
        stream.write(json.dumps(obj, ensure_ascii=False) + "\n")
        stream.flush()


def read_commands(stream=None, err_stream=None):
    stream = stream if stream is not None else sys.stdin
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            yield json.loads(line)
        except json.JSONDecodeError:
            emit({"event": "error", "where": "ipc",
                  "message": f"bad json: {line[:80]}"},
                 stream=err_stream if err_stream is not None else sys.stdout)
```

- [ ] **Step 5: Run to verify pass**

Run: `.venv\Scripts\python -m pytest tests/test_ipc.py -v` — Expected: 3 PASS.

- [ ] **Step 6: Commit**

```powershell
git add sidecar; git commit -m "feat(sidecar): package skeleton + JSON-lines IPC"
```

---

### Task 2: Sidecar config + VAD

**Files:**
- Create: `sidecar/asr_sidecar/config.py`, `sidecar/asr_sidecar/audio.py`, `sidecar/asr_sidecar/vad.py`
- Test: `sidecar/tests/test_config.py`, `sidecar/tests/test_vad.py`, fixture `sidecar/tests/fixtures/jfk.wav`

**Interfaces:**
- Produces: `SidecarConfig` dataclass with `from_cmd(dict)` (ignores unknown keys). Fields: `engine` ("auto"|"gpu"|"cpu"), `model`, `cpu_model`, `mic`, `dictionary: list[str]`, `gpu_server_exe`, `gpu_model_path`, `gpu_port`. `audio.load_wav(path) -> np.float32 mono@16k`. `vad.extract_speech(audio, sr=16000, pad_ms=200) -> np.ndarray | None` (None = no speech). VAD reuses faster-whisper's vendored Silero (onnxruntime — **no torch dependency**).

- [ ] **Step 1: Copy fixture**

```powershell
copy "H:\DeadMind V.3\LocalFlow\models\jfk.wav" "H:\DeadMind V.3\LocalFlow\sidecar\tests\fixtures\jfk.wav"
```

- [ ] **Step 2: Write failing tests** — `sidecar/tests/test_config.py`:

```python
from asr_sidecar.config import SidecarConfig


def test_defaults():
    c = SidecarConfig()
    assert c.engine == "auto" and c.cpu_model == "small" and c.dictionary == []


def test_from_cmd_ignores_unknown_keys():
    c = SidecarConfig.from_cmd({"cmd": "config", "engine": "cpu",
                                "dictionary": ["DeadMind"], "bogus": 1})
    assert c.engine == "cpu" and c.dictionary == ["DeadMind"]
```

`sidecar/tests/test_vad.py`:

```python
import numpy as np
from pathlib import Path
from asr_sidecar.audio import load_wav
from asr_sidecar.vad import extract_speech

FIXTURES = Path(__file__).parent / "fixtures"


def test_silence_returns_none():
    assert extract_speech(np.zeros(16000 * 2, dtype=np.float32)) is None


def test_speech_wav_returns_trimmed_audio():
    audio = load_wav(str(FIXTURES / "jfk.wav"))
    speech = extract_speech(audio)
    assert speech is not None
    assert 0 < len(speech) <= len(audio)
```

- [ ] **Step 3: Run to verify failure** — `.venv\Scripts\python -m pytest tests/test_config.py tests/test_vad.py -v` — Expected: FAIL (modules missing).

- [ ] **Step 4: Implement** — `sidecar/asr_sidecar/config.py`:

```python
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

    @classmethod
    def from_cmd(cls, cmd: dict) -> "SidecarConfig":
        known = {f.name for f in dataclasses.fields(cls)}
        return cls(**{k: v for k, v in cmd.items() if k in known})
```

`sidecar/asr_sidecar/audio.py`:

```python
import wave
import numpy as np


def load_wav(path: str) -> np.ndarray:
    """Load a 16 kHz mono 16-bit WAV as float32 in [-1, 1]."""
    with wave.open(path, "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1:
            raise ValueError(f"{path}: need 16kHz mono, got "
                             f"{w.getframerate()}Hz/{w.getnchannels()}ch")
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def to_wav_bytes(audio: np.ndarray, sr: int = 16000) -> bytes:
    import io
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((np.clip(audio, -1, 1) * 32767).astype(np.int16).tobytes())
    return buf.getvalue()
```

`sidecar/asr_sidecar/vad.py`:

```python
"""Silero VAD via faster-whisper's vendored copy (onnxruntime, no torch)."""
import numpy as np
from faster_whisper.vad import VadOptions, get_speech_timestamps


def extract_speech(audio: np.ndarray, sr: int = 16000,
                   pad_ms: int = 200) -> np.ndarray | None:
    ts = get_speech_timestamps(audio, VadOptions(min_silence_duration_ms=500))
    if not ts:
        return None
    pad = int(sr * pad_ms / 1000)
    chunks = [audio[max(0, t["start"] - pad):min(len(audio), t["end"] + pad)]
              for t in ts]
    return np.concatenate(chunks)
```

- [ ] **Step 5: Run to verify pass** — same command. Expected: 4 PASS. (If `faster_whisper.vad` import names differ in the installed version, check `pip show faster-whisper` ≥ 1.0 and adjust the import — the functions live in `faster_whisper/vad.py`.)

- [ ] **Step 6: Commit** — `git add sidecar; git commit -m "feat(sidecar): config dataclass, wav io, silero vad gate"`

---

### Task 3: CPU ASR engine (faster-whisper)

**Files:**
- Create: `sidecar/asr_sidecar/engines/__init__.py` (empty for now), `sidecar/asr_sidecar/engines/base.py`, `sidecar/asr_sidecar/engines/cpu_fasterwhisper.py`
- Test: `sidecar/tests/test_cpu_engine.py`

**Interfaces:**
- Produces: `AsrEngine` ABC: `name: str`, `transcribe(audio: np.ndarray, initial_prompt: str = "") -> str`, `close() -> None`. `CpuEngine(model_size: str = "small")` implements it (device="cpu", compute_type="int8").

- [ ] **Step 1: Write failing test** — `sidecar/tests/test_cpu_engine.py`:

```python
from pathlib import Path
import pytest
from asr_sidecar.audio import load_wav
from asr_sidecar.engines.cpu_fasterwhisper import CpuEngine

FIXTURES = Path(__file__).parent / "fixtures"


@pytest.mark.slow  # downloads the 'tiny' model (~75 MB) on first run
def test_cpu_engine_transcribes_jfk():
    eng = CpuEngine(model_size="tiny")
    text = eng.transcribe(load_wav(str(FIXTURES / "jfk.wav")))
    assert "country" in text.lower()
```

Register the marker in `sidecar/pytest.ini`:
```ini
[pytest]
markers =
    slow: downloads models / needs network on first run
    integration: spawns real subprocesses
```

- [ ] **Step 2: Run to verify failure** — `.venv\Scripts\python -m pytest tests/test_cpu_engine.py -v` — Expected: FAIL (module missing).

- [ ] **Step 3: Implement** — `sidecar/asr_sidecar/engines/base.py`:

```python
from abc import ABC, abstractmethod
import numpy as np


class AsrEngine(ABC):
    name: str = "base"

    @abstractmethod
    def transcribe(self, audio: np.ndarray, initial_prompt: str = "") -> str: ...

    def close(self) -> None:
        pass
```

`sidecar/asr_sidecar/engines/cpu_fasterwhisper.py`:

```python
from faster_whisper import WhisperModel
from .base import AsrEngine


class CpuEngine(AsrEngine):
    """CPU-only. CTranslate2 has no AMD-GPU path on Windows — never 'gpu' here."""
    name = "cpu"

    def __init__(self, model_size: str = "small"):
        self._model = WhisperModel(model_size, device="cpu", compute_type="int8")

    def transcribe(self, audio, initial_prompt=""):
        segments, _ = self._model.transcribe(
            audio, initial_prompt=initial_prompt or None)
        return " ".join(s.text.strip() for s in segments).strip()
```

- [ ] **Step 4: Run to verify pass** — same command. Expected: 1 PASS (first run downloads `tiny`).

- [ ] **Step 5: Commit** — `git add sidecar; git commit -m "feat(sidecar): AsrEngine interface + faster-whisper CPU engine"`

---

### Task 4: GPU ASR engine (whisper-server Vulkan client)

**Files:**
- Create: `sidecar/asr_sidecar/engines/gpu_whispercpp.py`
- Test: `sidecar/tests/test_gpu_engine.py`

**Interfaces:**
- Produces: `GpuEngine(server_exe, model_path, port=8910, startup_timeout=60, spawn=True, transport=None)` implementing `AsrEngine` (`name = "gpu"`). Raises `GpuEngineError` on missing exe/model or failed startup — the selector (Task 5) catches this to fall back. Owns a `whisper-server` subprocess; `close()` terminates it.

- [ ] **Step 1: Write failing tests** — `sidecar/tests/test_gpu_engine.py`:

```python
import json
import os
from pathlib import Path
import httpx
import numpy as np
import pytest
from asr_sidecar.engines.gpu_whispercpp import GpuEngine, GpuEngineError


def test_missing_exe_raises(tmp_path):
    with pytest.raises(GpuEngineError):
        GpuEngine(server_exe=str(tmp_path / "nope.exe"),
                  model_path=str(tmp_path / "nope.bin"))


def test_transcribe_posts_wav_and_parses_json():
    def handler(request: httpx.Request) -> httpx.Response:
        assert b"audio.wav" in request.read()  # multipart contains our file
        return httpx.Response(200, json={"text": " hello world "})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    text = eng.transcribe(np.zeros(16000, dtype=np.float32))
    assert text == "hello world"


@pytest.mark.integration
@pytest.mark.skipif(not os.environ.get("LOCALFLOW_WHISPER_SERVER"),
                    reason="set LOCALFLOW_WHISPER_SERVER + LOCALFLOW_WHISPER_MODEL")
def test_real_server_transcribes_jfk():
    from asr_sidecar.audio import load_wav
    eng = GpuEngine(server_exe=os.environ["LOCALFLOW_WHISPER_SERVER"],
                    model_path=os.environ["LOCALFLOW_WHISPER_MODEL"])
    try:
        text = eng.transcribe(load_wav(
            str(Path(__file__).parent / "fixtures" / "jfk.wav")))
        assert "country" in text.lower()
    finally:
        eng.close()
```

- [ ] **Step 2: Run to verify failure** — `.venv\Scripts\python -m pytest tests/test_gpu_engine.py -v` — Expected: FAIL (module missing).

- [ ] **Step 3: Implement** — `sidecar/asr_sidecar/engines/gpu_whispercpp.py`:

```python
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
        self._client = httpx.Client(transport=transport, timeout=120)
        self._proc = None
        if not spawn:
            return
        if not server_exe or not os.path.exists(server_exe):
            raise GpuEngineError(f"whisper-server not found: {server_exe!r}")
        if not model_path or not os.path.exists(model_path):
            raise GpuEngineError(f"model not found: {model_path!r}")
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
```

- [ ] **Step 4: Run to verify pass** — same command. Expected: 2 PASS, 1 SKIP. Then run the integration test once for real:

```powershell
$env:LOCALFLOW_WHISPER_SERVER = "H:\DeadMind V.3\LocalFlow\tools\whisper\whisper-server.exe"
$env:LOCALFLOW_WHISPER_MODEL  = "H:\DeadMind V.3\LocalFlow\models\ggml-large-v3-turbo.bin"
.venv\Scripts\python -m pytest tests/test_gpu_engine.py -v -m integration
```
Expected: 1 PASS (real Vulkan transcription on the 6800 XT).

- [ ] **Step 5: Commit** — `git add sidecar; git commit -m "feat(sidecar): whisper-server Vulkan GPU engine"`

---

### Task 5: Engine selector with auto-fallback

**Files:**
- Create: `sidecar/asr_sidecar/engines/__init__.py` (replace empty file)
- Test: `sidecar/tests/test_engine_select.py`

**Interfaces:**
- Produces: `create_engine(cfg: SidecarConfig, emit_fn) -> AsrEngine`. Rules: `engine="cpu"` → CpuEngine; `engine="gpu"` → GpuEngine (raise on failure); `engine="auto"` → try GpuEngine, on any exception call `emit_fn({"event":"degraded","engine":"cpu","reason":...})` and return CpuEngine.

- [ ] **Step 1: Write failing test** — `sidecar/tests/test_engine_select.py`:

```python
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
```

- [ ] **Step 2: Run to verify failure** — Expected: FAIL (`create_engine` missing).

- [ ] **Step 3: Implement** — `sidecar/asr_sidecar/engines/__init__.py`:

```python
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
```

Note: the tests monkeypatch `asr_sidecar.engines.GpuEngine`/`CpuEngine`, so `create_engine` must reference them via module globals exactly as written (bare names in this module) — do not import them inside the function.

- [ ] **Step 4: Run to verify pass** — Expected: 3 PASS.
- [ ] **Step 5: Commit** — `git add sidecar; git commit -m "feat(sidecar): engine selector with auto CPU fallback + degraded event"`

---

### Task 6: Mic capture + sidecar main loop (integration)

**Files:**
- Create: `sidecar/asr_sidecar/capture.py`, `sidecar/asr_sidecar/__main__.py`
- Test: `sidecar/tests/test_capture.py`, `sidecar/tests/test_sidecar_integration.py`

**Interfaces:**
- Produces: `MicCapture(device="default", sr=16000)` with `start()`, `stop() -> np.ndarray`, `cancel()`. The runnable sidecar: `python -m asr_sidecar` speaking the spec §3 protocol, plus a debug command `{"cmd":"transcribe_wav","path":...}` that runs VAD+ASR on a wav (this is the canned-wav test hook from spec §9). `final` events include `ms` (wall-clock ASR time).

- [ ] **Step 1: Write failing tests** — `sidecar/tests/test_capture.py`:

```python
import numpy as np
from asr_sidecar.capture import MicCapture


def test_frames_accumulate_and_stop_concatenates():
    cap = MicCapture()
    cap._recording = True  # simulate start without opening a real stream
    cap._on_frames(np.ones(160, dtype=np.float32))
    cap._on_frames(np.zeros(160, dtype=np.float32))
    audio = cap.stop()
    assert len(audio) == 320 and audio[0] == 1.0 and audio[-1] == 0.0


def test_cancel_discards():
    cap = MicCapture()
    cap._recording = True
    cap._on_frames(np.ones(160, dtype=np.float32))
    cap.cancel()
    assert len(cap.stop()) == 0
```

`sidecar/tests/test_sidecar_integration.py`:

```python
import json
import subprocess
import sys
from pathlib import Path
import pytest

FIXTURES = Path(__file__).parent / "fixtures"


@pytest.mark.integration
@pytest.mark.slow
def test_sidecar_end_to_end_cpu(tmp_path):
    proc = subprocess.Popen(
        [sys.executable, "-m", "asr_sidecar"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
        cwd=str(Path(__file__).parents[1]))

    def send(obj):
        proc.stdin.write(json.dumps(obj) + "\n")
        proc.stdin.flush()

    def events_until(name, limit=20):
        for _ in range(limit):
            e = json.loads(proc.stdout.readline())
            if e["event"] == name:
                return e
        raise AssertionError(f"never saw {name}")

    try:
        send({"cmd": "config", "engine": "cpu", "cpu_model": "tiny"})
        assert events_until("ready")["engine"] == "cpu"
        send({"cmd": "transcribe_wav", "path": str(FIXTURES / "jfk.wav")})
        final = events_until("final")
        assert "country" in final["text"].lower()
        assert final["ms"] > 0
        send({"cmd": "shutdown"})
        proc.wait(timeout=10)
    finally:
        proc.kill()
```

- [ ] **Step 2: Run to verify failure** — Expected: FAIL (modules missing).

- [ ] **Step 3: Implement** — `sidecar/asr_sidecar/capture.py`:

```python
import threading
import numpy as np


class MicCapture:
    """Records the full utterance between start() and stop(). 16 kHz mono."""

    def __init__(self, device: str = "default", sr: int = 16000):
        self._sr = sr
        self._device = None if device in (None, "", "default") else device
        self._frames: list[np.ndarray] = []
        self._stream = None
        self._recording = False
        self._lock = threading.Lock()

    def _on_frames(self, mono: np.ndarray) -> None:
        with self._lock:
            if self._recording:
                self._frames.append(mono)

    def _callback(self, indata, frames, time_info, status):
        self._on_frames(indata[:, 0].copy())

    def start(self) -> None:
        import sounddevice as sd
        with self._lock:
            self._frames = []
            self._recording = True
        self._stream = sd.InputStream(
            samplerate=self._sr, channels=1, dtype="float32",
            device=self._device, callback=self._callback)
        self._stream.start()

    def stop(self) -> np.ndarray:
        self._close_stream()
        with self._lock:
            self._recording = False
            frames, self._frames = self._frames, []
        return (np.concatenate(frames) if frames
                else np.zeros(0, dtype=np.float32))

    def cancel(self) -> None:
        self._close_stream()
        with self._lock:
            self._recording = False
            self._frames = []

    def _close_stream(self) -> None:
        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None
```

`sidecar/asr_sidecar/__main__.py`:

```python
"""LocalFlow ASR sidecar. Protocol: spec.md §3. stdout = protocol only."""
import logging
import sys
import time
import numpy as np
from .audio import load_wav
from .capture import MicCapture
from .config import SidecarConfig
from .engines import create_engine
from .ipc import emit, read_commands
from .vad import extract_speech

logging.basicConfig(stream=sys.stderr, level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("sidecar")


def _finish(audio: np.ndarray, cfg: SidecarConfig, engine) -> None:
    try:
        t0 = time.monotonic()
        speech = extract_speech(audio) if len(audio) else None
        if speech is None:
            emit({"event": "empty"})
            return
        text = engine.transcribe(speech,
                                 initial_prompt=", ".join(cfg.dictionary))
        ms = int((time.monotonic() - t0) * 1000)
        if not text:
            emit({"event": "empty"})
        else:
            emit({"event": "final", "text": text, "ms": ms})
    except Exception as e:
        log.exception("asr failed")
        emit({"event": "error", "where": "asr", "message": str(e)})


def main() -> None:
    cfg = SidecarConfig()
    engine = None
    cap = None
    for cmd in read_commands():
        c = cmd.get("cmd")
        try:
            if c == "config":
                cfg = SidecarConfig.from_cmd(cmd)
                if engine:
                    engine.close()
                engine = create_engine(cfg, emit)
                cap = MicCapture(cfg.mic)
                emit({"event": "ready", "engine": engine.name,
                      "model": cfg.model if engine.name == "gpu"
                      else cfg.cpu_model})
            elif c == "start":
                cap.start()
                emit({"event": "recording"})
            elif c == "stop":
                _finish(cap.stop(), cfg, engine)
            elif c == "cancel":
                cap.cancel()
            elif c == "transcribe_wav":  # test/debug hook (spec §9)
                _finish(load_wav(cmd["path"]), cfg, engine)
            elif c == "shutdown":
                break
            else:
                emit({"event": "error", "where": "ipc",
                      "message": f"unknown cmd: {c}"})
        except Exception as e:
            log.exception("command failed")
            emit({"event": "error", "where": "mic" if c in ("start", "stop")
                  else "asr", "message": str(e)})
    if engine:
        engine.close()


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run all sidecar tests** — `.venv\Scripts\python -m pytest tests -v` — Expected: all PASS (integration test takes ~30 s with `tiny`).

- [ ] **Step 5: Manual mic check** — run `.venv\Scripts\python -m asr_sidecar`, paste `{"cmd":"config","engine":"cpu","cpu_model":"tiny"}`, wait for `ready`, paste `{"cmd":"start"}`, speak, paste `{"cmd":"stop"}`. Expected: `final` with your words.

- [ ] **Step 6: Commit** — `git add sidecar; git commit -m "feat(sidecar): mic capture + main IPC loop + integration test"`

---

### Task 7: Host solution + config store

**Files:**
- Create: `host/LocalFlow.sln`, `host/LocalFlow.Core/LocalFlow.Core.csproj`, `host/LocalFlow.Core/Config/AppConfig.cs`, `host/LocalFlow.Core/Config/ConfigStore.cs`, `host/LocalFlow.Core.Tests/LocalFlow.Core.Tests.csproj`
- Test: `host/LocalFlow.Core.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `AppConfig` (spec §6 + `Sidecar` launch section), `ConfigStore.Load(path)` / `Save(config, path)` — round-trip JSON, missing file → defaults. `CleanupMode` enum `{ Faithful, Polished }`. All later host tasks consume `AppConfig`.

- [ ] **Step 1: Scaffold**

```powershell
cd "H:\DeadMind V.3\LocalFlow\host"
dotnet new sln -n LocalFlow
dotnet new classlib -n LocalFlow.Core -f net8.0
dotnet new xunit -n LocalFlow.Core.Tests -f net8.0
dotnet sln add LocalFlow.Core LocalFlow.Core.Tests
dotnet add LocalFlow.Core.Tests reference LocalFlow.Core
del LocalFlow.Core\Class1.cs; del LocalFlow.Core.Tests\UnitTest1.cs
```

- [ ] **Step 2: Write failing test** — `host/LocalFlow.Core.Tests/ConfigStoreTests.cs`:

```csharp
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigStore.Load(Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json"));
        Assert.Equal("RControl", cfg.Hotkey.Key);
        Assert.Equal(CleanupMode.Faithful, cfg.Cleanup.Mode);
        Assert.Equal(50, cfg.Cleanup.SkipGuardChars);
        Assert.Equal("qwen2.5:7b", cfg.Ollama.Model);
        Assert.Equal(8192, cfg.Ollama.NumCtx);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString(), "config.json");
        var cfg = new AppConfig();
        cfg.Dictionary.Add("DeadMind");
        cfg.Cleanup.Mode = CleanupMode.Polished;
        ConfigStore.Save(cfg, path);
        var loaded = ConfigStore.Load(path);
        Assert.Contains("DeadMind", loaded.Dictionary);
        Assert.Equal(CleanupMode.Polished, loaded.Cleanup.Mode);
    }
}
```

- [ ] **Step 3: Run to verify failure** — `dotnet test` — Expected: compile FAIL (types missing).

- [ ] **Step 4: Implement** — `host/LocalFlow.Core/Config/AppConfig.cs`:

```csharp
using System.Text.Json.Serialization;

namespace LocalFlow.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter<CleanupMode>))]
public enum CleanupMode { Faithful, Polished }

public sealed class AppConfig
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public string ModeToggleHotkey { get; set; } = "Ctrl+Alt+M";
    public AsrConfig Asr { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public CleanupConfig Cleanup { get; set; } = new();
    public PromptsConfig Prompts { get; set; } = new();
    public List<string> Dictionary { get; set; } = new();
    public string Mic { get; set; } = "default";
    public InjectConfig Inject { get; set; } = new();
    public SidecarLaunchConfig Sidecar { get; set; } = new();
}

public sealed class HotkeyConfig
{
    public string Key { get; set; } = "RControl";
    public string Mode { get; set; } = "hold";
}

public sealed class AsrConfig
{
    public string Engine { get; set; } = "auto"; // auto | gpu | cpu
    public string GpuModel { get; set; } = "large-v3-turbo";
    public string CpuModel { get; set; } = "small";
    public string GpuServerExe { get; set; } = @"..\..\tools\whisper\whisper-server.exe";
    public string GpuModelPath { get; set; } = @"..\..\models\ggml-large-v3-turbo.bin";
    public int GpuPort { get; set; } = 8910;
}

public sealed class OllamaConfig
{
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b";
    public int NumCtx { get; set; } = 8192;
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class CleanupConfig
{
    public CleanupMode Mode { get; set; } = CleanupMode.Faithful;
    public int SkipGuardChars { get; set; } = 50;
}

public sealed class PromptsConfig
{
    public string Faithful { get; set; } =
        "You clean raw speech-to-text transcripts. Remove filler words (um, uh, like, you know). " +
        "Fix punctuation, capitalization, and light grammar. If the speaker self-corrects, keep only " +
        "the corrected version. Preserve the speaker's meaning, wording, and tone. Do NOT add, infer, " +
        "summarize, reword, or answer anything. Preserve technical terms, names, commands, and file " +
        "paths exactly. Output ONLY the cleaned transcript with no preamble.";

    public string Polished { get; set; } =
        "You clean and lightly polish raw speech-to-text transcripts. Remove filler words, fix " +
        "punctuation/capitalization/grammar, keep only self-corrected versions, and smooth awkward " +
        "or run-on phrasing into clear, natural sentences. Preserve the speaker's meaning, intent, " +
        "and tone — do NOT add new information, summarize, or answer anything. Preserve technical " +
        "terms, names, commands, and file paths exactly. Output ONLY the polished transcript with " +
        "no preamble.";
}

public sealed class InjectConfig
{
    public string Method { get; set; } = "auto"; // auto | clipboard | sendinput
    public string PasteHotkey { get; set; } = "Ctrl+V";
    public int RestoreClipboardDelayMs { get; set; } = 150;
}

public sealed class SidecarLaunchConfig
{
    public string Python { get; set; } = @"..\..\sidecar\.venv\Scripts\python.exe";
    public string Args { get; set; } = "-m asr_sidecar";
    public string WorkingDir { get; set; } = @"..\..\sidecar";
}
```

`host/LocalFlow.Core/Config/ConfigStore.cs`:

```csharp
using System.Text.Json;

namespace LocalFlow.Core.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LocalFlow", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppConfig();
        return JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path), Options) ?? new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test` — Expected: 2 PASS.
- [ ] **Step 6: Commit** — `git add host; git commit -m "feat(host): solution scaffold + AppConfig/ConfigStore"`

---

### Task 8: Sidecar DTOs + SidecarManager

**Files:**
- Create: `host/LocalFlow.Core/Sidecar/SidecarEvent.cs`, `host/LocalFlow.Core/Sidecar/SidecarCommands.cs`, `host/LocalFlow.Core/Sidecar/SidecarManager.cs`
- Test: `host/LocalFlow.Core.Tests/SidecarManagerTests.cs`, fixture `host/LocalFlow.Core.Tests/fixtures/fake_sidecar.py`

**Interfaces:**
- Consumes: `AppConfig.Sidecar`, `AppConfig.Asr`, `AppConfig.Mic`, `AppConfig.Dictionary` (Task 7).
- Produces: `SidecarEvent` record (`Event`, `Engine`, `Model`, `Text`, `Ms`, `Reason`, `Where`, `Message`). `ISidecarControl { Task StartUtteranceAsync(); Task StopUtteranceAsync(); Task CancelAsync(); }`. `SidecarManager : ISidecarControl, IDisposable` with `event Action<SidecarEvent>? EventReceived`, `event Action? Faulted`, `Task LaunchAsync()`, `Task SendConfigAsync(AppConfig)`, `Task ShutdownAsync()`. Restarts on unexpected exit with backoff `1,2,4,8,16,30s`, max 5 consecutive failures → `Faulted`. Protocol keys are snake_case (`cpu_model`, `gpu_server_exe`, ...) to match the Python side.

- [ ] **Step 1: Write fixture** — `host/LocalFlow.Core.Tests/fixtures/fake_sidecar.py`:

```python
import json
import sys

print(json.dumps({"event": "ready", "engine": "cpu", "model": "fake"}),
      flush=True)
for line in sys.stdin:
    cmd = json.loads(line)
    if cmd["cmd"] == "start":
        print(json.dumps({"event": "recording"}), flush=True)
    elif cmd["cmd"] == "stop":
        print(json.dumps({"event": "final", "text": "hello world", "ms": 5}),
              flush=True)
    elif cmd["cmd"] == "shutdown":
        break
```

Add to `LocalFlow.Core.Tests.csproj`:
```xml
<ItemGroup>
  <None Update="fixtures\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write failing test** — `host/LocalFlow.Core.Tests/SidecarManagerTests.cs`:

```csharp
using LocalFlow.Core.Config;
using LocalFlow.Core.Sidecar;

namespace LocalFlow.Core.Tests;

public class SidecarManagerTests
{
    private static AppConfig FakeConfig() => new()
    {
        Sidecar = new SidecarLaunchConfig
        {
            Python = "python",
            Args = Path.Combine(AppContext.BaseDirectory,
                                "fixtures", "fake_sidecar.py"),
            WorkingDir = AppContext.BaseDirectory,
        }
    };

    [Fact]
    public async Task Launch_ReceivesReady_ThenStartStopRoundTrips()
    {
        using var mgr = new SidecarManager(FakeConfig());
        var events = new List<SidecarEvent>();
        var final = new TaskCompletionSource<SidecarEvent>();
        mgr.EventReceived += e =>
        {
            events.Add(e);
            if (e.Event == "final") final.TrySetResult(e);
        };

        await mgr.LaunchAsync();
        await mgr.StartUtteranceAsync();
        await mgr.StopUtteranceAsync();

        var f = await final.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("hello world", f.Text);
        Assert.Contains(events, e => e.Event == "ready");
        await mgr.ShutdownAsync();
    }
}
```

- [ ] **Step 3: Run to verify failure** — `dotnet test --filter SidecarManagerTests` — Expected: compile FAIL.

- [ ] **Step 4: Implement** — `host/LocalFlow.Core/Sidecar/SidecarEvent.cs`:

```csharp
using System.Text.Json.Serialization;

namespace LocalFlow.Core.Sidecar;

public sealed record SidecarEvent
{
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("ms")] public int? Ms { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("where")] public string? Where { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}
```

`host/LocalFlow.Core/Sidecar/SidecarCommands.cs`:

```csharp
using System.Text.Json.Serialization;
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Sidecar;

public sealed record ConfigCommand
{
    [JsonPropertyName("cmd")] public string Cmd => "config";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "auto";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("cpu_model")] public string CpuModel { get; init; } = "";
    [JsonPropertyName("mic")] public string Mic { get; init; } = "default";
    [JsonPropertyName("dictionary")] public List<string> Dictionary { get; init; } = new();
    [JsonPropertyName("gpu_server_exe")] public string GpuServerExe { get; init; } = "";
    [JsonPropertyName("gpu_model_path")] public string GpuModelPath { get; init; } = "";
    [JsonPropertyName("gpu_port")] public int GpuPort { get; init; } = 8910;

    public static ConfigCommand From(AppConfig c) => new()
    {
        Engine = c.Asr.Engine,
        Model = c.Asr.GpuModel,
        CpuModel = c.Asr.CpuModel,
        Mic = c.Mic,
        Dictionary = c.Dictionary,
        GpuServerExe = Path.GetFullPath(c.Asr.GpuServerExe,
            AppContext.BaseDirectory),
        GpuModelPath = Path.GetFullPath(c.Asr.GpuModelPath,
            AppContext.BaseDirectory),
        GpuPort = c.Asr.GpuPort,
    };
}

public sealed record SimpleCommand([property: JsonPropertyName("cmd")] string Cmd);
```

`host/LocalFlow.Core/Sidecar/SidecarManager.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Sidecar;

public interface ISidecarControl
{
    Task StartUtteranceAsync();
    Task StopUtteranceAsync();
    Task CancelAsync();
}

public sealed class SidecarManager : ISidecarControl, IDisposable
{
    private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };
    private readonly AppConfig _config;
    private Process? _proc;
    private int _consecutiveFailures;
    private bool _shuttingDown;

    public event Action<SidecarEvent>? EventReceived;
    public event Action? Faulted;

    public SidecarManager(AppConfig config) => _config = config;

    public async Task LaunchAsync()
    {
        var s = _config.Sidecar;
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = s.Python,
            Arguments = s.Args,
            WorkingDirectory = Path.GetFullPath(s.WorkingDir,
                AppContext.BaseDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        }) ?? throw new InvalidOperationException("failed to start sidecar");
        _proc.StandardInput.AutoFlush = true;

        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(() => _proc.StandardError.ReadToEndAsync()); // drain
        await SendConfigAsync(_config);
    }

    private async Task ReadLoopAsync()
    {
        var proc = _proc!;
        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<SidecarEvent>(line);
                if (e is not null)
                {
                    _consecutiveFailures = 0;
                    EventReceived?.Invoke(e);
                }
            }
            catch (JsonException) { /* garbage line — ignore */ }
        }
        if (!_shuttingDown) await RestartAsync();
    }

    private async Task RestartAsync()
    {
        if (_consecutiveFailures >= 5) { Faulted?.Invoke(); return; }
        var delay = BackoffSeconds[Math.Min(_consecutiveFailures,
            BackoffSeconds.Length - 1)];
        _consecutiveFailures++;
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (!_shuttingDown) await LaunchAsync();
    }

    public Task SendConfigAsync(AppConfig c) =>
        SendAsync(ConfigCommand.From(c));

    public Task StartUtteranceAsync() => SendAsync(new SimpleCommand("start"));
    public Task StopUtteranceAsync() => SendAsync(new SimpleCommand("stop"));
    public Task CancelAsync() => SendAsync(new SimpleCommand("cancel"));

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        try { await SendAsync(new SimpleCommand("shutdown")); } catch { }
        if (_proc is not null && !_proc.WaitForExit(5000)) _proc.Kill();
    }

    private async Task SendAsync(object cmd)
    {
        if (_proc is null) throw new InvalidOperationException("not launched");
        await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(cmd));
    }

    public void Dispose()
    {
        _shuttingDown = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
    }
}
```

Note for the test: `fake_sidecar.py` never receives a valid response to the initial `config` command it's sent (it only handles start/stop/shutdown) — that's fine; it emits `ready` unconditionally at startup.

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter SidecarManagerTests` — Expected: PASS.
- [ ] **Step 6: Commit** — `git add host; git commit -m "feat(host): sidecar DTOs + SidecarManager with restart/backoff"`

---

### Task 9: PromptBuilder + OllamaClient

**Files:**
- Create: `host/LocalFlow.Core/Cleanup/PromptBuilder.cs`, `host/LocalFlow.Core/Cleanup/OllamaClient.cs`
- Test: `host/LocalFlow.Core.Tests/PromptBuilderTests.cs`, `host/LocalFlow.Core.Tests/OllamaClientTests.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 7).
- Produces: `PromptBuilder.Build(CleanupMode, AppConfig) -> string`. `ITranscriptCleaner { Task<CleanupResult> CleanAsync(string transcript, CleanupMode mode, CancellationToken ct = default); }`. `record CleanupResult(string Text, bool Skipped, string? Reason)`. `OllamaClient(AppConfig cfg, HttpMessageHandler? handler = null) : ITranscriptCleaner`. Behavior: short transcript → skipped without HTTP; any HTTP/parse/timeout error → returns raw transcript with `Skipped=true` (words never lost).

- [ ] **Step 1: Write failing tests** — `host/LocalFlow.Core.Tests/PromptBuilderTests.cs`:

```csharp
using LocalFlow.Core.Cleanup;
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Faithful_NoDictionary_IsBasePrompt()
    {
        var cfg = new AppConfig();
        Assert.Equal(cfg.Prompts.Faithful,
            PromptBuilder.Build(CleanupMode.Faithful, cfg));
    }

    [Fact]
    public void Polished_WithDictionary_AppendsTerms()
    {
        var cfg = new AppConfig();
        cfg.Dictionary.AddRange(new[] { "DeadMind", "gfx1030" });
        var p = PromptBuilder.Build(CleanupMode.Polished, cfg);
        Assert.StartsWith(cfg.Prompts.Polished, p);
        Assert.Contains("DeadMind, gfx1030", p);
    }
}
```

`host/LocalFlow.Core.Tests/OllamaClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using LocalFlow.Core.Cleanup;
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Tests;

file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
}

public class OllamaClientTests
{
    private static AppConfig Cfg() => new();

    [Fact]
    public async Task ShortTranscript_SkipsLlm()
    {
        var handler = new StubHandler(_ => throw new Exception("must not call"));
        var client = new OllamaClient(Cfg(), handler);
        var r = await client.CleanAsync("hi there", CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal("hi there", r.Text);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Success_AccumulatesStreamedResponse()
    {
        var ndjson =
            "{\"response\":\"Hello\",\"done\":false}\n" +
            "{\"response\":\" world.\",\"done\":true}\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ndjson, Encoding.UTF8) });
        var client = new OllamaClient(Cfg(), handler);
        var longText = new string('x', 60);
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        Assert.False(r.Skipped);
        Assert.Equal("Hello world.", r.Text);
    }

    [Fact]
    public async Task HttpFailure_ReturnsRawTranscript()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var client = new OllamaClient(Cfg(), handler);
        var longText = new string('x', 60);
        var r = await client.CleanAsync(longText, CleanupMode.Faithful);
        Assert.True(r.Skipped);
        Assert.Equal(longText, r.Text);
        Assert.Contains("down", r.Reason);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "PromptBuilderTests|OllamaClientTests"` — Expected: compile FAIL.

- [ ] **Step 3: Implement** — `host/LocalFlow.Core/Cleanup/PromptBuilder.cs`:

```csharp
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Cleanup;

public static class PromptBuilder
{
    public static string Build(CleanupMode mode, AppConfig cfg)
    {
        var basePrompt = mode == CleanupMode.Faithful
            ? cfg.Prompts.Faithful : cfg.Prompts.Polished;
        if (cfg.Dictionary.Count == 0) return basePrompt;
        return basePrompt +
            "\nPreserve these terms exactly, correcting near-misspellings " +
            "to them: " + string.Join(", ", cfg.Dictionary) + ".";
    }
}
```

`host/LocalFlow.Core/Cleanup/OllamaClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using LocalFlow.Core.Config;

namespace LocalFlow.Core.Cleanup;

public interface ITranscriptCleaner
{
    Task<CleanupResult> CleanAsync(string transcript, CleanupMode mode,
        CancellationToken ct = default);
}

public sealed record CleanupResult(string Text, bool Skipped, string? Reason);

public sealed class OllamaClient : ITranscriptCleaner
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;

    public OllamaClient(AppConfig cfg, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(cfg.Ollama.TimeoutSeconds);
    }

    public async Task<CleanupResult> CleanAsync(string transcript,
        CleanupMode mode, CancellationToken ct = default)
    {
        if (transcript.Length < _cfg.Cleanup.SkipGuardChars)
            return new CleanupResult(transcript, true, "below skip guard");
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _cfg.Ollama.Model,
                system = PromptBuilder.Build(mode, _cfg),
                prompt = transcript,
                stream = true,
                options = new
                {
                    temperature = _cfg.Ollama.Temperature,
                    num_ctx = _cfg.Ollama.NumCtx,
                },
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                _cfg.Ollama.Url.TrimEnd('/') + "/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var reader = new StreamReader(
                await resp.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var r))
                    sb.Append(r.GetString());
            }
            var text = sb.ToString().Trim();
            return text.Length == 0
                ? new CleanupResult(transcript, true, "empty LLM output")
                : new CleanupResult(text, false, null);
        }
        catch (Exception ex)
        {
            return new CleanupResult(transcript, true, ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run to verify pass** — Expected: 5 PASS.
- [ ] **Step 5: Manual live check** (Ollama running): temporarily run a scratch console call or defer to Task 14's E2E.
- [ ] **Step 6: Commit** — `git add host; git commit -m "feat(host): PromptBuilder + streaming OllamaClient with raw-passthrough fallback"`

---

### Task 10: Text injection stack

**Files:**
- Create: `host/LocalFlow.Core/Inject/ITextInjector.cs`, `host/LocalFlow.Core/Inject/NativeInput.cs`, `host/LocalFlow.Core/Inject/SendInputInjector.cs`, `host/LocalFlow.Core/Inject/ClipboardPasteInjector.cs`, `host/LocalFlow.Core/Inject/CompositeInjector.cs`
- Test: `host/LocalFlow.Core.Tests/InjectTests.cs`

**Interfaces:**
- Produces: `ITextInjector { Task<bool> InjectAsync(string text); }` (composite: true = inserted; false = all strategies failed, **text left on clipboard**). `IInjectionStrategy { Task<bool> TryInjectAsync(string text); }`. `IClipboard { string? GetText(); void SetText(string text); }` (WPF implementation in Task 13; tests use fakes). `NativeInput.BuildUnicodeInputs(string) -> (ushort code, bool isReturn)[]` — pure, testable; `\r\n`/`\n` become VK_RETURN; .NET strings are already UTF-16 so surrogate pairs arrive as two units naturally.

- [ ] **Step 1: Write failing tests** — `host/LocalFlow.Core.Tests/InjectTests.cs`:

```csharp
using LocalFlow.Core.Inject;

namespace LocalFlow.Core.Tests;

file sealed class FakeClipboard : IClipboard
{
    public string? Stored;
    public string? GetText() => Stored;
    public void SetText(string text) => Stored = text;
}

file sealed class FakeStrategy(bool result) : IInjectionStrategy
{
    public int Calls;
    public string? LastText;
    public Task<bool> TryInjectAsync(string text)
    { Calls++; LastText = text; return Task.FromResult(result); }
}

public class InjectTests
{
    [Fact]
    public void BuildUnicodeInputs_EmojiYieldsTwoSurrogateUnits()
    {
        var units = NativeInput.BuildUnicodeInputs("a😀");
        Assert.Equal(3, units.Length);           // 'a' + high + low surrogate
        Assert.Equal((ushort)0xD83D, units[1].Code);
        Assert.Equal((ushort)0xDE00, units[2].Code);
        Assert.All(units, u => Assert.False(u.IsReturn));
    }

    [Fact]
    public void BuildUnicodeInputs_NewlinesBecomeReturn()
    {
        var units = NativeInput.BuildUnicodeInputs("a\r\nb\nc");
        Assert.Equal(5, units.Length);
        Assert.True(units[1].IsReturn);
        Assert.True(units[3].IsReturn);
    }

    [Fact]
    public async Task Composite_FirstStrategyWins()
    {
        var s1 = new FakeStrategy(true);
        var s2 = new FakeStrategy(true);
        var clip = new FakeClipboard();
        var inj = new CompositeInjector(new IInjectionStrategy[] { s1, s2 }, clip);
        Assert.True(await inj.InjectAsync("hello"));
        Assert.Equal(1, s1.Calls);
        Assert.Equal(0, s2.Calls);
    }

    [Fact]
    public async Task Composite_AllFail_LeavesTextOnClipboard()
    {
        var clip = new FakeClipboard();
        var inj = new CompositeInjector(
            new IInjectionStrategy[] { new FakeStrategy(false),
                                       new FakeStrategy(false) }, clip);
        Assert.False(await inj.InjectAsync("precious words"));
        Assert.Equal("precious words", clip.Stored);
    }

    [Fact]
    public async Task ClipboardPaste_RestoresPriorClipboardOnSuccess()
    {
        var clip = new FakeClipboard { Stored = "old stuff" };
        var pasted = new List<string?>();
        var strat = new ClipboardPasteInjector(clip,
            sendPaste: () => { pasted.Add(clip.GetText()); return true; },
            restoreDelayMs: 0);
        Assert.True(await strat.TryInjectAsync("new text"));
        Assert.Equal("new text", pasted.Single()); // pasted while ours was set
        Assert.Equal("old stuff", clip.Stored);    // then restored
    }
}
```

- [ ] **Step 2: Run to verify failure** — Expected: compile FAIL.

- [ ] **Step 3: Implement** — `host/LocalFlow.Core/Inject/ITextInjector.cs`:

```csharp
namespace LocalFlow.Core.Inject;

public interface ITextInjector
{
    /// <returns>true = injected; false = failed, text left on clipboard.</returns>
    Task<bool> InjectAsync(string text);
}

public interface IInjectionStrategy
{
    Task<bool> TryInjectAsync(string text);
}

public interface IClipboard
{
    string? GetText();
    void SetText(string text);
}
```

`host/LocalFlow.Core/Inject/NativeInput.cs`:

```csharp
using System.Runtime.InteropServices;

namespace LocalFlow.Core.Inject;

public static class NativeInput
{
    public readonly record struct UnicodeUnit(ushort Code, bool IsReturn);

    /// <summary>Pure planning step for SendInput. .NET strings are UTF-16, so
    /// surrogate pairs (emoji etc.) already arrive as two char units — exactly
    /// what KEYEVENTF_UNICODE requires. Newlines map to VK_RETURN.</summary>
    public static UnicodeUnit[] BuildUnicodeInputs(string text)
    {
        var list = new List<UnicodeUnit>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                list.Add(new UnicodeUnit(0, true));
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n') list.Add(new UnicodeUnit(0, true));
            else list.Add(new UnicodeUnit(c, false));
        }
        return list.ToArray();
    }

    // ---- Win32 SendInput plumbing (not unit-tested; exercised manually) ----

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT
        { wVk = vk, wScan = scan, dwFlags = flags } } };

    public static bool SendUnicodeText(string text)
    {
        foreach (var u in BuildUnicodeInputs(text))
        {
            INPUT[] pair = u.IsReturn
                ? new[] { Key(VK_RETURN, 0, 0),
                          Key(VK_RETURN, 0, KEYEVENTF_KEYUP) }
                : new[] { Key(0, u.Code, KEYEVENTF_UNICODE),
                          Key(0, u.Code, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP) };
            if (SendInput((uint)pair.Length, pair,
                    Marshal.SizeOf<INPUT>()) != pair.Length)
                return false;
        }
        return true;
    }

    public static bool SendCtrlV()
    {
        var seq = new[]
        {
            Key(VK_CONTROL, 0, 0), Key(VK_V, 0, 0),
            Key(VK_V, 0, KEYEVENTF_KEYUP), Key(VK_CONTROL, 0, KEYEVENTF_KEYUP),
        };
        return SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>())
            == seq.Length;
    }
}
```

`host/LocalFlow.Core/Inject/SendInputInjector.cs`:

```csharp
namespace LocalFlow.Core.Inject;

public sealed class SendInputInjector : IInjectionStrategy
{
    public Task<bool> TryInjectAsync(string text) =>
        Task.FromResult(NativeInput.SendUnicodeText(text));
}
```

`host/LocalFlow.Core/Inject/ClipboardPasteInjector.cs`:

```csharp
namespace LocalFlow.Core.Inject;

public sealed class ClipboardPasteInjector(
    IClipboard clipboard, Func<bool> sendPaste, int restoreDelayMs)
    : IInjectionStrategy
{
    public async Task<bool> TryInjectAsync(string text)
    {
        var previous = clipboard.GetText();
        clipboard.SetText(text);
        await Task.Delay(50); // let the clipboard settle before pasting
        if (!sendPaste()) return false;
        await Task.Delay(restoreDelayMs);
        if (previous is not null) clipboard.SetText(previous);
        return true;
    }
}
```

`host/LocalFlow.Core/Inject/CompositeInjector.cs`:

```csharp
namespace LocalFlow.Core.Inject;

public sealed class CompositeInjector(
    IReadOnlyList<IInjectionStrategy> strategies, IClipboard clipboard)
    : ITextInjector
{
    public async Task<bool> InjectAsync(string text)
    {
        foreach (var s in strategies)
        {
            try { if (await s.TryInjectAsync(text)) return true; }
            catch { /* try next strategy */ }
        }
        clipboard.SetText(text); // never lose words (spec §4.1 rule 3)
        return false;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter InjectTests` — Expected: 5 PASS.
- [ ] **Step 5: Commit** — `git add host; git commit -m "feat(host): dual-strategy text injection (clipboard-paste + Unicode SendInput)"`

---

### Task 11: Hold-to-talk hotkey

**Files:**
- Create: `host/LocalFlow.Core/Hotkey/HoldKeyStateMachine.cs`, `host/LocalFlow.Core/Hotkey/VkMap.cs`, `host/LocalFlow.Core/Hotkey/KeyboardHook.cs`
- Test: `host/LocalFlow.Core.Tests/HotkeyTests.cs`

**Interfaces:**
- Produces: `HoldKeyStateMachine(int vkCode)` with `event Action? HoldStarted`, `event Action? HoldEnded`, `void OnKeyEvent(int vk, bool isDown, bool injected)`. `VkMap.Resolve("RControl") -> 0xA3` (also: RAlt 0xA5, F13–F24, CapsLock 0x14; unknown throws `ArgumentException`). `KeyboardHook : IDisposable` — dedicated thread, `SetWindowsHookEx(WH_KEYBOARD_LL)`, message pump, forwards to the state machine, ignores `LLKHF_INJECTED` events (so our own SendInput/Ctrl+V can't re-trigger recording), trivial callback (spec §D6: a slow callback gets the hook silently removed).

- [ ] **Step 1: Write failing tests** — `host/LocalFlow.Core.Tests/HotkeyTests.cs`:

```csharp
using LocalFlow.Core.Hotkey;

namespace LocalFlow.Core.Tests;

public class HotkeyTests
{
    private const int VK = 0xA3; // RControl

    [Fact]
    public void DownFiresOnce_AutoRepeatIgnored_UpFires()
    {
        var sm = new HoldKeyStateMachine(VK);
        int started = 0, ended = 0;
        sm.HoldStarted += () => started++;
        sm.HoldEnded += () => ended++;

        sm.OnKeyEvent(VK, isDown: true, injected: false);
        sm.OnKeyEvent(VK, isDown: true, injected: false);  // auto-repeat
        sm.OnKeyEvent(VK, isDown: true, injected: false);
        sm.OnKeyEvent(VK, isDown: false, injected: false);

        Assert.Equal(1, started);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void OtherKeysAndInjectedEventsIgnored()
    {
        var sm = new HoldKeyStateMachine(VK);
        int started = 0;
        sm.HoldStarted += () => started++;
        sm.OnKeyEvent(0x41, true, false);   // 'A'
        sm.OnKeyEvent(VK, true, injected: true); // our own SendInput echo
        Assert.Equal(0, started);
    }

    [Fact]
    public void VkMap_ResolvesKnown_ThrowsUnknown()
    {
        Assert.Equal(0xA3, VkMap.Resolve("RControl"));
        Assert.Equal(0x7C, VkMap.Resolve("F13"));
        Assert.Throws<ArgumentException>(() => VkMap.Resolve("SuperKey"));
    }
}
```

- [ ] **Step 2: Run to verify failure** — Expected: compile FAIL.

- [ ] **Step 3: Implement** — `host/LocalFlow.Core/Hotkey/HoldKeyStateMachine.cs`:

```csharp
namespace LocalFlow.Core.Hotkey;

public sealed class HoldKeyStateMachine(int vkCode)
{
    private bool _held;

    public event Action? HoldStarted;
    public event Action? HoldEnded;

    public void OnKeyEvent(int vk, bool isDown, bool injected)
    {
        if (injected || vk != vkCode) return;
        if (isDown && !_held) { _held = true; HoldStarted?.Invoke(); }
        else if (!isDown && _held) { _held = false; HoldEnded?.Invoke(); }
    }
}
```

`host/LocalFlow.Core/Hotkey/VkMap.cs`:

```csharp
namespace LocalFlow.Core.Hotkey;

public static class VkMap
{
    private static readonly Dictionary<string, int> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["RControl"] = 0xA3, ["LControl"] = 0xA2,
        ["RAlt"] = 0xA5, ["LAlt"] = 0xA4,
        ["RShift"] = 0xA1, ["LShift"] = 0xA0,
        ["CapsLock"] = 0x14, ["Scroll"] = 0x91, ["Pause"] = 0x13,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F,
        ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
        ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
    };

    public static int Resolve(string name) =>
        Map.TryGetValue(name, out var vk) ? vk
        : throw new ArgumentException($"unknown hotkey name: {name}");
}
```

`host/LocalFlow.Core/Hotkey/KeyboardHook.cs`:

```csharp
using System.Runtime.InteropServices;

namespace LocalFlow.Core.Hotkey;

public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProc proc,
        nint hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode,
        nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out nint msg, nint hWnd,
        uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg,
        nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_QUIT = 0x0012;

    private readonly HoldKeyStateMachine _machine;
    private readonly HookProc _proc; // rooted: prevents GC of the delegate
    private readonly Thread _thread;
    private nint _hook;
    private uint _threadId;

    public KeyboardHook(HoldKeyStateMachine machine)
    {
        _machine = machine;
        _proc = Callback;
        _thread = new Thread(RunPump) { IsBackground = true,
            Name = "LocalFlow-KbHook" };
        _thread.Start();
    }

    private void RunPump()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, 0, 0);
        // Keep the callback trivial and this pump responsive: Windows silently
        // removes hooks whose callbacks time out (spec §D6).
        while (GetMessageW(out _, 0, 0, 0) > 0) { }
        if (_hook != 0) UnhookWindowsHookEx(_hook);
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isDown = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = wParam is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
                _machine.OnKeyEvent((int)data.vkCode, isDown,
                    (data.flags & LLKHF_INJECTED) != 0);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() =>
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
}
```

Note: `HoldStarted`/`HoldEnded` fire **on the hook thread** — subscribers must marshal to their own context and return fast (Task 13 wires them through `Dispatcher`/`Task.Run`).

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter HotkeyTests` — Expected: 3 PASS.
- [ ] **Step 5: Commit** — `git add host; git commit -m "feat(host): hold-to-talk state machine + low-level keyboard hook"`

---

### Task 12: Orchestrator state machine

**Files:**
- Create: `host/LocalFlow.Core/Orchestrator.cs`
- Test: `host/LocalFlow.Core.Tests/OrchestratorTests.cs`

**Interfaces:**
- Consumes: `ISidecarControl` (Task 8), `ITranscriptCleaner`/`CleanupResult` (Task 9), `ITextInjector` (Task 10), `SidecarEvent` (Task 8), `AppConfig` (Task 7).
- Produces: `FlowState { Idle, Recording, Transcribing, Cleaning, Injecting }`. `IUserNotifier { void SetState(FlowState state); void Toast(string message); }` (tray implements in Task 13). `Orchestrator(ISidecarControl, ITranscriptCleaner, ITextInjector, IUserNotifier, AppConfig)` with `FlowState State`, `CleanupMode Mode` (get/set), `Task OnHotkeyDownAsync()`, `Task OnHotkeyUpAsync()`, `Task OnSidecarEventAsync(SidecarEvent e)`, `event Action<string>? LatencyLogged`.

- [ ] **Step 1: Write failing tests** — `host/LocalFlow.Core.Tests/OrchestratorTests.cs`:

```csharp
using LocalFlow.Core;
using LocalFlow.Core.Cleanup;
using LocalFlow.Core.Config;
using LocalFlow.Core.Inject;
using LocalFlow.Core.Sidecar;

namespace LocalFlow.Core.Tests;

file sealed class FakeSidecar : ISidecarControl
{
    public int Starts, Stops, Cancels;
    public Task StartUtteranceAsync() { Starts++; return Task.CompletedTask; }
    public Task StopUtteranceAsync() { Stops++; return Task.CompletedTask; }
    public Task CancelAsync() { Cancels++; return Task.CompletedTask; }
}

file sealed class FakeCleaner(CleanupResult result) : ITranscriptCleaner
{
    public string? SeenTranscript;
    public Task<CleanupResult> CleanAsync(string t, CleanupMode m,
        CancellationToken ct = default)
    { SeenTranscript = t; return Task.FromResult(result); }
}

file sealed class FakeInjector(bool ok) : ITextInjector
{
    public string? Injected;
    public Task<bool> InjectAsync(string text)
    { Injected = text; return Task.FromResult(ok); }
}

file sealed class FakeNotifier : IUserNotifier
{
    public List<string> Toasts = new();
    public FlowState LastState;
    public void SetState(FlowState s) => LastState = s;
    public void Toast(string m) => Toasts.Add(m);
}

public class OrchestratorTests
{
    private static Orchestrator Make(FakeSidecar sc, ITranscriptCleaner cl,
        ITextInjector inj, FakeNotifier n) =>
        new(sc, cl, inj, n, new AppConfig());

    [Fact]
    public async Task HappyPath_CleansAndInjects()
    {
        var sc = new FakeSidecar();
        var cl = new FakeCleaner(new CleanupResult("Clean text.", false, null));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = Make(sc, cl, inj, n);

        await o.OnHotkeyDownAsync();
        Assert.Equal(FlowState.Recording, o.State);
        Assert.Equal(1, sc.Starts);

        await o.OnHotkeyUpAsync();
        Assert.Equal(FlowState.Transcribing, o.State);
        Assert.Equal(1, sc.Stops);

        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw text", Ms = 500 });
        Assert.Equal("raw text", cl.SeenTranscript);
        Assert.Equal("Clean text.", inj.Injected);
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task CleanupFailure_InjectsRaw_AndToasts()
    {
        var cl = new FakeCleaner(new CleanupResult("raw words here that are long",
            true, "connection refused"));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), cl, inj, n);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw words here that are long" });
        Assert.Equal("raw words here that are long", inj.Injected);
        Assert.Contains(n.Toasts, t => t.Contains("cleanup skipped"));
    }

    [Fact]
    public async Task InjectFailure_ToastsClipboardHint()
    {
        var cl = new FakeCleaner(new CleanupResult("text", true, "below skip guard"));
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), cl, new FakeInjector(ok: false), n);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "text" });
        Assert.Contains(n.Toasts, t => t.Contains("Ctrl+V"));
    }

    [Fact]
    public async Task EmptyEvent_ReturnsToIdle_NoInjection()
    {
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)), inj,
            new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "empty" });
        Assert.Null(inj.Injected);
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task HotkeyDown_WhileBusy_Ignored()
    {
        var sc = new FakeSidecar();
        var o = Make(sc, new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyDownAsync(); // second press while recording
        Assert.Equal(1, sc.Starts);
    }
}
```

- [ ] **Step 2: Run to verify failure** — Expected: compile FAIL.

- [ ] **Step 3: Implement** — `host/LocalFlow.Core/Orchestrator.cs`:

```csharp
using System.Diagnostics;
using LocalFlow.Core.Cleanup;
using LocalFlow.Core.Config;
using LocalFlow.Core.Inject;
using LocalFlow.Core.Sidecar;

namespace LocalFlow.Core;

public enum FlowState { Idle, Recording, Transcribing, Cleaning, Injecting }

public interface IUserNotifier
{
    void SetState(FlowState state);
    void Toast(string message);
}

public sealed class Orchestrator(
    ISidecarControl sidecar, ITranscriptCleaner cleaner,
    ITextInjector injector, IUserNotifier notifier, AppConfig config)
{
    private readonly Stopwatch _clock = new();
    private bool _degradedToastShown;

    public FlowState State { get; private set; } = FlowState.Idle;
    public CleanupMode Mode { get; set; } = config.Cleanup.Mode;

    public event Action<string>? LatencyLogged;

    private void SetState(FlowState s) { State = s; notifier.SetState(s); }

    public async Task OnHotkeyDownAsync()
    {
        if (State != FlowState.Idle) return;
        SetState(FlowState.Recording);
        await sidecar.StartUtteranceAsync();
    }

    public async Task OnHotkeyUpAsync()
    {
        if (State != FlowState.Recording) return;
        SetState(FlowState.Transcribing);
        _clock.Restart();
        await sidecar.StopUtteranceAsync();
    }

    public async Task OnSidecarEventAsync(SidecarEvent e)
    {
        switch (e.Event)
        {
            case "final":
                await HandleFinalAsync(e);
                break;
            case "empty":
                SetState(FlowState.Idle);
                break;
            case "degraded":
                if (!_degradedToastShown)
                {
                    _degradedToastShown = true;
                    notifier.Toast($"GPU unavailable ({e.Reason}) — using CPU");
                }
                break;
            case "error":
                notifier.Toast($"Error ({e.Where}): {e.Message}");
                SetState(FlowState.Idle);
                break;
        }
    }

    private async Task HandleFinalAsync(SidecarEvent e)
    {
        var asrMs = _clock.ElapsedMilliseconds;
        SetState(FlowState.Cleaning);
        var result = await cleaner.CleanAsync(e.Text ?? "", Mode);
        var cleanMs = _clock.ElapsedMilliseconds - asrMs;
        if (result.Skipped && result.Reason != "below skip guard")
            notifier.Toast($"cleanup skipped: {result.Reason}");

        SetState(FlowState.Injecting);
        var ok = await injector.InjectAsync(result.Text);
        if (!ok)
            notifier.Toast("Couldn't insert — text on clipboard, press Ctrl+V");
        LatencyLogged?.Invoke(
            $"asr={e.Ms ?? asrMs}ms clean={cleanMs}ms " +
            $"total={_clock.ElapsedMilliseconds}ms chars={result.Text.Length}");
        SetState(FlowState.Idle);
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test` — Expected: full suite PASS (all tasks so far).
- [ ] **Step 5: Commit** — `git add host; git commit -m "feat(host): orchestrator state machine wiring hotkey→sidecar→cleanup→inject"`

---

### Task 13: WPF app — tray, settings, wiring

**Files:**
- Create: `host/LocalFlow.App/LocalFlow.App.csproj`, `host/LocalFlow.App/App.xaml`, `host/LocalFlow.App/App.xaml.cs`, `host/LocalFlow.App/WpfClipboard.cs`, `host/LocalFlow.App/TrayNotifier.cs`, `host/LocalFlow.App/SettingsWindow.xaml`, `host/LocalFlow.App/SettingsWindow.xaml.cs`
- Modify: `host/LocalFlow.sln` (add project)

**Interfaces:**
- Consumes: everything from Tasks 7–12.
- Produces: the runnable tray app. `WpfClipboard : IClipboard` (STA-marshalled). `TrayNotifier : IUserNotifier` (H.NotifyIcon tooltip/state + balloon toasts).

This task is UI-heavy; automated tests don't apply — the deliverable is verified by the manual checklist in Step 5 and the E2E in Task 14.

- [ ] **Step 1: Scaffold the WPF project**

```powershell
cd "H:\DeadMind V.3\LocalFlow\host"
dotnet new wpf -n LocalFlow.App -f net8.0
dotnet sln add LocalFlow.App
dotnet add LocalFlow.App reference LocalFlow.Core
dotnet add LocalFlow.App package H.NotifyIcon.Wpf
del LocalFlow.App\MainWindow.xaml; del LocalFlow.App\MainWindow.xaml.cs
```
In `LocalFlow.App.csproj` ensure: `<UseWPF>true</UseWPF>`, `<OutputType>WinExe</OutputType>`.

- [ ] **Step 2: Implement clipboard + notifier** — `host/LocalFlow.App/WpfClipboard.cs`:

```csharp
using System.Windows;
using System.Windows.Threading;
using LocalFlow.Core.Inject;

namespace LocalFlow.App;

public sealed class WpfClipboard(Dispatcher dispatcher) : IClipboard
{
    public string? GetText() => dispatcher.Invoke(() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public void SetText(string text) => dispatcher.Invoke(() =>
        Clipboard.SetDataObject(text, copy: true));
}
```

`host/LocalFlow.App/TrayNotifier.cs`:

```csharp
using System.Windows.Threading;
using H.NotifyIcon;
using LocalFlow.Core;

namespace LocalFlow.App;

public sealed class TrayNotifier(TaskbarIcon tray, Dispatcher dispatcher)
    : IUserNotifier
{
    public void SetState(FlowState state) => dispatcher.BeginInvoke(() =>
        tray.ToolTipText = $"LocalFlow — {state}");

    public void Toast(string message) => dispatcher.BeginInvoke(() =>
        tray.ShowNotification("LocalFlow", message));
}
```

- [ ] **Step 3: App wiring** — `host/LocalFlow.App/App.xaml` (no StartupUri — tray-only):

```xml
<Application x:Class="LocalFlow.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources />
</Application>
```

`host/LocalFlow.App/App.xaml.cs`:

```csharp
using System.IO;
using System.Windows;
using H.NotifyIcon;
using LocalFlow.Core;
using LocalFlow.Core.Cleanup;
using LocalFlow.Core.Config;
using LocalFlow.Core.Hotkey;
using LocalFlow.Core.Inject;
using LocalFlow.Core.Sidecar;

namespace LocalFlow.App;

public partial class App : Application
{
    private AppConfig _config = null!;
    private SidecarManager _sidecar = null!;
    private Orchestrator _orchestrator = null!;
    private KeyboardHook _hook = null!;
    private TaskbarIcon _tray = null!;
    private StreamWriter _log = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _config = ConfigStore.Load();

        var logDir = Path.Combine(Path.GetDirectoryName(
            ConfigStore.DefaultPath)!, "logs");
        Directory.CreateDirectory(logDir);
        _log = new StreamWriter(Path.Combine(logDir,
            $"localflow-{DateTime.Now:yyyyMMdd}.log"), append: true)
        { AutoFlush = true };

        _tray = new TaskbarIcon
        {
            ToolTipText = "LocalFlow — starting…",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
        _tray.ForceCreate();
        var notifier = new TrayNotifier(_tray, Dispatcher);

        var clipboard = new WpfClipboard(Dispatcher);
        var injector = new CompositeInjector(new IInjectionStrategy[]
        {
            new ClipboardPasteInjector(clipboard, NativeInput.SendCtrlV,
                _config.Inject.RestoreClipboardDelayMs),
            new SendInputInjector(),
        }, clipboard);

        _sidecar = new SidecarManager(_config);
        var cleaner = new OllamaClient(_config);
        _orchestrator = new Orchestrator(_sidecar, cleaner, injector,
            notifier, _config);
        _orchestrator.LatencyLogged += line =>
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        _sidecar.EventReceived += ev =>
            _ = _orchestrator.OnSidecarEventAsync(ev);
        _sidecar.Faulted += () =>
            notifier.Toast("Sidecar keeps crashing — check logs.");

        var machine = new HoldKeyStateMachine(
            VkMap.Resolve(_config.Hotkey.Key));
        // Hook-thread callbacks must return fast: fire-and-forget to the pool.
        machine.HoldStarted += () => _ = _orchestrator.OnHotkeyDownAsync();
        machine.HoldEnded += () => _ = _orchestrator.OnHotkeyUpAsync();
        _hook = new KeyboardHook(machine);

        try { await _sidecar.LaunchAsync(); }
        catch (Exception ex)
        { notifier.Toast($"Sidecar failed to start: {ex.Message}"); }
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var mode = new System.Windows.Controls.MenuItem
        { Header = "Polished mode", IsCheckable = true };
        mode.Checked += (_, _) => _orchestrator.Mode = CleanupMode.Polished;
        mode.Unchecked += (_, _) => _orchestrator.Mode = CleanupMode.Faithful;

        var settings = new System.Windows.Controls.MenuItem
        { Header = "Settings…" };
        settings.Click += (_, _) =>
            new SettingsWindow(_config, OnSettingsSaved).Show();

        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += async (_, _) =>
        {
            _hook.Dispose();
            await _sidecar.ShutdownAsync();
            Shutdown();
        };

        menu.Items.Add(mode);
        menu.Items.Add(settings);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private async void OnSettingsSaved()
    {
        ConfigStore.Save(_config);
        await _sidecar.SendConfigAsync(_config); // hot-reload sidecar side
    }
}
```

- [ ] **Step 4: Settings window** — `host/LocalFlow.App/SettingsWindow.xaml`:

```xml
<Window x:Class="LocalFlow.App.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LocalFlow Settings" Width="440" Height="520"
        WindowStartupLocation="CenterScreen">
    <ScrollViewer Margin="12">
        <StackPanel>
            <TextBlock FontWeight="Bold" Text="Hotkey (hold to talk)"/>
            <ComboBox x:Name="HotkeyBox" Margin="0,4,0,12"/>

            <TextBlock FontWeight="Bold" Text="ASR engine"/>
            <ComboBox x:Name="EngineBox" Margin="0,4,0,12">
                <ComboBoxItem Content="auto"/>
                <ComboBoxItem Content="gpu"/>
                <ComboBoxItem Content="cpu"/>
            </ComboBox>

            <TextBlock FontWeight="Bold" Text="Ollama model"/>
            <TextBox x:Name="OllamaModelBox" Margin="0,4,0,12"/>

            <TextBlock FontWeight="Bold" Text="Default cleanup mode"/>
            <ComboBox x:Name="ModeBox" Margin="0,4,0,12">
                <ComboBoxItem Content="Faithful"/>
                <ComboBoxItem Content="Polished"/>
            </ComboBox>

            <TextBlock FontWeight="Bold"
                       Text="Custom dictionary (one term per line)"/>
            <TextBox x:Name="DictionaryBox" Margin="0,4,0,12" Height="120"
                     AcceptsReturn="True" VerticalScrollBarVisibility="Auto"/>

            <Button x:Name="SaveButton" Content="Save" Width="90"
                    HorizontalAlignment="Right" Click="OnSave"/>
        </StackPanel>
    </ScrollViewer>
</Window>
```

`host/LocalFlow.App/SettingsWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using LocalFlow.Core.Config;

namespace LocalFlow.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action _onSaved;

    private static readonly string[] HotkeyChoices =
    { "RControl", "RAlt", "CapsLock", "F13", "Scroll", "Pause" };

    public SettingsWindow(AppConfig config, Action onSaved)
    {
        InitializeComponent();
        _config = config;
        _onSaved = onSaved;
        foreach (var k in HotkeyChoices)
            HotkeyBox.Items.Add(new ComboBoxItem { Content = k });
        Select(HotkeyBox, _config.Hotkey.Key);
        Select(EngineBox, _config.Asr.Engine);
        OllamaModelBox.Text = _config.Ollama.Model;
        Select(ModeBox, _config.Cleanup.Mode.ToString());
        DictionaryBox.Text = string.Join(Environment.NewLine,
            _config.Dictionary);
    }

    private static void Select(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Content == value) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string Selected(ComboBox box) =>
        (string)((ComboBoxItem)box.SelectedItem).Content;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.Hotkey.Key = Selected(HotkeyBox);
        _config.Asr.Engine = Selected(EngineBox);
        _config.Ollama.Model = OllamaModelBox.Text.Trim();
        _config.Cleanup.Mode = Enum.Parse<CleanupMode>(Selected(ModeBox));
        _config.Dictionary = DictionaryBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries |
                                        StringSplitOptions.TrimEntries)
            .ToList();
        _onSaved();
        Close();
    }
}
```

Note: hotkey changes take effect after app restart in v0 (the hook is built once at startup) — acceptable; note it in the README.

- [ ] **Step 5: Build + manual checklist**

Run: `dotnet build` (expected: success), then `dotnet run --project LocalFlow.App` and verify:
- Tray icon appears; tooltip shows state.
- Hold Right Ctrl → tooltip "Recording"; speak; release → text lands in Notepad.
- Tray menu toggles Polished mode; Settings opens, saves, persists to `%APPDATA%\LocalFlow\config.json`.
- Exit cleanly terminates python (check Task Manager).

- [ ] **Step 6: Commit** — `git add host; git commit -m "feat(app): WPF tray app, settings window, full pipeline wiring"`

---

### Task 14: E2E validation, latency, README

**Files:**
- Modify: `README.md` (replace stub)

**Interfaces:**
- Consumes: the whole system.

- [ ] **Step 1: Full-suite regression**

```powershell
cd "H:\DeadMind V.3\LocalFlow\sidecar"; .venv\Scripts\python -m pytest tests -v
cd "H:\DeadMind V.3\LocalFlow\host"; dotnet test
```
Expected: all PASS.

- [ ] **Step 2: E2E smoke matrix (MANUAL — spec §9)**

With Ollama running and the app started, dictate a 2–3 sentence utterance into each: **Notepad**, **VS Code**, **a browser text field**, **Windows Terminal**. For each, record pass/fail + observed latency (from the log file `%APPDATA%\LocalFlow\logs\`). Also verify:
- Faithful vs Polished produce visibly different cleanup on a rambling utterance.
- Stop Ollama (`ollama stop qwen2.5:7b` + kill the service) → dictation still injects the **raw** transcript + "cleanup skipped" toast.
- Set `"engine": "cpu"` in settings → still works (slower ASR).
- Open an elevated PowerShell, dictate → expect the clipboard toast (UIPI limit).
- Latency: check log lines; GPU path total should land ≈1–3 s for a normal sentence (spec §D3 budget).

- [ ] **Step 3: Write the real README**

```markdown
# LocalFlow

Fully-local voice dictation for Windows 11 (a Wispr Flow clone). Hold **Right
Ctrl**, speak, release — your words are transcribed on-device (whisper.cpp
Vulkan on AMD / faster-whisper CPU) and cleaned up by a local LLM (Ollama),
then inserted at the cursor in any app. Nothing leaves your machine.

## Requirements
- Windows 11, AMD GPU (Vulkan) or any CPU
- Ollama ≥ 0.12.11 with `qwen2.5:7b` pulled
- Python 3.11+, .NET 8 SDK
- `tools/whisper/whisper-server.exe` (Vulkan build — see docs/spec.md §7)
- `models/ggml-large-v3-turbo.bin`

## Setup
1. `cd sidecar && python -m venv .venv && .venv\Scripts\pip install -r requirements.txt`
2. `ollama pull qwen2.5:7b`
3. `cd host && dotnet build -c Release`
4. Run `LocalFlow.App.exe` — a tray icon appears.

## Use
- **Hold Right Ctrl** → speak → release. Cleaned text lands at your cursor.
- Tray menu: toggle Faithful/Polished cleanup, Settings, Exit.
- Custom dictionary (Settings) biases recognition toward your jargon.

## Known limits (v0)
- Can't type into elevated/admin windows (Windows UIPI) — text is left on
  the clipboard; paste manually.
- Hotkey changes need an app restart.
- Utterance-at-a-time (no live streaming yet — Phase 1).

## Architecture
See docs/spec.md. Host (C#/WPF) ↔ Python ASR sidecar over stdio JSON;
Ollama does transcript cleanup. Phased roadmap in spec §10.
```

- [ ] **Step 4: Update de-risk checklist boxes in README if not already, commit**

```powershell
git add -A; git commit -m "docs: README, E2E smoke results, phase-0 complete"
```

- [ ] **Step 5: Refresh the durable backup** (per standing user practice): copy the repo to `C:\Users\auand\DeadMind-backup-2026-07-01\` sibling location if the user wants LocalFlow included — **ask first**, it's outside DeadMind proper.

---

## Self-review notes

- **Spec coverage:** §2 architecture → Tasks 6/8/13; §3 protocol → Tasks 1/6/8 (+ `transcribe_wav` debug extension, called out); §4.1 components → Tasks 7–13; §4.2 → Tasks 2–6; §5 prompts → Task 7 (verbatim) + Task 9 builder; §6 config → Task 7 (+ `sidecar` section, documented addition); §7 setup → Task 0; §8 error matrix → Tasks 5/6/8/9/10/12 (each row has an owner); §9 testing → per-task tests + Task 14; §10 phasing → this plan is Phase 0 only. Mode-toggle hotkey (`modeToggleHotkey`) is in config but v0 exposes the toggle via tray menu only — global mode-toggle hotkey deferred to Phase 4 polish (documented here; the config key is reserved).
- **Elevated-window detection** (spec §8): v0 does not pre-detect elevation; it relies on strategy failure → clipboard+toast, which the spec's fallback row permits. Pre-detection via `OpenProcess` is a Phase 4 nicety.
- **Type consistency check:** `ISidecarControl` names (`StartUtteranceAsync`/`StopUtteranceAsync`/`CancelAsync`) match Tasks 8→12→13; `CleanupResult(Text, Skipped, Reason)` matches 9→12; `IClipboard` matches 10→13; `VkMap.Resolve` matches 11→13; snake_case protocol keys match Task 2 (`SidecarConfig` fields) ↔ Task 8 (`ConfigCommand` JsonPropertyNames).
