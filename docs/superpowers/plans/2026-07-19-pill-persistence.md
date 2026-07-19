# DeadAir Pill Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the DeadAir recording pill on screen after hotkey release, captioned with the phase it is in (`transcribing… → translating… → sent`), instead of vanishing through the multi-second Ollama call.

**Architecture:** `Orchestrator` gains two additive events — `Outcome` (raised unconditionally at each terminal path) and `CleaningStarted` (mode + translating). A pure `PillStatus` maps those to captions **and** owns the suppression predicate. `RecordingIndicatorWindow` gains `ShowStatus`, lifted from DeadEye's `VoicePillWindow`. `App.xaml.cs` tracks the last `FlowState` and drops any terminal caption that would land during a live recording. All decision logic is in the WPF-free Core and unit-tested headless; the WPF window stays smoke-only.

**Tech Stack:** .NET 8, WPF, xunit 2.5.3, Hardcodet TaskbarIcon.

**Spec:** `docs/superpowers/specs/2026-07-19-pill-persistence-design.md`

> **Revision note — read before starting.** Two earlier drafts of this plan were killed by
> adversarial review. The second tried to give `Orchestrator` per-utterance *ownership* of
> outcomes via `_utteranceId`; that produced four blockers because `_utteranceId` cannot
> express UI lifetime (`OnHotkeyDownAsync` never advances it, and a `ready` mid-`Cleaning`
> advances it while preserving state). **Core no longer does any ownership checking.**
> Suppression lives in the App layer. Do not reintroduce a `RaiseOutcome` guard or an
> `_outcomeRaised` flag — see the spec's "Design history" note for why they cannot work.

## Global Constraints

- Branch: `feat/pill-persistence`. Do not merge to master.
- The `Recording` visual is byte-identical when done: no edit to the lantern/nebula scope geometry, energy follower, ignition, retract, or any `ScopeGeometry` math.
- `IUserNotifier` is NOT widened. `TrayNotifier` and every test fake implement it; adding a member breaks them all.
- No new config. The behavior is unconditional — no `AppConfig` change.
- No new XAML elements. `RecordingIndicatorWindow.xaml` is not edited at all. Captions write through the existing `InterimText` `TextBlock` via `SetPartial`.
- The pill must never call `Activate()`. It is a `WS_EX_NOACTIVATE` tool window and is now visible during `Ctrl+V` injection; stealing focus would paste the user's dictation into the pill.
- **The implementing worker must NOT run ANY `git` command — not even read-only.** The Codex sandbox writes Deny ACEs on `.git` by design. Every verification step here is achievable without git. The `git` commands in commit steps are for the controller only.
- Build with `--no-restore` (the sandbox cannot read `NuGet.Config`). The controller has already restored.
- Never pass a `.cs` file to `dotnet test` — that yields `MSB1008`. Pass the `.csproj`.
- Baseline suite before any change: **152 passing**, verified by the controller. It must not drop.
- Use forward slashes in every path.

---

### Task 1: `FlowOutcome` + `CleaningStarted` on `Orchestrator`

`FlowState` cannot express how an utterance ended — `empty` and `error` both land on `Idle`. Core raises these signals **unconditionally**; deciding whether to *draw* them is the App's job (Task 4).

