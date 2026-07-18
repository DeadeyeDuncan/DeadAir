using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using DeadAir.Core.Config;

namespace DeadAir.App;

public partial class SettingsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark-mode the non-client title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20).
        // Best-effort: on failure the bar just stays light.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private readonly AppConfig _config;
    private readonly Action _onSaved;

    private static readonly string[] HotkeyChoices =
    { "RControl", "RAlt", "CapsLock", "F13", "Scroll", "Pause" };

    public SettingsWindow(AppConfig config, Action onSaved)
    {
        InitializeComponent();
        _config = config;
        _onSaved = onSaved;
        foreach (var k in HotkeyChoices)
            HotkeyBox.Items.Add(new ComboBoxItem { Content = k });
        Select(HotkeyBox, _config.Hotkey.Key);
        Select(EngineBox, _config.Asr.Engine);
        OllamaModelBox.Text = _config.Ollama.Model;
        Select(ModeBox, _config.Cleanup.Mode.ToString());
        var outputLang = string.IsNullOrWhiteSpace(_config.Cleanup.OutputLanguage)
            ? "English" : _config.Cleanup.OutputLanguage.Trim();
        // Hand-edited languages (config is free-form) must survive the
        // settings round-trip, not silently snap back to English.
        if (!OutputLanguageBox.Items.Cast<ComboBoxItem>().Any(i =>
                string.Equals((string)i.Content, outputLang,
                    StringComparison.OrdinalIgnoreCase)))
            OutputLanguageBox.Items.Add(new ComboBoxItem { Content = outputLang });
        Select(OutputLanguageBox, outputLang);
        Select(SkinBox, _config.Pill.Skin);
        FanGainSlider.Value = Math.Clamp(_config.Pill.FanGain, 0.5, 8.0);
        WiggleSlider.Value = Math.Clamp(_config.Pill.Wiggle, 0.0, 1.5);
        WiggleSpeedSlider.Value = Math.Clamp(_config.Pill.WiggleSpeed, 0.0, 4.0);
        UpdateTuningLabels();
        DictionaryBox.Text = string.Join(Environment.NewLine,
            _config.Dictionary);
    }

    private static void Select(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
            if (string.Equals((string)item.Content, value,
                    StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string Selected(ComboBox box) =>
        (string)((ComboBoxItem)box.SelectedItem).Content;

    private void OnTuningChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e) => UpdateTuningLabels();

    private void UpdateTuningLabels()
    {
        // ValueChanged can fire during InitializeComponent before the labels exist.
        if (FanGainValue is null || WiggleValue is null || WiggleSpeedValue is null)
            return;
        FanGainValue.Text = FanGainSlider.Value.ToString("0.0");
        WiggleValue.Text = WiggleSlider.Value.ToString("0.00");
        WiggleSpeedValue.Text = WiggleSpeedSlider.Value.ToString("0.0");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.Hotkey.Key = Selected(HotkeyBox);
        _config.Asr.Engine = Selected(EngineBox);
        _config.Ollama.Model = OllamaModelBox.Text.Trim();
        _config.Cleanup.Mode = Enum.Parse<CleanupMode>(Selected(ModeBox));
        _config.Cleanup.OutputLanguage = Selected(OutputLanguageBox);
        _config.Pill.Skin = Selected(SkinBox);
        _config.Pill.FanGain = FanGainSlider.Value;
        _config.Pill.Wiggle = WiggleSlider.Value;
        _config.Pill.WiggleSpeed = WiggleSpeedSlider.Value;
        _config.Dictionary = DictionaryBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries |
                                        StringSplitOptions.TrimEntries)
            .ToList();
        _onSaved();
        Close();
    }
}
