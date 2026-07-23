namespace Mellow.Narrator.Core;

public sealed record ModelParameters(double? Temperature, double? TopP, string? ReasoningEffort);

public sealed record StoryGenerationSettings(
    int RecentTurnCount,
    int MaxStoryBibleEntries,
    int MaxStoryBibleEntryCharacters,
    int MaxStoryBibleCharacters,
    int StoryBibleWarningPercent);

public sealed record RetrySettings(int MaxAutomaticRetries, TimeSpan InitialDelay, TimeSpan MaxDelay, TimeSpan MaxRetryAfter);

public sealed record ContentLimitSettings(
    int MaxStoryTitleCharacters,
    int MaxStoryLabelCharacters,
    int MaxStoryPromptCharacters,
    int MaxPlayerQuestionCharacters,
    int MaxValidationInstructionCharacters,
    int MaxPlayerAnswerCharacters,
    int MaxPlayerActionCharacters,
    int MaxNarrationCharacters,
    int MaxSuggestedActions,
    int MaxSuggestedActionCharacters,
    int MaxStoryBibleCategoryCharacters,
    int MaxStoryBibleNameCharacters,
    int MaxStoryBibleUpdatesPerResponse,
    int MaxResponseBodyBytes);

public enum StructuredOutputTier { Untested, StrictJsonSchema, JsonMode, PromptedJson, Unsupported }

public sealed record ConnectionCapabilities(
    bool SupportsModelDiscovery,
    StructuredOutputTier StructuredOutputTier,
    string? TestedModelId,
    DateTimeOffset? TestedAtUtc);

public sealed record ApiConnectionSettings(
    Uri? BaseUrl,
    string? ModelId,
    TimeSpan RequestTimeout,
    int MaxOutputTokens,
    ModelParameters Parameters,
    StoryGenerationSettings StoryGeneration,
    RetrySettings Retry,
    ContentLimitSettings ContentLimits,
    ConnectionCapabilities Capabilities);

public static class NarratorDefaults
{
    public static ApiConnectionSettings Create() => new(
        null,
        null,
        TimeSpan.FromSeconds(120),
        4096,
        new(null, null, null),
        new(8, 200, 4000, 60000, 80),
        new(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
        new(200, 200, 20000, 1000, 2000, 4000, 4000, 20000, 6, 500, 100, 200, 100, 2 * 1024 * 1024),
        new(false, StructuredOutputTier.Untested, null, null));
}

public static class SettingsValidator
{
    public static IReadOnlyDictionary<string, string> Validate(ApiConnectionSettings value)
    {
        var errors = new Dictionary<string, string>();
        Range(errors, nameof(value.RequestTimeout), value.RequestTimeout.TotalSeconds, 10, 900);
        Range(errors, nameof(value.MaxOutputTokens), value.MaxOutputTokens, 256, 131072);
        OptionalRange(errors, "Temperature", value.Parameters.Temperature, 0, 2);
        OptionalRange(errors, "TopP", value.Parameters.TopP, 0, 1);
        Range(errors, "RecentTurnCount", value.StoryGeneration.RecentTurnCount, 0, 100);
        Range(errors, "MaxStoryBibleEntries", value.StoryGeneration.MaxStoryBibleEntries, 1, 2000);
        Range(errors, "MaxStoryBibleEntryCharacters", value.StoryGeneration.MaxStoryBibleEntryCharacters, 100, 50000);
        Range(errors, "MaxStoryBibleCharacters", value.StoryGeneration.MaxStoryBibleCharacters, 1000, 1000000);
        Range(errors, "StoryBibleWarningPercent", value.StoryGeneration.StoryBibleWarningPercent, 50, 95);
        Range(errors, "MaxAutomaticRetries", value.Retry.MaxAutomaticRetries, 0, 5);
        Range(errors, "InitialDelay", value.Retry.InitialDelay.TotalSeconds, .25, 30);
        Range(errors, "MaxDelay", value.Retry.MaxDelay.TotalSeconds, 1, 120);
        Range(errors, "MaxRetryAfter", value.Retry.MaxRetryAfter.TotalSeconds, 1, 600);
        if (value.Retry.MaxDelay < value.Retry.InitialDelay)
            errors["MaxDelay"] = "Maximum retry delay must be at least the initial delay.";

        var c = value.ContentLimits;
        Range(errors, nameof(c.MaxStoryTitleCharacters), c.MaxStoryTitleCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryLabelCharacters), c.MaxStoryLabelCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryPromptCharacters), c.MaxStoryPromptCharacters, 100, 200000);
        Range(errors, nameof(c.MaxPlayerQuestionCharacters), c.MaxPlayerQuestionCharacters, 1, 10000);
        Range(errors, nameof(c.MaxValidationInstructionCharacters), c.MaxValidationInstructionCharacters, 1, 20000);
        Range(errors, nameof(c.MaxPlayerAnswerCharacters), c.MaxPlayerAnswerCharacters, 1, 50000);
        Range(errors, nameof(c.MaxPlayerActionCharacters), c.MaxPlayerActionCharacters, 1, 50000);
        Range(errors, nameof(c.MaxNarrationCharacters), c.MaxNarrationCharacters, 100, 200000);
        Range(errors, nameof(c.MaxSuggestedActions), c.MaxSuggestedActions, 1, 20);
        Range(errors, nameof(c.MaxSuggestedActionCharacters), c.MaxSuggestedActionCharacters, 1, 5000);
        Range(errors, nameof(c.MaxStoryBibleCategoryCharacters), c.MaxStoryBibleCategoryCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryBibleNameCharacters), c.MaxStoryBibleNameCharacters, 1, 2000);
        Range(errors, nameof(c.MaxStoryBibleUpdatesPerResponse), c.MaxStoryBibleUpdatesPerResponse, 1, 1000);
        Range(errors, nameof(c.MaxResponseBodyBytes), c.MaxResponseBodyBytes, 64 * 1024, 16 * 1024 * 1024);
        return errors;
    }

    private static void OptionalRange(IDictionary<string, string> errors, string name, double? value, double min, double max)
    {
        if (value is not null) Range(errors, name, value.Value, min, max);
    }

    private static void Range(IDictionary<string, string> errors, string name, double value, double min, double max)
    {
        if (value < min || value > max) errors[name] = $"Must be between {min} and {max}.";
    }
}

public static class SecureStorageKeys
{
    public const string ApiCredential = "mellow-narrator.api-credential";
}
