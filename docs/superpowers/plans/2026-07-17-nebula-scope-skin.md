# Nebula Scope Skin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Nebula skin (tamed 3-strand smoke bundle + haze, silvered amber) to the pill oscilloscope, selectable via Settings/config, default nebula.

**Architecture:** Pure noise/envelope math joins `DeadAir.Core/ScopeGeometry.cs` (tested). `RecordingIndicatorWindow` gains four nebula Polylines and a `SetSkin` switch; the render tick branches per skin through one shared frame renderer (lantern behavior unchanged). `AppConfig` gains host-only `Pill.Skin`; Settings gets a dropdown; `App` applies it at startup and on save.

**Tech Stack:** .NET 8, WPF, xunit 2.5.3. Spec: `docs/superpowers/specs/2026-07-17-nebula-scope-skin-design.md`. Continues branch `feat/lantern-scope` (tasks numbered 5-7 after the Lantern plan's 1-4).

## Global Constraints

- Nebula constants (spec, from DeadEye tamed wispLitStrand): strands `#EEDAC2` 0.85 px α 0.65; hot core `#FFF9F0` 1.1 px α 1.0; haze `#EEDAC2` 9 px α 0.05, noise amp ×0.7, seed 34.7; strand seeds `3.7 + s·5.7`, drift rates `0.9 + s·0.13`; noise amp `3.0 px × (0.35 + s·0.22)`; drift clock `(now − showT0)·0.33`, continuous across states.
- `WispNoff`/`WispEnv` formulas verbatim from the spec table; `WispEnv` exactly 0 at/outside endpoints.
- Nebula does NOT breathe; nebula waveform taper = `WispEnv(u)` (not `Envelope`).
- Lantern skin behavior unchanged: same formulas/constants as shipped (`Envelope × Breathe × IgnitionAmp`, core 0.95/glow 0.30 opacities).
- Default skin `"nebula"`; any value other than `"lantern"` normalizes to `"nebula"`.
- `Pill.Skin` is host-only: `ConfigCommand.From` untouched (pinned by test).
- Never-activate constraints preserved verbatim; render tick unhook rules unchanged; real mic samples only.
- `DeadAir.Core` stays WPF-free.
- Work on branch `feat/lantern-scope` (already checked out).
- Test command: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore` (append `--filter "FullyQualifiedName~ScopeGeometryTests"` or `~PillConfigTests` while iterating). `--no-restore` is required: NuGet.Config is unreadable in the worker sandbox and no package refs change.

---

### Task 5: ScopeGeometry nebula math (WispNoff, WispEnv, BuildNebulaPoints)

**Files:**
- Modify: `host/DeadAir.Core/ScopeGeometry.cs` (append three members)
- Test: `host/DeadAir.Core.Tests/ScopeGeometryTests.cs` (append test region)

**Interfaces:**
- Consumes: the existing `Bump` static field in `ScopeGeometryTests` (`{0,0,1,0,0}`).
- Produces (Task 6 relies on these exact signatures):
  - `static double ScopeGeometry.WispNoff(double u, double t, double seed, double k)`
  - `static double ScopeGeometry.WispEnv(double u)`
  - `static (double X, double Y)[] ScopeGeometry.BuildNebulaPoints(IReadOnlyList<double> samples, double width, double height, Func<double, double> ampAt, double tSlow, double seed, double k, double noiseAmp, double visibleFrom = 0.0, double visibleTo = 1.0)`
  - BuildNebulaPoints semantics: `y = mid − samples[i]·mid·ampAt(u) + WispNoff(u,tSlow,seed,k)·noiseAmp·WispEnv(u)`; x fixed per index; same visibility window as `BuildPoints`; `n<2` → empty.

- [ ] **Step 1: Write the failing tests**

Append inside the `ScopeGeometryTests` class in `host/DeadAir.Core.Tests/ScopeGeometryTests.cs` (before the closing brace):

```csharp
    // ---- WispEnv: nebula strand envelope sin(pi*u)^0.75, exact-zero endpoints ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void WispEnv_IsExactlyZeroAtAndOutsideEndpoints(double u)
        => Assert.Equal(0.0, ScopeGeometry.WispEnv(u));

    [Fact]
    public void WispEnv_PeaksAtMidpoint()
        => Assert.Equal(1.0, ScopeGeometry.WispEnv(0.5), 12);

    [Fact]
    public void WispEnv_IsSymmetric()
        => Assert.Equal(ScopeGeometry.WispEnv(0.25), ScopeGeometry.WispEnv(0.75), 12);

    [Fact]
    public void WispEnv_QuarterPointMatchesPow()
        => Assert.Equal(0.7711, ScopeGeometry.WispEnv(0.25), 4);

    // ---- WispNoff: three layered sines normalized to [-1, 1] ----

    [Fact]
    public void WispNoff_StaysBounded()
    {
        for (double u = 0; u <= 1.0001; u += 0.03)
            for (double t = 0; t < 60000; t += 1700)
                Assert.InRange(ScopeGeometry.WispNoff(u, t, 3.7, 1.0), -1.0, 1.0);
    }

    [Fact]
    public void WispNoff_IsDeterministic()
        => Assert.Equal(ScopeGeometry.WispNoff(0.4, 1234, 9.4, 1.03),
                        ScopeGeometry.WispNoff(0.4, 1234, 9.4, 1.03));

    [Fact]
    public void WispNoff_SeedChangesStrand()
        => Assert.NotEqual(ScopeGeometry.WispNoff(0.5, 1000, 3.7, 1.0),
                           ScopeGeometry.WispNoff(0.5, 1000, 9.4, 1.0));

    [Fact]
    public void WispNoff_DriftsOverTime()
        => Assert.NotEqual(ScopeGeometry.WispNoff(0.5, 0, 3.7, 1.0),
                           ScopeGeometry.WispNoff(0.5, 50000, 3.7, 1.0));

    // ---- BuildNebulaPoints: waveform + enveloped noise, BuildPoints window semantics ----

    [Fact]
    public void BuildNebulaPoints_ZeroNoiseZeroSamplesIsMidline()
    {
        var pts = ScopeGeometry.BuildNebulaPoints(new double[5], 100, 40,
            _ => 1.0, 1000, 3.7, 1.0, 0.0);
        Assert.Equal(5, pts.Length);
        Assert.All(pts, p => Assert.Equal(20.0, p.Y, 12));
    }

    [Fact]
    public void BuildNebulaPoints_NoiseBoundedByAmpTimesEnv()
    {
        var pts = ScopeGeometry.BuildNebulaPoints(new double[5], 100, 40,
            _ => 1.0, 1000, 3.7, 1.0, 2.0);
        for (int i = 0; i < pts.Length; i++)
        {
            double u = i / 4.0;
            Assert.True(Math.Abs(pts[i].Y - 20.0)
                <= 2.0 * ScopeGeometry.WispEnv(u) + 1e-9,
                $"point {i} exceeded the noise envelope");
        }
    }

    [Fact]
    public void BuildNebulaPoints_NoiseVisibleInInterior()
    {
        var pts = ScopeGeometry.BuildNebulaPoints(new double[5], 100, 40,
            _ => 1.0, 1000, 3.7, 1.0, 2.0);
        Assert.Contains(pts, p => Math.Abs(p.Y - 20.0) > 0.001);
    }

    [Fact]
    public void BuildNebulaPoints_XStaysFixedUnderWindow()
    {
        var pts = ScopeGeometry.BuildNebulaPoints(Bump, 100, 40,
            _ => 1.0, 0, 3.7, 1.0, 0.0, visibleFrom: 0.5);
        Assert.Equal(new[] { 50.0, 75.0, 100.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildNebulaPoints_VisibleToOmitsBeyondHead()
    {
        var pts = ScopeGeometry.BuildNebulaPoints(Bump, 100, 40,
            _ => 1.0, 0, 3.7, 1.0, 0.0, visibleTo: 0.5);
        Assert.Equal(new[] { 0.0, 25.0, 50.0 }, pts.Select(p => p.X).ToArray());
    }

    [Fact]
    public void BuildNebulaPoints_FewerThanTwoSamplesIsEmpty()
        => Assert.Empty(ScopeGeometry.BuildNebulaPoints(new[] { 0.5 }, 100, 40,
            _ => 1.0, 0, 3.7, 1.0, 1.0));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal --no-restore`
Expected: BUILD FAILURE — `error CS0117: 'ScopeGeometry' does not contain a definition for 'WispEnv'` (and/or `WispNoff`, `BuildNebulaPoints`).

- [ ] **Step 3: Write the implementation**

Append to `ScopeGeometry` in `host/DeadAir.Core/ScopeGeometry.cs` (before the class closing brace):

```csharp
    /// <summary>Nebula strand noise (DeadEye wispNoff verbatim): three layered
    /// sines normalized to [-1, 1]. The caller pre-slows t (tamed drift = t·0.33).</summary>
    public static double WispNoff(double u, double t, double seed, double k)
        => (Math.Sin(u * 5.1 + seed * 3.7 + t * 0.00021 * k)
          + Math.Sin(u * 11.7 + seed * 9.1 - t * 0.00013 * k) * 0.55
          + Math.Sin(u * 23.3 + seed * 17.3 + t * 0.00034 * k) * 0.28) / 1.83;

    /// <summary>Nebula strand envelope sin(π·u)^0.75 (DeadEye wispEnv verbatim,
    /// including the exact-zero endpoint clamp — float sin(π)^0.75 leaks ~1e-12,
    /// which would defeat "strands pinch to nothing").</summary>
    public static double WispEnv(double u)
        => u <= 0 || u >= 1 ? 0 : Math.Pow(Math.Sin(Math.PI * u), 0.75);

    /// <summary>Nebula variant of BuildPoints: the waveform (scaled by ampAt)
    /// plus a WispNoff strand offset enveloped by WispEnv. Same fixed-x and
    /// visibility-window semantics as BuildPoints; fewer than two samples → empty.</summary>
    public static (double X, double Y)[] BuildNebulaPoints(
        IReadOnlyList<double> samples, double width, double height,
        Func<double, double> ampAt, double tSlow, double seed, double k,
        double noiseAmp, double visibleFrom = 0.0, double visibleTo = 1.0)
    {
        int n = samples.Count;
        if (n < 2) return Array.Empty<(double, double)>();
        double mid = height / 2.0, step = width / (n - 1);
        var pts = new List<(double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double u = (double)i / (n - 1);
            if (u < visibleFrom || u > visibleTo) continue;
            double y = mid - samples[i] * mid * ampAt(u)
                     + WispNoff(u, tSlow, seed, k) * noiseAmp * WispEnv(u);
            pts.Add((i * step, y));
        }
        return pts.ToArray();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal --no-restore`
Expected: PASS — 51 tests (34 existing + 17 new), 0 failed.

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore`
Expected: PASS — 126 total, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add host/DeadAir.Core/ScopeGeometry.cs host/DeadAir.Core.Tests/ScopeGeometryTests.cs
git commit -m "feat(host): nebula strand math (WispNoff/WispEnv/BuildNebulaPoints)"
```

---

### Task 6: Pill window — Nebula render path + skin switch

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml` (add nebula polylines)
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs` (full rewrite below)

**Interfaces:**
- Consumes: everything Task 5 produces plus the existing ScopeGeometry members (`Envelope`, `Breathe`, `IgnitionHead`, `IgnitionAmp`, `RetractFraction`, `BuildPoints`).
- Produces: `public void SetSkin(string skin)` on `RecordingIndicatorWindow` — normalizes anything ≠ `"lantern"` to `"nebula"`; Task 7 wires it from config. All other public members unchanged.

- [ ] **Step 1: Add the nebula elements to XAML**

In `host/DeadAir.App/RecordingIndicatorWindow.xaml`, insert between the `ScopeLine` Polyline and the `BeamPip` Ellipse:

```xml
                <!-- Nebula skin (spec 2026-07-17): 9px haze under a tamed
                     3-strand bundle; StrandHot is the white core. Only one
                     skin's elements are Visible at a time (SetSkin). -->
                <Polyline x:Name="HazeLine" Stroke="#EEDAC2" StrokeThickness="9"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.05"
                          Visibility="Collapsed"/>
                <Polyline x:Name="Strand1" Stroke="#EEDAC2" StrokeThickness="0.85"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.65"
                          Visibility="Collapsed"/>
                <Polyline x:Name="Strand2" Stroke="#EEDAC2" StrokeThickness="0.85"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.65"
                          Visibility="Collapsed"/>
                <Polyline x:Name="StrandHot" Stroke="#FFF9F0" StrokeThickness="1.1"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="1.0"
                          Visibility="Collapsed"/>
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
using System.Windows.Shapes;
using DeadAir.Core;

namespace DeadAir.App;

public partial class RecordingIndicatorWindow : Window
{
    private const int ScopeSamples = 296;      // one sample per horizontal px
    private const double ScopeWidth = 296, ScopeHeight = 40;
    private const int InterimMaxChars = 46;
    private const double CoreOpacity = 0.95, GlowOpacity = 0.30;
    private const double PipFadeMs = 150.0;

    // Nebula skin (spec 2026-07-17): tamed 3-strand smoke bundle riding the
    // live waveform + 9px haze understroke; strand 0 is the hot white core.
    // Constants carried from DeadEye's tamed wispLitStrand pass.
    private const double StrandOpacity = 0.65, HotOpacity = 1.0, HazeOpacity = 0.05;
    private const double NebulaAmpPx = 3.0;    // base noise amplitude (screen px) — the tuning dial
    private const double NebulaDrift = 0.33;   // 3x-slowed drift
    private const double SeedBase = 3.7, SeedStep = 5.7, HazeSeed = 34.7;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    /// <summary>Lantern lifecycle (spec 2026-07-16): ignition sweep on show,
    /// live trace, retract-then-hide on stop. Shared by both skins.</summary>
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
    private string _skin = "nebula";  // "nebula" | "lantern" (spec 2026-07-17 default)

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
        ApplySkinVisibility();
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

    /// <summary>Select the scope skin. Unknown values fall back to nebula
    /// (the default) — same degrade-don't-crash posture as the hotkey.</summary>
    public void SetSkin(string skin)
    {
        _skin = skin == "lantern" ? "lantern" : "nebula";
        ApplySkinVisibility();
    }

    private bool Nebula => _skin == "nebula";

    private void ApplySkinVisibility()
    {
        var neb = Nebula ? Visibility.Visible : Visibility.Collapsed;
        var lan = Nebula ? Visibility.Collapsed : Visibility.Visible;
        HazeLine.Visibility = neb;
        Strand1.Visibility = neb;
        Strand2.Visibility = neb;
        StrandHot.Visibility = neb;
        GlowLine.Visibility = lan;
        ScopeLine.Visibility = lan;
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
        StrandHot.Opacity = HotOpacity;
        Strand1.Opacity = StrandOpacity;
        Strand2.Opacity = StrandOpacity;
        HazeLine.Opacity = HazeOpacity;
        var empty = new PointCollection();
        empty.Freeze();   // frozen: safely shared across polylines
        ScopeLine.Points = empty;
        GlowLine.Points = empty;
        StrandHot.Points = empty;
        Strand1.Points = empty;
        Strand2.Points = empty;
        HazeLine.Points = empty;
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
        double breathe = ScopeGeometry.Breathe(now - _showT0);   // lantern only

        switch (_state)
        {
            case ScopeState.Igniting:
            {
                double head = ScopeGeometry.IgnitionHead(tState);
                _visibleTo = head;
                RenderFrame(now, breathe, head, 0.0, head, 1.0);
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
                RenderFrame(now, breathe, 1.0, 0.0, 1.0, 1.0);
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
                RenderFrame(now, breathe, 1.0, visibleFrom, _visibleTo, rf);
                return;
            }
            default:
                return;
        }
    }

    /// <summary>One frame for the active skin. head is 1 outside ignition
    /// (IgnitionAmp then returns 1 for every u); fade is 1 outside retract
    /// and scales each skin element's base opacity during it.</summary>
    private void RenderFrame(long now, double breathe, double head,
        double visibleFrom, double visibleTo, double fade)
    {
        if (Nebula)
        {
            double tSlow = (now - _showT0) * NebulaDrift;
            Func<double, double> ampAt = u =>
                ScopeGeometry.WispEnv(u) * ScopeGeometry.IgnitionAmp(u, head);
            SetLine(HazeLine, HazeOpacity * fade, ScopeGeometry.BuildNebulaPoints(
                _wave.Values, ScopeWidth, ScopeHeight, ampAt, tSlow,
                HazeSeed, 1.0, NebulaAmpPx * 0.7, visibleFrom, visibleTo));
            for (int s = 0; s < 3; s++)
            {
                var line = s == 0 ? StrandHot : s == 1 ? Strand1 : Strand2;
                double baseA = s == 0 ? HotOpacity : StrandOpacity;
                SetLine(line, baseA * fade, ScopeGeometry.BuildNebulaPoints(
                    _wave.Values, ScopeWidth, ScopeHeight, ampAt, tSlow,
                    SeedBase + s * SeedStep, 0.9 + s * 0.13,
                    NebulaAmpPx * (0.35 + s * 0.22), visibleFrom, visibleTo));
            }
        }
        else
        {
            Func<double, double> ampAt = u =>
                ScopeGeometry.Envelope(u) * breathe
                * ScopeGeometry.IgnitionAmp(u, head);
            var raw = ScopeGeometry.BuildPoints(_wave.Values, ScopeWidth,
                ScopeHeight, ampAt, visibleFrom, visibleTo);
            var pts = new PointCollection(raw.Length);
            foreach (var (x, y) in raw) pts.Add(new Point(x, y));
            pts.Freeze();   // frozen: safely shared by both polylines
            ScopeLine.Opacity = CoreOpacity * fade;
            GlowLine.Opacity = GlowOpacity * fade;
            ScopeLine.Points = pts;
            GlowLine.Points = pts;
        }
    }

    private static void SetLine(Polyline line, double opacity,
        (double X, double Y)[] raw)
    {
        var pts = new PointCollection(raw.Length);
        foreach (var (x, y) in raw) pts.Add(new Point(x, y));
        pts.Freeze();
        line.Opacity = opacity;
        line.Points = pts;
    }

    /// <summary>Center the pip on the rightmost drawn point (the beam head)
    /// of the active skin's primary line.</summary>
    private void PlacePip()
    {
        var pts = (Nebula ? StrandHot : ScopeLine).Points;
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

Run: `dotnet build "host/DeadAir.App/DeadAir.App.csproj" --no-restore`
Expected: Build succeeded, 0 errors. (The pre-existing NU1701 warning is acceptable. If the build fails with MSB3027 file-lock, a stale DeadAir.App.exe is running — report it; the controller kills it.)

- [ ] **Step 4: Run the full Core suite (no regressions)**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore`
Expected: PASS — 126 total, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(host): nebula scope skin render path + SetSkin switch"
```

---

### Task 7: Pill.Skin config + Settings dropdown + live wiring

**Files:**
- Modify: `host/DeadAir.Core/Config/AppConfig.cs` (PillConfig)
- Modify: `host/DeadAir.App/SettingsWindow.xaml` (+dropdown)
- Modify: `host/DeadAir.App/SettingsWindow.xaml.cs` (load/save the skin)
- Modify: `host/DeadAir.App/App.xaml.cs` (apply at startup + on save)
- Test: `host/DeadAir.Core.Tests/PillConfigTests.cs` (new)

**Interfaces:**
- Consumes: `RecordingIndicatorWindow.SetSkin(string)` from Task 6; existing `ConfigCommand.From` (untouched).
- Produces: `AppConfig.Pill` (`PillConfig` with `string Skin = "nebula"`), consumed nowhere else.

- [ ] **Step 1: Write the failing tests**

Create `host/DeadAir.Core.Tests/PillConfigTests.cs`:

```csharp
using System.Text.Json;
using DeadAir.Core.Config;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class PillConfigTests
{
    [Fact]
    public void Default_Skin_IsNebula()
        => Assert.Equal("nebula", new AppConfig().Pill.Skin);

    [Fact]
    public void Skin_RoundTripsThroughJson()
    {
        var cfg = new AppConfig();
        cfg.Pill.Skin = "lantern";
        var back = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(cfg))!;
        Assert.Equal("lantern", back.Pill.Skin);
    }

    [Fact]
    public void SidecarConfigCommand_DoesNotCarryTheSkin()
    {
        var cfg = new AppConfig();
        cfg.Pill.Skin = "lantern";
        var json = JsonSerializer.Serialize(ConfigCommand.From(cfg));
        Assert.DoesNotContain("skin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pill", json, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~PillConfigTests" -v minimal --no-restore`
Expected: BUILD FAILURE — `error CS1061: 'AppConfig' does not contain a definition for 'Pill'`.

- [ ] **Step 3: Add PillConfig**

In `host/DeadAir.Core/Config/AppConfig.cs`, add to the `AppConfig` class after the `Inject` property:

```csharp
    public PillConfig Pill { get; set; } = new();
```

and append after the `InjectConfig` class:

```csharp
public sealed class PillConfig
{
    public string Skin { get; set; } = "nebula"; // nebula | lantern (host-only, never sent to the sidecar)
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~PillConfigTests" -v minimal --no-restore`
Expected: PASS — 3 tests, 0 failed.

- [ ] **Step 5: Settings dropdown**

In `host/DeadAir.App/SettingsWindow.xaml`, insert after the `ModeBox` ComboBox block (before the dictionary TextBlock):

```xml
            <TextBlock FontWeight="Bold" Text="Scope skin"/>
            <ComboBox x:Name="SkinBox" Margin="0,4,0,12">
                <ComboBoxItem Content="nebula"/>
                <ComboBoxItem Content="lantern"/>
            </ComboBox>
```

In `host/DeadAir.App/SettingsWindow.xaml.cs`:
- In the constructor, after `Select(ModeBox, _config.Cleanup.Mode.ToString());` add:

```csharp
        Select(SkinBox, _config.Pill.Skin);
```

- In `OnSave`, after the `_config.Cleanup.Mode = ...` line add:

```csharp
        _config.Pill.Skin = Selected(SkinBox);
```

(The existing `Select` helper falls back to index 0 — `nebula` — on unknown config values.)

- [ ] **Step 6: Apply the skin at startup and on save**

In `host/DeadAir.App/App.xaml.cs`:
- In `OnStartup`, after `_indicator = new RecordingIndicatorWindow();` add:

```csharp
        _indicator.SetSkin(_config.Pill.Skin);
```

- In `OnSettingsSaved`, after the `_modeMenuItem.IsChecked = ...` line add:

```csharp
            _indicator.SetSkin(_config.Pill.Skin); // apply skin live, no restart needed
```

- [ ] **Step 7: Build + full suite**

Run: `dotnet build "host/DeadAir.App/DeadAir.App.csproj" --no-restore`
Expected: Build succeeded, 0 errors.
Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore`
Expected: PASS — 129 total, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add host/DeadAir.Core/Config/AppConfig.cs host/DeadAir.App/SettingsWindow.xaml host/DeadAir.App/SettingsWindow.xaml.cs host/DeadAir.App/App.xaml.cs host/DeadAir.Core.Tests/PillConfigTests.cs
git commit -m "feat(host): Pill.Skin config + Settings scope-skin dropdown, applied live"
```

---

### Task 8: Live smoke (user)

**Files:** none (verification only).

**Interfaces:**
- Consumes: the running app with Tasks 5-7 complete.
- Produces: user sign-off gating the final whole-branch review.

- [ ] **Step 1: Relaunch the app**

Stop any running DeadAir.App instance first (tray → Exit, or `taskkill /IM DeadAir.App.exe /F` — tray-only app, no window for WM_CLOSE), then:
Run: `dotnet run --project "host/DeadAir.App/DeadAir.App.csproj"` (background).

- [ ] **Step 2: User smokes the nebula skin (default)**

Hold the hotkey, speak, release. Expected: silvered-amber 3-strand bundle drifting slowly around the live waveform, hot white core strand, wide faint haze; strands pinch to nothing at both ends; ignition sweep + pip and right-anchored retract work as on lantern; no breathing (drift only).

- [ ] **Step 3: User smokes the Settings switch**

Tray → Settings → Scope skin → `lantern` → Save. Next recording shows the amber Lantern skin (glow + core + breathing). Switch back to `nebula`, Save, verify again. config.json shows the `Pill.Skin` value after each save.

- [ ] **Step 4: Log check**

Run (PowerShell): `Get-Content "$env:APPDATA\DeadAir\logs\deadair-$(Get-Date -Format yyyyMMdd).log" -Tail 20`
Expected: no ERROR lines from the smoke session.
