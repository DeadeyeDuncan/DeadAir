using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DeadAir.Core;
using DeadAir.Core.Config;

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
    private const int NebulaSegs = 48, HazeSegs = 16;        // 48 renders the voice-gated 2u turbulence octave (~40px waves); haze stays calm at 16
    private const double TurbScrollRate = 0.00035;           // u per phase-ms: octave crosses ~1/3 of the pill per second at full voice
    private const int NebulaStrands = 6;
    // Mic loudness -> fan: smoothed energy drives spread width A and brightness.
    private const double EnergyAttack = 0.20, EnergyRelease = 0.05;
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
    private double _nebPhase;          // accumulated drift phase (voice-speed clock)
    private long _nebLastT;            // last nebula frame time for dt
    // Nebula dials (config-backed via ApplyPillTuning; defaults = shipped constants).
    private double _fanGain = 3.0, _turbSpan = 0.6, _wiggleSpeed = 1.0;
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

    /// <summary>Apply the nebula tuning dials from config, range-clamped
    /// (degrade-don't-crash: hand-edited config lands in the legal range).</summary>
    public void ApplyPillTuning(PillConfig pill)
    {
        _fanGain = Math.Clamp(pill.FanGain, 0.5, 8.0);
        _turbSpan = Math.Clamp(pill.Wiggle, 0.0, 1.5);
        _wiggleSpeed = Math.Clamp(pill.WiggleSpeed, 0.0, 4.0);
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
        _nebPhase = 0;
        _nebLastT = Environment.TickCount64;
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
            double dt = Math.Clamp(now - _nebLastT, 0, 100);   // hitch clamp (DeadEye precedent)
            _nebLastT = now;
            // Mic loudness -> asymmetric follower (fast swell, slow settle) -> fan width + glow.
            double raw = ScopeGeometry.MeanAbs(_wave.Values);
            _nebEnergy += (raw > _nebEnergy ? EnergyAttack : EnergyRelease) * (raw - _nebEnergy);
            double enorm = Math.Clamp(_nebEnergy * _fanGain, 0, 1);
            _nebPhase += dt * NebulaDrift * ScopeGeometry.NebulaPhaseRate(enorm);
            double tSlow = _nebPhase;
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
                        0.9 + s * 0.13, head, visibleFrom, visibleTo,
                        _turbSpan * enorm,     // voice-gated wiggle; haze stays calm
                        tSlow * TurbScrollRate * _wiggleSpeed));   // traveling wave — scroll speed rides the voice-rated clock
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
