# DeadAir — Nebula Scope Skin (second pill-oscilloscope skin, DeadEye nebula style)

> **SUPERSEDED (2026-07-17, same day):** the nebula render design below (3-strand
> bundle riding the live PCM waveform) was rejected at live smoke and replaced by
> `docs/superpowers/plans/2026-07-17-nebula-look-redesign.md` (+ its T12–T14
> amendments): nebula draws NO PCM waveform — 6 smooth strands whose fan width,
> brightness, drift speed, and traveling-wave turbulence are driven by mic
> loudness; three Settings dials (fan sensitivity / wiggle / wiggle speed); the
> ignition sweep + pip were removed from nebula (fade-in instead). **Update
> 2026-07-18:** the lantern skin was removed and nebula is now the sole pill
> scope, so the skin switch (`Pill.Skin`, Settings dropdown) is gone; the
> shipped strand palette is all-red (`#98180F`/`#C11D12`/`#E8382B`), not the
> historical values in the table below.

**Date:** 2026-07-17
**Status:** Superseded — see banner (was: Design approved)
**Look source:** DeadEye Nebula edge style, final tamed form (`feat/nebula-lit-bundles` `819ecbf`; helpers + lit-strand pass in `claude-memory-compiler/scripts/templates/graph_explorer.html`, WISP-HELPERS block + `wispLitStrand`).
**Extends:** `2026-07-16-lantern-scope-design.md` (the Lantern skin, shipped on `feat/lantern-scope`, user-smoked PASS).

## Summary

Add a second render **skin** to the recording pill's oscilloscope: **Nebula** —
a tamed 3-strand smoke bundle riding the live waveform, with a wide faint haze
understroke and a hot white core strand, in a **silvered-amber** palette. The
skin is chosen via `Pill.Skin` in config.json and a **Settings-window
dropdown**, applies live on save, and **defaults to `nebula`** (user choice).
The Lantern skin remains available and untouched.

Shared between skins (unchanged): the lifecycle state machine
(Idle→Igniting→Live→Retracting), the 300 ms ignition sweep + beam pip, the
450 ms right-anchored retract with continuous fade, the real-mic-data rule,
never-activate constraints, render tick hook/unhook discipline.

Skin-specific: stroke composition (which Polylines draw) and per-point math.

## Scope decisions (from brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Switch surface | **Settings dropdown + config.json**, applies live on save | House pattern (engine/model pickers). |
| Palette | **Bone-silvered red** | Same family as Lantern (red-theme pivot 2026-07-17); nebula silvering formula over the Lantern lit color. Silver target follows DeadEye's rebrand: `#E2E6EE` → bone `#DDD6CE`. |
| Default skin | **`nebula`** | User choice; unknown/invalid values also fall back to `nebula`. |
| Breathing | **Lantern only** | DeadEye fidelity: nebula edges don't breathe — the strand drift IS the motion. |
| Strand count | **Fixed 3 + haze** | DeadEye's tamed/heavy count; one 296 px trace needs no LOD gate. |

## Look constants (from DeadEye WISP helpers + tamed `wispLitStrand` pass)

| Element | Value | Source |
|---|---|---|
| Strand color (1,2) | `#C11D12` (DeadEye `--accent`, direct token), 0.85 px, α 0.65 | Silvering formula ABANDONED on the pill (user smoke: mixed tones read salmon on thin normal-alpha strokes); strands use the theme accent raw |
| Hot core (strand 0) | `#E8382B` (DeadEye `--accent-strong`), 1.1 px | White hot-core retired at user smoke (read as a white line mid-bundle); core is now the brightest red, hierarchy by opacity |
| Haze understroke | `#98180F` (logo deep blood red), 9 px, α 0.05, noise amp ×0.7 | haze pass: 9px, `(0.016+0.034·eLit)` ≈ 0.05, amp ×0.7 |
| Noise | `WispNoff(u, t·0.33, seed, k)` — 3 layered sines `/1.83` | `wispNoff` verbatim; `t·0.33` = the tamed 3×-slowed drift |
| Noise envelope | `WispEnv(u) = sin(π·u)^0.75`, **exactly 0** at/outside endpoints | `wispEnv` verbatim incl. the endpoint clamp (their `wispEnv(1)=1.16e-12` bug — pinned as a test here) |
| Strand seeds / drift rates | seed `3.7 + s·5.7` (s=0,1,2), k `0.9 + s·0.13`; haze seed `34.7`, k 1.0 | per-strand `_phase·10 + s·5.7`, `0.9 + s·0.13` with a fixed phase 0.37; haze seed `+31` |
| Noise amplitude | `3.0 px × (0.35 + s·0.22)` → 1.05 / 1.71 / 2.37 px; haze `3.0×0.7` | strand amp ladder `(0.35 + s·0.22)`; base tamed to 3 px so the real waveform stays legible (DeadEye's edge-relative base doesn't map to a 40 px scope) |
| Waveform taper (nebula) | `WispEnv(u)` replaces the Lantern `Envelope(u)` | strands and base pinch with the same envelope — coherent bundle ends |
| Drift clock | `(now − showT0) · 0.33`, continuous across states | drift never resets mid-recording |

