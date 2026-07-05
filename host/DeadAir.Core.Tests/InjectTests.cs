using DeadAir.Core.Inject;

namespace DeadAir.Core.Tests;

file sealed class FakeClipboard : IClipboard
{
    public string? Stored;
    public string? GetText() => Stored;
    public void SetText(string text) => Stored = text;
}

file sealed class FakeStrategy(bool result) : IInjectionStrategy
{
    public int Calls;
    public string? LastText;
    public Task<bool> TryInjectAsync(string text)
    { Calls++; LastText = text; return Task.FromResult(result); }
}

file sealed class RestoreThrowingClipboard : IClipboard
{
    public int SetCalls;
    public string? GetText() => "old clipboard";
    public void SetText(string text)
    {
        SetCalls++;
        if (SetCalls > 1) // first set = inject text; second = restore
            throw new InvalidOperationException("CLIPBRD_E_CANT_OPEN");
    }
}

file sealed class AlwaysThrowingClipboard : IClipboard
{
    public string? GetText() => null;
    public void SetText(string text) =>
        throw new InvalidOperationException("CLIPBRD_E_CANT_OPEN");
}

public class InjectTests
{
    [Fact]
    public void BuildUnicodeInputs_EmojiYieldsTwoSurrogateUnits()
    {
        var units = NativeInput.BuildUnicodeInputs("a😀");
        Assert.Equal(3, units.Length);           // 'a' + high + low surrogate
        Assert.Equal((ushort)0xD83D, units[1].Code);
        Assert.Equal((ushort)0xDE00, units[2].Code);
        Assert.All(units, u => Assert.False(u.IsReturn));
    }

    [Fact]
    public void BuildUnicodeInputs_NewlinesBecomeReturn()
    {
        var units = NativeInput.BuildUnicodeInputs("a\r\nb\nc");
        Assert.Equal(5, units.Length);
        Assert.True(units[1].IsReturn);
        Assert.True(units[3].IsReturn);
    }

    [Fact]
    public async Task Composite_FirstStrategyWins()
    {
        var s1 = new FakeStrategy(true);
        var s2 = new FakeStrategy(true);
        var clip = new FakeClipboard();
        var inj = new CompositeInjector(new IInjectionStrategy[] { s1, s2 }, clip);
        Assert.True(await inj.InjectAsync("hello"));
        Assert.Equal(1, s1.Calls);
        Assert.Equal(0, s2.Calls);
    }

    [Fact]
    public async Task Composite_AllFail_LeavesTextOnClipboard()
    {
        var clip = new FakeClipboard();
        var inj = new CompositeInjector(
            new IInjectionStrategy[] { new FakeStrategy(false),
                                       new FakeStrategy(false) }, clip);
        Assert.False(await inj.InjectAsync("precious words"));
        Assert.Equal("precious words", clip.Stored);
    }

    [Fact]
    public void InputStruct_MarshalsToWin32Size()
    {
        // Win32 x64: INPUT = 4(type) + 4(pad) + 32(union incl. MOUSEINPUT) = 40.
        // A wrong size makes SendInput reject every call with ERROR_INVALID_PARAMETER.
        Assert.Equal(40, NativeInput.InputStructSize);
    }

    [Fact]
    public async Task ClipboardPaste_RestoresPriorClipboardOnSuccess()
    {
        var clip = new FakeClipboard { Stored = "old stuff" };
        var pasted = new List<string?>();
        var strat = new ClipboardPasteInjector(clip,
            sendPaste: () => { pasted.Add(clip.GetText()); return true; },
            restoreDelayMs: 0);
        Assert.True(await strat.TryInjectAsync("new text"));
        Assert.Equal("new text", pasted.Single()); // pasted while ours was set
        Assert.Equal("old stuff", clip.Stored);    // then restored
    }

    [Fact]
    public async Task ClipboardPaste_RestoreFailureAfterPaste_StillCommits()
    {
        var clip = new RestoreThrowingClipboard();
        var strat = new ClipboardPasteInjector(clip, sendPaste: () => true,
            restoreDelayMs: 0);
        Assert.True(await strat.TryInjectAsync("new text")); // must NOT throw
    }

    [Fact]
    public async Task Composite_NoDoubleInjection_WhenRestoreThrows()
    {
        var clip = new RestoreThrowingClipboard();
        var paste = new ClipboardPasteInjector(clip, sendPaste: () => true,
            restoreDelayMs: 0);
        var typer = new FakeStrategy(true); // fallback strategy
        var inj = new CompositeInjector(
            new IInjectionStrategy[] { paste, typer }, clip);
        Assert.True(await inj.InjectAsync("hello"));
        Assert.Equal(0, typer.Calls); // fallback must never fire
    }

    [Fact]
    public async Task Composite_AllStrategiesFail_ClipboardSetTextThrows_ReturnsFalseWithoutThrowing()
    {
        // Last-resort "never lose words" clipboard fallback (spec §4.1 rule 3) — if the
        // clipboard itself is contended and SetText throws, InjectAsync must still return
        // false rather than let the exception escape and wedge the orchestrator's state.
        var clip = new AlwaysThrowingClipboard();
        var inj = new CompositeInjector(
            new IInjectionStrategy[] { new FakeStrategy(false), new FakeStrategy(false) },
            clip);
        var ok = await inj.InjectAsync("precious words");
        Assert.False(ok);
    }
}
