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
        // COMMITTED: the paste has fired. Nothing below may fail the strategy,
        // or the composite would re-inject via SendInput (double-typed text).
        try
        {
            await Task.Delay(restoreDelayMs);
            if (previous is not null) clipboard.SetText(previous);
        }
        catch
        {
            // Restore failure loses the user's old clipboard — tolerable;
            // double-injection is not.
        }
        return true;
    }
}
