# DeadAir Recording Indicator — Design Spec

A small floating pill window with a live voice-driven soundwave, shown while
hold-to-talk recording is active. Approved 2026-07-05 (post-Phase-0 add-on,
built as Task 15 of the phase-0 pipeline).

## Goal

When the user holds the hotkey, a frameless dark pill appears bottom-center
(above the taskbar) with ~24 bars dancing to the **actual microphone level**,
and disappears when recording stops. Honest feedback: a dead mic shows flat
bars.

## Non-goals (YAGNI)

- No waveform history/scrubbing, no spectrogram — RMS level bars only.
- No indicator during Transcribing/Cleaning/Injecting states (v1: visible only
  while Recording).
- No per-monitor configuration; primary screen only.
- No clicks/interaction on the pill.

## Architecture

Audio PCM lives in the Python sidecar; the host never sees it. The sidecar
computes levels and streams tiny events over the existing stdout JSON-lines
protocol; the host renders them.

```
capture callback (16 kHz blocks, already exists)
   └─ while recording: RMS per ~40 ms block → normalize → throttle ~25 Hz
        └─ emit {"event":"level","rms":0.00-1.00}          (sidecar)
              └─ SidecarManager.EventReceived               (host)
                    ├─ "level" → RecordingIndicatorWindow.Push(rms)   [Dispatcher.BeginInvoke]
                    └─ everything else → Orchestrator (unchanged; its switch
                       ignores unknown events already)
Show/hide: App observes FlowState via the existing IUserNotifier.SetState path —
entering Recording → Show + reset bars; leaving Recording → Hide.
```

## Components

### Sidecar: `asr_sidecar/levels.py` + capture hook
- `rms_to_level(block: np.ndarray) -> float`: RMS of float32 [-1,1] block,
  mapped through a log-ish curve so normal speech spans the visual range:
  `level = clip((log10(max(rms, 1e-4)) + 4) / 4, 0, 1)` (1e-4 floor ≈ silence,
  1.0 ≈ full-scale).
- `LevelEmitter(emit_fn, min_interval_ms=40)`: throttles — emits at most one
  `{"event":"level","rms":<2-decimal float>}` per interval; called from
  `MicCapture`'s frame path only while `_recording`.
- Wiring: `MicCapture` gains an optional `on_block` callback (set by
  `__main__.py` to the emitter) invoked with each callback block while
  recording. No change to VAD/ASR flow; levels are computed pre-VAD.

### Host: protocol + window
- `SidecarEvent` gains `[JsonPropertyName("rms")] public double? Rms`.
- `App.xaml.cs` EventReceived handler: `if (ev.Event == "level")` →
  `Dispatcher.BeginInvoke(() => _indicator.Push(ev.Rms ?? 0))` and return
  (level events bypass Orchestrator/FireAndForget entirely — no state-machine
  or log traffic at 25 Hz).
- `RecordingIndicatorWindow.xaml(.cs)`:
  - `WindowStyle=None`, `AllowsTransparency`, `Background=Transparent`,
    `Topmost`, `ShowInTaskbar=False`, `ShowActivated=False`.
  - **`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`** applied in `OnSourceInitialized`
    via `SetWindowLong(GWL_EXSTYLE, ...)` — the pill must NEVER take focus or
    injection would target it instead of the user's app. Load-bearing.
  - Layout: rounded-corner dark pill (~280×48), 24 vertical bars (Rectangles
    in a UniformGrid/Canvas), bar height = `4 + level * 32` px.
  - `Push(double level)`: shift ring buffer left, append, re-render. 25 Hz is
    trivial; no animation framework needed.
  - `ShowIndicator()`: reset buffer to floor, position bottom-center of
    primary work area (`SystemParameters.WorkArea`), `Show()` (never
    `Activate()`); `HideIndicator()`: `Hide()`.
  - Show/hide calls wrapped in try/catch — indicator failure must never break
    the dictation pipeline.
- Show/hide driven from the existing state path: `TrayNotifier.SetState`
  already receives every `FlowState`; App passes the indicator to it (or a
  parallel small `IStateObserver`) — entering `Recording` shows, any other
  state hides.

## Error handling
- No/level-starved events → bars sit at floor height (window still proves
  "recording" state; flat bars = mic problem, by design honest).
- Malformed rms (null) → treated as 0.
- Indicator window exceptions → caught, logged once, pipeline unaffected.

## Testing
- Sidecar unit: `rms_to_level` mapping (silence→~0, full-scale→~1, speechy
  mid-levels in range); `LevelEmitter` throttling (N rapid blocks → ≤ expected
  event count) and recording-gated emission. Injectable `emit_fn` — no real
  stdout/mic needed.
- Host unit: `SidecarEvent` parses `rms`; ring-buffer push/shift math (extract
  as a small testable `LevelRingBuffer` class).
- Manual (user smoke): pill appears on hold, bars follow voice, disappears on
  release, focus stays in the target app, injection still lands.

## Sequencing
Built as Task 15 after the Phase-0 smoke checklist completes; covered by the
same final whole-branch review.