**Files:**
- Create: `host/DeadAir.Core/FlowOutcome.cs`
- Modify: `host/DeadAir.Core/Orchestrator.cs`
- Test: `host/DeadAir.Core.Tests/OrchestratorTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `public enum FlowOutcome { Injected, NothingHeard, Failed, TimedOut, Interrupted }`, `public event Action<FlowOutcome>? Outcome;`, `public event Action<CleanupMode, bool>? CleaningStarted;`.

- [ ] **Step 1: Write the failing tests**

Append inside the existing `OrchestratorTests` class. `CleanupResult` is `record CleanupResult(string Text, bool Skipped, string? Reason)`.

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
    public async Task SuccessfulInject_RaisesInjected()
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
        // The existing suite pins that this exception PROPAGATES
        // (HandleFinal_InjectorThrows_StillReturnsToIdle uses ThrowsAsync).
        // The outcome must still be raised from the finally before it escapes.
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new ThrowingInjector(), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" }));
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
    public async Task ReadyWhileIdle_RaisesNothing()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Empty(seen);
    }

    [Fact]
    public async Task ReadyMidCleaning_StillLetsTheFinalTailRaiseInjected()
    {
        // ready mid-Cleaning must not force Idle (pinned by
        // ReadyEvent_MidCleaning_DoesNotForceIdle) and must not swallow the
        // tail's outcome: the words still land, so "sent" must still be raised.
        var cl = new BlockingCleaner();
        var o = Make(new FakeSidecar(), cl, new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "hello" });
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        cl.Gate.SetResult(new CleanupResult("hello", false, null));
        await finalTask;
        Assert.Equal(new[] { FlowOutcome.Injected }, seen);
    }

    [Fact]
    public async Task CleaningStarted_ReportsModeAndTranslating()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.Mode = CleanupMode.Polished;
        var o = new Orchestrator(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(true), new FakeNotifier(), cfg);
        var seen = new List<(CleanupMode, bool)>();
        o.CleaningStarted += (m, t) => seen.Add((m, t));
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        var (mode, translating) = Assert.Single(seen);
        Assert.Equal(CleanupMode.Polished, mode);
        Assert.Equal(cfg.Cleanup.TranslationActive, translating);
    }
```

If `AppConfig`'s cleanup fields are not settable exactly as written above, construct the config the way neighbouring tests already do and adjust — report the difference rather than forcing this shape.

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
/// caption an ASR error "nothing heard".
///
/// Raised unconditionally. Deciding whether a given outcome should be DRAWN
/// (e.g. suppressing a superseded tail's caption during a live recording) is
/// the App layer's job -- see PillStatus.SuppressTerminal. Do not add
/// ownership or idempotence logic here; two review rounds proved this state
/// machine's _utteranceId cannot express UI lifetime.</summary>
public enum FlowOutcome
{
    Injected,
    NothingHeard,
    Failed,
    TimedOut,
    Interrupted,
}
```

- [ ] **Step 4: Add the events**

In `host/DeadAir.Core/Orchestrator.cs`, beside the existing `LatencyLogged` event:

```csharp
    public event Action<FlowOutcome>? Outcome;
    public event Action<CleanupMode, bool>? CleaningStarted;
```

- [ ] **Step 5: Raise from the terminal paths**

Every raise happens **outside** `lock (_gate)` — handlers marshal to the WPF dispatcher, and invoking under the state lock invites deadlock. No id capture, no flags.

1. `ScheduleUtteranceTimeout`, after the existing `notifier.Toast("ASR timed out");`:

```csharp
            Outcome?.Invoke(FlowOutcome.TimedOut);
```

2. `case "empty"`, after the existing lock block:

```csharp
                Outcome?.Invoke(FlowOutcome.NothingHeard);
```

3. `case "error"`, after the existing `notifier.Toast(...)`:

```csharp
                Outcome?.Invoke(FlowOutcome.Failed);
```

4. `case "ready"` — raise `Interrupted` only when this event actually abandoned an
   in-flight utterance. Capture that fact inside the existing lock; raise after it. Note
   the existing code deliberately does NOT reset while `Cleaning`/`Injecting`, and that
   path must stay silent so the final tail can report its own outcome:

```csharp
            case "ready":
                bool abandoned;
                lock (_gate)
                {
                    abandoned = State is FlowState.Recording or FlowState.Transcribing;
                    _utteranceId++;
                    if (State is not FlowState.Cleaning and not FlowState.Injecting)
                        SetState(FlowState.Idle);
                }
                if (abandoned) Outcome?.Invoke(FlowOutcome.Interrupted);
                break;
