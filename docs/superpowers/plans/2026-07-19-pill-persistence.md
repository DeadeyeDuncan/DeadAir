# DeadAir Pill Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the DeadAir recording pill on screen after hotkey release, captioned with the phase it is in (`transcribing… → translating… → sent`), instead of vanishing through the multi-second Ollama call.

**Architecture:** Two additive events on `Orchestrator` in the WPF-free `DeadAir.Core` — a per-utterance-owned `Outcome` signal and a `CleaningStarted` snapshot — plus a pure `PillStatus` mapping, a `ShowStatus` method on `RecordingIndicatorWindow` lifted from DeadEye's `VoicePillWindow`, and a rewire of the notifier state hook in `App.xaml.cs`. All decision logic lives in Core and is unit-tested headless; the WPF window stays smoke-only.

**Tech Stack:** .NET 8, WPF, xunit 2.5.3, Hardcodet TaskbarIcon.

**Spec:** `docs/superpowers/specs/2026-07-19-pill-persistence-design.md`

> **Revision note (post plan-roast).** A first draft of this plan raised `Outcome`
> unconditionally at each terminal path. An adversarial review proved that wrong against
> the existing suite: `HandleFinal_TailDoesNotStompNewRecording` pins a legal sequence
> where a superseded utterance still injects while a NEW recording is live, so an
> unconditional raise would stomp and retract the new recording's pill. Task 1 below now
> carries utterance ownership + idempotence. Read the spec's "The outcome must be owned by
> an utterance" section before starting.

## Global Constraints

- Branch: `feat/pill-persistence`. Do not merge to master.
- The `Recording` visual is byte-identical when done: no edit to the lantern/nebula scope geometry, energy follower, ignition, retract, or any `ScopeGeometry` math.
- `IUserNotifier` is NOT widened. `TrayNotifier` and every test fake implement it; adding a member breaks them all.
- No new config. The behavior is unconditional — no `AppConfig` change.
- No new XAML elements. Captions write through the existing `InterimText` `TextBlock` via `SetPartial`.
- The pill must never call `Activate()`. It is a `WS_EX_NOACTIVATE` tool window and is now visible during `Ctrl+V` injection; stealing focus would paste the user's dictation into the pill.
- **The implementing worker must NOT run ANY `git` command — not even a read-only one.** The Codex sandbox writes Deny ACEs on `.git` by design and re-applies them every session. Every verification step in this plan is achievable without git. The `git` commands shown in commit steps are for the controller only.
- Build with `--no-restore` (the sandbox cannot read `NuGet.Config`). The controller has already run `dotnet restore`.
- Never pass a `.cs` file to `dotnet test` — that yields `MSB1008`. Pass the `.csproj`.
- Baseline suite before any change: **152 passing**, verified by the controller. It must not drop.
- Use forward slashes in every path.

---

### Task 1: Owned `FlowOutcome` + `CleaningStarted` on `Orchestrator`

`FlowState` cannot express how an utterance ended — `empty` and `error` both land on `Idle`. And an outcome must belong to a specific utterance, or a superseded tail will caption over a live recording. This task adds both additive events; no existing signature changes.

