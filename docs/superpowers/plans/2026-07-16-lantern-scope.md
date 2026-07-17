# Lantern Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the recording pill's oscilloscope to DeadEye's Lantern trace look — phosphor double-stroke, warm tone, endpoint taper, breathing amplitude, ignition sweep on record start, retract on stop.

**Architecture:** All new math is pure static functions in `DeadAir.Core/ScopeGeometry.cs` (WPF-free, unit-tested — same pattern as `PartialText`). `RecordingIndicatorWindow` gains a second glow `Polyline`, a beam-pip `Ellipse`, and a four-state lifecycle (`Idle → Igniting → Live → Retracting`) driven by a `CompositionTarget.Rendering` tick that runs only while the pill is visible. Sidecar, protocol, ring buffer, and injection are untouched.

**Tech Stack:** .NET 8, WPF (App project), xunit 2.5.3 (Core.Tests). Spec: `docs/superpowers/specs/2026-07-16-lantern-scope-design.md`.

## Global Constraints

- Colors (spec, from DeadEye LANTERN-SPEC §5): core stroke `#FFC77F` 1.3 px; glow stroke `#FFB454` 3.5 px at 0.30 opacity; beam pip `#FFF9F0`.
- Envelope math verbatim: taper `sin(π·u)`; breathing `0.72 + 0.28·sin(t/900)` (t in ms); ignition 300 ms; retract 450 ms smoothstep, toward the RIGHT end.
- The trace shows REAL mic samples — never synthesize waveform data.
- Never-activate constraints preserved verbatim: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`, `IsHitTestVisible=false`, `Focusable=false`, never `Activate()`. `HideIndicator` stays non-blocking.
- The render tick MUST be unhooked when the pill hides and when the window closes (CPU-leak hazard).
- `DeadAir.Core` stays WPF-free (plain `net8.0`, no new package refs).
- Work on branch `feat/lantern-scope` (created in Task 1 from `docs/lantern-scope-spec`).
- Test command: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal` (append `--filter "FullyQualifiedName~ScopeGeometryTests"` while iterating).

---

### Task 1: ScopeGeometry scalar functions (envelope, breathe, ignition, retract)

**Files:**
- Create: `host/DeadAir.Core/ScopeGeometry.cs`
- Test: `host/DeadAir.Core.Tests/ScopeGeometryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (Task 2 and Task 3 rely on these exact signatures):
  - `const double ScopeGeometry.IgnitionMs = 300.0`
  - `const double ScopeGeometry.RetractMs = 450.0`
  - `static double ScopeGeometry.Envelope(double u)`
  - `static double ScopeGeometry.Breathe(double tMs)`
  - `static double ScopeGeometry.IgnitionHead(double tMs)`
  - `static double ScopeGeometry.IgnitionAmp(double u, double head)`
  - `static double ScopeGeometry.RetractFraction(double tMs)`

- [ ] **Step 1: Create the branch**

```bash
cd "H:/DeadMind V.3/DeadAir"
git checkout docs/lantern-scope-spec
git checkout -b feat/lantern-scope
```

- [ ] **Step 2: Write the failing tests**

Create `host/DeadAir.Core.Tests/ScopeGeometryTests.cs`:

```csharp
using DeadAir.Core;

namespace DeadAir.Core.Tests;

public class ScopeGeometryTests
{
    // ---- Envelope: taper sin(pi*u), exactly zero at/outside both endpoints ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Envelope_IsExactlyZeroAtAndOutsideEndpoints(double u)
        => Assert.Equal(0.0, ScopeGeometry.Envelope(u));

    [Fact]
    public void Envelope_PeaksAtMidpoint()
        => Assert.Equal(1.0, ScopeGeometry.Envelope(0.5), 12);

    [Fact]
    public void Envelope_IsSymmetric()
        => Assert.Equal(ScopeGeometry.Envelope(0.25), ScopeGeometry.Envelope(0.75), 12);

