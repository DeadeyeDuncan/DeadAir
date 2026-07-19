# DeadAir Pill Persistence (Status Through Transcribe/Clean/Inject) — Design Spec

Date: 2026-07-19
Branch: `feat/pill-persistence`
Status: approved, unbuilt

## Problem

The recording pill dies the instant the hotkey is released. `App.xaml.cs` wires the
notifier state hook as `state == FlowState.Recording ? ShowIndicator() : HideIndicator()`,
so `Transcribing`, `Cleaning`, and `Injecting` all retract the pill.

`Cleaning` is the slow phase — it is the Ollama `qwen2.5:7b` round-trip, multiple seconds
when translating. The user gets no visual signal during it: the pill vanishes, then text
appears at the cursor some seconds later, with nothing in between distinguishing "working"
from "wedged".

DeadEye's Voice pill already solves this and the behavior is liked as-is. This spec ports
that behavior to DeadAir faithfully.

## Decisions (from brainstorming)

1. **Faithful port, no redesign.** DeadEye's caption-based status is the target behavior.
   Do not invent new visual treatments.
2. **The `Recording` visual is untouched.** The lantern/nebula scope, its audio-reactive
   energy follower, ignition and retract all stay byte-identical. This work only extends
   *when* the pill is visible and what text it carries outside `Recording`.
3. **Captions reuse the existing text surface.** `RecordingIndicatorWindow` already has an
   `InterimText` `TextBlock` driven by the partial-text layout path. Status captions write
   through the same surface — no new XAML elements.
4. **`Cleaning` caption is mode-aware.** DeadEye has no equivalent phase, so this row is
   DeadAir-specific: `"translating…"` when `config.Cleanup.TranslationActive`, else
   `"polishing…"` for `CleanupMode.Polished` and `"cleaning…"` for `CleanupMode.Faithful`.
5. **Terminal outcomes need a signal that `FlowState` does not carry** (see below).

## The state-only mapping does not work

`Orchestrator` collapses distinct terminal outcomes onto the same state:

- the `empty` sidecar event → `SetState(FlowState.Idle)`
- the `error` sidecar event → `SetState(FlowState.Idle)` + `Toast`
- the utterance timeout → `SetState(FlowState.Idle)` + `Toast("ASR timed out")`
- the normal path → `Injecting`, then the `finally` lands on `Idle`

A mapping keyed on `FlowState` alone cannot tell "nothing heard" from "ASR errored" — both
are `Transcribing → Idle`. Labelling an error "nothing heard" would be a lie in the UI.

**Resolution:** add an additive outcome signal on `Orchestrator`. Do **not** widen
`IUserNotifier` — that interface is implemented by `TrayNotifier` and by fakes across
`OrchestratorTests`, and widening it breaks every implementer for no gain.

```csharp
public enum FlowOutcome { Injected, NothingHeard, Failed, TimedOut }

// on Orchestrator, additive:
public event Action<FlowOutcome>? Outcome;
```

Raised at the four existing terminal points: after injection completes, on the `empty`
event, on the `error` event, and in the utterance-timeout callback.

## Changes

### 1. `DeadAir.Core` — `FlowOutcome` + `Orchestrator.Outcome`

New enum, new event, raised at the four terminal points listed above. No existing signature
changes. `IUserNotifier` is untouched.

### 2. `DeadAir.Core` — `PillStatus` (pure mapping)

A pure static mapping, unit-testable headless, matching the `ScopeGeometry` precedent of
keeping math and decisions in the WPF-free Core:

```csharp
public readonly record struct PillCaption(string Text, bool Dismiss);

public static class PillStatus
{
    public static PillCaption? ForState(FlowState state, CleanupMode mode, bool translating);
    public static PillCaption ForOutcome(FlowOutcome outcome);
}
```

`ForState` returns `null` for `Recording` (scope only, no caption) and for `Idle`.

| Input | Caption | Dismiss |
|---|---|---|
| `Recording` | *(none — scope visual)* | — |
| `Transcribing` | `"transcribing…"` | no |
| `Cleaning`, `translating: true` | `"translating…"` | no |
| `Cleaning`, `Polished` | `"polishing…"` | no |
| `Cleaning`, `Faithful` | `"cleaning…"` | no |
| `Injecting` | `"injecting…"` | no |
| `Outcome.Injected` | `"sent"` | yes |
| `Outcome.NothingHeard` | `"nothing heard"` | yes |
| `Outcome.Failed` | `"failed"` | yes |
| `Outcome.TimedOut` | `"timed out"` | yes |

The `translating` flag is read from `config.Cleanup.TranslationActive`. `Orchestrator`
already snapshots that value before the cleanup await so a mid-flight tray toggle cannot
mislabel the operation; the caption uses the same snapshot for the same reason.

### 3. `DeadAir.App` — `RecordingIndicatorWindow.ShowStatus`

Lift from `DeadEye.Shell/Voice/VoicePillWindow.ShowStatus`, which is already the same shape
(dispatcher marshal → show-if-hidden → clear last partial → write caption → restart a
dismiss timer):

```csharp
public void ShowStatus(string text, bool dismiss)
```

- marshals to the dispatcher when called off-thread
- calls `ShowIndicator()` first if not currently visible, so a caption can never arrive
  with no window to carry it
