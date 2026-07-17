# DeadAir — Lantern Scope (DeadEye trace look for the pill oscilloscope)

**Date:** 2026-07-16
**Status:** Design approved — ready for implementation plan
**Look source:** DeadEye Lantern lit-edge traces — `claude-memory-compiler/scripts/templates/graph_explorer.html` (LANTERN-SPEC §5) and `docs/design/lantern-handoff/LANTERN-SPEC.md` in the DeadEye repo.

## Summary

Restyle the recording pill's oscilloscope line to match DeadEye's Lantern
connection traces: a **phosphor double-stroke** (soft wide glow under a thin
bright core), **warm lantern color**, **endpoint taper** (the trace pinches to
nothing at both ends), a **breathing amplitude envelope**, and the Lantern
**lifecycle** — an ignition sweep when recording starts (the trace writes
itself left→right with a bright pip riding the head) and a retract when
recording stops (the trace withdraws toward the newest-samples end, fading
continuously, then the pill hides).

The **data stays the real mic waveform**. Nothing changes in the sidecar, the
waveform protocol, the ring buffer, or what gets injected. This is a render-
and lifecycle-only change in the host pill.

## Scope decisions (from brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Treatment depth | **Full lifecycle** (glow + taper + breathing + ignition/retract) | User picked the complete Lantern identity, not just the static look. |
| Color | **Warm lantern tone** — base `#FFB454` | User picked DeadEye's warm ignition family over DeadAir's cyan. Base matches DeadEye's warm accent (`--warn`/gaming `#ffb454`, warm dust `rgb(255,214,170)`). |
| Implementation | **A — multi-Polyline phosphor** | Two strokes is literally what the canvas source does (wide soft pass under bright core). No shader effects, no new deps; B (DropShadowEffect) rasterizes per frame and has a fixed glow radius; C (custom additive visual/Skia) is overkill for 296×40. |
| Interim text | **Unchanged** (stays cyan) | Scope-only restyle per user. Warming the text is a possible follow-up. |

## Look constants (carried from LANTERN-SPEC §5)

| Element | Value | Source |
|---|---|---|
| Core stroke | `#FFC77F` ≈ `mix(#FFB454, #ffffff, 0.25)`, 1.3 px | Lantern `colLit = lighten(edgeCol, 0.25)`, core 1.2 px |
| Glow stroke | `#FFB454` at ~0.30 opacity, 3.5 px, under the core | Lantern phosphor pass: 3 px at 0.32× core alpha |
| Beam pip | `#FFF9F0` small ellipse + faded halo | Lantern hot-core white |
| Endpoint taper | amplitude × `sin(π·u)`, `u` = x/width | Lantern trace envelope |
| Breathing | amplitude × `(0.72 + 0.28·sin(t/900))`, t in ms | Lantern breathing envelope, 900 ms period (single trace → phase 0) |
| Ignition sweep | 300 ms, head moves linearly u=0→1 | Lantern `hopMs: 300` |
| Retract | ~450 ms, smoothstep, toward the **right** end | **Deviation:** Lantern dims over 4.8 s; the pill must leave promptly. Right end = newest samples ("withdraws into now"); Lantern anchors at the brighter endpoint — no analog here. |

WPF has no additive (`lighter`) compositing for vector strokes; layered
translucent strokes approximate the phosphor bloom. Accepted.

## Component design

### `DeadAir.Core/ScopeGeometry.cs` (new, pure math, WPF-free)

Static helpers, unit-testable, same Core-holds-the-logic pattern as
`PartialText`:

- `Envelope(double u)` → `sin(π·u)`; exactly 0 at u=0 and u=1.
- `Breathe(double tMs)` → `0.72 + 0.28·sin(tMs/900)`.
- `IgnitionHead(double tMs)` → head position u over the 300 ms sweep, clamped
  [0,1]; amplitude factor for a sample at `u` given the head (grows toward the
  head: `u/head`, 0 beyond it) — matches the beam writing the waveform
  origin→head with amplitude growing toward the head.
- `RetractFraction(double tMs)` → visible fraction 1→0 over 450 ms,
  smoothstep-eased (Lantern retract easing); also the continuous alpha fade
  factor so the trace reaches zero alpha exactly when length reaches zero — no
  pop (Lantern user amendment: retraction completes at the gate).
