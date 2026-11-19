using ErpConnect.Core.Settings;

namespace ErpConnect.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_match_the_validated_retention()
    {
        var s = new EngineSettings();
        s.RunHeaderRetentionDays.ShouldBe(396);
        s.SucceededLogRetentionDays.ShouldBe(30);
        s.FailedLogRetentionDays.ShouldBe(90);
        s.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Retention_below_seven_days_is_refused()
    {
        var errors = (new EngineSettings() with { SucceededLogRetentionDays = 6, LogLevel = "Verbose" }).Validate();
        errors.Select(e => e.Field).ShouldBe(["SucceededLogRetentionDays", "LogLevel"], ignoreOrder: true);
    }
}
