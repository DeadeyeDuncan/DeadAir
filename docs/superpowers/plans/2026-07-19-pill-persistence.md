# DeadAir Pill Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the DeadAir recording pill on screen after hotkey release, captioned with the phase it is in (`transcribing… → translating… → sent`), instead of vanishing through the multi-second Ollama call.

**Architecture:** Two additions in the WPF-free `DeadAir.Core` — a `FlowOutcome` signal on `Orchestrator` (because `FlowState` collapses "nothing heard" and "error" onto `Idle`) and a pure `PillStatus` mapping — plus a `ShowStatus` method on `RecordingIndicatorWindow` lifted from DeadEye's `VoicePillWindow`, and a rewire of the notifier state hook in `App.xaml.cs`. All decision logic lives in Core and is unit-tested headless; the WPF window stays smoke-only.

**Tech Stack:** .NET 8, WPF, xunit 2.5.3, Hardcodet TaskbarIcon.

**Spec:** `docs/superpowers/specs/2026-07-19-pill-persistence-design.md`

## Global Constraints

- Branch: `feat/pill-persistence`. Do not merge to master.
- The `Recording` visual is byte-identical when done: no edit to the lantern/nebula scope geometry, energy follower, ignition, retract, or any `ScopeGeometry` math.
- `IUserNotifier` is NOT widened. `TrayNotifier` and every test fake implement it; adding a member breaks them all.
- No new config. The behavior is unconditional — no `AppConfig` change.
- No new XAML elements. Captions write through the existing `InterimText` `TextBlock` via `SetPartial`.
- The pill must never call `Activate()`. It is a `WS_EX_NOACTIVATE` tool window and is now visible during `Ctrl+V` injection; stealing focus would paste the user's dictation into the pill.
- Build with `--no-restore` (the Codex sandbox cannot read `NuGet.Config`).
- Never pass a `.cs` file to `dotnet test` — that yields `MSB1008`. Pass the `.csproj`.
- Baseline suite before any change: 152 passing. It must not drop.

---

### Task 1: `FlowOutcome` signal on `Orchestrator`

`FlowState` cannot express how an utterance ended — the `empty` event and the `error` event both call `SetState(FlowState.Idle)` from `Transcribing`. A caption keyed on state alone would label an ASR error "nothing heard". This task adds an additive event; no existing signature changes.

**Files:**
- Create: `host/DeadAir.Core/FlowOutcome.cs`
- Modify: `host/DeadAir.Core/Orchestrator.cs`
- Test: `host/DeadAir.Core.Tests/OrchestratorTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `public enum FlowOutcome { Injected, NothingHeard, Failed, TimedOut }` and `public event Action<FlowOutcome>? Outcome;` on `Orchestrator`. Task 2 maps the enum; Task 4 subscribes to the event.

- [ ] **Step 1: Write the failing tests**

Append to `host/DeadAir.Core.Tests/OrchestratorTests.cs`, inside the existing `OrchestratorTests` class:

```csharp
    [Fact]
    public async Task EmptyEvent_RaisesNothingHeard()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "empty" });

        Assert.Equal(new[] { FlowOutcome.NothingHeard }, seen);
    }

    [Fact]
    public async Task ErrorEvent_RaisesFailed()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);

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
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(true), n);
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });

        Assert.Equal(new[] { FlowOutcome.Injected }, seen);
    }

    [Fact]
    public async Task FailedInject_RaisesFailed()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(false), n);
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });

        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task UtteranceTimeout_RaisesTimedOut()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        o.UtteranceTimeoutMs = 30;
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await Task.Delay(300);

        Assert.Equal(new[] { FlowOutcome.TimedOut }, seen);
    }
```

`CleanupResult` is `record CleanupResult(string Text, bool Skipped, string? Reason)` — the positional form above is correct and matches the neighbouring tests.

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
}
```

- [ ] **Step 4: Raise the event from the four terminal paths**

In `host/DeadAir.Core/Orchestrator.cs`, beside the existing `LatencyLogged` event:

```csharp
    public event Action<FlowOutcome>? Outcome;
```

Raise it at each terminal point, always **outside** the `lock (_gate)` — handlers marshal to the WPF dispatcher and must never run under the state lock:

1. In `ScheduleUtteranceTimeout`, after `notifier.Toast("ASR timed out");`:

```csharp
            Outcome?.Invoke(FlowOutcome.TimedOut);
```

2. In `OnSidecarEventAsync`, `case "empty"`, after the lock block:

```csharp
                Outcome?.Invoke(FlowOutcome.NothingHeard);
```

3. In `OnSidecarEventAsync`, `case "error"`, after the existing `notifier.Toast(...)`:

```csharp
                Outcome?.Invoke(FlowOutcome.Failed);
```

4. In `HandleFinalAsync`, immediately after the `injector.InjectAsync` result is known — the paste either reached the user's app or did not:

```csharp
            var ok = await injector.InjectAsync(result.Text);
            if (!ok)
                notifier.Toast("Couldn't insert — text on clipboard, press Ctrl+V");
            Outcome?.Invoke(ok ? FlowOutcome.Injected : FlowOutcome.Failed);
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total count 157 (152 baseline + 5 new), 0 failed.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.Core/FlowOutcome.cs host/DeadAir.Core/Orchestrator.cs host/DeadAir.Core.Tests/OrchestratorTests.cs
git commit -m "feat(core): FlowOutcome signal for terminal utterance results"
```

---

### Task 2: `PillStatus` pure caption mapping

**Files:**
- Create: `host/DeadAir.Core/PillStatus.cs`
- Test: `host/DeadAir.Core.Tests/PillStatusTests.cs`

**Interfaces:**
- Consumes: `FlowOutcome` from Task 1; `FlowState` and `CleanupMode` already exist in `DeadAir.Core`.
- Produces: `PillCaption(string Text, bool Dismiss)`, `PillStatus.ForState(FlowState, CleanupMode, bool) → PillCaption?`, `PillStatus.ForOutcome(FlowOutcome) → PillCaption`. Task 4 calls both.

- [ ] **Step 1: Write the failing test**

Create `host/DeadAir.Core.Tests/PillStatusTests.cs`:

```csharp
using DeadAir.Core;
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class PillStatusTests
{
    [Fact]
    public void Recording_HasNoCaption() =>
        Assert.Null(PillStatus.ForState(FlowState.Recording, CleanupMode.Polished, false));

    [Fact]
    public void Idle_HasNoCaption() =>
        Assert.Null(PillStatus.ForState(FlowState.Idle, CleanupMode.Polished, false));

    [Fact]
    public void Transcribing_Captions()
    {
        var c = PillStatus.ForState(FlowState.Transcribing, CleanupMode.Polished, false);
        Assert.Equal("transcribing…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Cleaning_WhileTranslating_SaysTranslating()
    {
        var c = PillStatus.ForState(FlowState.Cleaning, CleanupMode.Faithful, true);
        Assert.Equal("translating…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Cleaning_Polished_SaysPolishing()
    {
        var c = PillStatus.ForState(FlowState.Cleaning, CleanupMode.Polished, false);
        Assert.Equal("polishing…", c!.Value.Text);
    }

    [Fact]
    public void Cleaning_Faithful_SaysCleaning()
    {
        var c = PillStatus.ForState(FlowState.Cleaning, CleanupMode.Faithful, false);
        Assert.Equal("cleaning…", c!.Value.Text);
    }

    [Fact]
    public void Injecting_Captions()
    {
        var c = PillStatus.ForState(FlowState.Injecting, CleanupMode.Polished, false);
        Assert.Equal("injecting…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Theory]
    [InlineData(FlowOutcome.Injected, "sent")]
    [InlineData(FlowOutcome.NothingHeard, "nothing heard")]
    [InlineData(FlowOutcome.Failed, "failed")]
    [InlineData(FlowOutcome.TimedOut, "timed out")]
    public void Outcomes_CaptionAndAlwaysDismiss(FlowOutcome outcome, string text)
    {
        var c = PillStatus.ForOutcome(outcome);
        Assert.Equal(text, c.Text);
        Assert.True(c.Dismiss);
    }
}
```

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
    /// <summary>Caption for an in-flight state, or null when the pill should
    /// show no caption: Recording is the scope visual, and Idle is owned by
    /// the terminal outcome caption.</summary>
    public static PillCaption? ForState(FlowState state, CleanupMode mode, bool translating)
        => state switch
        {
            FlowState.Transcribing => new PillCaption("transcribing…", false),
            FlowState.Cleaning => new PillCaption(
                translating ? "translating…"
                    : mode == CleanupMode.Polished ? "polishing…" : "cleaning…",
                false),
            FlowState.Injecting => new PillCaption("injecting…", false),
            _ => null,
        };

    /// <summary>Caption for a finished utterance. Always self-dismisses.</summary>
    public static PillCaption ForOutcome(FlowOutcome outcome)
        => outcome switch
        {
            FlowOutcome.Injected => new PillCaption("sent", true),
            FlowOutcome.NothingHeard => new PillCaption("nothing heard", true),
            FlowOutcome.TimedOut => new PillCaption("timed out", true),
            _ => new PillCaption("failed", true),
        };
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: PASS, total 166 (157 + 9), 0 failed.

- [ ] **Step 5: Commit**

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
- Consumes: the existing `SetPartial(string)`, `ShowIndicator()`, `HideIndicator()` on this window.
- Produces: `public void ShowStatus(string text, bool dismiss)`. Task 4 calls it.

- [ ] **Step 1: Add the dismiss timer field**

The window currently has no timer. Beside the other private fields in `RecordingIndicatorWindow.xaml.cs`:

```csharp
    private readonly DispatcherTimer _statusTimer;
```

Add `using System.Windows.Threading;` if not already present.

- [ ] **Step 2: Initialise the timer in the constructor**

In the constructor, after the existing field setup:

```csharp
        _statusTimer = new DispatcherTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); HideIndicator(); };
```

