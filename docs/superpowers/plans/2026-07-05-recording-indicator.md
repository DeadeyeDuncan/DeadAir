# DeadAir Recording Indicator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A frameless bottom-center pill window whose 24 bars dance to the real microphone level while hold-to-talk recording is active.

**Architecture:** The Python sidecar (owner of all PCM) computes a normalized RMS level per ~40 ms capture block and streams throttled `{"event":"level","rms":0.42}` events over the existing stdout JSON-lines IPC. The C# host routes level events straight to a WPF pill window (bypassing the Orchestrator), and shows/hides the pill off the existing `FlowState` notification path. Spec: `../specs/2026-07-05-recording-indicator-design.md`.

**Tech Stack:** Python (numpy, existing `asr_sidecar` package), C#/.NET 8 WPF (existing DeadAir.App), xUnit + pytest.

## Global Constraints

- Level event shape EXACTLY: `{"event":"level","rms":<float 0.00-1.00, 2 decimals>}`; emitted ONLY between `start` and `stop`, throttled to ≥40 ms apart (~25 Hz max).
- Normalization curve verbatim from spec: `level = clip((log10(max(rms, 1e-4)) + 4) / 4, 0, 1)`.
- Level events must NEVER enter the Orchestrator, the FireAndForget path, or the log file — routed in App's EventReceived handler before any other dispatch.
- The pill window must NEVER take focus: `ShowActivated=False` AND `WS_EX_NOACTIVATE (0x08000000) | WS_EX_TOOLWINDOW (0x00000080)` set in `OnSourceInitialized`. Never call `Activate()`.
- Indicator failures must never break dictation: every show/hide/push wrapped so exceptions are swallowed (one optional log line).
- Level computation must never break capture: the sidecar's `on_block` hook is try/except-swallowed.
- Sidecar stdout remains protocol-only; Python logging stays on stderr.
- Commit messages: Task 15A `feat(sidecar): mic level events for recording indicator`; Task 15B `feat(app): recording indicator pill with live level bars`.

---

### Task 15A: Sidecar level events

**Files:**
- Create: `sidecar/asr_sidecar/levels.py`
- Modify: `sidecar/asr_sidecar/capture.py` (add `on_block` hook)
- Modify: `sidecar/asr_sidecar/__main__.py` (wire emitter in the `config` handler)
- Test: `sidecar/tests/test_levels.py`

**Interfaces:**
- Consumes: `asr_sidecar.ipc.emit(obj)` (existing); `MicCapture._on_frames(mono)` internal frame path (existing).
- Produces: `rms_to_level(block: np.ndarray) -> float`; `LevelEmitter(emit_fn, min_interval_ms=40, now_fn=time.monotonic)` with method `on_block(block: np.ndarray) -> None`; `MicCapture.on_block: callable | None` attribute invoked with each mono block while recording. Task 15B relies on the exact event shape from Global Constraints.

- [ ] **Step 1: Write the failing tests** — `sidecar/tests/test_levels.py`:

```python
import numpy as np
from asr_sidecar.capture import MicCapture
from asr_sidecar.levels import LevelEmitter, rms_to_level


def test_rms_to_level_silence_is_zero():
    assert rms_to_level(np.zeros(640, dtype=np.float32)) == 0.0


def test_rms_to_level_full_scale_is_one():
    assert rms_to_level(np.ones(640, dtype=np.float32)) == 1.0


def test_rms_to_level_midrange():
    # rms = 0.01 -> (log10(0.01) + 4) / 4 = 0.5
    block = np.full(640, 0.01, dtype=np.float32)
    assert abs(rms_to_level(block) - 0.5) < 0.01


def test_emitter_throttles_and_shapes_event():
    events = []
    clock = [0.0]
    em = LevelEmitter(events.append, min_interval_ms=40,
                      now_fn=lambda: clock[0])
    block = np.full(640, 0.01, dtype=np.float32)
    em.on_block(block)          # t=0 -> emits
    clock[0] = 0.020
    em.on_block(block)          # 20ms later -> throttled
    clock[0] = 0.045
    em.on_block(block)          # 45ms later -> emits
    assert len(events) == 2
    assert events[0] == {"event": "level", "rms": 0.5}


def test_capture_on_block_fires_only_while_recording_and_swallows_errors():
    cap = MicCapture()
    seen = []
    cap.on_block = seen.append
    cap._on_frames(np.ones(160, dtype=np.float32))       # not recording
    cap._recording = True
    cap._on_frames(np.ones(160, dtype=np.float32))       # recording
    assert len(seen) == 1

    def boom(_):
        raise RuntimeError("must not escape")
    cap.on_block = boom
    cap._on_frames(np.ones(160, dtype=np.float32))       # swallowed
    assert len(cap.stop()) == 320                        # frames intact
```

