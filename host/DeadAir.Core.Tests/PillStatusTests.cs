using DeadAir.Core;
using DeadAir.Core.Config;

namespace DeadAir.Core.Tests;

public class PillStatusTests
{
    [Theory]
    [InlineData(FlowState.Recording)]
    [InlineData(FlowState.Idle)]
    [InlineData(FlowState.Cleaning)]
    public void StatesWithNoCaption(FlowState state) =>
        Assert.Null(PillStatus.ForState(state));

    [Fact]
    public void Transcribing_Captions()
    {
        var c = PillStatus.ForState(FlowState.Transcribing);
        Assert.Equal("transcribing…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Injecting_Captions()
    {
        var c = PillStatus.ForState(FlowState.Injecting);
        Assert.Equal("injecting…", c!.Value.Text);
        Assert.False(c.Value.Dismiss);
    }

    [Fact]
    public void Cleaning_WhileTranslating_SaysTranslating()
    {
        var c = PillStatus.ForCleaning(CleanupMode.Faithful, translating: true);
        Assert.Equal("translating…", c.Text);
        Assert.False(c.Dismiss);
    }

    [Fact]
    public void Cleaning_Polished_SaysPolishing() =>
        Assert.Equal("polishing…", PillStatus.ForCleaning(CleanupMode.Polished, false).Text);

    [Fact]
    public void Cleaning_Faithful_SaysCleaning() =>
        Assert.Equal("cleaning…", PillStatus.ForCleaning(CleanupMode.Faithful, false).Text);

    [Theory]
    [InlineData(FlowOutcome.Injected, "sent")]
    [InlineData(FlowOutcome.NothingHeard, "nothing heard")]
    [InlineData(FlowOutcome.Failed, "failed")]
    [InlineData(FlowOutcome.TimedOut, "timed out")]
    [InlineData(FlowOutcome.Interrupted, "interrupted")]
    public void Outcomes_CaptionAndAlwaysDismiss(FlowOutcome outcome, string text)
    {
        var c = PillStatus.ForOutcome(outcome);
        Assert.Equal(text, c.Text);
        Assert.True(c.Dismiss);
    }

    [Theory]
    [InlineData(FlowState.Recording, true)]
    [InlineData(FlowState.Idle, false)]
    [InlineData(FlowState.Transcribing, false)]
    [InlineData(FlowState.Cleaning, false)]
    [InlineData(FlowState.Injecting, false)]
    public void SuppressTerminal_OnlyDuringRecording(FlowState last, bool expected) =>
        Assert.Equal(expected, PillStatus.SuppressTerminal(last));
}
