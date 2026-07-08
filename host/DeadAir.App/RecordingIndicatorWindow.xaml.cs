using System.Runtime.InteropServices;
using System.Windows;
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int value);

    private readonly WaveformRingBuffer _wave = new(ScopeSamples);
    private readonly SolidColorBrush _dim =
        new(Color.FromArgb(0x66, 0xE1, 0xF5, 0xFE));
    private readonly SolidColorBrush _hot =
        new(Color.FromArgb(0xFF, 0x8B, 0xE9, 0xFD));
    private string _lastPartial = "";

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        _dim.Freeze();
        _hot.Freeze();
        RenderScope();
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

    public void PushWaveform(IReadOnlyList<double> samples)
    {
        _wave.PushRange(samples);
        RenderScope();
    }

    public void SetPartial(string text)
    {
        int common = PartialText.CommonPrefixWords(_lastPartial, text);
        _lastPartial = text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string stable = string.Join(' ', words.Take(common));
        string changed = string.Join(' ', words.Skip(common));

        // Left-elide the stable head so the newest (changed) words stay visible.
        stable = PartialText.LeftElide(stable, InterimMaxChars);

        InterimText.Inlines.Clear();
        if (stable.Length > 0)
            InterimText.Inlines.Add(new Run(stable + " ") { Foreground = _dim });
        if (changed.Length > 0)
            InterimText.Inlines.Add(new Run(changed) { Foreground = _hot });
    }

    public void ShowIndicator()
    {
        _wave.Reset();
        _lastPartial = "";
        InterimText.Inlines.Clear();
        RenderScope();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 16;
        Show(); // never Activate()
    }

    public void HideIndicator() => Hide();

    private void RenderScope()
    {
        var v = _wave.Values;
        var pts = new PointCollection(v.Count);
        double mid = ScopeHeight / 2.0;
        for (int i = 0; i < v.Count; i++)
        {
            double x = i * (ScopeWidth / (v.Count - 1));
            double y = mid - v[i] * mid;   // sample in [-1,1] -> canvas y
            pts.Add(new Point(x, y));
        }
        ScopeLine.Points = pts;
    }
}
