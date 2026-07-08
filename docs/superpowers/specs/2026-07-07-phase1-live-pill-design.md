# DeadAir Phase 1 — Live Pill (streaming partials + PCM oscilloscope)

**Date:** 2026-07-07
**Status:** Design approved — ready for implementation plan
**Spec base:** `docs/spec.md` §10 (Phase 1 — Streaming/partials)

## Summary

Make the recording pill *live*. While the user holds the hotkey and speaks, the
pill shows two real-time streams: a **PCM oscilloscope** of the incoming audio,
and a **self-correcting interim transcript** produced by re-decoding the audio
every ~600 ms. Nothing changes about what actually gets typed — the authoritative
transcript is still produced on key-up, cleaned by Ollama, and injected exactly
as today. The live streams are **preview only**.

Three cohesive pieces, all on the one pill window:

1. **Streaming partials** — periodic GPU re-decode → interim text under the
   waveform, with changed words flashing as whisper firms up its hypothesis.
2. **True PCM oscilloscope** — replaces the RMS level bars with a scrolling
   waveform drawn from real (downsampled) audio samples.
3. **Pill rework** — grow the window, render scope + interim line, wire the new
   events on the existing focus-safe fast path.

## Scope decisions (from brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Where interim text shows | **Preview only** — pill, never injected | Lowest risk; the injected result stays the Ollama-cleaned final. |
| Engine scope | **GPU-only partials** | Re-decoding a growing clip is cheap on the 6800 XT (Vulkan whisper-server); CPU `small` on the Surface can't keep real-time. CPU path unchanged. |
| Pill layout | Interim text **under** the waveform | Keeps the soundwave identity; text is a secondary dim cue. |
| Waveform type | **True PCM oscilloscope** | Real sample amplitudes, not RMS envelope. Needs the sidecar to stream downsampled samples. |
| "Live editing" | **Partials self-correcting** | As whisper re-decodes, earlier words revise; changed words flash. (Cleanup-diff animation explicitly out of scope.) |
| Partial-decode strategy | **A — sidecar re-decode thread, one shared server** | All ASR stays in the sidecar; host stays a dumb renderer; reuse the single whisper-server, serialized. (B: 2nd server — overkill. C: native streaming ASR — too big; revisit as Phase 1.5 if redundant compute bites.) |

## Architecture & data flow

Two independent live streams run while recording, plus the unchanged
authoritative path on key-up:

```
                    ┌─ waveform events (~40 Hz) ──────────▶ pill: scrolling oscilloscope
mic block (25 Hz) ──┤
                    └─ (buffer accumulates in MicCapture)

partial loop (GPU only, ~600 ms tick):
   snapshot buffer → POST whisper-server /inference → partial{text,seq} ──▶ pill: interim line
                                                                            (diff vs prev → flash changed words)

KEY-UP (unchanged authoritative path):
   stop partial loop → abandon in-flight partial → _finish(): VAD-trim → transcribe → final
        → host: Ollama clean (stream) → inject → pill hides
```

### Invariants

- **One whisper-server, single-flight.** Partial POSTs and the final POST
  serialize on a shared `threading.Lock`. A partial in flight at key-up means the
  final waits for it to return (bounded, ~one tick), discards it, then decodes the
  authoritative clip. No second server, no second model in VRAM.
- **Partials are best-effort and isolated.** Any partial error → stderr log only.
  Never emits an `error` event; never triggers the GPU self-heal `_respawn`. If the
  server actually died mid-partial, the *final* POST hits the normal self-heal path
  and recovers — partials just go quiet.
- **Preview-only preserved.** Partial text is never injected. Only `final` →
  Ollama clean → inject, exactly as today.
- **GPU-only.** The partial loop activates only when `engine.name == "gpu"`. The
  CPU (Surface) path is byte-for-byte unchanged: no partials, final only.
- **Waveform supersedes RMS bars.** `level` / `LevelEmitter` / `LevelRingBuffer` /
  bar-render are replaced by `waveform` / `WaveformEmitter` / sample-ring /
  scope-render.
- **Cost guard.** Partial re-decode is capped to the last `partial_window_s`
  (~30 s) of audio so a long dictation can't grow the per-tick decode unbounded.

## Component design

### Sidecar — partial loop (new `asr_sidecar/partials.py`)

- **`MicCapture.snapshot() -> np.ndarray`** — new non-destructive peek: returns a
  copy of frames-so-far under the lock, without stopping capture. (Today `stop()`
  is the only way to read frames.)
- **`PartialLoop` thread** — started on the `start` command when
  `cfg.partials and engine.name == "gpu"`. Each tick (`partial_interval_ms`,
  default 600 ms):
  1. `snapshot()`; skip unless the buffer grew ≥ a min delta **and** total ≥
     `partial_min_ms` (~700 ms).
  2. Acquire the shared server lock; POST the last ≤ `partial_window_s` via
     `GpuEngine.try_partial(audio, prompt)`; release.
  3. Emit `{event:"partial", text, seq}` (monotonic `seq`).
  - The whole tick is wrapped in `try/except` → stderr only.