The interval is set per call in `ShowStatus` — a self-dismissing caption uses 900 ms (DeadEye's value); a persistent caption arms a 60 s watchdog so a flow that never reaches a terminal outcome cannot strand the pill on screen forever.

- [ ] **Step 3: Add `ShowStatus`**

```csharp
    /// <summary>Show a status caption on the pill. Self-shows if hidden, so a
    /// caption can never arrive with no window to carry it. Never calls
    /// Activate(): the pill is visible during Ctrl+V injection, and taking
    /// focus would paste the user's dictation into the pill itself.</summary>
    public void ShowStatus(string text, bool dismiss)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowStatus(text, dismiss));
            return;
        }
        if (!IsVisible) ShowIndicator();
        _lastPartial = "";          // caption renders whole, not diffed vs the last partial
        SetPartial(text);
        _statusTimer.Stop();
        _statusTimer.Interval = dismiss
            ? TimeSpan.FromMilliseconds(900)
            : TimeSpan.FromMilliseconds(60_000);   // watchdog: never strand the pill
        _statusTimer.Start();
    }
```

`_lastPartial = ""` matters: `SetPartial` runs `PartialText.LayoutInterim(_lastPartial, text, …)`, which diffs against the previous partial to colour dim-vs-hot. Without the reset, a caption arriving after live partial text would render as a partial diff of it.

- [ ] **Step 4: Stop the timer on re-show**

In `ShowIndicator()`, beside the existing `_lastPartial = "";` reset, so a new recording cannot inherit the previous utterance's pending dismissal:

```csharp
        _statusTimer.Stop();
```

- [ ] **Step 5: Verify it builds**

```bash
dotnet build host/DeadAir.slnx --no-restore
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(pill): ShowStatus caption with dismiss timer"
```

---

### Task 4: Rewire the notifier state hook

**Files:**
- Modify: `host/DeadAir.App/App.xaml.cs:76-79` (the `TrayNotifier` construction and its state lambda) and after line 97 (the `_orchestrator` assignment).

**Interfaces:**
- Consumes: `PillStatus.ForState` / `ForOutcome` (Task 2), `Orchestrator.Outcome` (Task 1), `RecordingIndicatorWindow.ShowStatus` (Task 3).
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
            // Mode lives on the orchestrator, not the config: the tray "Polished"
            // toggle mutates Orchestrator.Mode, so a config read captions a stale
            // mode. _orchestrator is a field, so this reads it at invoke time —
            // long after it is assigned below.
            var mode = _orchestrator is not null ? _orchestrator.Mode : _config.Cleanup.Mode;
            var caption = PillStatus.ForState(state, mode, _config.Cleanup.TranslationActive);
            if (caption is { } c) _indicator.ShowStatus(c.Text, c.Dismiss);
            // Idle maps to null and is deliberately ignored: the terminal outcome
            // caption owns dismissal. Hiding here would stomp "sent" instantly.
        });
```

- [ ] **Step 2: Subscribe to `Outcome`**

Directly after the existing `_orchestrator.LatencyLogged += …` registration:

```csharp
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

Expected: 0 errors. If `PillStatus` is unresolved, add `using DeadAir.Core;`.

- [ ] **Step 4: Commit**

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

Expected: 0 errors, 0 warnings introduced by this branch.

- [ ] **Step 2: Full suite**

```bash
dotnet test host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj --no-restore
```

Expected: 166 passed, 0 failed. A drop below 152 means an existing test regressed — stop and report rather than adjusting the test.

- [ ] **Step 3: Confirm the Recording visual is untouched**

```bash
git diff master --stat
```

Expected: no changes to `ScopeGeometry.cs`. The only `RecordingIndicatorWindow.xaml.cs` changes are the timer field, its constructor init, `ShowStatus`, and one `_statusTimer.Stop()` line in `ShowIndicator`. Confirm no scope-geometry or render-tick lines moved.

- [ ] **Step 4: Report for manual smoke**

Do NOT run the app yourself — hand these to the controller, who runs them with the user:

1. Hold hotkey, speak, release → caption walks `transcribing… → translating… → sent`, then retracts on its own after ~900 ms.
2. Translate toggle off, Polished mode → `polishing…`. Faithful mode → `cleaning…`.
3. Hold hotkey, stay silent, release → `nothing heard`, self-dismisses.
4. Stop Ollama, dictate → the existing "translation skipped" toast still fires AND the pill still lands on `sent`.
5. **Focus check (the one that matters):** put the cursor in Notepad, dictate, confirm the text lands in Notepad. The pill is on screen during the paste; if it ever took focus the text would vanish into the pill.
6. Start a new recording immediately after a `sent` caption → the new recording's scope shows, no stale caption, no early hide.

---

## Notes for the implementer

- Do not "fix" the fact that `FlowState.Idle` maps to `null`. That is load-bearing: `Orchestrator` calls `SetState(Idle)` from the same terminal paths that raise `Outcome`, and both marshal through the dispatcher. Because `Idle` is ignored, a late `Idle` cannot clear a caption already shown, and arrival order stops mattering.
- Do not widen `IUserNotifier` to carry the outcome. Every fake in `OrchestratorTests` implements it.
- If a step's real code disagrees with a snippet here, follow the real code and say so in the task report — the snippets were transcribed from the tree at spec time.