```

5. `HandleFinalAsync` — capture the mode **once** so the caption and the cleanup call
   cannot disagree, raise `CleaningStarted` at that same point, and raise the outcome from
   `finally` so a throwing cleaner or injector still reports. The existing throw must still
   propagate — do not swallow it:

```csharp
        var outcome = FlowOutcome.Failed;   // pessimistic; only success overwrites
        try
        {
            var asrMs = _clock.ElapsedMilliseconds;
            var translating = config.Cleanup.TranslationActive;
            var mode = Mode;                       // ONE read, used by both below
            CleaningStarted?.Invoke(mode, translating);
            var result = await cleaner.CleanAsync(e.Text ?? "", mode);
            // ... existing skip-toast, state advance, injection ...
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
            Outcome?.Invoke(outcome);
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total **162** (152 baseline + 10 new), 0 failed. Every pre-existing test must still pass untouched — especially `HandleFinal_TailDoesNotStompNewRecording`, `ReadyEvent_MidCleaning_DoesNotForceIdle`, and `HandleFinal_InjectorThrows_StillReturnsToIdle`.

- [ ] **Step 7: Stop and report — the controller commits**

```bash
git add host/DeadAir.Core/FlowOutcome.cs host/DeadAir.Core/Orchestrator.cs host/DeadAir.Core.Tests/OrchestratorTests.cs
git commit -m "feat(core): FlowOutcome + CleaningStarted signals"
```

---

### Task 2: `PillStatus` — captions and the suppression predicate

**Files:**
- Create: `host/DeadAir.Core/PillStatus.cs`
- Test: `host/DeadAir.Core.Tests/PillStatusTests.cs`

**Interfaces:**
- Consumes: `FlowOutcome` (Task 1); `FlowState`, `CleanupMode` already exist.
- Produces: `PillCaption(string Text, bool Dismiss)`, `PillStatus.ForState(FlowState) → PillCaption?`, `PillStatus.ForCleaning(CleanupMode, bool) → PillCaption`, `PillStatus.ForOutcome(FlowOutcome) → PillCaption`, `PillStatus.SuppressTerminal(FlowState) → bool`. Task 4 calls all four.

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

    [Theory]
    [InlineData(FlowState.Recording, true)]
    [InlineData(FlowState.Idle, false)]
    [InlineData(FlowState.Transcribing, false)]
    [InlineData(FlowState.Cleaning, false)]
    [InlineData(FlowState.Injecting, false)]
    public void SuppressTerminal_OnlyDuringRecording(FlowState last, bool expected) =>
        Assert.Equal(expected, PillStatus.SuppressTerminal(last));
}
```

That is 3 + 1 + 1 + 1 + 1 + 1 + 5 + 5 = **18 tests**.

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

/// <summary>Maps flow states and outcomes to pill captions, and decides when a
/// terminal caption must be withheld. Pure and WPF-free so it is unit-testable
/// headless, matching ScopeGeometry.</summary>
public static class PillStatus
{
    /// <summary>Caption for an in-flight state, or null when the state carries
    /// no caption: Recording is the scope visual, Idle is owned by the terminal
    /// outcome caption, and Cleaning is captioned from CleaningStarted.</summary>
    public static PillCaption? ForState(FlowState state)
        => state switch
        {
            FlowState.Transcribing => new PillCaption("transcribing…", false),
            FlowState.Injecting => new PillCaption("injecting…", false),
            _ => null,
        };

    /// <summary>Caption for the cleanup phase, from the values the operation
    /// was submitted with.</summary>
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

    /// <summary>True when a terminal caption must NOT be drawn: a superseded
    /// utterance's tail can still report an outcome while a NEW recording is
    /// live, and drawing it would overwrite that recording's scope and arm a
    /// dismissal that retracts its pill.</summary>
    public static bool SuppressTerminal(FlowState lastState)
        => lastState == FlowState.Recording;
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total **180** (162 + 18), 0 failed.

- [ ] **Step 5: Stop and report — the controller commits**

```bash
git add host/DeadAir.Core/PillStatus.cs host/DeadAir.Core.Tests/PillStatusTests.cs
git commit -m "feat(core): PillStatus captions + terminal suppression predicate"
```

---

### Task 3: `RecordingIndicatorWindow.ShowStatus`

Lifted from `DeadEye.Shell/Voice/VoicePillWindow.ShowStatus`. WPF window: smoke-tested only, no headless test. `RecordingIndicatorWindow.xaml` is NOT edited.

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`

