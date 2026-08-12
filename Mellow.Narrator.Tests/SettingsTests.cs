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
        Assert.Equal(NarratorLogLevel.Information, settings.Logging.MinimumLevel);
        Assert.Equal(2, settings.ContentLimits.MinSuggestedActions);
        Assert.Equal(3, settings.ContentLimits.MaxSuggestedActions);
    }

    [Fact]
    public void PromptTemplateDefaults_AreGeneratedFromSharedTemplates()
    {
        var templates = PromptTemplateDefaults.Create();
        Assert.Contains("Story Bible", templates.StoryDefinitionInstruction);
        Assert.Contains("Story Definition Prompt", templates.StoryDefinitionInstruction);
        Assert.Contains("the entire Story", templates.StoryDefinitionInstruction);
        Assert.Contains(
            PromptTemplateDefaults.ValidationErrorPlaceholder,
            templates.CorrectiveRetryInstruction);
        Assert.Contains(
            PromptTemplateDefaults.SchemaPlaceholder,
            templates.PromptedJsonInstruction);
        Assert.Contains(
            PromptTemplateDefaults.MinSuggestedActionsPlaceholder,
            templates.StoryNarrationInstruction);
        Assert.Contains(
            PromptTemplateDefaults.MaxSuggestedActionsPlaceholder,
            templates.StoryNarrationInstruction);
        Assert.Contains("Condition gate: treat a planned event's non-null condition as a hard lock", templates.StoryNarrationInstruction);
        Assert.Contains("locked event until a later turn", templates.StoryNarrationInstruction);
        Assert.Contains("Pacing and progression: every response must do both", templates.StoryNarrationInstruction);
        Assert.Contains("leave the player in a materially new situation", templates.StoryNarrationInstruction);
        Assert.Contains("hard response requirements, not stylistic targets", templates.StoryNarrationInstruction);
    }

    [Fact]
    public void Validator_RejectsUnknownLoggingLevel()
    {
        var settings = NarratorDefaults.Create() with
        {
            Logging = new((NarratorLogLevel)999)
        };

        Assert.Contains(
            nameof(LoggingSettings.MinimumLevel),
            SettingsValidator.Validate(settings).Keys);
    }

    [Fact]
    public void Validator_DoesNotClampInvalidValues()
    {
        var settings = NarratorDefaults.Create() with { MaxOutputTokens = 1 };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(nameof(ApiConnectionSettings.MaxOutputTokens), errors.Keys);
        Assert.Equal(1, settings.MaxOutputTokens);
    }

    [Fact]
    public void Validator_AcceptsEveryInclusiveLowerBoundary()
    {
        var value = NarratorDefaults.Create() with
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            MaxOutputTokens = 256,
            Parameters = new(0, 0, null),
            StoryGeneration = new(0, 1, 100, 1000, 50, 1, 100, 1000, 50),
            Retry = new(0, TimeSpan.FromSeconds(.25), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
            ContentLimits = new(1, 1, 100, 1, 100, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 500, 64 * 1024) { MinSuggestedActions = 1 }
        };
        Assert.Empty(SettingsValidator.Validate(value));
    }

    [Fact]
    public void Validator_AcceptsEveryInclusiveUpperBoundary()
    {
        var value = NarratorDefaults.Create() with
        {
            RequestTimeout = TimeSpan.FromSeconds(900),
            MaxOutputTokens = 131072,
            Parameters = new(2, 1, "high"),
            StoryGeneration = new(100, 2000, 50000, 1000000, 95, 500, 50000, 1000000, 95),
            Retry = new(5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(600)),
            ContentLimits = new(1000, 1000, 200000, 50000, 200000, 20, 5000, 1000, 2000, 1000, 5000, 5000, 1000, 200, 5000, 20000, 16 * 1024 * 1024) { MinSuggestedActions = 20 }
        };
        Assert.Empty(SettingsValidator.Validate(value));
    }

    [Fact]
    public void Validator_RejectsMaximumDelayBelowInitialDelay()
    {
        var value = NarratorDefaults.Create() with
        {
            Retry = new(2, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60))
        };
        Assert.Contains("MaxDelay", SettingsValidator.Validate(value).Keys);
    }

    [Fact]
    public void Validator_RejectsMinSuggestedActionsAboveMax()
    {
        var value = NarratorDefaults.Create() with
        {
            ContentLimits = NarratorDefaults.Create().ContentLimits with { MinSuggestedActions = 5, MaxSuggestedActions = 3 }
        };
        Assert.Contains(nameof(ContentLimitSettings.MinSuggestedActions), SettingsValidator.Validate(value).Keys);
    }

    [Fact]
    public void Validator_RejectsRelativeBaseUrl()
    {
        var value = NarratorDefaults.Create() with { BaseUrl = new Uri("/v1", UriKind.Relative) };
        Assert.Contains(nameof(ApiConnectionSettings.BaseUrl), SettingsValidator.Validate(value).Keys);
    }

    [Fact]
    public void Validator_RejectsNonHttpBaseUrlScheme()
    {
        var value = NarratorDefaults.Create() with { BaseUrl = new Uri("ftp://provider.example/v1") };
        Assert.Contains(nameof(ApiConnectionSettings.BaseUrl), SettingsValidator.Validate(value).Keys);
    }

    [Fact]
    public void Validator_AcceptsAbsoluteHttpsBaseUrl()
    {
        var value = NarratorDefaults.Create() with { BaseUrl = new Uri("https://provider.example/v1") };
        Assert.Empty(SettingsValidator.Validate(value));
    }

    [Fact]
    public void Validator_RejectsEntryCharacterLimitAboveTotalLimit()
    {
        var value = NarratorDefaults.Create() with
        {
            StoryGeneration = NarratorDefaults.Create().StoryGeneration with
            {
                MaxStoryBibleEntryCharacters = 2000,
                MaxStoryBibleCharacters = 1000
            }
        };
        Assert.Contains("MaxStoryBibleEntryCharacters", SettingsValidator.Validate(value).Keys);
    }
}