**Files:**
- Create: `host/DeadAir.Core/FlowOutcome.cs`
- Modify: `host/DeadAir.Core/Orchestrator.cs`
- Test: `host/DeadAir.Core.Tests/OrchestratorTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `public enum FlowOutcome { Injected, NothingHeard, Failed, TimedOut, Interrupted }`, `public event Action<FlowOutcome>? Outcome;`, and `public event Action<CleanupMode, bool>? CleaningStarted;` on `Orchestrator`. Task 2 maps them; Task 4 subscribes.

- [ ] **Step 1: Write the failing tests**

Append inside the existing `OrchestratorTests` class in `host/DeadAir.Core.Tests/OrchestratorTests.cs`. `CleanupResult` is `record CleanupResult(string Text, bool Skipped, string? Reason)` — the positional form below is correct and matches neighbouring tests.

```csharp
    private static List<FlowOutcome> Watch(Orchestrator o)
    {
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);
        return seen;
    }

    [Fact]
    public async Task EmptyEvent_RaisesNothingHeard()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "empty" });
        Assert.Equal(new[] { FlowOutcome.NothingHeard }, seen);
    }

    [Fact]
    public async Task ErrorEvent_RaisesFailed()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        {
            Event = "error", Where = "asr", Message = "boom",
        });
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task SuccessfulInject_RaisesInjectedExactlyOnce()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        Assert.Equal(new[] { FlowOutcome.Injected }, seen);
    }

    [Fact]
    public async Task FailedInject_RaisesFailed()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(false), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task ThrowingInjector_StillRaisesFailedFromFinally()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new ThrowingInjector(), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task UtteranceTimeout_RaisesTimedOut()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        o.UtteranceTimeoutMs = 30;
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await Task.Delay(300);
        Assert.Equal(new[] { FlowOutcome.TimedOut }, seen);
    }

    [Fact]
    public async Task ReadyDuringTranscribing_RaisesInterrupted()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Equal(new[] { FlowOutcome.Interrupted }, seen);
    }

    [Fact]
    public async Task SupersededTail_RaisesNoOutcomeForTheNewRecording()
    {
        // Mirrors HandleFinal_TailDoesNotStompNewRecording: an unsolicited error
        // resets mid-cleanup, a NEW recording starts, and the old tail still
        // injects. The old utterance no longer owns the flow, so it must stay
        // silent — a late "sent" caption would retract the new recording's pill.
        var cl = new BlockingCleaner();
        var inj = new FakeInjector(true);
        var o = Make(new FakeSidecar(), cl, inj, new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "old words" });

        await o.OnSidecarEventAsync(new SidecarEvent
        {
            Event = "error", Where = "asr", Message = "unsolicited",
        });
        await o.OnHotkeyDownAsync();          // NEW recording now owns the flow
        var seen = Watch(o);                  // watch only from here on

        cl.Gate.SetResult(new CleanupResult("old words", false, null));
        await finalTask;

        Assert.Equal("old words", inj.Injected);   // words are never lost
        Assert.Empty(seen);                        // ...but the tail stays silent
        Assert.Equal(FlowState.Recording, o.State);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd "H:/DeadMind V.3/DeadAir"
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: compile error — `'Orchestrator' does not contain a definition for 'Outcome'`.

- [ ] **Step 3: Create the enum**

Create `host/DeadAir.Core/FlowOutcome.cs`:

```csharp
namespace DeadAir.Core;

/// <summary>How one utterance ended. FlowState cannot carry this: the empty
/// and error paths both land on FlowState.Idle, so a state-keyed UI would
/// caption an ASR error "nothing heard".</summary>
public enum FlowOutcome
{
    Injected,
    NothingHeard,
    Failed,
    TimedOut,
    Interrupted,
}
```

- [ ] **Step 4: Add the events and the guarded raise**

In `host/DeadAir.Core/Orchestrator.cs`, beside the existing `LatencyLogged` event:

```csharp
    public event Action<FlowOutcome>? Outcome;
    public event Action<CleanupMode, bool>? CleaningStarted;
```

Add a field beside `_degradedToastShown`:

```csharp
    private bool _outcomeRaised;
```

Add the helper. **The invoke must be outside `lock (_gate)`** — handlers marshal to the WPF dispatcher, and invoking under the state lock invites deadlock:

```csharp
    /// <summary>Raise at most one outcome per utterance, and only while that
    /// utterance still owns the flow. A superseded tail (see
    /// HandleFinal_TailDoesNotStompNewRecording) injects its words but stays
    /// silent, so its caption cannot retract a newer recording's pill.</summary>
    private void RaiseOutcome(FlowOutcome outcome, long utteranceId)
    {
        lock (_gate)
        {
            if (_utteranceId != utteranceId || _outcomeRaised) return;
            _outcomeRaised = true;
        }
        Outcome?.Invoke(outcome);
    }
```

Clear the flag when a new utterance begins. In `OnHotkeyUpAsync`, inside the existing `lock (_gate)` where `_utteranceId` is incremented:

```csharp
            _outcomeRaised = false;
```

- [ ] **Step 5: Raise from every terminal path**

Each site must capture the utterance id it belongs to, then call the helper outside the lock.

1. `ScheduleUtteranceTimeout` — it already has `utteranceId` in scope. After `notifier.Toast("ASR timed out");`:

```csharp
            RaiseOutcome(FlowOutcome.TimedOut, utteranceId);
```

2. `OnSidecarEventAsync`, `case "ready"` — capture the id **before** the increment inside the lock, and only raise when this reset actually abandoned an in-flight utterance (the existing code skips the reset while `Cleaning`/`Injecting`):

