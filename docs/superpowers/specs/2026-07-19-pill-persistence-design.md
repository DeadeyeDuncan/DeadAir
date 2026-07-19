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
public enum FlowOutcome { Injected, NothingHeard, Failed, TimedOut, Interrupted }

// on Orchestrator, additive:
public event Action<FlowOutcome>? Outcome;
```

### Superseded outcomes are suppressed in the App layer, not in Core

> **Design history — two failed attempts, kept as a warning.** The first draft raised
> `Outcome` unconditionally with no suppression at all. The second tried to give Core
> per-utterance *ownership* by comparing a captured `_utteranceId` inside a guarded
> `RaiseOutcome`. Two rounds of adversarial review killed the second approach with four
> distinct blockers, because `_utteranceId` was never designed to express UI lifetime:
> `OnHotkeyDownAsync` does not advance it (so a new recording does not invalidate an old
> captured id), a `ready` arriving mid-`Cleaning` advances it while preserving state (so
> the final tail's id goes stale and raises nothing), and any "raised" flag has no correct
> reset point because an utterance begins on hotkey-*down* while transcription begins on
> hotkey-*up*. Each patch bolted another condition onto semantics that could not carry it.
> **Do not reintroduce Core-side ownership.**

The constraint being protected is a *pill* constraint: a terminal caption must never
overwrite a live recording's scope. So the guard belongs in the layer that owns the pill.

- **Core raises unconditionally.** No ownership, no idempotence flag, no id capture. Each
  terminal path raises its outcome and nothing more. This removes the entire class of
  blockers above.
- **The App suppresses terminal captions while a recording is live.** `App.xaml.cs` already
  receives every `FlowState` through the notifier hook; it records the latest one. When an
  outcome arrives and the last state was `Recording`, the caption is dropped.
- **Ordering is reliable because both paths marshal through the same dispatcher.**
  `TrayNotifier.SetState` dispatches the state hook, and the outcome handler dispatches too,
  so they serialize on the UI thread. If the new `Recording` was queued first, the guard
  sees it and drops the stale caption; if the caption was queued first, it lands before the
  recording begins and `ShowIndicator` clears it. There is no window where a stale caption
  survives into a live recording.
- **The suppression predicate is pure and testable headless**, so it does not hide in a
  WPF file:

```csharp
// on PillStatus
public static bool SuppressTerminal(FlowState lastState) => lastState == FlowState.Recording;
```

A superseded tail therefore still injects its words and still raises `Injected`; the App
simply declines to draw it. Words are never lost, and the new recording's pill is never
touched.

### Historical note: the abandoned ownership approach

A bare "raise on each terminal path" is **wrong**, and the existing test suite already
pins why. `HandleFinal_TailDoesNotStompNewRecording` documents this legal sequence:

1. A `final` enters `Cleaning`.
2. An unsolicited `error` resets state to `Idle`.
3. The user starts a **new** recording.
4. The old utterance's cleanup tail still injects its words — deliberately, because words
   are never lost — without demoting the new `Recording`.

Raising unconditionally would emit `Failed` at step 2 and `Injected` at step 4. That second
caption lands **while a new recording is live**, overwriting its scope and arming a 900 ms
dismissal that retracts the new pill. That breaks exactly-once *and* functionally violates
the byte-identical-Recording constraint. There are zero-fire paths too: the `ready` event
resets `Transcribing → Idle`, and a throwing cleaner or injector reaches `finally` without
passing an inject-site raise — in both cases the pill would hang on a stale caption.

Therefore every terminal path funnels through one guarded helper:

```csharp
private void RaiseOutcome(FlowOutcome outcome, long utteranceId)
{
    lock (_gate)
    {
        if (_utteranceId != utteranceId || _outcomeRaised) return;
        _outcomeRaised = true;
    }
    Outcome?.Invoke(outcome);   // always outside the lock
}
```

- **Ownership:** an utterance that no longer owns the flow (`_utteranceId` moved on) raises
  nothing. A superseded tail therefore injects its words **silently** — the new recording's
  pill is never touched. This is the deliberate choice, not an oversight.
- **Idempotence:** at most one outcome per utterance.
- **Zero-fire coverage:** the final path raises in `finally`, so a throwing cleaner or
  injector still lands an outcome. The `ready` reset raises `Interrupted`.

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
| `Injecting` | `"injecting…"` | no |
| `Cleaning` | *(none from state — see below)* | — |
| `CleaningStarted`, `translating: true` | `"translating…"` | no |
| `CleaningStarted`, `Polished` | `"polishing…"` | no |
| `CleaningStarted`, `Faithful` | `"cleaning…"` | no |
| `Outcome.Injected` | `"sent"` | yes |
| `Outcome.NothingHeard` | `"nothing heard"` | yes |
| `Outcome.Failed` | `"failed"` | yes |
| `Outcome.TimedOut` | `"timed out"` | yes |
| `Outcome.Interrupted` | `"interrupted"` | yes |

**Why `Cleaning` is not captioned from the state hook.** The mode and translate flags are
mutable from the tray at any moment, and `TrayNotifier.SetState` dispatches the hook
*asynchronously* — so a hook that re-reads `_orchestrator.Mode` / `config.Cleanup
.TranslationActive` can caption an operation that was never submitted. The caption is
therefore delivered by a second additive event raised where the values are read:

**Honest limit on "snapshot".** `Mode` can be made exact: capture it into one local and
pass that same local to both `CleaningStarted` and `CleanAsync`. `translating` cannot be
made exact without reworking cleanup plumbing — `OllamaClient` re-reads
`TranslationActive` for its skip guard and `PromptBuilder` reads it again when building the
prompt, so a toggle flipped mid-flight can still make the caption disagree with the prompt
actually sent. That is a **pre-existing property of the existing toast**, which snapshots
at the same place and can drift the same way; this feature does not make it worse and does
not fix it. Do not claim the caption is guaranteed to match the submitted prompt.

```csharp
public event Action<CleanupMode, bool>? CleaningStarted;   // (mode, translating)
```

`PillStatus.ForState` therefore returns `null` for `Cleaning`, and the app captions the
cleanup phase from `CleaningStarted`.

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
steals activation, and must reuse the existing `Show()`-only path.

This is enforced by construction and by manual smoke, **not** by a unit test:
`RecordingIndicatorWindow` is a WPF window and this codebase keeps WPF smoke-only
(the testable decisions live in `DeadAir.Core`). The smoke step is: dictate into Notepad
and confirm the text lands there while the pill is visible.

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
- If a terminal outcome never arrives, the pill would hang visible, so a persistent
  caption arms a watchdog. The watchdog must **not** be 60 s: `UtteranceTimeoutMs` is
  also 60 s, and a tie lets the watchdog start the retract first — after which the
  `TimedOut` caption arrives, sees `IsVisible == true`, declines to re-show, and gets
  hidden by the in-flight retract well before its 900 ms. Use **90 s**, comfortably
  outside the ASR timeout.
- Relatedly, `ShowStatus` must re-show when the window is **retracting**, not only when it
  is hidden. `IsVisible` stays true throughout the 450 ms retract, so an `IsVisible`-only
  check silently drops any caption that lands mid-retract.
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