    [Fact]
    public void Envelope_QuarterPointMatchesSinPiOver4()
        => Assert.Equal(0.70711, ScopeGeometry.Envelope(0.25), 4);

    // ---- Breathe: 0.72 + 0.28*sin(t/900), bounded [0.44, 1.0] ----

    [Fact]
    public void Breathe_AtZeroIsBaseline()
        => Assert.Equal(0.72, ScopeGeometry.Breathe(0), 12);

    [Fact]
    public void Breathe_PeaksAtQuarterPeriod()
        => Assert.Equal(1.0, ScopeGeometry.Breathe(900.0 * Math.PI / 2), 12);

    [Fact]
    public void Breathe_TroughsAtThreeQuarterPeriod()
        => Assert.Equal(0.44, ScopeGeometry.Breathe(900.0 * 3 * Math.PI / 2), 12);

    [Fact]
    public void Breathe_StaysBounded()
    {
        for (double t = 0; t < 20000; t += 37)
        {
            double b = ScopeGeometry.Breathe(t);
            Assert.InRange(b, 0.44, 1.0);
        }
    }

    // ---- IgnitionHead: linear 0->1 over IgnitionMs (300), clamped ----

    [Theory]
    [InlineData(-50, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(150, 0.5)]
    [InlineData(300, 1.0)]
    [InlineData(400, 1.0)]
    public void IgnitionHead_LinearAndClamped(double tMs, double expected)
        => Assert.Equal(expected, ScopeGeometry.IgnitionHead(tMs), 12);

    // ---- IgnitionAmp: grows toward the head (u/head), 0 beyond, 1 after arrival ----

    [Fact]
    public void IgnitionAmp_GrowsTowardHead()
        => Assert.Equal(0.5, ScopeGeometry.IgnitionAmp(0.25, 0.5), 12);

    [Fact]
    public void IgnitionAmp_ZeroBeyondHead()
        => Assert.Equal(0.0, ScopeGeometry.IgnitionAmp(0.6, 0.5));

    [Fact]
    public void IgnitionAmp_ZeroWhenHeadNotStarted()
        => Assert.Equal(0.0, ScopeGeometry.IgnitionAmp(0.3, 0.0));

    [Fact]
    public void IgnitionAmp_FullAfterArrival()
        => Assert.Equal(1.0, ScopeGeometry.IgnitionAmp(0.3, 1.0));

    [Fact]
    public void IgnitionAmp_MonotonicTowardHead()
    {
        double prev = -1;
        for (double u = 0; u <= 0.5; u += 0.05)
        {
            double a = ScopeGeometry.IgnitionAmp(u, 0.5);
            Assert.True(a >= prev, $"amp fell at u={u}");
            prev = a;
        }
    }

    // ---- RetractFraction: smoothstep-eased 1->0 over RetractMs (450) ----

    [Theory]
    [InlineData(-10, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(225, 0.5)]      // smoothstep(0.5) == 0.5
    [InlineData(112.5, 0.84375)] // smoothstep(0.75) = 0.75^2*(3-1.5)
    [InlineData(450, 0.0)]
    [InlineData(500, 0.0)]      // exactly zero at/after completion — no pop
    public void RetractFraction_EasedAndClamped(double tMs, double expected)
        => Assert.Equal(expected, ScopeGeometry.RetractFraction(tMs), 12);

    [Fact]
    public void RetractFraction_MonotonicDecreasing()
    {
        double prev = 2;
        for (double t = 0; t <= 460; t += 10)
        {
            double f = ScopeGeometry.RetractFraction(t);
            Assert.True(f <= prev, $"fraction rose at t={t}");
            prev = f;
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal`
Expected: BUILD FAILURE — `error CS0103: The name 'ScopeGeometry' does not exist` (the type isn't defined yet).

- [ ] **Step 4: Write the implementation**

Create `host/DeadAir.Core/ScopeGeometry.cs`:

```csharp
namespace DeadAir.Core;

/// <summary>
/// Pure math for the Lantern-styled pill oscilloscope
/// (docs/superpowers/specs/2026-07-16-lantern-scope-design.md).
/// Envelope/breathing constants carried verbatim from DeadEye LANTERN-SPEC §5;
/// retract is deliberately 450 ms (not Lantern's 4.8 s) so the pill leaves fast.
/// </summary>
public static class ScopeGeometry
{
    public const double IgnitionMs = 300.0;   // Lantern hopMs
    public const double RetractMs = 450.0;

    /// <summary>Endpoint taper sin(π·u). Exactly 0 at/outside the endpoints —
    /// float sin(π) leaks ~1e-16, which would defeat "pinches to nothing".</summary>
    public static double Envelope(double u)
        => u <= 0 || u >= 1 ? 0 : Math.Sin(Math.PI * u);

    /// <summary>Breathing amplitude envelope, 900 ms period, phase 0 (single trace).</summary>
    public static double Breathe(double tMs)
        => 0.72 + 0.28 * Math.Sin(tMs / 900.0);

    /// <summary>Beam head position u, linear 0→1 over IgnitionMs, clamped.</summary>
    public static double IgnitionHead(double tMs)
        => Math.Clamp(tMs / IgnitionMs, 0.0, 1.0);

    /// <summary>Amplitude during ignition: grows toward the head (u/head),
    /// nothing beyond the head, full field once the head arrives.</summary>
    public static double IgnitionAmp(double u, double head)
    {
        if (head >= 1) return 1;
        if (head <= 0 || u > head) return 0;
        return u / head;
    }

    /// <summary>Visible fraction 1→0 over RetractMs, smoothstep-eased.
    /// Doubles as the alpha fade so length and alpha hit zero together — no pop.</summary>
    public static double RetractFraction(double tMs)
    {
        double p = 1 - Math.Clamp(tMs / RetractMs, 0.0, 1.0);
        return p * p * (3 - 2 * p);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal`
Expected: PASS — 28 tests (theories expand), 0 failed.

- [ ] **Step 6: Run the full Core suite (no regressions)**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal`
Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add host/DeadAir.Core/ScopeGeometry.cs host/DeadAir.Core.Tests/ScopeGeometryTests.cs
git commit -m "feat(host): ScopeGeometry scalar math for the Lantern scope"
```

---

### Task 2: ScopeGeometry.BuildPoints (sample→canvas mapping with visibility window)

**Files:**
- Modify: `host/DeadAir.Core/ScopeGeometry.cs` (append one method)
- Test: `host/DeadAir.Core.Tests/ScopeGeometryTests.cs` (append test region)

**Interfaces:**
- Consumes: nothing from Task 1 (independent method).
- Produces (Task 3 relies on this exact signature):
  - `static (double X, double Y)[] ScopeGeometry.BuildPoints(IReadOnlyList<double> samples, double width, double height, Func<double, double> ampAt, double visibleFrom = 0.0, double visibleTo = 1.0)`
  - Semantics: `u = i/(n-1)`; x is FIXED per index (`i·width/(n-1)`) — never remapped, so ignition/retract unveil or withdraw the wave rather than compress it (Lantern true-u rule); `y = height/2 − samples[i]·(height/2)·ampAt(u)`; points with `u < visibleFrom` or `u > visibleTo` are omitted; fewer than 2 samples → empty array.

- [ ] **Step 1: Write the failing tests**

Append inside the `ScopeGeometryTests` class in `host/DeadAir.Core.Tests/ScopeGeometryTests.cs`:

```csharp
    // ---- BuildPoints: fixed-x mapping with a visibility window ----

    private static readonly double[] Bump = { 0.0, 0.0, 1.0, 0.0, 0.0 };

    [Fact]
    public void BuildPoints_MapsSamplesToCanvas()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0);
        Assert.Equal(5, pts.Length);
        Assert.Equal(new[] { 0.0, 25.0, 50.0, 75.0, 100.0 },
            pts.Select(p => p.X).ToArray());
        Assert.Equal(20.0, pts[0].Y, 12);   // v=0 -> midline
        Assert.Equal(0.0, pts[2].Y, 12);    // v=1, amp 1 -> top
    }

    [Fact]
    public void BuildPoints_AmpScalesDeflectionNotMidline()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 0.5);
        Assert.Equal(20.0, pts[0].Y, 12);   // v=0 stays on the midline
        Assert.Equal(10.0, pts[2].Y, 12);   // v=1 halved toward midline
    }

    [Fact]
    public void BuildPoints_AmpAtReceivesU()
    {
        var seen = new List<double>();
        ScopeGeometry.BuildPoints(Bump, 100, 40, u => { seen.Add(u); return 1.0; });
        Assert.Equal(new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }, seen.ToArray());
    }

    [Fact]
    public void BuildPoints_VisibleToOmitsPointsBeyondHead()
    {
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0, visibleTo: 0.5);
        Assert.Equal(new[] { 0.0, 25.0, 50.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildPoints_VisibleFromKeepsFixedXPositions()
    {
        // Retract: left edge slides right; surviving xs are NOT remapped to 0.
        var pts = ScopeGeometry.BuildPoints(Bump, 100, 40, _ => 1.0, visibleFrom: 0.5);
        Assert.Equal(new[] { 50.0, 75.0, 100.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildPoints_FewerThanTwoSamplesIsEmpty()
    {
        Assert.Empty(ScopeGeometry.BuildPoints(Array.Empty<double>(), 100, 40, _ => 1.0));
        Assert.Empty(ScopeGeometry.BuildPoints(new[] { 0.7 }, 100, 40, _ => 1.0));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal`
Expected: BUILD FAILURE — `error CS0117: 'ScopeGeometry' does not contain a definition for 'BuildPoints'`.

- [ ] **Step 3: Write the implementation**

Append to `ScopeGeometry` in `host/DeadAir.Core/ScopeGeometry.cs`:

```csharp
    /// <summary>
    /// Map samples to canvas points. x is FIXED per index (true-u rule: ignition
    /// and retract unveil/withdraw the wave, never compress it); y = mid −
    /// v·mid·ampAt(u). Points with u &lt; visibleFrom or u &gt; visibleTo are
    /// omitted. Fewer than two samples → empty.
    /// </summary>
    public static (double X, double Y)[] BuildPoints(
        IReadOnlyList<double> samples, double width, double height,
        Func<double, double> ampAt, double visibleFrom = 0.0, double visibleTo = 1.0)
    {
        int n = samples.Count;
        if (n < 2) return Array.Empty<(double, double)>();
        double mid = height / 2.0, step = width / (n - 1);
        var pts = new List<(double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double u = (double)i / (n - 1);
            if (u < visibleFrom || u > visibleTo) continue;
            pts.Add((i * step, mid - samples[i] * mid * ampAt(u)));
        }
        return pts.ToArray();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal`
Expected: PASS — 34 tests, 0 failed.

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal`
Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.Core/ScopeGeometry.cs host/DeadAir.Core.Tests/ScopeGeometryTests.cs
git commit -m "feat(host): ScopeGeometry.BuildPoints sample-to-canvas mapping"
```

---

### Task 3: Pill window — Lantern render + lifecycle state machine

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml` (scope canvas children)
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs` (full rewrite below)

**Interfaces:**
- Consumes: everything `ScopeGeometry` produces (Tasks 1–2, exact signatures listed there); `WaveformRingBuffer` (`Values`, `PushRange`, `Reset` — unchanged).
- Produces: public surface of `RecordingIndicatorWindow` is UNCHANGED — `ShowIndicator()`, `HideIndicator()`, `PushWaveform(IReadOnlyList<double>)`, `SetPartial(string)`. `App.xaml.cs` needs no edits. Behavior change only: `HideIndicator()` now starts a ~450 ms retract and the window hides itself when it completes.

- [ ] **Step 1: Replace the scope canvas children in XAML**

In `host/DeadAir.App/RecordingIndicatorWindow.xaml` replace:

```xml
            <Canvas x:Name="ScopeCanvas" Grid.Row="0" Width="296" Height="40"
                    ClipToBounds="True">
                <Polyline x:Name="ScopeLine" Stroke="#4FC3F7" StrokeThickness="1.4"
                          StrokeLineJoin="Round"/>
            </Canvas>
```

with:

```xml
            <Canvas x:Name="ScopeCanvas" Grid.Row="0" Width="296" Height="40"
                    ClipToBounds="True">
                <!-- Lantern phosphor double-stroke (spec 2026-07-16): soft wide
                     glow under a thin bright core; both share the same Points.
                     WPF has no additive compositing for strokes — layered
                     translucent strokes approximate the canvas 'lighter' pass. -->
                <Polyline x:Name="GlowLine" Stroke="#FFB454" StrokeThickness="3.5"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.30"/>
                <Polyline x:Name="ScopeLine" Stroke="#FFC77F" StrokeThickness="1.3"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.95"/>
                <Ellipse x:Name="BeamPip" Width="11" Height="11"
                         Visibility="Collapsed" IsHitTestVisible="False">
                    <Ellipse.Fill>
                        <RadialGradientBrush>
                            <GradientStop Color="#FFFFF9F0" Offset="0"/>
                            <GradientStop Color="#CCFFB454" Offset="0.45"/>
                            <GradientStop Color="#00FFB454" Offset="1"/>
                        </RadialGradientBrush>
                    </Ellipse.Fill>
                </Ellipse>
            </Canvas>
```

- [ ] **Step 2: Rewrite the code-behind**

Replace the entire contents of `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs` with:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using DeadAir.Core;

namespace DeadAir.App;

public partial class RecordingIndicatorWindow : Window
{
    private const int ScopeSamples = 296;      // one sample per horizontal px
    private const double ScopeWidth = 296, ScopeHeight = 40;
    private const int InterimMaxChars = 46;
    private const double CoreOpacity = 0.95, GlowOpacity = 0.30;
    private const double PipFadeMs = 150.0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    /// <summary>Lantern lifecycle (spec 2026-07-16): ignition sweep on show,
    /// breathing live trace, retract-then-hide on stop.</summary>
    private enum ScopeState { Idle, Igniting, Live, Retracting }

    private readonly WaveformRingBuffer _wave = new(ScopeSamples);
    private readonly SolidColorBrush _dim =
        new(Color.FromArgb(0x66, 0xE1, 0xF5, 0xFE));
    private readonly SolidColorBrush _hot =
        new(Color.FromArgb(0xFF, 0x8B, 0xE9, 0xFD));
    private string _lastPartial = "";

    private ScopeState _state = ScopeState.Idle;
    private long _showT0, _stateT0;   // Environment.TickCount64 at show / state entry
    // Rightmost drawable u. Tracks the beam head during ignition and is left
    // frozen if a retract starts mid-ignition, so the retract can't reveal
    // trace the beam never wrote.
    private double _visibleTo = 1.0;
    private bool _tickHooked;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
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

    protected override void OnClosed(EventArgs e)
    {
        UnhookTick();
        base.OnClosed(e);
    }

    public void PushWaveform(IReadOnlyList<double> samples)
        => _wave.PushRange(samples);   // the render tick draws; no per-push render

    public void SetPartial(string text)
    {
        var layout = PartialText.LayoutInterim(_lastPartial, text, InterimMaxChars);
        _lastPartial = text;

        InterimText.Inlines.Clear();
        if (layout.Dim.Length > 0)
            InterimText.Inlines.Add(new Run(layout.Dim + " ") { Foreground = _dim });
        if (layout.Hot.Length > 0)
            InterimText.Inlines.Add(new Run(layout.Hot) { Foreground = _hot });
    }

    public void ShowIndicator()
    {
        _wave.Reset();
        _lastPartial = "";
        InterimText.Inlines.Clear();

        _state = ScopeState.Igniting;   // re-show mid-retract re-ignites
        _showT0 = _stateT0 = Environment.TickCount64;
        _visibleTo = 0.0;
        ScopeLine.Opacity = CoreOpacity;
        GlowLine.Opacity = GlowOpacity;
        var empty = new PointCollection();
        empty.Freeze();   // frozen: safely shared by both polylines
        ScopeLine.Points = empty;
        GlowLine.Points = empty;
        BeamPip.Visibility = Visibility.Visible;
        BeamPip.Opacity = 1.0;
        if (!_tickHooked)
        {
            CompositionTarget.Rendering += OnRenderTick;
            _tickHooked = true;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 16;
        Show(); // never Activate()
    }

    /// <summary>Starts the retract; the window hides itself ~450 ms later.
    /// Non-blocking — callers are unaffected by the visual lag.</summary>
    public void HideIndicator()
    {
        if (_state == ScopeState.Idle) { Hide(); return; }
        if (_state == ScopeState.Retracting) return;
        _state = ScopeState.Retracting;   // _visibleTo keeps any mid-ignition cap
        _stateT0 = Environment.TickCount64;
        BeamPip.Visibility = Visibility.Collapsed;
    }

    private void UnhookTick()
    {
        if (!_tickHooked) return;
        CompositionTarget.Rendering -= OnRenderTick;
        _tickHooked = false;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double tState = now - _stateT0;
        double breathe = ScopeGeometry.Breathe(now - _showT0);

        switch (_state)
        {
            case ScopeState.Igniting:
            {
                double head = ScopeGeometry.IgnitionHead(tState);
                _visibleTo = head;
                RenderScope(u => ScopeGeometry.Envelope(u) * breathe
                                 * ScopeGeometry.IgnitionAmp(u, head),
                            0.0, head);
                PlacePip();
                if (head >= 1)
                {
                    _state = ScopeState.Live;   // pip fade timing starts here
                    _stateT0 = now;
                    _visibleTo = 1.0;
                }
                return;
            }
            case ScopeState.Live:
            {
                RenderScope(u => ScopeGeometry.Envelope(u) * breathe, 0.0, 1.0);
                if (BeamPip.Visibility == Visibility.Visible)
                {
                    double pip = 1 - tState / PipFadeMs;
                    if (pip <= 0) BeamPip.Visibility = Visibility.Collapsed;
                    else { BeamPip.Opacity = pip; PlacePip(); }
                }
                return;
            }
            case ScopeState.Retracting:
            {
                double rf = ScopeGeometry.RetractFraction(tState);
                double visibleFrom = 1 - rf;
                if (rf <= 0 || visibleFrom >= _visibleTo)
                {
                    _state = ScopeState.Idle;
                    Hide();
                    UnhookTick();
                    return;
                }
                // Length and alpha reach zero together — no pop (spec).
                ScopeLine.Opacity = CoreOpacity * rf;
                GlowLine.Opacity = GlowOpacity * rf;
                RenderScope(u => ScopeGeometry.Envelope(u) * breathe,
                            visibleFrom, _visibleTo);
                return;
            }
            default:
                return;
        }
    }

    private void RenderScope(Func<double, double> ampAt,
        double visibleFrom, double visibleTo)
    {
        var raw = ScopeGeometry.BuildPoints(_wave.Values, ScopeWidth, ScopeHeight,
            ampAt, visibleFrom, visibleTo);
        var pts = new PointCollection(raw.Length);
        foreach (var (x, y) in raw) pts.Add(new Point(x, y));
        pts.Freeze();   // frozen: safely shared by both polylines
        ScopeLine.Points = pts;
        GlowLine.Points = pts;
    }

    /// <summary>Center the pip on the rightmost drawn point (the beam head).</summary>
    private void PlacePip()
    {
        var pts = ScopeLine.Points;
        double x = 0, y = ScopeHeight / 2.0;
        if (pts.Count > 0)
        {
            var p = pts[pts.Count - 1];
            x = p.X; y = p.Y;
        }
        Canvas.SetLeft(BeamPip, x - BeamPip.Width / 2);
        Canvas.SetTop(BeamPip, y - BeamPip.Height / 2);
    }
}
```

- [ ] **Step 3: Build the app**

Run: `dotnet build "host/DeadAir.App/DeadAir.App.csproj"`
Expected: Build succeeded, 0 errors. (Warnings unrelated to this change are acceptable.)

- [ ] **Step 4: Run the full Core suite (no regressions)**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal`
Expected: PASS, 0 failed.

- [ ] **Step 5: Review the diff against the never-activate constraints**

Run: `git diff host/DeadAir.App/RecordingIndicatorWindow.xaml.cs`
Verify by eye: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` intact; no `Activate()` call anywhere; XAML root still has `ShowActivated="False"`, `IsHitTestVisible="False"`, `Focusable="False"`; `HideIndicator` contains no waits/sleeps.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(host): Lantern scope render + ignition/retract lifecycle on the pill"
```

---

### Task 4: Live smoke + docs sync

**Files:**
- Modify: `README.md:44-47` (pill feature bullet)

**Interfaces:**
- Consumes: the running app (Tasks 1–3 complete).
- Produces: nothing downstream — final verification + docs.

- [ ] **Step 1: Launch the app**

Run: `dotnet run --project "host/DeadAir.App/DeadAir.App.csproj"` (run in background; the app lives in the tray).
Note: if the sidecar fails to launch on this machine, the pill still shows on hotkey — the waveform just stays flat. Ignition/retract are still fully observable.

- [ ] **Step 2: Smoke the lifecycle**

Hold the hotkey (default `RControl`), speak a sentence, release. Synthetic input can't drive this meaningfully — a human at the keyboard does it. Observe:
- On press: trace writes itself left→right in ~0.3 s with a warm-white pip riding the head; pip fades right after arrival.
- While held: warm amber phosphor trace (soft glow under bright core), amplitude pinched to nothing at both edges, gently breathing (~5.7 s cycle).
- On release: trace withdraws toward the RIGHT edge over ~0.45 s while fading, then the pill disappears. No flat-line pop at the end.
- Rapid re-press during the retract re-ignites cleanly.
- Dictated text still lands in the focused app exactly as before.

If anything is off, fix forward on this branch before proceeding. Then exit the app via tray → Exit.

- [ ] **Step 3: Check the log for new errors**

Run (PowerShell): `Get-Content "$env:APPDATA\DeadAir\logs\deadair-$(Get-Date -Format yyyyMMdd).log" -Tail 30`
(If the log dir differs, it's printed on the app's first log line.)
Expected: no `ERROR` lines from this session's recording.

- [ ] **Step 4: Update the README pill bullet**

In `README.md`, replace:

```markdown
- **Live pill overlay (v0.2)** — while you hold the key, a small window shows a
  scrolling PCM oscilloscope and a self-correcting interim transcript. The
  interim text is a preview only and is never injected; the authoritative decode
  happens on key-up. (Waveform on all engines; live text is GPU-only.)
```

with:

```markdown
- **Live pill overlay (v0.2)** — while you hold the key, a small window shows a
  scrolling PCM oscilloscope and a self-correcting interim transcript. The
  scope wears DeadEye's Lantern trace look: a warm phosphor double-stroke that
  ignites left→right on press, breathes while you speak, and retracts away on
  release. The interim text is a preview only and is never injected; the
  authoritative decode happens on key-up. (Waveform on all engines; live text
  is GPU-only.)
```

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: README pill bullet — Lantern scope look"
```