- [ ] **Step 2: Run to verify failure**

Run: `cd "H:\DeadMind V.3\DeadAir\sidecar"; .venv\Scripts\python -m pytest tests/test_levels.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'asr_sidecar.levels'`.

- [ ] **Step 3: Implement** — `sidecar/asr_sidecar/levels.py`:

```python
"""Mic-level events for the host's recording indicator (spec: recording-indicator design)."""
import time

import numpy as np


def rms_to_level(block: np.ndarray) -> float:
    """Map a float32 [-1,1] block to a display level in [0,1].

    Log-ish curve so normal speech spans the visual range:
    level = clip((log10(max(rms, 1e-4)) + 4) / 4, 0, 1).
    """
    if len(block) == 0:
        return 0.0
    rms = float(np.sqrt(np.mean(np.square(block, dtype=np.float64))))
    level = (np.log10(max(rms, 1e-4)) + 4.0) / 4.0
    return float(np.clip(level, 0.0, 1.0))


class LevelEmitter:
    """Throttled level-event emitter; at most one event per min_interval_ms."""

    def __init__(self, emit_fn, min_interval_ms: int = 40,
                 now_fn=time.monotonic):
        self._emit = emit_fn
        self._interval = min_interval_ms / 1000.0
        self._now = now_fn
        self._last = float("-inf")

    def on_block(self, block: np.ndarray) -> None:
        now = self._now()
        if now - self._last < self._interval:
            return
        self._last = now
        self._emit({"event": "level", "rms": round(rms_to_level(block), 2)})
```

Modify `sidecar/asr_sidecar/capture.py` — in `MicCapture.__init__` add:

```python
        self.on_block = None  # optional per-block hook (level events); never breaks capture
```

and replace `_on_frames` with:

```python
    def _on_frames(self, mono: np.ndarray) -> None:
        with self._lock:
            recording = self._recording
            if recording:
                self._frames.append(mono)
        if recording and self.on_block is not None:
            try:
                self.on_block(mono)
            except Exception:
                pass  # level emission must never break capture
```

Modify `sidecar/asr_sidecar/__main__.py` — add import `from .levels import LevelEmitter`, and in the `config` handler, right after `cap = MicCapture(cfg.mic)`:

```python
                cap.on_block = LevelEmitter(emit).on_block
```

- [ ] **Step 4: Run to verify pass**

Run: `.venv\Scripts\python -m pytest tests/test_levels.py -v` — Expected: 5 PASS.

- [ ] **Step 5: Full sidecar suite**

Run: `.venv\Scripts\python -m pytest tests -v` — Expected: all pass (prior 18 + 5 new = 23, 1 skip for GPU without env vars), no regressions.

- [ ] **Step 6: Commit**

```bash
git add sidecar
git commit -m "feat(sidecar): mic level events for recording indicator"
```

---

### Task 15B: Host pill window + wiring

**Files:**
- Create: `host/DeadAir.Core/LevelRingBuffer.cs`, `host/DeadAir.App/RecordingIndicatorWindow.xaml`, `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`
- Modify: `host/DeadAir.Core/Sidecar/SidecarEvent.cs` (add Rms), `host/DeadAir.App/TrayNotifier.cs` (optional state hook), `host/DeadAir.App/App.xaml.cs` (route level events; create/show/hide indicator)
- Test: `host/DeadAir.Core.Tests/LevelRingBufferTests.cs`, add one case to `host/DeadAir.Core.Tests/SidecarManagerTests.cs`-adjacent parsing (new file section below puts it in LevelRingBufferTests.cs for simplicity)