**Interfaces:**
- Consumes: the existing `SetPartial(string)`, `ShowIndicator()`, `HideIndicator()`, the `_lastPartial` field, and the `ScopeState` enum / `_state` field.
- Produces: `public void ShowStatus(string text, bool dismiss)`.

- [ ] **Step 1: Add the dismiss timer field**

Beside the other private fields:

```csharp
    private readonly DispatcherTimer _statusTimer;
```

Add `using System.Windows.Threading;` if not already present.

- [ ] **Step 2: Initialise it in the constructor**

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

`_lastPartial = ""` matters: `SetPartial` runs `PartialText.LayoutInterim(_lastPartial, text, …)`, which diffs against the previous partial to colour dim-vs-hot. Without the reset a caption renders as a diff of live partial text.

- [ ] **Step 4: Stop the timer on re-show**

In `ShowIndicator()`, beside the existing `_lastPartial = "";` reset and before `_state = ScopeState.Igniting;`, so a new recording cannot inherit a pending dismissal:

```csharp
        _statusTimer.Stop();
```

`ShowStatus` calls `ShowIndicator()` and then re-arms the timer, so this ordering is safe.

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

### Task 4: Wire the App layer, including the suppression guard

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs` (the `TrayNotifier` construction around line 76, and after the `_orchestrator` assignment around line 97)

**Interfaces:**
- Consumes: `PillStatus.ForState` / `ForCleaning` / `ForOutcome` / `SuppressTerminal` (Task 2), `Orchestrator.Outcome` and `.CleaningStarted` (Task 1), `RecordingIndicatorWindow.ShowStatus` (Task 3).

- [ ] **Step 1: Add the last-state field**

Beside the other private fields on `App`:

```csharp
    private FlowState _lastFlowState = FlowState.Idle;
```

- [ ] **Step 2: Replace the state lambda**

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
            _lastFlowState = state;   // set on the dispatcher thread; read by the handlers below
            if (state == FlowState.Recording) { _indicator.ShowIndicator(); return; }
            var caption = PillStatus.ForState(state);
            if (caption is { } c) _indicator.ShowStatus(c.Text, c.Dismiss);
            // Idle and Cleaning map to null and are deliberately ignored: the
            // terminal outcome caption owns dismissal (hiding here would stomp
            // "sent" instantly), and Cleaning is captioned from CleaningStarted.
        });
```

Add `using DeadAir.Core;` if `PillStatus` does not resolve.

- [ ] **Step 3: Subscribe to both events, suppressing terminals during a recording**

Directly after the existing `_orchestrator.LatencyLogged += …` registration:

```csharp
        _orchestrator.CleaningStarted += (mode, translating) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (PillStatus.SuppressTerminal(_lastFlowState)) return;
                var c = PillStatus.ForCleaning(mode, translating);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });

        _orchestrator.Outcome += o => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // A superseded utterance's tail still reports its outcome. If a
                // NEW recording is already live, drawing it would overwrite that
                // recording's scope and retract its pill -- so drop it.
                if (PillStatus.SuppressTerminal(_lastFlowState)) return;
                var c = PillStatus.ForOutcome(o);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });
```

Both the state hook and these handlers run on the dispatcher, so `_lastFlowState` is written and read on one thread and the ordering is serialized — no lock needed, and no window where a stale caption survives into a live recording.

