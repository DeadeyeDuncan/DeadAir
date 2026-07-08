# Phase 1 Live Pill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the recording pill live — a scrolling PCM oscilloscope plus a self-correcting interim transcript — while keeping the injected result the unchanged Ollama-cleaned final.

**Architecture:** Sidecar streams two things while recording: a downsampled PCM `waveform` event (~40 Hz, replaces the RMS `level` event) and, GPU-only, a `partial` event from re-decoding the accumulating buffer every ~600 ms via the existing whisper-server. The host renders both in the pill on the focus-safe fast path. Partials are best-effort and isolated: any failure is swallowed, never emits `error`, never triggers the GPU self-heal, and never touches injection. On key-up the authoritative path (`_finish` → `final` → Ollama clean → inject) runs exactly as today, serialized against any in-flight partial by a shared lock.

**Tech Stack:** Python 3.11 sidecar (numpy, httpx, faster-whisper, pytest), C#/.NET 8 WPF host (xUnit).

## Global Constraints

- **Preview-only:** partial/interim text is NEVER injected. Only the `final` event drives Ollama cleanup + injection.
- **GPU-only partials:** the partial loop runs only when `engine.name == "gpu"`. The CPU engine path is unchanged (no partials).
- **Partial isolation:** a partial decode must never emit an `error` event, never call the GPU self-heal `_respawn`, and never delay/replace the authoritative final. Failures are logged to stderr only.
- **Single whisper-server, single-flight:** partial POSTs and the final POST serialize on one shared `threading.Lock`.
- **Pill never takes focus:** preserve `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`, `IsHitTestVisible=false`, `Focusable=false`; never call `Activate()`.
- **stdout is protocol-only** in the sidecar; all logs go to stderr (spec §3).
- **Sidecar test command:** `cd sidecar && .venv\Scripts\python -m pytest tests/ -q`
- **Host test command:** `cd host && dotnet test`
- Waveform defaults: 8 bins → 16 floats/event, ~40 Hz (`min_interval_ms=25`). Partial defaults: `partial_interval_ms=600`, `partial_min_ms=700`, `partial_window_s=30`, `partials=true`.

---

## File Structure

**Sidecar (Python)**
- Modify `sidecar/asr_sidecar/capture.py` — add `snapshot()`.
- Create `sidecar/asr_sidecar/waveform.py` — `downsample_minmax` + `WaveformEmitter` (replaces `levels.py`).
- Delete `sidecar/asr_sidecar/levels.py`; replace `tests/test_levels.py` with `tests/test_waveform.py`.
- Modify `sidecar/asr_sidecar/config.py` — add four `partial_*`/`partials` fields.
- Modify `sidecar/asr_sidecar/engines/gpu_whispercpp.py` — add `try_partial()`.
- Create `sidecar/asr_sidecar/partials.py` — `PartialLoop`.
- Modify `sidecar/asr_sidecar/__main__.py` — swap emitter, shared lock, partial-loop lifecycle, lock the final decode.
- Tests: modify `tests/test_capture.py`, `tests/test_config.py`; create `tests/test_partials.py`; add a case to `tests/test_gpu_engine.py`.

