namespace DeadAir.Core.Inject;

public sealed class SendInputInjector : IInjectionStrategy
{
    public Task<bool> TryInjectAsync(string text) =>
        Task.FromResult(NativeInput.SendUnicodeText(text));
}
