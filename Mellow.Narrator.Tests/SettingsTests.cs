using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var settings = NarratorDefaults.Create();
        Assert.Empty(SettingsValidator.Validate(settings));
        Assert.Equal(8, settings.StoryGeneration.RecentTurnCount);
        Assert.Equal(200, settings.StoryGeneration.MaxStoryBibleEntries);
    }

    [Fact]
    public void Validator_DoesNotClampInvalidValues()
    {
        var settings = NarratorDefaults.Create() with { MaxOutputTokens = 1 };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(nameof(ApiConnectionSettings.MaxOutputTokens), errors.Keys);
        Assert.Equal(1, settings.MaxOutputTokens);
    }
}