```csharp
            case "ready":
                long readyId;
                bool readyReset;
                lock (_gate)
                {
                    readyId = _utteranceId;
                    readyReset = State is not FlowState.Cleaning and not FlowState.Injecting
                                 && State != FlowState.Idle;
                    _utteranceId++;
                    if (State is not FlowState.Cleaning and not FlowState.Injecting)
                        SetState(FlowState.Idle);
                }
                if (readyReset) RaiseOutcome(FlowOutcome.Interrupted, readyId);
                break;
```

Note `RaiseOutcome` compares against the *incremented* `_utteranceId`, so pass `readyId` — the id the abandoned utterance owned — and see Step 6 for why the comparison still succeeds.

3. `case "empty"` and `case "error"` — same shape: capture the pre-increment id, raise after the lock.

```csharp
            case "empty":
                long emptyId;
                lock (_gate) { emptyId = _utteranceId; _utteranceId++; SetState(FlowState.Idle); }
                RaiseOutcome(FlowOutcome.NothingHeard, emptyId);
                break;
```

```csharp
            case "error":
                long errorId;
                lock (_gate) { errorId = _utteranceId; _utteranceId++; SetState(FlowState.Idle); }
                notifier.Toast($"Error ({e.Where}): {e.Message}");
                RaiseOutcome(FlowOutcome.Failed, errorId);
                break;
```

4. `HandleFinalAsync` — capture the owning id at entry, track whether the inject succeeded, and raise in `finally` so a throwing cleaner or injector still lands an outcome:

```csharp
        long myId;
        lock (_gate) myId = _utteranceId;
        var outcome = FlowOutcome.Failed;   // pessimistic: only success overwrites it
        try
        {
            // ... existing body ...
            var ok = await injector.InjectAsync(result.Text);
            if (!ok)
                notifier.Toast("Couldn't insert — text on clipboard, press Ctrl+V");
            outcome = ok ? FlowOutcome.Injected : FlowOutcome.Failed;
            // ... existing LatencyLogged ...
        }
        finally
        {
            lock (_gate)
            {
                if (State is FlowState.Cleaning or FlowState.Injecting)
                    SetState(FlowState.Idle);
            }
            RaiseOutcome(outcome, myId);
        }
```

5. Raise `CleaningStarted` at the **existing** snapshot site in `HandleFinalAsync`, immediately after `var translating = config.Cleanup.TranslationActive;` and before the `CleanAsync` await, so the caption uses exactly the values the operation was submitted with:

```csharp
            CleaningStarted?.Invoke(Mode, translating);
```

- [ ] **Step 6: Reconcile the id comparison**

`RaiseOutcome` rejects when `_utteranceId != utteranceId`, but the terminal paths above increment `_utteranceId` before raising, which would reject every legitimate outcome. Choose ONE consistent convention and apply it everywhere:

**Convention (use this):** capture the id, and do the `_utteranceId++` increment only where the existing code already does it, but pass `RaiseOutcome` the *post*-increment value at those sites — i.e. capture `myId` AFTER the increment inside the same lock:

```csharp
            case "empty":
                long emptyId;
                lock (_gate) { emptyId = ++_utteranceId; SetState(FlowState.Idle); }
                RaiseOutcome(FlowOutcome.NothingHeard, emptyId);
                break;
```

Apply the same `++_utteranceId` capture to `ready` and `error`. For `HandleFinalAsync`, capture `myId` at entry with no increment (the `final` case already incremented before calling it), so the tail's id goes stale exactly when a newer utterance increments past it — which is the ownership behavior the superseded-tail test pins. For `ScheduleUtteranceTimeout`, the existing `utteranceId` parameter is already the owning id; pass it unchanged.

If any test in Step 1 fails after this, the convention is inconsistent somewhere — fix the capture sites, do NOT weaken `RaiseOutcome`'s guard.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total **160** (152 baseline + 8 new), 0 failed. Every pre-existing test — especially `HandleFinal_TailDoesNotStompNewRecording` and the `ready`-reset test — must still pass untouched.

- [ ] **Step 8: Stop and report — the controller commits**

Report the verbatim test tail. Controller runs:

```bash
git add host/DeadAir.Core/FlowOutcome.cs host/DeadAir.Core/Orchestrator.cs host/DeadAir.Core.Tests/OrchestratorTests.cs
git commit -m "feat(core): utterance-owned FlowOutcome + CleaningStarted signals"
```

---

### Task 2: `PillStatus` pure caption mapping

**Files:**
- Create: `host/DeadAir.Core/PillStatus.cs`
- Test: `host/DeadAir.Core.Tests/PillStatusTests.cs`