- [ ] **Step 4: Verify it builds**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors.

- [ ] **Step 5: Stop and report — the controller commits**

```bash
git add host/DeadAir.App/App.xaml.cs
git commit -m "feat(app): pill rides transcribe/clean/inject, suppressed during recording"
```

---

### Task 5: Full verification

**Files:** none modified.

- [ ] **Step 1: Full build**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors, no new warnings attributable to this branch.

- [ ] **Step 2: Full suite**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: **180 passed, 0 failed**. A drop below 152 means a pre-existing test regressed — stop and report rather than adjusting the test.

- [ ] **Step 3: Prove the protected visual is untouched — WITHOUT git**

The worker cannot run `git diff`. Hash both protected files and compare:

```bash
sha256sum host/DeadAir.Core/ScopeGeometry.cs host/DeadAir.App/RecordingIndicatorWindow.xaml
```

Expected, byte-for-byte:

```
0c83bf2dfa313fe28d63a2df5eec37e8968c2134afba874b941c9567e1710cb4  host/DeadAir.Core/ScopeGeometry.cs
0b93f3968cb40729d74ecf7f4e64cc7ce18bf9bf659ce6847563bf62a5120b92  host/DeadAir.App/RecordingIndicatorWindow.xaml
```

Either hash differing is a hard-constraint violation — stop and report immediately.

`RecordingIndicatorWindow.xaml.cs` cannot be hashed (Task 3 edits it), so instead **quote verbatim, in your report, every line you added or changed in that file**. It must be exactly four edits: the timer field, the constructor init, `ShowStatus`, and one `_statusTimer.Stop()` inside `ShowIndicator`. Any edit touching the render tick, ignition, retract, or energy follower is a violation.

- [ ] **Step 4: Report for manual smoke**

Do NOT run the app yourself. **The controller must rebuild before smoking** — the user's DeadAir was already found running a binary ~19 hours stale, which would silently invalidate the whole smoke.

1. Hold hotkey, speak, release → caption walks `transcribing… → translating… → sent`, retracts after ~900 ms.
2. Translate toggle off, Polished → `polishing…`; Faithful → `cleaning…`.
3. Hold hotkey, stay silent, release → `nothing heard`, self-dismisses.
4. Stop Ollama, dictate → existing "translation skipped" toast still fires AND the pill lands on `sent`.
5. **Focus check:** cursor in Notepad, dictate, confirm the text lands in Notepad. The pill is on screen during the paste; if it took focus the text would vanish into it.
6. Start a new recording immediately after a `sent` caption → new scope shows, no stale caption, no early hide.
7. **Suppression check:** dictate, and while cleanup is still running start a new recording. The old utterance's words must still be injected, and the new recording's pill must NOT be interrupted by a late `sent`.

---

## Notes for the implementer

- Do not reintroduce Core-side outcome ownership (`RaiseOutcome`, `_outcomeRaised`, captured `_utteranceId`). Two review rounds proved it cannot work here. Suppression lives in the App layer only.
- Do not "fix" `FlowState.Idle` mapping to `null`. It is load-bearing: `Idle` follows every terminal path, and hiding on it would stomp the outcome caption instantly.
- Do not widen `IUserNotifier`. Every fake in `OrchestratorTests` implements it.
- Do not swallow the injector exception in `HandleFinalAsync`. The existing suite pins that it propagates; the `finally` raises the outcome before it escapes.
- `InjectAsync` returning true means the paste was *sent*, not that the target app received it (`ClipboardPasteInjector` returns after the synthetic Ctrl+V). "sent" is the honest caption.
- The `translating` value in `CleaningStarted` is best-effort, not a guarantee: `OllamaClient` and `PromptBuilder` re-read `TranslationActive` later, so a mid-flight toggle can still make caption and prompt disagree. The existing toast has the same property. Do not claim otherwise in comments.
- If real code disagrees with a snippet here, follow the real code and say so in the report.