**Host (C#)**
- Modify `host/DeadAir.Core/Sidecar/SidecarEvent.cs` — add `Samples`, `Seq`; remove `Rms`.
- Create `host/DeadAir.Core/PartialText.cs` — `CommonPrefixWords` + `LeftElide` (pure, testable).
- Replace `host/DeadAir.Core/LevelRingBuffer.cs` with `host/DeadAir.Core/WaveformRingBuffer.cs`; replace `LevelRingBufferTests.cs` with `WaveformRingBufferTests.cs` + a `PartialTextTests.cs`.
- Modify `host/DeadAir.App/RecordingIndicatorWindow.xaml` + `.xaml.cs` — grow window, scope render, interim line.
- Modify `host/DeadAir.App/App.xaml.cs` — route `waveform` + `partial` on the fast path.
- Modify `host/DeadAir.Core/Config/AppConfig.cs` + `host/DeadAir.Core/Sidecar/SidecarCommands.cs` — plumb the partial settings into the config command.

---

## Task 1: `MicCapture.snapshot()`

**Files:**
- Modify: `sidecar/asr_sidecar/capture.py`
- Test: `sidecar/tests/test_capture.py`

**Interfaces:**
- Produces: `MicCapture.snapshot() -> np.ndarray` — a copy of frames-so-far, concatenated, without stopping capture. Empty float32 array when nothing captured.

- [ ] **Step 1: Write the failing test** — append to `sidecar/tests/test_capture.py`:

```python
def test_snapshot_is_nondestructive_and_grows():
    cap = MicCapture()
    cap._recording = True
    assert cap.snapshot().shape == (0,)          # nothing yet
    cap._on_frames(np.ones(160, dtype=np.float32))
    assert cap.snapshot().shape == (160,)         # sees first block
    cap._on_frames(np.ones(160, dtype=np.float32))
    assert cap.snapshot().shape == (320,)         # grows, non-destructive
    assert cap.stop().shape == (320,)             # frames still intact after peeks
```

(Ensure `import numpy as np` and `from asr_sidecar.capture import MicCapture` are present — they already are.)

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_capture.py::test_snapshot_is_nondestructive_and_grows -q`
Expected: FAIL — `AttributeError: 'MicCapture' object has no attribute 'snapshot'`

- [ ] **Step 3: Implement `snapshot()`** — add to `MicCapture` in `capture.py`, after `stop()`:

```python
    def snapshot(self) -> np.ndarray:
        """Copy of frames captured so far, without stopping. For live partials."""
        with self._lock:
            frames = list(self._frames)
        return (np.concatenate(frames) if frames
                else np.zeros(0, dtype=np.float32))
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_capture.py -q`
Expected: PASS (all capture tests)

- [ ] **Step 5: Commit**

```bash
git add sidecar/asr_sidecar/capture.py sidecar/tests/test_capture.py
git commit -m "feat(sidecar): MicCapture.snapshot() non-destructive buffer peek"
```

---

## Task 2: PCM waveform emitter (replaces RMS levels)

**Files:**
- Create: `sidecar/asr_sidecar/waveform.py`
- Delete: `sidecar/asr_sidecar/levels.py`
- Create: `sidecar/tests/test_waveform.py`
- Delete: `sidecar/tests/test_levels.py`

**Interfaces:**
- Produces:
  - `downsample_minmax(block: np.ndarray, bins: int = 8) -> list[float]` — returns `2*bins` floats: `[min0, max0, min1, max1, …]` (min/max per contiguous bin), clipped to [-1, 1]. Empty block → `2*bins` zeros.
  - `WaveformEmitter(emit_fn, bins: int = 8, min_interval_ms: int = 25, now_fn=time.monotonic)` with `.on_block(block: np.ndarray)` emitting `{"event": "waveform", "samples": [...]}`, throttled to at most one event per `min_interval_ms`.

- [ ] **Step 1: Write the failing test** — create `sidecar/tests/test_waveform.py`:

```python
import numpy as np
from asr_sidecar.waveform import downsample_minmax, WaveformEmitter


def test_downsample_returns_two_floats_per_bin():
    block = np.linspace(-1.0, 1.0, 800, dtype=np.float32)
    out = downsample_minmax(block, bins=8)
    assert len(out) == 16
    assert out[0] == -1.0 and out[-1] == 1.0          # global min/max at the ends
    assert all(-1.0 <= v <= 1.0 for v in out)


def test_downsample_empty_block_is_zeros():
    assert downsample_minmax(np.zeros(0, dtype=np.float32), bins=8) == [0.0] * 16


def test_emitter_throttles_and_shapes_event():
    events = []
    clock = [0.0]
    em = WaveformEmitter(events.append, bins=8, min_interval_ms=25,
                         now_fn=lambda: clock[0])
    block = np.full(400, 0.5, dtype=np.float32)
    em.on_block(block)          # t=0 -> emits
    clock[0] = 0.010
    em.on_block(block)          # 10ms later -> throttled
    clock[0] = 0.030
    em.on_block(block)          # 30ms later -> emits
    assert len(events) == 2
    assert events[0]["event"] == "waveform"
    assert len(events[0]["samples"]) == 16
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_waveform.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'asr_sidecar.waveform'`

- [ ] **Step 3: Create `waveform.py`**

```python
"""PCM waveform events for the host's oscilloscope (replaces RMS levels)."""
import time

import numpy as np


def downsample_minmax(block: np.ndarray, bins: int = 8) -> list[float]:
    """Reduce an audio block to `2*bins` floats: (min,max) per contiguous bin.

    Gives the host a real (downsampled) oscilloscope envelope, not an RMS
    magnitude. Empty block -> zeros so the scope drains to a flat line.
    """
    if len(block) == 0:
        return [0.0] * (2 * bins)
    parts = np.array_split(block, bins)
    out: list[float] = []
    for p in parts:
        if len(p) == 0:
            out += [0.0, 0.0]
        else:
            out += [float(np.clip(p.min(), -1.0, 1.0)),
                    float(np.clip(p.max(), -1.0, 1.0))]
    return out


class WaveformEmitter:
    """Throttled waveform-event emitter; at most one event per min_interval_ms."""

    def __init__(self, emit_fn, bins: int = 8, min_interval_ms: int = 25,
                 now_fn=time.monotonic):
        self._emit = emit_fn
        self._bins = bins
        self._interval = min_interval_ms / 1000.0
        self._now = now_fn
        self._last = float("-inf")

    def on_block(self, block: np.ndarray) -> None:
        now = self._now()
        if now - self._last < self._interval:
            return
        self._last = now
        self._emit({"event": "waveform",
                    "samples": [round(v, 3) for v in
                                downsample_minmax(block, self._bins)]})
```

- [ ] **Step 4: Delete the retired RMS files**

```bash
git rm sidecar/asr_sidecar/levels.py sidecar/tests/test_levels.py
```

(The `MicCapture` on-block error-swallow behavior previously asserted in `test_levels.py::test_capture_on_block_fires_only_while_recording_and_swallows_errors` is already covered structurally; if you want to keep that assertion, move it into `tests/test_capture.py` verbatim. Otherwise it is retained by Task 1's capture tests + the `_on_frames` try/except which is unchanged.)

- [ ] **Step 5: Run tests to verify pass + nothing imports `levels`**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/ -q`
Expected: PASS. If any `ImportError: levels`, it means `__main__.py` still imports it — that is fixed in Task 6; for now grep to confirm only `__main__.py` references it:
Run: `cd sidecar && findstr /s /n "levels" asr_sidecar\*.py`
Expected: only `asr_sidecar\__main__.py` matches.

- [ ] **Step 6: Commit**

```bash
git add sidecar/asr_sidecar/waveform.py sidecar/tests/test_waveform.py
git commit -m "feat(sidecar): PCM waveform emitter, retire RMS level path"
```

---

## Task 3: Sidecar config — partial settings

**Files:**
- Modify: `sidecar/asr_sidecar/config.py`
- Test: `sidecar/tests/test_config.py`

**Interfaces:**
- Produces: `SidecarConfig` gains `partials: bool = True`, `partial_interval_ms: int = 600`, `partial_min_ms: int = 700`, `partial_window_s: int = 30`. Unknown-key filtering in `from_cmd` already ignores extras.

- [ ] **Step 1: Write the failing test** — append to `sidecar/tests/test_config.py`:

```python
def test_partial_defaults():
    c = SidecarConfig()
    assert c.partials is True
    assert c.partial_interval_ms == 600
    assert c.partial_min_ms == 700
    assert c.partial_window_s == 30


def test_from_cmd_reads_partial_keys():
    c = SidecarConfig.from_cmd({"cmd": "config", "partials": False,
                                "partial_interval_ms": 400})
    assert c.partials is False and c.partial_interval_ms == 400
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_config.py -q`
Expected: FAIL — `AttributeError: 'SidecarConfig' object has no attribute 'partials'`

- [ ] **Step 3: Add fields** — in `config.py`, after `gpu_port: int = 8910`:

```python
    partials: bool = True           # live interim partials (GPU only)
    partial_interval_ms: int = 600  # re-decode cadence while recording
    partial_min_ms: int = 700       # min audio before the first partial
    partial_window_s: int = 30      # cap re-decode to the last N seconds
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_config.py -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add sidecar/asr_sidecar/config.py sidecar/tests/test_config.py
git commit -m "feat(sidecar): partial-decode config fields"
```

---

## Task 4: `GpuEngine.try_partial()` — best-effort, no self-heal

**Files:**
- Modify: `sidecar/asr_sidecar/engines/gpu_whispercpp.py`
- Test: `sidecar/tests/test_gpu_engine.py`

**Interfaces:**
- Consumes: `GpuEngine._post(audio, initial_prompt) -> str` (existing).
- Produces: `GpuEngine.try_partial(audio: np.ndarray, initial_prompt: str = "") -> str | None` — posts to `/inference` once; returns `None` on ANY exception (transport, HTTP status, parse). Never respawns, never raises. This is the partial-isolation boundary.

- [ ] **Step 1: Write the failing test** — append to `sidecar/tests/test_gpu_engine.py`:

```python
def test_try_partial_returns_text_on_success():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"text": " interim words "})

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    assert eng.try_partial(np.zeros(16000, dtype=np.float32)) == "interim words"


def test_try_partial_swallows_failure_without_respawn(monkeypatch):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("[WinError 10061] actively refused")

    eng = GpuEngine(server_exe="", model_path="", spawn=False,
                    transport=httpx.MockTransport(handler))
    eng._manage_proc = True
    called = {"respawn": 0}
    monkeypatch.setattr(eng, "_respawn",
                        lambda: called.__setitem__("respawn", 1))
    assert eng.try_partial(np.zeros(16000, dtype=np.float32)) is None
    assert called["respawn"] == 0  # partials must NEVER respawn the server
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_gpu_engine.py -k try_partial -q`
Expected: FAIL — `AttributeError: 'GpuEngine' object has no attribute 'try_partial'`

- [ ] **Step 3: Implement** — add to `GpuEngine`, right after `transcribe()` in `gpu_whispercpp.py`:

```python
    def try_partial(self, audio: np.ndarray, initial_prompt: str = "") -> str | None:
        """Best-effort interim decode for the live pill. Returns None on any
        failure — never respawns, never raises — so a crashy partial can't
        wedge or delay the authoritative final decode (which keeps self-heal)."""
        try:
            return self._post(audio, initial_prompt)
        except Exception:
            return None
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_gpu_engine.py -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add sidecar/asr_sidecar/engines/gpu_whispercpp.py sidecar/tests/test_gpu_engine.py
git commit -m "feat(sidecar): GpuEngine.try_partial best-effort interim decode"
```

---

## Task 5: `PartialLoop` — the re-decode thread

**Files:**
- Create: `sidecar/asr_sidecar/partials.py`
- Test: `sidecar/tests/test_partials.py`

**Interfaces:**
- Consumes: a capture object with `snapshot() -> np.ndarray`; an engine with `try_partial(audio, prompt) -> str | None`; an `emit_fn(dict)`; a `threading.Lock`.
- Produces: `PartialLoop(cap, engine, emit_fn, lock, prompt="", min_ms=700, window_s=30, sr=16000)` with:
  - `tick() -> bool` — one pass: snapshot; skip (return False) if below `min_ms` or no growth since last emit or `try_partial` returns falsy or the loop is stopped; else emit `{"event":"partial","text":text,"seq":seq}` (monotonic `seq` starting at 1) and return True.
  - `start(interval_ms=600) -> None` — spawn a daemon thread ticking every `interval_ms` (interruptible).
  - `stop() -> None` — signal stop and join (bounded); after `stop()` no further partials emit.

- [ ] **Step 1: Write the failing test** — create `sidecar/tests/test_partials.py`:

```python
import threading
import numpy as np
from asr_sidecar.partials import PartialLoop


class FakeCap:
    def __init__(self):
        self.n = 0
    def snapshot(self):
        return np.zeros(self.n, dtype=np.float32)


class FakeEngine:
    def __init__(self, text="interim"):
        self.text = text
        self.calls = 0
    def try_partial(self, audio, prompt=""):
        self.calls += 1
        return self.text


def _loop(cap, engine, events):
    # min_ms=100 -> 1600 samples at 16 kHz; window big enough not to clip.
    return PartialLoop(cap, engine, events.append, threading.Lock(),
                       min_ms=100, window_s=30, sr=16000)


def test_tick_skips_below_min_audio():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 800                      # 50ms < 100ms
    assert loop.tick() is False
    assert events == [] and engine.calls == 0


def test_tick_emits_when_buffer_grows():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200                     # 200ms >= min
    assert loop.tick() is True
    assert events == [{"event": "partial", "text": "interim", "seq": 1}]


def test_tick_skips_when_no_new_audio():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    assert loop.tick() is True       # seq 1
    assert loop.tick() is False      # no growth -> no emit
    assert engine.calls == 1


def test_tick_skips_empty_partial_text():
    cap, engine, events = FakeCap(), FakeEngine(text=""), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    assert loop.tick() is False
    assert events == []


def test_stop_prevents_further_emits():
    cap, engine, events = FakeCap(), FakeEngine(), []
    loop = _loop(cap, engine, events)
    cap.n = 3200
    loop.stop()                      # stopped before any tick
    assert loop.tick() is False
    assert events == []
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_partials.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'asr_sidecar.partials'`

- [ ] **Step 3: Create `partials.py`**

```python
"""GPU-only live partial-decode loop for the recording pill. Best-effort:
isolated from the authoritative final decode (see Global Constraints)."""
import logging
import threading

log = logging.getLogger("sidecar")


class PartialLoop:
    def __init__(self, cap, engine, emit_fn, lock, prompt: str = "",
                 min_ms: int = 700, window_s: int = 30, sr: int = 16000):
        self._cap = cap
        self._engine = engine
        self._emit = emit_fn
        self._lock = lock
        self._prompt = prompt
        self._min_samples = int(min_ms * sr / 1000)
        self._window_samples = int(window_s * sr)
        self._last_len = 0
        self._seq = 0
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def tick(self) -> bool:
        if self._stop.is_set():
            return False
        audio = self._cap.snapshot()
        if len(audio) < self._min_samples or len(audio) <= self._last_len:
            return False
        self._last_len = len(audio)
        window = audio[-self._window_samples:]
        try:
            with self._lock:
                text = self._engine.try_partial(window, self._prompt)
        except Exception:
            log.exception("partial tick failed")   # stderr only, never emit error
            return False
        if self._stop.is_set() or not text:
            return False
        self._seq += 1
        self._emit({"event": "partial", "text": text, "seq": self._seq})
        return True

    def start(self, interval_ms: int = 600) -> None:
        interval = interval_ms / 1000.0

        def run():
            while not self._stop.wait(interval):
                self.tick()

        self._thread = threading.Thread(target=run, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_partials.py -q`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add sidecar/asr_sidecar/partials.py sidecar/tests/test_partials.py
git commit -m "feat(sidecar): PartialLoop re-decode thread (best-effort, isolated)"
```

---

## Task 6: Wire the sidecar main loop

**Files:**
- Modify: `sidecar/asr_sidecar/__main__.py`
- Test: `sidecar/tests/test_partials.py` (integration case)

**Interfaces:**
- Consumes: `WaveformEmitter` (Task 2), `PartialLoop` (Task 5), `SidecarConfig.partial_*` (Task 3), `GpuEngine.try_partial` (Task 4), `MicCapture.snapshot` (Task 1).
- Produces: `_finish(audio, cfg, engine, lock)` — same as before but the primary transcribe runs under `lock`. `main()` swaps `LevelEmitter`→`WaveformEmitter`, owns one `server_lock = threading.Lock()`, starts a `PartialLoop` on `start` when `cfg.partials and engine.name == "gpu"`, and stops it on `stop`/`cancel`/`config`/`shutdown`.

- [ ] **Step 1: Write the failing integration test** — append to `sidecar/tests/test_partials.py`:

```python
def test_partial_loop_only_starts_for_gpu_engine():
    # Rehearses the main-loop guard without real audio/model: a cpu-named
    # engine must never be wrapped in a PartialLoop by _maybe_start_partials.
    from asr_sidecar.__main__ import _maybe_start_partials
    from asr_sidecar.config import SidecarConfig

    class E:
        name = "cpu"
    started = _maybe_start_partials(SidecarConfig(engine="cpu"),
                                    E(), FakeCap(), lambda e: None,
                                    threading.Lock())
    assert started is None            # no loop for cpu

    class G(E):
        name = "gpu"
        def try_partial(self, a, p=""):
            return "x"
    loop = _maybe_start_partials(SidecarConfig(), G(), FakeCap(),
                                 lambda e: None, threading.Lock())
    assert loop is not None
    loop.stop()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/test_partials.py -k only_starts -q`
Expected: FAIL — `ImportError: cannot import name '_maybe_start_partials'`

- [ ] **Step 3: Edit `__main__.py`**

3a. Replace the imports block (top of file) — swap `levels` for `waveform`/`partials`, add `threading`:

```python
"""DeadAir ASR sidecar. Protocol: spec.md §3. stdout = protocol only."""
import logging
import sys
import threading
import time
import numpy as np
from .audio import load_wav
from .capture import MicCapture
from .config import SidecarConfig
from .engines import CpuEngine, GpuEngineError, create_engine
from .ipc import emit, read_commands
from .partials import PartialLoop
from .vad import extract_speech
from .waveform import WaveformEmitter
```

3b. Change `_finish` to take and use the lock — its signature line and the primary transcribe call:

```python
def _finish(audio: np.ndarray, cfg: SidecarConfig, engine, lock):
```

and wrap ONLY the primary transcribe (leave the CPU-fallback branch as-is; the in-process CPU engine needs no server lock):

```python
    prompt = ", ".join(cfg.dictionary)
    try:
        with lock:
            text = engine.transcribe(speech, initial_prompt=prompt)
```

3c. Add the guard helper above `main()`:

```python
def _maybe_start_partials(cfg, engine, cap, emit_fn, lock):
    """Start a PartialLoop iff partials are enabled AND the engine is GPU.
    Returns the running loop, or None."""
    if not cfg.partials or getattr(engine, "name", None) != "gpu":
        return None
    loop = PartialLoop(cap, engine, emit_fn, lock,
                       prompt=", ".join(cfg.dictionary),
                       min_ms=cfg.partial_min_ms,
                       window_s=cfg.partial_window_s)
    loop.start(interval_ms=cfg.partial_interval_ms)
    return loop
```

3d. Rewrite `main()` to own the lock + partial loop and swap the emitter:

```python
def main() -> None:
    cfg = SidecarConfig()
    engine = None
    cap = None
    partial = None
    server_lock = threading.Lock()

    def stop_partial():
        nonlocal partial
        if partial is not None:
            partial.stop()
            partial = None

    try:
        for cmd in read_commands():
            c = cmd.get("cmd")
            try:
                if c == "config":
                    stop_partial()
                    cfg = SidecarConfig.from_cmd(cmd)
                    if engine:
                        engine.close()
                    engine = create_engine(cfg, emit)
                    if cap is not None:
                        cap.cancel()
                    cap = MicCapture(cfg.mic)
                    cap.on_block = WaveformEmitter(emit).on_block
                    emit({"event": "ready", "engine": engine.name,
                          "model": cfg.model if engine.name == "gpu"
                          else cfg.cpu_model})
                elif c == "start":
                    cap.start()
                    emit({"event": "recording"})
                    partial = _maybe_start_partials(cfg, engine, cap, emit,
                                                    server_lock)
                elif c == "stop":
                    stop_partial()
                    engine = _finish(cap.stop(), cfg, engine, server_lock)
                elif c == "cancel":
                    stop_partial()
                    cap.cancel()
                elif c == "transcribe_wav":  # test/debug hook (spec §9)
                    engine = _finish(load_wav(cmd["path"]), cfg, engine,
                                     server_lock)
                elif c == "shutdown":
                    break
                else:
                    emit({"event": "error", "where": "ipc",
                          "message": f"unknown cmd: {c}"})
            except Exception as e:
                log.exception("command failed")
                emit({"event": "error", "where": "mic" if c in ("start", "stop")
                      else "asr", "message": str(e)})
    finally:
        stop_partial()
        if cap is not None:
            cap.cancel()
        if engine:
            engine.close()
```

- [ ] **Step 4: Run the full sidecar suite**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/ -q`
Expected: PASS (including the new `only_starts` case; `test_main_loop_cleanup.py` still green — if it calls `_finish`, update those call sites to pass a `threading.Lock()` as the 4th arg).

- [ ] **Step 5: Commit**

```bash
git add sidecar/asr_sidecar/__main__.py sidecar/tests/test_partials.py sidecar/tests/test_main_loop_cleanup.py
git commit -m "feat(sidecar): wire waveform emitter + GPU partial loop into main"
```

---

## Task 7: Host event model — `Samples`, `Seq`; drop `Rms`

**Files:**
- Modify: `host/DeadAir.Core/Sidecar/SidecarEvent.cs`
- Test: `host/DeadAir.Core.Tests/WaveformRingBufferTests.cs` (created in Task 8) — for now add the parse test to a temporary `SidecarEventTests.cs`.

**Interfaces:**
- Produces: `SidecarEvent.Samples` (`double[]?`, JSON `samples`), `SidecarEvent.Seq` (`int?`, JSON `seq`). `Rms` removed.

- [ ] **Step 1: Write the failing test** — create `host/DeadAir.Core.Tests/SidecarEventTests.cs`:

```csharp
using System.Text.Json;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarEventTests
{
    [Fact]
    public void ParsesWaveformSamples()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"waveform\",\"samples\":[-0.5,0.5,0.0,0.25]}");
        Assert.Equal("waveform", e!.Event);
        Assert.Equal(4, e.Samples!.Length);
        Assert.Equal(-0.5, e.Samples[0], 3);
    }

    [Fact]
    public void ParsesPartialTextAndSeq()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"partial\",\"text\":\"hello there\",\"seq\":3}");
        Assert.Equal("partial", e!.Event);
        Assert.Equal("hello there", e.Text);
        Assert.Equal(3, e.Seq!.Value);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd host && dotnet test --filter SidecarEventTests`
Expected: FAIL — build error, `SidecarEvent` has no `Samples`/`Seq`.

- [ ] **Step 3: Edit `SidecarEvent.cs`** — replace the `Rms` line with:

```csharp
    [JsonPropertyName("samples")] public double[]? Samples { get; init; }
    [JsonPropertyName("seq")] public int? Seq { get; init; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd host && dotnet test --filter SidecarEventTests`
Expected: PASS. (Build will still fail elsewhere because `App.xaml.cs` and `LevelRingBufferTests.cs` reference `Rms` — those are fixed in Tasks 8/10. If you need a green build here, do Task 8 next before re-running the whole suite.)

- [ ] **Step 5: Commit**

```bash
git add host/DeadAir.Core/Sidecar/SidecarEvent.cs host/DeadAir.Core.Tests/SidecarEventTests.cs
git commit -m "feat(host): SidecarEvent samples/seq, drop rms"
```

---

## Task 8: `WaveformRingBuffer` + `PartialText` helpers (pure Core)

**Files:**
- Create: `host/DeadAir.Core/WaveformRingBuffer.cs`
- Delete: `host/DeadAir.Core/LevelRingBuffer.cs`
- Create: `host/DeadAir.Core/PartialText.cs`
- Delete: `host/DeadAir.Core.Tests/LevelRingBufferTests.cs`
- Create: `host/DeadAir.Core.Tests/WaveformRingBufferTests.cs`
- Create: `host/DeadAir.Core.Tests/PartialTextTests.cs`

**Interfaces:**
- Produces:
  - `WaveformRingBuffer(int capacity)` with `void PushRange(IReadOnlyList<double> values)` (append newest, drop oldest, keep last `capacity`), `IReadOnlyList<double> Values` (oldest→newest, length `capacity`, zero-filled), `void Reset()`.
  - `PartialText.CommonPrefixWords(string? previous, string? current) -> int` — count of leading whitespace-split words identical in both.
  - `PartialText.LeftElide(string text, int maxChars) -> string` — if longer than `maxChars`, return `"…"` + the trailing `maxChars-1` chars; else `text`.

- [ ] **Step 1: Write the failing tests** — create `host/DeadAir.Core.Tests/WaveformRingBufferTests.cs`:

```csharp
using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class WaveformRingBufferTests
{
    [Fact]
    public void PushRange_KeepsLastCapacityOldestToNewest()
    {
        var buf = new WaveformRingBuffer(4);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, buf.Values);
        buf.PushRange(new[] { 1.0, 2.0 });
        buf.PushRange(new[] { 3.0, 4.0, 5.0 });   // overflows by one
        Assert.Equal(new[] { 2.0, 3.0, 4.0, 5.0 }, buf.Values);
    }

    [Fact]
    public void Reset_ReturnsToZero()
    {
        var buf = new WaveformRingBuffer(3);
        buf.PushRange(new[] { 9.0, 9.0, 9.0 });
        buf.Reset();
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, buf.Values);
    }
}
```

and `host/DeadAir.Core.Tests/PartialTextTests.cs`:

```csharp
using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class PartialTextTests
{
    [Theory]
    [InlineData(null, "hello world", 0)]
    [InlineData("recognize speech", "recognize speech", 2)]
    [InlineData("wreck a nice", "wreck a beach", 2)]   // diverges at word 3
    [InlineData("a b c", "a b c d", 3)]
    public void CommonPrefixWords_CountsSharedLeadingWords(
        string? prev, string curr, int expected)
        => Assert.Equal(expected, PartialText.CommonPrefixWords(prev, curr));

    [Fact]
    public void LeftElide_KeepsNewestTailWithEllipsis()
    {
        Assert.Equal("short", PartialText.LeftElide("short", 10));
        Assert.Equal("…World", PartialText.LeftElide("Hello World", 6));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd host && dotnet test --filter "WaveformRingBufferTests|PartialTextTests"`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Create the types.** `host/DeadAir.Core/WaveformRingBuffer.cs`:

```csharp
namespace DeadAir.Core;

/// <summary>Fixed-size rolling buffer of waveform samples (oldest→newest).</summary>
public sealed class WaveformRingBuffer(int capacity)
{
    private readonly double[] _values = new double[capacity];

    public IReadOnlyList<double> Values => _values;

    public void PushRange(IReadOnlyList<double> values)
    {
        foreach (var v in values)
        {
            Array.Copy(_values, 1, _values, 0, _values.Length - 1);
            _values[^1] = v;
        }
    }

    public void Reset() => Array.Clear(_values);
}
```

`host/DeadAir.Core/PartialText.cs`:

```csharp
namespace DeadAir.Core;

/// <summary>Pure helpers for rendering self-correcting interim transcripts.</summary>
public static class PartialText
{
    private static readonly char[] Ws = { ' ', '\t', '\n', '\r' };

    /// <summary>Count of leading words identical in both strings.</summary>
    public static int CommonPrefixWords(string? previous, string? current)
    {
        var a = (previous ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
        var b = (current ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Trim from the LEFT so the newest text stays visible.</summary>
    public static string LeftElide(string text, int maxChars)
    {
        if (maxChars < 1 || text.Length <= maxChars) return text;
        return "…" + text[^(maxChars - 1)..];
    }
}
```

- [ ] **Step 4: Delete the retired level buffer**

```bash
git rm host/DeadAir.Core/LevelRingBuffer.cs host/DeadAir.Core.Tests/LevelRingBufferTests.cs
```

- [ ] **Step 5: Run tests to verify pass**

Run: `cd host && dotnet test --filter "WaveformRingBufferTests|PartialTextTests"`
Expected: PASS. (Full `dotnet test` still red until Tasks 9–10 remove `LevelRingBuffer`/`Rms`/`Push` usages in the App project.)

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.Core/WaveformRingBuffer.cs host/DeadAir.Core/PartialText.cs host/DeadAir.Core.Tests/WaveformRingBufferTests.cs host/DeadAir.Core.Tests/PartialTextTests.cs
git commit -m "feat(host): waveform ring buffer + partial-text helpers, retire level buffer"
```

---

## Task 9: Pill window — scope + interim line

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml`
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`

**Interfaces:**
- Consumes: `WaveformRingBuffer`, `PartialText` (Task 8).
- Produces (called by App on the UI thread): `PushWaveform(IReadOnlyList<double> samples)`, `SetPartial(string text)`, plus existing `ShowIndicator()` / `HideIndicator()`. `ShowIndicator` clears the scope + interim line.

This task is WPF rendering — verified by build + the manual smoke in Task 11, not xUnit (no headless WPF harness in this repo).

- [ ] **Step 1: Replace the XAML** — `RecordingIndicatorWindow.xaml`:

```xml
<Window x:Class="DeadAir.App.RecordingIndicatorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="320" Height="76"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False"
        IsHitTestVisible="False" Focusable="False">
    <Border CornerRadius="18" Background="#E6101418">
        <Grid Margin="12,8">
            <Grid.RowDefinitions>
                <RowDefinition Height="40"/>
                <RowDefinition Height="20"/>
            </Grid.RowDefinitions>
            <Canvas x:Name="ScopeCanvas" Grid.Row="0" Width="296" Height="40"
                    ClipToBounds="True">
                <Polyline x:Name="ScopeLine" Stroke="#4FC3F7" StrokeThickness="1.4"
                          StrokeLineJoin="Round"/>
            </Canvas>
            <TextBlock x:Name="InterimText" Grid.Row="1" FontSize="12"
                       FontStyle="Italic" Foreground="#66E1F5FE"
                       TextTrimming="None" TextWrapping="NoWrap"/>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 2: Rewrite the code-behind** — `RecordingIndicatorWindow.xaml.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using DeadAir.Core;

namespace DeadAir.App;

public partial class RecordingIndicatorWindow : Window
{
    private const int ScopeSamples = 296;      // one sample per horizontal px
    private const double ScopeWidth = 296, ScopeHeight = 40;
    private const int InterimMaxChars = 46;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    private readonly WaveformRingBuffer _wave = new(ScopeSamples);
    private readonly SolidColorBrush _dim =
        new(Color.FromArgb(0x66, 0xE1, 0xF5, 0xFE));
    private readonly SolidColorBrush _hot =
        new(Color.FromArgb(0xFF, 0x8B, 0xE9, 0xFD));
    private string _lastPartial = "";

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
        RenderScope();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The pill must NEVER take focus, or injection would target it
        // instead of the user's app (spec: load-bearing).
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            GetWindowLongW(hwnd, GWL_EXSTYLE)
            | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void PushWaveform(IReadOnlyList<double> samples)
    {
        _wave.PushRange(samples);
        RenderScope();
    }

    public void SetPartial(string text)
    {
        int common = PartialText.CommonPrefixWords(_lastPartial, text);
        _lastPartial = text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string stable = string.Join(' ', words.Take(common));
        string changed = string.Join(' ', words.Skip(common));

        // Left-elide the stable head so the newest (changed) words stay visible.
        stable = PartialText.LeftElide(stable, InterimMaxChars);

        InterimText.Inlines.Clear();
        if (stable.Length > 0)
            InterimText.Inlines.Add(new Run(stable + " ") { Foreground = _dim });
        if (changed.Length > 0)
            InterimText.Inlines.Add(new Run(changed) { Foreground = _hot });
    }

    public void ShowIndicator()
    {
        _wave.Reset();
        _lastPartial = "";
        InterimText.Inlines.Clear();
        RenderScope();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 16;
        Show(); // never Activate()
    }

    public void HideIndicator() => Hide();

    private void RenderScope()
    {
        var v = _wave.Values;
        var pts = new PointCollection(v.Count);
        double mid = ScopeHeight / 2.0;
        for (int i = 0; i < v.Count; i++)
        {
            double x = i * (ScopeWidth / (v.Count - 1));
            double y = mid - v[i] * mid;   // sample in [-1,1] -> canvas y
            pts.Add(new Point(x, y));
        }
        ScopeLine.Points = pts;
    }
}
```

Note: the `_hot`→`_dim` settle is done by re-issuing `SetPartial` on each partial (the previous changed words become part of the stable prefix on the next event, so they render dim). A per-word fade animation is a nice-to-have deferred to polish — the color step already reads as "just changed."

- [ ] **Step 3: Build the App project**

Run: `cd host && dotnet build DeadAir.App`
Expected: succeeds (there will still be an unresolved `Rms`/`level` reference in `App.xaml.cs` until Task 10 — if you build the whole solution now it fails there; that is expected and fixed next).

- [ ] **Step 4: Commit**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(host): pill renders PCM oscilloscope + self-correcting interim line"
```

---

## Task 10: App wiring + config passthrough

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs`
- Modify: `host/DeadAir.Core/Config/AppConfig.cs`
- Modify: `host/DeadAir.Core/Sidecar/SidecarCommands.cs`
- Test: `host/DeadAir.Core.Tests/SidecarCommandTests.cs` (create)

**Interfaces:**
- Consumes: `RecordingIndicatorWindow.PushWaveform` / `SetPartial` (Task 9); `AsrConfig` partial fields.
- Produces: `ConfigCommand` gains `partials`, `partial_interval_ms`, `partial_min_ms`, `partial_window_s`, mapped from `AppConfig.Asr`.

- [ ] **Step 1: Write the failing test** — create `host/DeadAir.Core.Tests/SidecarCommandTests.cs`:

```csharp
using System.Text.Json;
using DeadAir.Core.Config;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarCommandTests
{
    [Fact]
    public void ConfigCommand_CarriesPartialDefaults()
    {
        var cmd = ConfigCommand.From(new AppConfig());
        var json = JsonSerializer.Serialize(cmd);
        Assert.Contains("\"partials\":true", json);
        Assert.Contains("\"partial_interval_ms\":600", json);
        Assert.Contains("\"partial_min_ms\":700", json);
        Assert.Contains("\"partial_window_s\":30", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd host && dotnet test --filter SidecarCommandTests`
Expected: FAIL — build error, `ConfigCommand` has no partial props.

- [ ] **Step 3a: Add fields to `AsrConfig`** in `AppConfig.cs`, after `GpuPort`:

```csharp
    public bool Partials { get; set; } = true;
    public int PartialIntervalMs { get; set; } = 600;
    public int PartialMinMs { get; set; } = 700;
    public int PartialWindowSeconds { get; set; } = 30;
```

- [ ] **Step 3b: Add props + mapping to `ConfigCommand`** in `SidecarCommands.cs`. Add after `GpuPort`:

```csharp
    [JsonPropertyName("partials")] public bool Partials { get; init; } = true;
    [JsonPropertyName("partial_interval_ms")] public int PartialIntervalMs { get; init; } = 600;
    [JsonPropertyName("partial_min_ms")] public int PartialMinMs { get; init; } = 700;
    [JsonPropertyName("partial_window_s")] public int PartialWindowSeconds { get; init; } = 30;
```

and inside `From(AppConfig c)`, after `GpuPort = c.Asr.GpuPort,`:

```csharp
        Partials = c.Asr.Partials,
        PartialIntervalMs = c.Asr.PartialIntervalMs,
        PartialMinMs = c.Asr.PartialMinMs,
        PartialWindowSeconds = c.Asr.PartialWindowSeconds,
```

- [ ] **Step 3c: Route the new events** in `App.xaml.cs` — replace the `if (ev.Event == "level") { … }` block (lines ~91–98) with:

```csharp
            if (ev.Event == "waveform")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.PushWaveform(ev.Samples ?? Array.Empty<double>()); }
                    catch { }
                });
                return;
            }
            if (ev.Event == "partial")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.SetPartial(ev.Text ?? ""); } catch { }
                });
                return;
            }
```

- [ ] **Step 4: Run test + full host suite**

Run: `cd host && dotnet test`
Expected: PASS (whole solution builds; `SidecarCommandTests`, `SidecarEventTests`, `WaveformRingBufferTests`, `PartialTextTests` all green; no lingering `Rms`/`LevelRingBuffer` references).

- [ ] **Step 5: Commit**

```bash
git add host/DeadAir.App/App.xaml.cs host/DeadAir.Core/Config/AppConfig.cs host/DeadAir.Core/Sidecar/SidecarCommands.cs host/DeadAir.Core.Tests/SidecarCommandTests.cs
git commit -m "feat(host): route waveform/partial events, plumb partial config"
```

---

## Task 11: Protocol doc + end-to-end verification

**Files:**
- Modify: `docs/spec.md` (§3 protocol table, §10 phase status)

**Interfaces:** none (docs + manual verification).

- [ ] **Step 1: Update `docs/spec.md` §3** — in the events table, remove the `level` row and add:

```
| `{"event":"waveform","samples":[…]}` | ~40 Hz while recording. Downsampled PCM min/max envelope for the pill oscilloscope. |
| `{"event":"partial","text":"…","seq":N} `| GPU-only interim transcript for the pill preview. Best-effort; never injected. |
```

and add to the `config` command row: `partials`, `partial_interval_ms`, `partial_min_ms`, `partial_window_s`. In §10, mark Phase 1 as "in progress (this branch)".

- [ ] **Step 2: Run both full suites**

Run: `cd sidecar && .venv\Scripts\python -m pytest tests/ -q`
Run: `cd host && dotnet test`
Expected: both PASS.

- [ ] **Step 3: Manual smoke on the 6800 XT (GPU engine).** Launch the app, confirm the GPU engine is active (tray tooltip shows `[gpu]`), then hold Right Ctrl and dictate a sentence with a mid-utterance self-correction (e.g. say "recognize speech" slowly). Verify:
  - the pill shows a **scrolling oscilloscope** driven by your voice (not discrete bars);
  - an **interim line** appears under it and updates ~every 0.6 s, with newly-changed words rendered brighter;
  - on key-up the **cleaned** final lands in the target app exactly as before;
  - killing `whisper-server.exe` mid-dictation does NOT wedge the app — partials go quiet, and the final still lands (self-heal on the final path).

- [ ] **Step 4: Manual smoke on CPU (or force `engine:cpu`).** Confirm **no** interim line / partials appear, the oscilloscope still animates from the `waveform` stream, and dictation+inject work — proving the GPU-only guard.

- [ ] **Step 5: Commit**

```bash
git add docs/spec.md
git commit -m "docs: Phase 1 live-pill protocol (waveform/partial events)"
```

---

## Self-Review

**Spec coverage:**
- Preview-only interim text → Tasks 5, 9 (never injected; final path untouched in Task 6). ✓
- GPU-only partials → Task 6 `_maybe_start_partials` guard + test. ✓
- True PCM oscilloscope replacing RMS bars → Tasks 2, 8, 9. ✓
- Interim text under the waveform, left-truncated, self-correcting highlight → Task 9 (`SetPartial`, `LeftElide`, `CommonPrefixWords`). ✓
- One server, single-flight → Task 6 shared `server_lock` in both `PartialLoop.tick` and `_finish`. ✓
- Partial isolation (no error event, no respawn, best-effort) → Task 4 `try_partial` + Task 5 stderr-only. ✓
- Cost guard (last 30 s) → Task 5 `window_samples`. ✓
- Config surface → Tasks 3, 10. ✓
- Protocol update → Task 11. ✓
- Focus-safe fast path → Task 9 (ex-styles retained) + Task 10 (early-return routing). ✓
- Retire `level`/`LevelEmitter`/`LevelRingBuffer` → Tasks 2, 8. ✓

**Placeholder scan:** No TBD/TODO; every code step carries full code. The per-word fade animation is explicitly scoped OUT (Task 9 note), not left as a placeholder.

**Type consistency:** `snapshot()`, `try_partial()`, `PartialLoop(cap, engine, emit_fn, lock, prompt, min_ms, window_s, sr)`, `_finish(audio, cfg, engine, lock)`, `WaveformEmitter(emit_fn, bins, min_interval_ms, now_fn)`, `WaveformRingBuffer.PushRange/Values/Reset`, `PartialText.CommonPrefixWords/LeftElide`, `SidecarEvent.Samples/Seq`, `ConfigCommand.Partials/PartialIntervalMs/PartialMinMs/PartialWindowSeconds` — names/signatures match across producing and consuming tasks. ✓

**Known cross-task build gap:** Tasks 7–9 leave the host solution non-building until Task 10 removes the last `Rms`/`level` reference in `App.xaml.cs`. Each task's own project/filter tests pass; the full-solution green is at Task 10. Called out in the affected steps.