**Interfaces:**
- Consumes: `FlowOutcome` (Task 1); `FlowState` and `CleanupMode` already exist in `DeadAir.Core`.
- Produces: `PillCaption(string Text, bool Dismiss)`, `PillStatus.ForState(FlowState) → PillCaption?`, `PillStatus.ForCleaning(CleanupMode, bool) → PillCaption`, `PillStatus.ForOutcome(FlowOutcome) → PillCaption`. Task 4 calls all three.

Note `ForState` takes **only** the state — the cleanup caption comes from `ForCleaning`, fed by `CleaningStarted`'s snapshot, because re-reading the live mode from an async hook can caption an operation that was never submitted.

- [ ] **Step 1: Write the failing test**

Create `host/DeadAir.Core.Tests/PillStatusTests.cs`:

```csharp
using DeadAir.Core;
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class PillStatusTests
{
    [Theory]
    [InlineData(FlowState.Recording)]
    [InlineData(FlowState.Idle)]
    [InlineData(FlowState.Cleaning)]
    public void StatesWithNoCaption(FlowState state) =>
        Assert.Null(PillStatus.ForState(state));

    [Fact]
    public void Transcribing_Captions()
    {
        var c = PillStatus.ForState(FlowState.Transcribing);
        Assert.Equal("transcribing…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Injecting_Captions()
    {
        var c = PillStatus.ForState(FlowState.Injecting);
        Assert.Equal("injecting…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Cleaning_WhileTranslating_SaysTranslating()
    {
        var c = PillStatus.ForCleaning(CleanupMode.Faithful, translating: true);
        Assert.Equal("translating…", c.Text);
        Assert.False(c.Dismiss);
    }

    [Fact]
    public void Cleaning_Polished_SaysPolishing() =>
        Assert.Equal("polishing…", PillStatus.ForCleaning(CleanupMode.Polished, false).Text);

    [Fact]
    public void Cleaning_Faithful_SaysCleaning() =>
        Assert.Equal("cleaning…", PillStatus.ForCleaning(CleanupMode.Faithful, false).Text);

    [Theory]
    [InlineData(FlowOutcome.Injected, "sent")]
    [InlineData(FlowOutcome.NothingHeard, "nothing heard")]
    [InlineData(FlowOutcome.Failed, "failed")]
    [InlineData(FlowOutcome.TimedOut, "timed out")]
    [InlineData(FlowOutcome.Interrupted, "interrupted")]
    public void Outcomes_CaptionAndAlwaysDismiss(FlowOutcome outcome, string text)
    {
        var c = PillStatus.ForOutcome(outcome);
        Assert.Equal(text, c.Text);
        Assert.True(c.Dismiss);
    }
}
```

That is 3 + 1 + 1 + 1 + 1 + 1 + 5 = **13 tests**.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: compile error — `The name 'PillStatus' does not exist`.

- [ ] **Step 3: Implement**

Create `host/DeadAir.Core/PillStatus.cs`:

```csharp
using DeadAir.Core.Config;

namespace DeadAir.Core;

/// <summary>A pill caption and whether it self-dismisses.</summary>
public readonly record struct PillCaption(string Text, bool Dismiss);

/// <summary>Maps flow states and terminal outcomes to pill captions. Pure and
/// WPF-free so it is unit-testable headless, matching ScopeGeometry.</summary>
public static class PillStatus
{
    /// <summary>Caption for an in-flight state, or null when the state carries
    /// no caption: Recording is the scope visual, Idle is owned by the terminal
    /// outcome caption, and Cleaning is captioned from CleaningStarted's
    /// snapshot instead (a live re-read can name an unsubmitted mode).</summary>
    public static PillCaption? ForState(FlowState state)
        => state switch
        {
            FlowState.Transcribing => new PillCaption("transcribing…", false),
            FlowState.Injecting => new PillCaption("injecting…", false),
            _ => null,
        };

    /// <summary>Caption for the cleanup phase, from the snapshot the operation
    /// was actually submitted with.</summary>
    public static PillCaption ForCleaning(CleanupMode mode, bool translating)
        => new(translating ? "translating…"
                : mode == CleanupMode.Polished ? "polishing…" : "cleaning…",
               false);

    /// <summary>Caption for a finished utterance. Always self-dismisses.</summary>
    public static PillCaption ForOutcome(FlowOutcome outcome)
        => outcome switch
        {
            FlowOutcome.Injected => new PillCaption("sent", true),
            FlowOutcome.NothingHeard => new PillCaption("nothing heard", true),
            FlowOutcome.TimedOut => new PillCaption("timed out", true),
            FlowOutcome.Interrupted => new PillCaption("interrupted", true),
            _ => new PillCaption("failed", true),
        };
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total **173** (160 + 13), 0 failed.

- [ ] **Step 5: Stop and report — the controller commits**

```bash
git add host/DeadAir.Core/PillStatus.cs host/DeadAir.Core.Tests/PillStatusTests.cs
git commit -m "feat(core): PillStatus caption mapping"
```

---

### Task 3: `RecordingIndicatorWindow.ShowStatus`

Lifted from `DeadEye.Shell/Voice/VoicePillWindow.ShowStatus`. This is a WPF window: per house convention it is **smoke-tested only**, no headless test. The focus-safety constraint is enforced by construction (reuse the existing `Show()` path) and verified in the Task 5 manual smoke.

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`