## Component design

### `DeadAir.Core/ScopeGeometry.cs` (additions — pure, tested)

- `WispNoff(double u, double t, double seed, double k)` — DeadEye formula
  verbatim; caller pre-slows `t` (contract identical to DeadEye's).
- `WispEnv(double u)` — `sin(π·u)^0.75` with the exact-zero endpoint clamp.
- `BuildNebulaPoints(IReadOnlyList<double> samples, double width, double
  height, Func<double,double> ampAt, double tSlow, double seed, double k,
  double noiseAmp, double visibleFrom = 0, double visibleTo = 1)` →
  `y = mid − v[i]·mid·ampAt(u) + WispNoff(u,tSlow,seed,k)·noiseAmp·WispEnv(u)`;
  x fixed per index; same visibility-window semantics as `BuildPoints`;
  `n<2` → empty.

### `RecordingIndicatorWindow` (App)

- **XAML:** four new Polylines inside `ScopeCanvas`, z-order bottom→top:
  `HazeLine` (9 px, `#98180F`, α .05) → `Strand1`/`Strand2` (0.85 px,
  `#C11D12`, α .65) → `StrandHot` (1.1 px, `#E8382B`). Pip shared.
- **Skin state:** `_skin` field (default `"nebula"`), `public void
  SetSkin(string skin)` — normalizes (anything ≠ `"lantern"` → `"nebula"`),
  toggles element visibility between the lantern set {GlowLine, ScopeLine}
  and the nebula set {HazeLine, Strand1, Strand2, StrandHot}.
- **Render tick:** branches per skin. Nebula: no breathe; waveform amp =
  `WispEnv(u) × ignition/1`; four `BuildNebulaPoints` calls per frame (haze +
  3 strands) with the constants above; retract multiplies each nebula
  element's base opacity by the fade. The lantern path is restructured into
  the shared per-skin frame renderer but stays behavior-identical (same
  formulas, same constants).
- **Pip:** `PlacePip` reads the active skin's primary line (`StrandHot` on
  nebula, `ScopeLine` on lantern).

### Config + Settings

- `AppConfig` gains `PillConfig Pill` with `string Skin = "nebula"`.
  **Host-only:** `ConfigCommand.From` is untouched, so nothing reaches the
  sidecar (pinned by test).
- `SettingsWindow`: "Scope skin" ComboBox (`nebula`/`lantern`) between the
  cleanup-mode picker and the dictionary; the existing `Select` helper's
  index-0 fallback makes unknown config values land on `nebula`.
- `App.OnStartup`: `_indicator.SetSkin(_config.Pill.Skin)` right after the
  indicator is constructed. `App.OnSettingsSaved`: same call — applies live.

## Testing plan

**Core** — `WispEnv`: exact zero at/outside endpoints (theory), peak 1 at 0.5,
symmetry, `0.25 → sin(π/4)^0.75 ≈ 0.7711`. `WispNoff`: |v| ≤ 1 over a sweep,
deterministic, seed-sensitive, time-drifts. `BuildNebulaPoints`: zero
samples + zero noise → midline; noise bounded by `noiseAmp·WispEnv`; nonzero
somewhere in the interior; window semantics + fixed x (mirrors the `BuildPoints`
tests); `n<2` → empty.

**Config** — default `nebula`; JSON round-trip preserves `lantern`;
`ConfigCommand.From(...)` serialization contains no `"skin"` (sidecar
isolation).

**App** — build + full suite; existing pill behavior unchanged on the lantern
path; manual smoke of the nebula skin + Settings switch (user).

## Risks / notes

- **Noise atop real data:** ±2.4 px max strand offset on a 40 px canvas —
  legibility judged at live smoke; `NebulaAmpPx` is the single tuning dial.
- **Six Polylines resident, two-to-four active** — visibility-collapsed
  elements cost nothing per frame.
- **4× BuildNebulaPoints per frame** (~1200 points + 4 PointCollections):
  same order of cost as the Lantern path; fine at 60 Hz.
- The running app instance must be stopped before rebuilding (known MSB3027
  file-lock gotcha).

## Non-goals

- No per-skin dials/settings beyond the skin picker.
- No rest state, gusts, embers, churn, or rest-smoke layer (DeadEye's rejected
  1.17.0 extras stay out — this is the tamed final form only).
- Interim text colors unchanged.
- Sidecar untouched.
