namespace DeadAir.Core;

/// <summary>Dim (stable prefix) + hot (just-changed suffix) segments of the
/// interim pill line.</summary>
public readonly record struct InterimLayout(string Dim, string Hot);

/// <summary>Pure helpers for rendering self-correcting interim transcripts.</summary>
public static class PartialText
{
    private static readonly char[] Ws = { ' ', '\t', '\n', '\r' };

    /// <summary>Split into words on any ASCII whitespace, dropping empties.
    /// The single tokenizer the diff and the pill layout both share, so a
    /// tab/newline inside a partial can't misalign the dim/hot boundary.</summary>
    public static string[] SplitWords(string? s) =>
        (s ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Count of leading words identical in both strings.</summary>
    public static int CommonPrefixWords(string? previous, string? current)
    {
        var a = SplitWords(previous);
        var b = SplitWords(current);
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Trim from the LEFT so the newest text stays visible.</summary>
    public static string LeftElide(string text, int maxChars)
    {
        if (maxChars < 1 || text.Length <= maxChars) return text;
        int cut = text.Length - (maxChars - 1);
        // Never split a surrogate pair: a cut landing on the low half would
        // leave a lone surrogate at the front (renders as U+FFFD).
        if (char.IsLowSurrogate(text[cut])) cut++;
        return "…" + text[cut..];
    }

    /// <summary>The dim (stable prefix) / hot (just-changed suffix) split for the
    /// interim line, elided from the LEFT so the newest words always stay visible
    /// within <paramref name="maxChars"/> — even on a re-decode that rewrites the
    /// whole line (all-hot) or produces a hot tail longer than the budget.</summary>
    public static InterimLayout LayoutInterim(string? previous, string? current, int maxChars)
    {
        if (maxChars <= 0) return new InterimLayout("", "");
        var words = SplitWords(current);
        int common = CommonPrefixWords(previous, current);
        string stable = string.Join(' ', words.Take(common));
        string changed = string.Join(' ', words.Skip(common));

        // The just-changed tail is the newest text — it must never be clipped.
        // If it alone fills the budget, drop the stable head and keep the tail's
        // newest chars.
        if (changed.Length >= maxChars)
            return new InterimLayout("", LeftElide(changed, maxChars));

        // Reserve room for the hot tail (+1 for the joining space when present),
        // then left-elide the stable head into whatever budget remains.
        int stableBudget = maxChars - changed.Length - (changed.Length > 0 ? 1 : 0);
        string dim = stableBudget <= 0 ? "" : LeftElide(stable, stableBudget);
        return new InterimLayout(dim, changed);
    }
}
