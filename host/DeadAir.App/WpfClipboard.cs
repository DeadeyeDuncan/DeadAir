using System.Windows;
using System.Windows.Threading;
using DeadAir.Core.Inject;

namespace DeadAir.App;

public sealed class WpfClipboard(Dispatcher dispatcher) : IClipboard
{
    public string? GetText() => dispatcher.Invoke(() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public void SetText(string text) => dispatcher.Invoke(() =>
        Clipboard.SetDataObject(text, copy: true));
}
