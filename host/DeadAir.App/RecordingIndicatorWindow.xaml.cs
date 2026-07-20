using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeadAir.Core;
using DeadAir.Core.Config;

namespace DeadAir.App;

public partial class RecordingIndicatorWindow : Window
{
    private const int ScopeSamples = 296;      // one sample per horizontal px
    private const double ScopeWidth = 296, ScopeHeight = 40;
    private const int InterimMaxChars = 46;

    // Nebula skin (spec 2026-07-17, redesign amendment): a faithful DeadEye
    // connection-strand port — 6 smooth wisp strands + a 9px haze understroke
    // drawn along the midline (NO PCM spine). Mic loudness opens/closes the fan
    // width and brightness. Ladders carried verbatim from DeadEye's wispLitStrand.
    private const double StrandOpacity = 0.34, HotOpacity = 0.62, HazeOpacity = 0.05;
    private const double NebulaDrift = 0.33;                 // 3x-slowed drift
    private const double SeedBase = 3.7, SeedStep = 5.7, HazeSeed = 34.7;
    private const int NebulaSegs = 48, HazeSegs = 16;        // 48 renders the voice-gated 2u turbulence octave (~40px waves); haze stays calm at 16
    private const double TurbScrollRate = 0.00035;           // octave pattern shift per phase-ms; the 2u term halves the u-space translation -> ~1/6 pill per second at full voice, x WiggleSpeed dial
    private const int NebulaStrands = 6;
    // Mic loudness -> fan: smoothed energy drives spread width A and brightness.
    private const double EnergyAttack = 0.20, EnergyRelease = 0.05;
    private const double SpreadFloor = 5.5, SpreadSpan = 7.5;   // fan width A in [5.5, 13.0] px

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    /// <summary>Nebula lifecycle: fade-in on show, live drift, retract-then-hide
    /// on stop.</summary>
    private enum ScopeState { Idle, Igniting, Live, Retracting }

    private readonly WaveformRingBuffer _wave = new(ScopeSamples);
    private readonly SolidColorBrush _dim =
        new(Color.FromArgb(0xFF, 0x98, 0x18, 0x0F));   // settled interim words: dark red
    private readonly SolidColorBrush _hot =
        new(Color.FromArgb(0xFF, 0xE8, 0x38, 0x2B));   // freshest word: light red
    private string _lastPartial = "";