**Interfaces:**
- Consumes: the existing `SetPartial(string)`, `ShowIndicator()`, `HideIndicator()`, the `_lastPartial` field, and the `ScopeState` enum / `_state` field on this window.
- Produces: `public void ShowStatus(string text, bool dismiss)`. Task 4 calls it.

- [ ] **Step 1: Add the dismiss timer field**

The window currently has no timer. Beside the other private fields:

```csharp
    private readonly DispatcherTimer _statusTimer;
```

Add `using System.Windows.Threading;` if not already present.

- [ ] **Step 2: Initialise the timer in the constructor**

```csharp
        _statusTimer = new DispatcherTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); HideIndicator(); };
```

- [ ] **Step 3: Add `ShowStatus`**

```csharp
    /// <summary>Show a status caption on the pill. Self-shows if hidden OR
    /// mid-retract, so a caption can never arrive with no window to carry it
    /// and can never be swallowed by an in-flight retract. Never calls
    /// Activate(): the pill is visible during Ctrl+V injection, and taking
    /// focus would paste the user's dictation into the pill itself.</summary>
    public void ShowStatus(string text, bool dismiss)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowStatus(text, dismiss));
            return;
        }
        // IsVisible stays true for the whole 450 ms retract, so an
        // IsVisible-only check silently drops a caption landing mid-retract.
        if (!IsVisible || _state == ScopeState.Retracting) ShowIndicator();
        _lastPartial = "";          // caption renders whole, not diffed vs the last partial
        SetPartial(text);
        _statusTimer.Stop();
        _statusTimer.Interval = dismiss
            ? TimeSpan.FromMilliseconds(900)
            // 90 s, NOT 60 s: UtteranceTimeoutMs is 60 s, and a tie lets this
            // watchdog start the retract just before the TimedOut caption lands.
            : TimeSpan.FromMilliseconds(90_000);
        _statusTimer.Start();
    }
```

`_lastPartial = ""` matters: `SetPartial` runs `PartialText.LayoutInterim(_lastPartial, text, …)`, which diffs against the previous partial to colour dim-vs-hot. Without the reset, a caption arriving after live partial text renders as a diff of it.

- [ ] **Step 4: Stop the timer on re-show**

In `ShowIndicator()`, beside the existing `_lastPartial = "";` reset, so a new recording cannot inherit the previous utterance's pending dismissal:

```csharp
        _statusTimer.Stop();
```

Place it before the `_state = ScopeState.Igniting;` line. Note `ShowStatus` calls `ShowIndicator()` and then re-arms the timer, so this ordering is safe.

- [ ] **Step 5: Verify it builds**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors.

- [ ] **Step 6: Stop and report — the controller commits**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(pill): ShowStatus caption with retract-aware re-show"
```

---

### Task 4: Rewire the notifier state hook

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs` (the `TrayNotifier` construction around line 76, and after the `_orchestrator` assignment around line 97)

**Interfaces:**
- Consumes: `PillStatus.ForState` / `ForCleaning` / `ForOutcome` (Task 2), `Orchestrator.Outcome` and `Orchestrator.CleaningStarted` (Task 1), `RecordingIndicatorWindow.ShowStatus` (Task 3).
- Produces: nothing downstream.

- [ ] **Step 1: Replace the state lambda**

`App.xaml.cs` currently reads:

```csharp
        var notifier = new TrayNotifier(_tray, Dispatcher, state =>
        {
            if (state == FlowState.Recording) _indicator.ShowIndicator();
            else _indicator.HideIndicator();
        });
```

Replace with:

```csharp
        var notifier = new TrayNotifier(_tray, Dispatcher, state =>
        {
            if (state == FlowState.Recording) { _indicator.ShowIndicator(); return; }
            var caption = PillStatus.ForState(state);
            if (caption is { } c) _indicator.ShowStatus(c.Text, c.Dismiss);
            // Idle and Cleaning map to null and are deliberately ignored: the
            // terminal outcome caption owns dismissal (hiding here would stomp
            // "sent" instantly), and Cleaning is captioned from CleaningStarted.
        });
```

Add `using DeadAir.Core;` if `PillStatus` does not resolve.

- [ ] **Step 2: Subscribe to both events**

Directly after the existing `_orchestrator.LatencyLogged += …` registration:

```csharp
        _orchestrator.CleaningStarted += (mode, translating) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var c = PillStatus.ForCleaning(mode, translating);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });

        _orchestrator.Outcome += o => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var c = PillStatus.ForOutcome(o);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });
```

The `try/catch` mirrors the existing guard in `TrayNotifier.SetState`, which already swallows state-hook exceptions for the same reason.

- [ ] **Step 3: Verify it builds**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors.

- [ ] **Step 4: Stop and report — the controller commits**

```bash
git add host/DeadAir.App/App.xaml.cs
git commit -m "feat(app): pill rides transcribe/clean/inject via captions"
```

---

### Task 5: Full verification

**Files:** none modified.

- [ ] **Step 1: Full build**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors, and no new warnings attributable to this branch.

- [ ] **Step 2: Full suite**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: **173 passed, 0 failed**. A drop below 152 means a pre-existing test regressed — stop and report rather than adjusting the test.

- [ ] **Step 3: Prove the Recording visual is untouched — WITHOUT git**

The worker cannot run `git diff`. Hash the protected file instead and compare to the controller's pre-change value:

```bash
sha256sum host/DeadAir.Core/ScopeGeometry.cs
```

Expected, byte-for-byte:

```
0c83bf2dfa313fe28d63a2df5eec37e8968c2134afba874b941c9567e1710cb4
```

If the hash differs, `ScopeGeometry.cs` was modified — stop and report immediately; that is a hard-constraint violation.

Then confirm `RecordingIndicatorWindow.xaml.cs` gained only the four intended edits (timer field, constructor init, `ShowStatus`, one `_statusTimer.Stop()` in `ShowIndicator`) by listing every line you changed in that file in your report. Do not paraphrase — quote them.

- [ ] **Step 4: Report for manual smoke**

Do NOT run the app yourself — hand these to the controller, who runs them with the user. **The controller must rebuild first: the user's running DeadAir has been observed to be a stale Debug binary predating recent merges.**

1. Hold hotkey, speak, release → caption walks `transcribing… → translating… → sent`, then retracts on its own after ~900 ms.
2. Translate toggle off, Polished mode → `polishing…`. Faithful mode → `cleaning…`.
3. Hold hotkey, stay silent, release → `nothing heard`, self-dismisses.
4. Stop Ollama, dictate → the existing "translation skipped" toast still fires AND the pill still lands on `sent`.
5. **Focus check (the one that matters):** put the cursor in Notepad, dictate, confirm the text lands in Notepad. The pill is on screen during the paste; if it ever took focus the text would vanish into the pill.
6. Start a new recording immediately after a `sent` caption → the new recording's scope shows, no stale caption, no early hide.
7. Flip the translate toggle *during* an in-flight cleanup → the caption must describe the operation that was actually submitted, not the new toggle position.

---

## Notes for the implementer

- Do not "fix" the fact that `FlowState.Idle` maps to `null`. That is load-bearing: `Orchestrator` calls `SetState(Idle)` from the same terminal paths that raise `Outcome`, and both marshal through the dispatcher. Because `Idle` is ignored, a late `Idle` cannot clear a caption already shown, and arrival order stops mattering.
- Do not weaken `RaiseOutcome`'s ownership guard to make a test pass. The guard is the fix for the review blocker; if a test fails, the id-capture convention is wrong at a call site.
- Do not widen `IUserNotifier` to carry the outcome. Every fake in `OrchestratorTests` implements it.
- `InjectAsync` returning true means the paste was *sent*, not that the target app received it (`ClipboardPasteInjector` returns after the synthetic Ctrl+V). "sent" is the honest caption; do not reword it to imply confirmed delivery.
- If a step's real code disagrees with a snippet here, follow the real code and say so in the task report — the snippets were transcribed from the tree at spec time.