- **Stop/cancel** sets a stop flag and joins the thread (bounded timeout). Because
  `_finish()` acquires the same lock, it naturally waits for any in-flight partial,
  discards it, then runs the authoritative decode untouched.
- **`GpuEngine.try_partial(audio, prompt) -> str | None`** — best-effort sibling of
  `transcribe`: posts to `/inference` with **no respawn/self-heal**, returns `None`
  on any `TransportError`/failure. This is the isolation boundary — the real
  self-heal stays on the `final` path only.

### Sidecar — PCM waveform (replaces RMS)

- **`WaveformEmitter`** swaps in for `LevelEmitter` on `cap.on_block`. Each audio
  block → downsample to N **min/max envelope pairs** (default 8 bins → 16 floats),
  emit `{event:"waveform", samples:[…]}`, throttled to ~40 Hz.
- The RMS path (`levels.py` `rms_to_level`, the `level` event, `LevelEmitter`) is
  retired. `levels.py` becomes `waveform.py` (or is repurposed).

### Sidecar — config additions

`partials` (bool, default `true`), `partial_interval_ms` (int, default 600),
`partial_window_s` (int, default 30), `partial_min_ms` (int, default 700).

### Host — pill

- **`SidecarEvent`**: add `Partial(text, seq)` and `Waveform(float[] samples)`;
  remove `Rms`. Both routed on the existing **fast path** first in
  `EventReceived` with an early `return` — no orchestrator/log traffic at 40 Hz.
- **`RecordingIndicatorWindow`** grows (~280×48 → ~320×76): scope canvas on top,
  interim text line below. Never-activate constraints preserved verbatim
  (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`,
  `IsHitTestVisible=false`, `Focusable=false`, never `Activate()`).
- **Oscilloscope**: `waveform` samples → sample ring buffer → scrolling filled
  path, right-to-left. Replaces the 24-bar renderer and `LevelRingBuffer`.
- **Interim line**: dim italic, **left-truncated** so the newest words stay
  visible (needs a small custom measure — WPF `TextTrimming` trims the *end* by
  default).
- **Self-correcting highlight**: keep the previous partial string; on each new
  partial, compute the longest-common-prefix **word diff**; the divergent suffix
  words flash briefly (bright → fade ~250 ms) then settle to dim italic. Flash is
  scoped to after the common prefix so the whole line doesn't strobe on each
  re-decode.
- **Lifecycle**: show on `recording`, update on `waveform` + `partial`, clear +
  hide on `final` / `empty`. No extended cleanup phase (declined).
- **Settings**: surface the `partials` toggle + interval (at minimum in
  `config.json`; optionally in the settings window).

### Protocol (spec §3)

- `config` command gains the four `partial_*` keys.
- New events: `partial{text, seq}`, `waveform{samples:[…]}`. `level` removed.
- Update the §3 protocol table and mark §10 Phase 1 in-progress.

## Testing plan

**Sidecar**
- `snapshot()` is non-destructive and reflects the growing buffer.
- Partial gating: no new audio → no emit; below `partial_min_ms` → no emit;
  buffer grows → emits `partial` with monotonically increasing `seq`.
- **Isolation**: `try_partial` raising / `TransportError` → loop stops, **no**
  `error` event emitted, and the `final` path still runs and emits `final`.
- Serialization: a `final` issued while a partial is in flight waits on the lock
  and the authoritative text wins.
- GPU-only: with the CPU engine active, zero `partial` events and no loop thread.
- Waveform downsample: a block → N min/max pairs (correct length + peak values);
  cadence throttle holds ~40 Hz.

**Host**
- Partial LCP word-diff → correct changed-word set across a sequence of partials.
- Waveform ring + render smoke; never-focus assertions retained.
- Fast-path: `waveform`/`partial` bypass the orchestrator and log.

**Integration**
- Fake transport emitting evolving partials → pill shows self-correction; the
  `final` overrides the last partial.

## Risks / open impl details

- **Prefix churn.** Whisper re-decode can rewrite beyond the tail; the LCP diff
  keeps flashing scoped, but heavy prefix churn could under/over-flash. Acceptable
  for a provisional preview; tune later.
- **No POST cancellation.** httpx can't cancel an in-flight `/inference`, so the
  final waits for an in-flight partial to return (bounded to one tick).
- **Left-truncation.** WPF `TextTrimming` trims the end; the interim line needs a
  small custom measure/clip to trim the start instead.

## Non-goals (this phase)

- Cleanup-diff animation (watching Ollama edit raw → cleaned). Deferred.
- Partials on the CPU engine.
- Injecting interim text into the target app (stays preview-only).
- Native streaming ASR / confirmed-prefix decoding (possible Phase 1.5).
