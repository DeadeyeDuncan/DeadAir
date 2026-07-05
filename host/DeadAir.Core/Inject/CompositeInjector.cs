namespace DeadAir.Core.Inject;

public sealed class CompositeInjector(
    IReadOnlyList<IInjectionStrategy> strategies, IClipboard clipboard)
    : ITextInjector
{
    public async Task<bool> InjectAsync(string text)
    {
        foreach (var s in strategies)
        {
            try { if (await s.TryInjectAsync(text)) return true; }
            catch { /* try next strategy */ }
        }
        try { clipboard.SetText(text); } // never lose words (spec §4.1 rule 3)
        catch { /* clipboard contention — nothing left to fall back to; report failure */ }
        return false;
    }
}
