using System.Windows;
using System.Windows.Controls;
using DeadAir.Core.Config;

namespace DeadAir.App;

public partial class SettingsWindow : Window
{
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
            if ((string)item.Content == value) { box.SelectedItem = item; return; }
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