**Interfaces:**
- Consumes: `SidecarEvent` (Task 8), `TrayNotifier(TaskbarIcon, Dispatcher)` (Task 13), `FlowState` (Task 12), level events from Task 15A.
- Produces: `LevelRingBuffer(int size)` with `void Push(double v)`, `void Reset()`, `IReadOnlyList<double> Values` (oldest→newest, always `size` long, floor 0.0). `RecordingIndicatorWindow` with `void Push(double level)`, `void ShowIndicator()`, `void HideIndicator()`. `TrayNotifier` gains optional `Action<FlowState>? stateHook` ctor param invoked inside its BeginInvoke. `SidecarEvent.Rms` (`double?`, JSON `rms`).

- [ ] **Step 1: Write the failing tests** — `host/DeadAir.Core.Tests/LevelRingBufferTests.cs`:

```csharp
using System.Text.Json;
using DeadAir.Core;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class LevelRingBufferTests
{
    [Fact]
    public void StartsAtFloor_PushShiftsLeft()
    {
        var buf = new LevelRingBuffer(4);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, buf.Values);
        buf.Push(0.5);
        buf.Push(0.9);
        Assert.Equal(new[] { 0.0, 0.0, 0.5, 0.9 }, buf.Values);
    }

    [Fact]
    public void Reset_ReturnsToFloor()
    {
        var buf = new LevelRingBuffer(3);
        buf.Push(1.0);
        buf.Reset();
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, buf.Values);
    }

    [Fact]
    public void SidecarEvent_ParsesRms()
    {
        var e = JsonSerializer.Deserialize<SidecarEvent>(
            "{\"event\":\"level\",\"rms\":0.42}");
        Assert.Equal("level", e!.Event);
        Assert.Equal(0.42, e.Rms!.Value, 3);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd "H:\DeadMind V.3\DeadAir\host"; dotnet test --filter LevelRingBufferTests`
Expected: compile FAIL (`LevelRingBuffer` missing, `Rms` missing).

- [ ] **Step 3: Implement Core pieces**

`host/DeadAir.Core/LevelRingBuffer.cs`:

```csharp
namespace DeadAir.Core;

/// <summary>Fixed-size rolling buffer of display levels (oldest→newest).</summary>
public sealed class LevelRingBuffer(int size)
{
    private readonly double[] _values = new double[size];

    public IReadOnlyList<double> Values => _values;

    public void Push(double v)
    {
        Array.Copy(_values, 1, _values, 0, _values.Length - 1);
        _values[^1] = v;
    }

    public void Reset() => Array.Clear(_values);
}
```

`host/DeadAir.Core/Sidecar/SidecarEvent.cs` — add one property alongside the existing ones:

```csharp
    [JsonPropertyName("rms")] public double? Rms { get; init; }
```

- [ ] **Step 4: Run Core tests to verify pass**

Run: `dotnet test --filter LevelRingBufferTests` — Expected: 3 PASS.

- [ ] **Step 5: Implement the pill window**

`host/DeadAir.App/RecordingIndicatorWindow.xaml`:

```xml
<Window x:Class="DeadAir.App.RecordingIndicatorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="280" Height="48"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False"
        IsHitTestVisible="False" Focusable="False">
    <Border CornerRadius="24" Background="#E6101418">
        <Canvas x:Name="BarCanvas" Width="240" Height="40"
                HorizontalAlignment="Center" VerticalAlignment="Center"/>
    </Border>
</Window>
```

