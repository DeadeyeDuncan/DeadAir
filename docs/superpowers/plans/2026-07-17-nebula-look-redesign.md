# Nebula Look Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the nebula scope skin read like DeadEye's connection lines — smooth, widely-fanned, low-frequency flowing strands with no jagged waveform spine.

**Why (root cause from the design panel, `wf_6af13124-3c6`):** the reference image *is* DeadEye's own `wispNoff` output. The pill looked jagged for two reasons, neither of which is the noise math: (1) it drew the raw PCM min/max envelope as a **spine** under the strands, and (2) it sampled each strand at **all 296 pixel columns** instead of DeadEye's **16 segments** — oversampling a smooth curve surfaces high-frequency ripple. DeadEye reads smooth *with* the full three-sine `wispNoff` precisely because it draws ~16 points and no waveform. So the fix is a **faithful port, not a re-tune**: keep `WispNoff`/`WispEnv` byte-identical (preserves the `.mjs` WISP-HELPERS parity + all their tests), delete the spine, sample at 16 segments, fan to 6 strands with DeadEye's verbatim ladders.

**User-signed-off semantic change:** the nebula skin **stops drawing the literal PCM waveform**. Mic samples now feed a single smoothed loudness scalar that opens/closes the **fan width** and brightness (wide+bright when speaking, narrow+calm when quiet). The **lantern skin still shows the true waveform** and is untouched.

**Architecture:** replace `ScopeGeometry.BuildNebulaPoints` with two pure functions — `MeanAbs` (the sole audio→visual coupling) and `BuildStrandPoints` (a straight-midline wisp strand, no PCM, sampled at N segments). Rewrite only the nebula branch of `RenderFrame` to run an asymmetric energy follower and draw 6 strands + haze. Lantern path and the state machine are untouched.

**Tech Stack:** .NET 8, WPF, xunit 2.5.3. Continues branch `feat/lantern-scope`. Tasks numbered 9–11.

## Global Constraints

