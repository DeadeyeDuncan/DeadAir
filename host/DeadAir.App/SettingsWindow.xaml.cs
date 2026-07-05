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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.Hotkey.Key = Selected(HotkeyBox);
        _config.Asr.Engine = Selected(EngineBox);
        _config.Ollama.Model = OllamaModelBox.Text.Trim();
        _config.Cleanup.Mode = Enum.Parse<CleanupMode>(Selected(ModeBox));
        _config.Dictionary = DictionaryBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries |
                                        StringSplitOptions.TrimEntries)
            .ToList();
        _onSaved();
        Close();
    }
}