`host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DeadAir.Core;

namespace DeadAir.App;

public partial class RecordingIndicatorWindow : Window
{
    private const int BarCount = 24;
    private const double BarWidth = 6, BarGap = 4, CanvasHeight = 40;
    private const double MinBarHeight = 4, MaxBarGrowth = 32;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    private readonly LevelRingBuffer _levels = new(BarCount);
    private readonly Rectangle[] _bars = new Rectangle[BarCount];

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        var brush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        brush.Freeze();
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = BarWidth,
                RadiusX = 3,
                RadiusY = 3,
                Fill = brush,
            };
            Canvas.SetLeft(_bars[i], i * (BarWidth + BarGap));
            BarCanvas.Children.Add(_bars[i]);
        }
        Render();
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

    public void Push(double level)
    {
        _levels.Push(level);
        Render();
    }

    public void ShowIndicator()
    {
        _levels.Reset();
        Render();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 16;
        Show(); // never Activate()
    }

    public void HideIndicator() => Hide();

    private void Render()
    {
        var values = _levels.Values;
        for (int i = 0; i < BarCount; i++)
        {
            var h = MinBarHeight + values[i] * MaxBarGrowth;
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (CanvasHeight - h) / 2);
        }
    }
}
```

(Add `using System.Windows.Controls;` if `Canvas` needs it.)

- [ ] **Step 6: Wire into TrayNotifier + App**

`host/DeadAir.App/TrayNotifier.cs` — extend with an optional state hook, invoked on the UI thread:

```csharp
using System.Windows.Threading;
using H.NotifyIcon;
using DeadAir.Core;

namespace DeadAir.App;

public sealed class TrayNotifier(TaskbarIcon tray, Dispatcher dispatcher,
    Action<FlowState>? stateHook = null) : IUserNotifier
{
    public void SetState(FlowState state) => dispatcher.BeginInvoke(() =>
    {
        tray.ToolTipText = $"DeadAir — {state}";
        try { stateHook?.Invoke(state); }
        catch { /* indicator failures never break the pipeline */ }
    });

    public void Toast(string message) => dispatcher.BeginInvoke(() =>
        tray.ShowNotification("DeadAir", message));
}
```

`host/DeadAir.App/App.xaml.cs` — three edits:

1. Field: `private RecordingIndicatorWindow _indicator = null!;`
2. In `OnStartup`, create the indicator BEFORE the notifier, then pass the hook:

```csharp
        _indicator = new RecordingIndicatorWindow();
        var notifier = new TrayNotifier(_tray, Dispatcher, state =>
        {
            if (state == FlowState.Recording) _indicator.ShowIndicator();
            else _indicator.HideIndicator();
        });
```

3. In the `EventReceived` subscription, route level events FIRST (they bypass Orchestrator/FireAndForget/log entirely):

```csharp
        _sidecar.EventReceived += ev =>
        {
            if (ev.Event == "level")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.Push(ev.Rms ?? 0); } catch { }
                });
                return;
            }
            FireAndForget(() => _orchestrator.OnSidecarEventAsync(ev),
                "sidecar-event");
        };
```

Also close the indicator on exit: in the exit handler's `finally`, before `Shutdown()`: `try { _indicator.Close(); } catch { }`.

- [ ] **Step 7: Build + full suite**

Run: `dotnet build DeadAir.slnx` (clean; known NU1701/NETSDK1057 warnings acceptable) then `dotnet test` — Expected: 33/33 (30 prior + 3 new).

- [ ] **Step 8: Mechanical launch check**

Launch the app, wait ~15 s, verify process + python child alive, then terminate cleanly (taskkill /T) and confirm no orphans. (Visual pill check — appears on hold, bars dance, no focus steal — is the USER's smoke step; do not claim it.)

- [ ] **Step 9: Commit**

```bash
git add host
git commit -m "feat(app): recording indicator pill with live level bars"
```

---

## Self-review notes

- **Spec coverage:** event shape/throttle/curve → 15A (tests pin all three); capture hook isolation → 15A test 5; Rms field, ring buffer, pill window, NOACTIVATE flags, bottom-center placement, show/hide on state, level-event bypass routing, exit close → 15B; error-isolation (hook try/except, stateHook try/catch, Push try/catch) → both. Manual visual check assigned to user smoke, consistent with the phase-0 pattern.
- **Placeholder scan:** none.
- **Type consistency:** `LevelRingBuffer.Values` IReadOnlyList<double> used by window Render; `SidecarEvent.Rms double?` used in App routing (`ev.Rms ?? 0`); `TrayNotifier` ctor extension keeps old 2-arg call sites valid via default param (App is the only call site and is updated); `MicCapture.on_block` name matches between capture.py, __main__.py, and tests.
