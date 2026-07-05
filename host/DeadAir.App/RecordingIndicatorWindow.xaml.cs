using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