- **Keep `WispNoff` and `WispEnv` byte-identical** — do NOT edit them (WISP-HELPERS/.mjs parity contract; their tests stay green). The jaggedness fix is structural (no spine + 16-segment sampling), never a change to the noise math.
- Nebula draws NO PCM spine: the strand spine is the straight midline `y = height/2`, with a smooth perpendicular `WispNoff` offset enveloped by `WispEnv` (exactly 0 at u≤0 and u≥1).
- Segment counts: strands `NebulaSegs = 16`, haze `HazeSegs = 8` (DeadEye's `_segN`/`_segN/2`).
- Strand count `6`. Per-strand ladders (DeadEye verbatim): amp factor `(0.35 + s·0.22)`, seed `SeedBase + s·SeedStep = 3.7 + s·5.7`, k `0.9 + s·0.13`. Haze: seed `34.7`, k `1.0`, amp `A·0.7`.
- Audio coupling: `raw = MeanAbs(_wave.Values)`; asymmetric follower `_nebEnergy += (raw>_nebEnergy ? EnergyAttack : EnergyRelease)·(raw−_nebEnergy)` with `EnergyAttack=0.20`, `EnergyRelease=0.05`; `enorm = clamp(_nebEnergy·EnergyGain, 0, 1)`, `EnergyGain=3.0`. Fan width `A = SpreadFloor + enorm·SpreadSpan = 2.5 + enorm·10.5` (px), so `A ∈ [2.5, 13.0]`. Brightness `glow = 0.55 + 0.45·enorm` (floored so the bundle is always visible while recording). Haze opacity `HazeOpacity·(0.5 + 0.5·enorm)`. `_nebEnergy` resets to 0 in `ShowIndicator`.
- Opacities: `StrandOpacity = 0.34`, `HotOpacity = 0.62`, `HazeOpacity = 0.05` (DeadEye's 3/6 brightness compensation for doubling the strand count). Palette unchanged: strands `#EEDAC2` 0.85 px, hot core `StrandHot` `#FFF9F0` 1.1 px, haze `#EEDAC2` 9 px.
- Clip safety (40 px canvas, ClipToBounds): outer strand max offset `A(13.0)·(0.35+5·0.22=1.45) = 18.85 px` from mid=20 → y∈[1.15, 38.85], inside the ~[1,39] visible band. `|WispNoff| ≤ 1` exactly (weights 1+0.55+0.28=1.83, /1.83), so offset never exceeds `amp`.
- Never-activate constraints, the shared state machine (`Idle→Igniting→Live→Retracting`), the ignition sweep / retract window (`visibleFrom`/`visibleTo`), pip, and the **lantern render path** are all untouched. Real mic samples remain the only data source.
- `DeadAir.Core` stays WPF-free.
- Test command: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore` (`--no-restore` required — NuGet.Config unreadable in the worker sandbox, no package refs change). Filter with `--filter "FullyQualifiedName~ScopeGeometryTests"` while iterating.
- Stop any running `DeadAir.App.exe` before rebuilding (MSB3027 file-lock).

---

### Task 9: ScopeGeometry — MeanAbs + BuildStrandPoints (replace BuildNebulaPoints)

**Files:**
- Modify: `host/DeadAir.Core/ScopeGeometry.cs` (remove `BuildNebulaPoints`; add `MeanAbs` + `BuildStrandPoints`)
- Test: `host/DeadAir.Core.Tests/ScopeGeometryTests.cs` (remove the 6 `BuildNebulaPoints_*` tests; add the region below)

**Interfaces:**
- Consumes: existing `WispNoff`, `WispEnv`, `IgnitionAmp` (all unchanged).
- Produces (Task 10 relies on these exact signatures):
  - `static double ScopeGeometry.MeanAbs(IReadOnlyList<double> samples)`
  - `static (double X, double Y)[] ScopeGeometry.BuildStrandPoints(double width, double height, int segs, double amp, double tSlow, double seed, double k, double head, double visibleFrom = 0.0, double visibleTo = 1.0)`
  - BuildStrandPoints: returns `segs+1` points spanning `[visibleFrom, visibleTo]` (empty if `segs<1` or `visibleTo<=visibleFrom`); `x = u·width` (fixed true-u); `y = height/2 + WispNoff(u,tSlow,seed,k)·amp·WispEnv(u)·IgnitionAmp(u,head)`.

- [ ] **Step 1: Remove the old tests**

In `host/DeadAir.Core.Tests/ScopeGeometryTests.cs`, delete the six tests whose names begin with `BuildNebulaPoints_` (the region added for the old nebula math) and the `Bump`-based ones among them. Leave the `Bump` field and all `WispNoff_*` / `WispEnv_*` tests intact (they're reused / still valid).

- [ ] **Step 2: Write the new failing tests**

Append inside the `ScopeGeometryTests` class (before the closing brace):

```csharp
    // ---- MeanAbs: mean |sample| over the buffer (the sole audio->visual coupling) ----

    [Fact]
    public void MeanAbs_EmptyIsZero()
        => Assert.Equal(0.0, ScopeGeometry.MeanAbs(Array.Empty<double>()));

    [Fact]
    public void MeanAbs_AllZeroIsZero()
        => Assert.Equal(0.0, ScopeGeometry.MeanAbs(new double[8]));

    [Fact]
    public void MeanAbs_AlternatingHalfMagnitudeIsHalf()
        => Assert.Equal(0.5, ScopeGeometry.MeanAbs(new[] { -0.5, 0.5, -0.5, 0.5 }), 12);

    [Fact]
    public void MeanAbs_SingleValueIsItsMagnitude()
        => Assert.Equal(0.8, ScopeGeometry.MeanAbs(new[] { -0.8 }), 12);

    // ---- BuildStrandPoints: smooth wisp strand along the midline, NO PCM spine ----

    [Fact]
    public void BuildStrandPoints_EmptyWhenSegsBelowOne()
        => Assert.Empty(ScopeGeometry.BuildStrandPoints(296, 40, 0, 10, 0, 3.7, 1.0, 1.0));

    [Fact]
    public void BuildStrandPoints_EmptyWhenWindowNonPositive()
        => Assert.Empty(ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0, 0.5, 0.5));

    [Fact]
    public void BuildStrandPoints_ReturnsSegsPlusOnePoints()
        => Assert.Equal(17, ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0).Length);

    [Fact]
    public void BuildStrandPoints_EndpointXIsExactTrueU()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0, 0.25, 0.75);
        Assert.Equal(0.25 * 296, p[0].X, 12);
        Assert.Equal(0.75 * 296, p[16].X, 12);
    }

    [Fact]
    public void BuildStrandPoints_FullWindowEndpointsPinnedToMidline()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 1.0);
        Assert.Equal(20.0, p[0].Y, 12);    // WispEnv(0) == 0
        Assert.Equal(20.0, p[16].Y, 12);   // WispEnv(1) == 0
    }

    [Fact]
    public void BuildStrandPoints_OffsetBoundedByAmp()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 1234, 9.4, 1.03, 1.0);
        Assert.All(p, q => Assert.True(Math.Abs(q.Y - 20.0) <= 10.0 + 1e-9));
    }

    [Fact]
    public void BuildStrandPoints_StaysWithinClipAtMaxAmp()
    {
        // A=13.0 (loud), outer strand factor 1.45 -> amp 18.85; must never clip the 40px canvas.
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 13.0 * 1.45, 777, 32.2, 1.55, 1.0);
        Assert.All(p, q => Assert.True(Math.Abs(q.Y - 20.0) < 19.45));
    }

    [Fact]
    public void BuildStrandPoints_IgnitionGatesPointsBeyondHead()
    {
        var p = ScopeGeometry.BuildStrandPoints(296, 40, 16, 10, 0, 3.7, 1.0, 0.5); // head 0.5
        for (int i = 0; i < p.Length; i++)
        {
            double u = (double)i / 16;
            if (u > 0.5) Assert.Equal(20.0, p[i].Y, 12);   // IgnitionAmp(u>head) == 0
        }
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal --no-restore`
Expected: BUILD FAILURE — `error CS0117: 'ScopeGeometry' does not contain a definition for 'MeanAbs'` (and `BuildStrandPoints`).

- [ ] **Step 4: Remove BuildNebulaPoints and add the new functions**

In `host/DeadAir.Core/ScopeGeometry.cs`: delete the entire `BuildNebulaPoints` method (the one taking `Func<double,double> ampAt, double tSlow, double seed, double k, double noiseAmp, ...`). Keep `WispNoff`, `WispEnv`, `IgnitionAmp`, `BuildPoints`, and all scalar helpers unchanged. Append (before the class closing brace):

```csharp
    /// <summary>Mean |sample| over the buffer — the sole audio→visual coupling for
    /// the nebula skin. On the min/max peak-envelope buffer this is peak magnitude;
    /// ~0 at silence, ~0.3–0.45 for loud speech (samples in [-1,1]).</summary>
    public static double MeanAbs(IReadOnlyList<double> samples)
    {
        int n = samples.Count;
        if (n == 0) return 0;
        double a = 0;
        for (int i = 0; i < n; i++) a += Math.Abs(samples[i]);
        return a / n;
    }

    /// <summary>One smooth nebula strand: a straight spine at y=height/2 with a
    /// WispNoff perpendicular offset (NO PCM waveform), faithfully ported from
    /// DeadEye's wispLitStrand. Sampled at `segs` segments (segs+1 points) across
    /// the true-u window [visibleFrom, visibleTo] — low sampling is what keeps the
    /// curve smooth. WispEnv pinches the ends; IgnitionAmp gates the ignition sweep.</summary>
    public static (double X, double Y)[] BuildStrandPoints(
        double width, double height, int segs, double amp,
        double tSlow, double seed, double k, double head,
        double visibleFrom = 0.0, double visibleTo = 1.0)
    {
        if (segs < 1 || visibleTo <= visibleFrom) return Array.Empty<(double, double)>();
        double mid = height / 2.0, span = visibleTo - visibleFrom;
        var pts = new (double, double)[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            double u = visibleFrom + span * ((double)i / segs);
            double off = WispNoff(u, tSlow, seed, k) * amp * WispEnv(u) * IgnitionAmp(u, head);
            pts[i] = (u * width, mid + off);
        }
        return pts;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" --filter "FullyQualifiedName~ScopeGeometryTests" -v minimal --no-restore`
Expected: PASS — 57 filtered tests (51 − 6 removed + 12 added), 0 failed.

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore`
Expected: PASS — 135 total (129 − 6 + 12), 0 failed. (Report the actual count; **0 failed is the gate**.)

- [ ] **Step 7: Commit**

```bash
git add host/DeadAir.Core/ScopeGeometry.cs host/DeadAir.Core.Tests/ScopeGeometryTests.cs
git commit -m "feat(host): nebula strand math redesign (MeanAbs + BuildStrandPoints, drop spine)"
```

---

### Task 10: Pill window — Nebula redesign render (6 strands, energy-driven fan)

**Files:**
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml` (add Strand3/4/5, retune opacities)
- Modify: `host/DeadAir.App/RecordingIndicatorWindow.xaml.cs` (full rewrite below)

**Interfaces:**
- Consumes: Task 9's `MeanAbs` + `BuildStrandPoints`, plus the unchanged `Breathe`/`Envelope`/`IgnitionAmp`/`BuildPoints`/`IgnitionHead`/`RetractFraction`.
- Produces: no public-surface change. `SetSkin`, `ShowIndicator`, `HideIndicator`, `PushWaveform`, `SetPartial` all keep their signatures.

- [ ] **Step 1: Add Strand3/4/5 and retune opacities in XAML**

In `host/DeadAir.App/RecordingIndicatorWindow.xaml`, change `Strand1` and `Strand2` `Opacity="0.65"` → `Opacity="0.34"`, change `StrandHot` `Opacity="1.0"` → `Opacity="0.62"`, and insert three more strands between `Strand2` and `StrandHot`:

```xml
                <Polyline x:Name="Strand3" Stroke="#EEDAC2" StrokeThickness="0.85"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.34"
                          Visibility="Collapsed"/>
                <Polyline x:Name="Strand4" Stroke="#EEDAC2" StrokeThickness="0.85"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.34"
                          Visibility="Collapsed"/>
                <Polyline x:Name="Strand5" Stroke="#EEDAC2" StrokeThickness="0.85"
                          StrokeLineJoin="Round" StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" Opacity="0.34"
                          Visibility="Collapsed"/>
```

(Result order: `HazeLine`, `Strand1`, `Strand2`, `Strand3`, `Strand4`, `Strand5`, `StrandHot` — `StrandHot` last so the white core draws on top. `GlowLine`/`ScopeLine`/`BeamPip` unchanged.)

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

    // Nebula skin (spec 2026-07-17, redesign amendment): a faithful DeadEye
    // connection-strand port — 6 smooth wisp strands + a 9px haze understroke
    // drawn along the midline (NO PCM spine). Mic loudness opens/closes the fan
    // width and brightness. Ladders carried verbatim from DeadEye's wispLitStrand.
    private const double StrandOpacity = 0.34, HotOpacity = 0.62, HazeOpacity = 0.05;
    private const double NebulaDrift = 0.33;                 // 3x-slowed drift
    private const double SeedBase = 3.7, SeedStep = 5.7, HazeSeed = 34.7;
    private const int NebulaSegs = 16, HazeSegs = 8;         // DeadEye segment counts (smooth at low sampling)
    private const int NebulaStrands = 6;
    // Mic loudness -> fan: smoothed energy drives spread width A and brightness.
    private const double EnergyAttack = 0.20, EnergyRelease = 0.05, EnergyGain = 3.0;
    private const double SpreadFloor = 2.5, SpreadSpan = 10.5;   // fan width A in [2.5, 13.0] px

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
    private double _nebEnergy;         // smoothed mic loudness (asymmetric follower)
    private Polyline[] _strands = null!;  // [0] = StrandHot core, [1..5] = Strand1..5

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
        _strands = new[] { StrandHot, Strand1, Strand2, Strand3, Strand4, Strand5 };
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
        foreach (var s in _strands) s.Visibility = neb;
        GlowLine.Visibility = lan;
        ScopeLine.Visibility = lan;
    }

    public void ShowIndicator()
    {
        _wave.Reset();
        _lastPartial = "";
        _nebEnergy = 0;                 // each recording swells in from calm
        InterimText.Inlines.Clear();

        _state = ScopeState.Igniting;   // re-show mid-retract re-ignites
        _showT0 = _stateT0 = Environment.TickCount64;
        _visibleTo = 0.0;
        ScopeLine.Opacity = CoreOpacity;
        GlowLine.Opacity = GlowOpacity;
        HazeLine.Opacity = HazeOpacity;
        foreach (var s in _strands) s.Opacity = StrandOpacity;
        StrandHot.Opacity = HotOpacity;
        var empty = new PointCollection();
        empty.Freeze();   // frozen: safely shared across polylines
        ScopeLine.Points = empty;
        GlowLine.Points = empty;
        HazeLine.Points = empty;
        foreach (var s in _strands) s.Points = empty;
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
            // Mic loudness -> asymmetric follower (fast swell, slow settle) -> fan width + glow.
            double raw = ScopeGeometry.MeanAbs(_wave.Values);
            _nebEnergy += (raw > _nebEnergy ? EnergyAttack : EnergyRelease) * (raw - _nebEnergy);
            double enorm = Math.Clamp(_nebEnergy * EnergyGain, 0, 1);
            double a = SpreadFloor + enorm * SpreadSpan;   // fan width, 2.5..13.0 px
            double glow = 0.55 + 0.45 * enorm;             // strand brightness, floored
            SetLine(HazeLine, HazeOpacity * (0.5 + 0.5 * enorm) * fade,
                ScopeGeometry.BuildStrandPoints(ScopeWidth, ScopeHeight, HazeSegs,
                    a * 0.7, tSlow, HazeSeed, 1.0, head, visibleFrom, visibleTo));
            for (int s = 0; s < NebulaStrands; s++)
            {
                double baseA = (s == 0 ? HotOpacity : StrandOpacity) * glow * fade;
                SetLine(_strands[s], baseA,
                    ScopeGeometry.BuildStrandPoints(ScopeWidth, ScopeHeight, NebulaSegs,
                        a * (0.35 + s * 0.22), tSlow, SeedBase + s * SeedStep,
                        0.9 + s * 0.13, head, visibleFrom, visibleTo));
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
Expected: Build succeeded, 0 errors (pre-existing NU1701 warning acceptable). If MSB3027 file-lock, a stale `DeadAir.App.exe` is running — report it.

- [ ] **Step 4: Run the full Core suite (no regressions)**

Run: `dotnet test "host/DeadAir.Core.Tests/DeadAir.Core.Tests.csproj" -v minimal --no-restore`
Expected: PASS — 135 total, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add host/DeadAir.App/RecordingIndicatorWindow.xaml host/DeadAir.App/RecordingIndicatorWindow.xaml.cs
git commit -m "feat(host): nebula look redesign — DeadEye connection strands, energy-driven fan"
```

---

### Task 11: Live smoke (user)

**Files:** none (verification only).

- [ ] **Step 1: Relaunch the app**

Stop any running instance (`taskkill /IM DeadAir.App.exe /F`), then `dotnet run --project "host/DeadAir.App/DeadAir.App.csproj"` (background).

- [ ] **Step 2: User smokes the redesigned nebula**

Hold the hotkey, speak, release. Expected: smooth silvered-amber connection strands (no jagged waveform), the bundle **fans wide and brightens while speaking**, settles **narrow and calm** when quiet; ignition sweep + pip and right-anchored retract still work; lantern skin (via Settings) unchanged and still shows the literal waveform.

- [ ] **Step 3: If the fan over/under-opens — tune EnergyGain**

The one empirical constant is `EnergyGain = 3.0` (assumes loud-speech `MeanAbs ≈ 0.33`). If the fan barely opens, raise it; if it pins wide instantly, lower it. This is a single-constant change in `RecordingIndicatorWindow.xaml.cs`, re-smoke.

- [ ] **Step 4: Log check**

`Get-Content "$env:APPDATA\DeadAir\logs\deadair-$(Get-Date -Format yyyyMMdd).log" -Tail 20` — no ERROR lines from the smoke.
