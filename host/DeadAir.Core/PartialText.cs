namespace DeadAir.Core;

/// <summary>Pure helpers for rendering self-correcting interim transcripts.</summary>
public static class PartialText
{
    private static readonly char[] Ws = { ' ', '\t', '\n', '\r' };

    /// <summary>Count of leading words identical in both strings.</summary>
    public static int CommonPrefixWords(string? previous, string? current)
    {
        var a = (previous ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
        var b = (current ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Trim from the LEFT so the newest text stays visible.</summary>
    public static string LeftElide(string text, int maxChars)
    {
        if (maxChars < 1 || text.Length <= maxChars) return text;
        return "…" + text[^(maxChars - 1)..];
    }
}