- `BuildPoints(IReadOnlyList<double> samples, double width, double height,
  Func<double, double> ampAt)` → `(double X, double Y)[]`. `ampAt(u)` is the
  per-point amplitude factor the caller composes from `Envelope(u)` ×
  `Breathe(t)` × the active ignition/retract factor; BuildPoints itself only
  maps samples to canvas points. Retract draws only `u ≥ 1−rf` (left edge
  slides right); sample→x mapping is fixed, so the wave is unveiled /
  withdrawn, never compressed (Lantern true-u rule).

### `RecordingIndicatorWindow` (App)

- **XAML:** add `GlowLine` Polyline (3.5 px, `#FFB454`, opacity 0.30,
  round join) *under* `ScopeLine`; restyle `ScopeLine` to `#FFC77F`, 1.3 px;
  add `BeamPip` Ellipse (`#FFF9F0`, ~5 px, hidden at rest). Both polylines get
  the same `Points`; pip is positioned by Canvas.Left/Top.
- **State machine:** `Idle → Igniting(300 ms) → Live → Retracting(450 ms) →
  Idle`. `ShowIndicator()` → reset buffer/text (as today), state=Igniting,
  hook a `CompositionTarget.Rendering` tick. `HideIndicator()` → state=
  Retracting; on completion `Hide()` and unhook the tick. Calling
  `ShowIndicator` mid-retract cancels the retract and re-ignites; calling
  `HideIndicator` while hidden/retracting is a no-op.
- **Render tick:** each frame compute `t` (ms since state entry,
  `Environment.TickCount64`), fold taper × breathe × ignition/retract factors
  per point, assign `Points` to both polylines, set line opacities
  (core ≈ 0.95 × fade, glow ≈ 0.30 × fade), place/fade the pip (centered on the
  head sample's computed (x, y); visible during ignition, ~150 ms fade-out
  after arrival). Tick runs **only while the pill
  is visible** — hook on show, unhook after hide.
- **Waveform push path unchanged:** `PushWaveform` still fills the ring
  buffer; geometry is rebuilt on the render tick instead of per push (40 Hz
  push, ~60 Hz draw; the 296-point loop ×2 polylines is trivial).
- **Never-activate constraints preserved verbatim** (`WS_EX_NOACTIVATE |
  WS_EX_TOOLWINDOW`, `ShowActivated=false`, `IsHitTestVisible=false`,
  `Focusable=false`, never `Activate()`); retract must not touch focus,
  z-order, or blocking waits — `HideIndicator` stays non-blocking for the
  orchestrator.

## Testing plan

**Core (`DeadAir.Core.Tests/ScopeGeometryTests.cs`)**
- Envelope: 0 at both endpoints, 1 at midpoint, symmetric.
- Breathe: bounded [0.44, 1.0], period 2π·900 ms.
- Ignition: head 0 at t=0, 1 at t≥300; amp factor 0 beyond head, grows
  monotonically toward it; full envelope after completion.
- Retract: fraction 1 at t=0, exactly 0 at t≥450, monotonic, smoothstep-eased;
  alpha fade continuous and 0 exactly at completion (no pop).
- BuildPoints: v=0 → y=mid regardless of factors; x spacing uniform; retract
  mapping draws only the right `rf` of the width at fixed x positions.

**App**
- Existing pill tests (focus-safety, `PartialText`) untouched and green.
- Manual smoke (synthetic input can't reach the pill meaningfully): record →
  ignition sweep with pip; hold → breathing warm trace, taper at both ends;
  release → retract-into-the-right then hide; rapid re-record mid-retract
  re-ignites cleanly.

## Risks / notes

- **Breathing modulates real data** (±28 % displayed amplitude). Cosmetic
  distortion of a preview; accepted with the "full lifecycle" choice.
- **Retract delays the pill's disappearance ~450 ms.** Injection is unaffected
  (the pill never takes focus); it is purely visual lag after key-up.
- **`CompositionTarget.Rendering` leak risk:** the handler must be unhooked on
  hide (and on window close) or it burns CPU forever. Explicit test target for
  review.
- **No additive blending in WPF vectors** — the bloom is an approximation;
  verified by eye at smoke time.

## Non-goals

- Sidecar, protocol, ring buffer, injection: untouched.
- Interim text colors (stay cyan; possible follow-up).
- Lantern dials (glow gain / dim floor / hold time) — no settings surface.
- Firefly blips, dust, jump-beam hop delays across multiple "edges" — the pill
  has one trace, not a graph.