    private ScopeState _state = ScopeState.Idle;
    private long _stateT0;   // Environment.TickCount64 at state entry
    // Rightmost drawable u. Tracks the beam head during ignition and is left
    // frozen if a retract starts mid-ignition, so the retract can't reveal
    // trace the beam never wrote.
    private double _visibleTo = 1.0;
    private bool _tickHooked;
    private double _nebEnergy;         // smoothed mic loudness (asymmetric follower)
    private double _nebPhase;          // accumulated drift phase (voice-speed clock)
    private long _nebLastT;            // last nebula frame time for dt
    // Nebula dials (config-backed via ApplyPillTuning; defaults = shipped constants).
    private double _fanGain = 3.0, _turbSpan = 0.6, _wiggleSpeed = 1.0;
    private Polyline[] _strands = null!;  // [0] = StrandHot core, [1..5] = Strand1..5
    private readonly DispatcherTimer _statusTimer;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
        _strands = new[] { StrandHot, Strand1, Strand2, Strand3, Strand4, Strand5 };
        _statusTimer = new DispatcherTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); HideIndicator(); };
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

    /// <summary>Apply the nebula tuning dials from config, range-clamped
    /// (degrade-don't-crash: hand-edited config lands in the legal range).</summary>
    public void ApplyPillTuning(PillConfig pill)
    {
        _fanGain = Math.Clamp(pill.FanGain, 0.5, 8.0);
        _turbSpan = Math.Clamp(pill.Wiggle, 0.0, 1.5);
        _wiggleSpeed = Math.Clamp(pill.WiggleSpeed, 0.0, 4.0);
    }

    public void ShowIndicator()
    {
        _wave.Reset();
        _lastPartial = "";
        _nebEnergy = 0;                 // each recording swells in from calm
        _nebPhase = 0;
        _nebLastT = Environment.TickCount64;
        InterimText.Inlines.Clear();

        _statusTimer.Stop();
        _state = ScopeState.Igniting;   // re-show mid-retract re-ignites
        _stateT0 = Environment.TickCount64;
        _visibleTo = 0.0;
        HazeLine.Opacity = HazeOpacity;
        foreach (var s in _strands) s.Opacity = StrandOpacity;
        StrandHot.Opacity = HotOpacity;
        var empty = new PointCollection();
        empty.Freeze();   // frozen: safely shared across polylines
        HazeLine.Points = empty;
        foreach (var s in _strands) s.Points = empty;
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

        switch (_state)
        {
            case ScopeState.Igniting:
            {
                double head = ScopeGeometry.IgnitionHead(tState);
                _visibleTo = head;
                RenderFrame(now, 0.0, 1.0);
                if (head >= 1)
                {
                    _state = ScopeState.Live;
                    _stateT0 = now;
                    _visibleTo = 1.0;
                }
                return;
            }
            case ScopeState.Live:
            {
                RenderFrame(now, 0.0, 1.0);
                return;
            }
            case ScopeState.Retracting:
            {
                double rf = ScopeGeometry.RetractFraction(tState);
                double visibleFrom = 1 - rf;
                if (rf <= 0 || visibleFrom >= 1.0)
                {
                    _state = ScopeState.Idle;
                    Hide();
                    UnhookTick();
                    return;
                }
                // Length and alpha reach zero together — no pop (spec).
                RenderFrame(now, visibleFrom, rf);
                return;
            }
            default:
                return;
        }
    }

    /// <summary>One nebula frame. fade is 1 outside retract and scales each
    /// strand's base opacity during it; visibleFrom slides the left edge in as
    /// the pill retracts.</summary>
    private void RenderFrame(long now, double visibleFrom, double fade)
    {
        double dt = Math.Clamp(now - _nebLastT, 0, 100);   // hitch clamp (DeadEye precedent)
        _nebLastT = now;
        // Mic loudness -> asymmetric follower (fast swell, slow settle) -> fan width + glow.
        double raw = ScopeGeometry.MeanAbs(_wave.Values);
        _nebEnergy += (raw > _nebEnergy ? EnergyAttack : EnergyRelease) * (raw - _nebEnergy);
        double enorm = Math.Clamp(_nebEnergy * _fanGain, 0, 1);
        _nebPhase += dt * NebulaDrift * ScopeGeometry.NebulaPhaseRate(enorm);
        double tSlow = _nebPhase;
        double a = SpreadFloor + enorm * SpreadSpan;   // fan width, 5.5..13.0 px
        double glow = 0.55 + 0.45 * enorm;             // strand brightness, floored
        // Fade-in from _visibleTo: it freezes the ignition progress if a hide
        // interrupts the ramp, so the retract fades from the brightness the
        // strands actually reached.
        double ign = _visibleTo;
        double ignFade = ign >= 1 ? 1.0 : ign * ign * (3 - 2 * ign);
        SetLine(HazeLine, HazeOpacity * (0.5 + 0.5 * enorm) * ignFade * fade,
            ScopeGeometry.BuildStrandPoints(ScopeWidth, ScopeHeight, HazeSegs,
                a * 0.7, tSlow, HazeSeed, 1.0, head: 1.0, visibleFrom, visibleTo: 1.0));
        for (int s = 0; s < NebulaStrands; s++)
        {
            double baseA = (s == 0 ? HotOpacity : StrandOpacity) * glow * ignFade * fade;
            SetLine(_strands[s], baseA,
                ScopeGeometry.BuildStrandPoints(ScopeWidth, ScopeHeight, NebulaSegs,
                    a * (0.35 + s * 0.22), tSlow, SeedBase + s * SeedStep,
                    0.9 + s * 0.13, head: 1.0, visibleFrom, visibleTo: 1.0,
                    _turbSpan * enorm,     // voice-gated wiggle; haze stays calm
                    tSlow * TurbScrollRate * _wiggleSpeed));   // traveling wave — scroll speed rides the voice-rated clock
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
}