- clears `_lastPartial` and writes `text` through the existing `InterimText` path
- stops the dismiss timer; starts it only when `dismiss` is true
- timer elapse calls the existing `HideIndicator()` (the 450 ms retract is reused unchanged)

### 4. `DeadAir.App` — rewire the state hook

`App.xaml.cs` replaces the `Recording ? Show : Hide` lambda:

**Construction order.** `TrayNotifier` is built at `App.xaml.cs:76` but `_orchestrator` is
not assigned until line 97, so the hook cannot read a local. It does not need to:
`_orchestrator` is a field (`private Orchestrator _orchestrator = null!;`), so the lambda
reads it at *invoke* time, long after assignment. Read the mode off the field, not off
`_config` — the tray "Polished" toggle mutates `Orchestrator.Mode`, not the config object,
so a config read would caption a stale mode. The `is not null` guard covers only the
never-in-practice case of a state arriving before line 97.

```csharp
var notifier = new TrayNotifier(_tray, Dispatcher, state =>
{
    if (state == FlowState.Recording) { _indicator.ShowIndicator(); return; }
    var mode = _orchestrator is not null ? _orchestrator.Mode : _config.Cleanup.Mode;
    var caption = PillStatus.ForState(state, mode, _config.Cleanup.TranslationActive);
    if (caption is { } c) _indicator.ShowStatus(c.Text, c.Dismiss);
    // Idle maps to null and is intentionally ignored: the terminal outcome
    // caption owns dismissal, so Idle must not retract the pill here.
});

// … existing construction at line 97 …
_orchestrator.Outcome += o => Dispatcher.BeginInvoke(() =>
{
    var c = PillStatus.ForOutcome(o);
    _indicator.ShowStatus(c.Text, c.Dismiss);
});
```

`Idle` deliberately does **not** hide on its own — the terminal outcome caption owns the
dismissal, and its timer performs the retract. This is what keeps "sent" readable instead
of being stomped by the `Idle` transition that immediately follows injection.

**Ordering caveat.** `Orchestrator` raises `Outcome` and calls `SetState(Idle)` from the
same terminal paths, and both handlers marshal through the dispatcher. The outcome caption
must win regardless of arrival order. Because `Idle` maps to `null` and the hook ignores
it, order does not matter: a late `Idle` cannot clear a caption already shown. Do not
"fix" this by making `Idle` hide the pill — that reintroduces the stomp.

## Focus safety (the one real risk)

The pill is a `WS_EX_NOACTIVATE` tool window and `ShowIndicator` calls `Show()`, never
`Activate()`. Keeping it visible *through injection* means it is now on screen at the exact
moment `ClipboardPasteInjector` sends `Ctrl+V` to whatever the user has focused. If the pill
ever took focus, the paste would land in the pill's own window instead of the user's app —
silent text loss into a UI that cannot even display it.

`ShowStatus` must therefore never call `Activate()`, never set `Topmost` in a way that
steals activation, and must reuse the existing `Show()`-only path. A test pins that
`ShowStatus` reaches `Show()` and not `Activate()`.

## Not changing

- The lantern/nebula scope geometry, energy follower, ignition, retract, and all
  `ScopeGeometry` math.
- `IUserNotifier` and every existing implementer.
- The toasts. `"translation skipped: …"` etc. stay exactly as they are; captions are
  additive, not a replacement.
- The sidecar, ASR, Ollama, and injection paths.
- `AppConfig` — no new config. The behavior is unconditional.

## Error handling

- An exception inside the state hook is already swallowed by `TrayNotifier` so indicator
  failures never break the pipeline. The `Outcome` handler gets the same treatment.
- If a terminal outcome never arrives (a path that neither injects nor errors), the pill
  would otherwise hang visible. `ShowStatus(dismiss: false)` starts no timer, so a
  watchdog is required: any caption left up longer than the utterance timeout
  (`UtteranceTimeoutMs`, 60 s) retracts itself.
- `ShowStatus` called before `ShowIndicator` self-heals by showing first.

## Testing

Headless in `DeadAir.Core.Tests` (WPF window stays smoke-only, matching `ScopeGeometry`):

- `PillStatus.ForState` returns the right caption per state × mode × translating flag,
  including `null` for `Recording` and `Idle`.
- `PillStatus.ForOutcome` covers all four outcomes and every one sets `Dismiss: true`.
- `Orchestrator` raises `Outcome.NothingHeard` on the `empty` event, `Failed` on `error`,
  `TimedOut` from the timeout callback, and `Injected` after a successful inject.
- `Outcome` fires exactly once per utterance.
- The existing `OrchestratorTests` suite still passes with no signature changes.

Manual smoke (the parts a headless test cannot reach):

- Hold hotkey, speak, release → caption walks `transcribing… → translating… → sent`, then
  retracts on its own.
- With the translate toggle off → `polishing…` / `cleaning…` per mode.
- Speak silence → `nothing heard`.
- Stop Ollama mid-flight → the existing skip toast still fires and the pill still lands on
  `sent`.
- **Focus check:** inject into Notepad and confirm the text lands in Notepad, not lost —
  the pill must never take focus while visible during paste.

## Non-goals

- No new pill skin, no layout change, no new config surface.
- No change to what DeadEye does; this is a one-way port into DeadAir.
- No progress bar, spinner, or percentage — captions only.
