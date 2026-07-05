namespace DeadAir.Core.Inject;

public interface ITextInjector
{
    /// <returns>true = injected; false = failed, text left on clipboard.</returns>
    Task<bool> InjectAsync(string text);
}

public interface IInjectionStrategy
{
    Task<bool> TryInjectAsync(string text);
}

public interface IClipboard
{
    string? GetText();
    void SetText(string text);
}
