namespace DeadAir.Core.Inject;

public sealed class ClipboardPasteInjector(
    IClipboard clipboard, Func<bool> sendPaste, int restoreDelayMs)
    : IInjectionStrategy
{
    public async Task<bool> TryInjectAsync(string text)
    {
        var previous = clipboard.GetText();
        clipboard.SetText(text);
        await Task.Delay(50); // let the clipboard settle before pasting
        if (!sendPaste()) return false;
        await Task.Delay(restoreDelayMs);
        if (previous is not null) clipboard.SetText(previous);
        return true;
    }
}
