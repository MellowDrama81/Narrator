namespace Mellow.Narrator.Core;

public sealed record ModelParameters(double? Temperature, double? TopP, string? ReasoningEffort);

public enum NarratorLogLevel { Trace, Debug, Information, Warning, Error, Off }

public sealed record LoggingSettings(NarratorLogLevel MinimumLevel);

public static class LoggingDefaults
{
    public static LoggingSettings Create() => new(NarratorLogLevel.Information);
}

public sealed record PromptTemplateSettings(
    string StoryDefinitionInstruction,
    string StoryNarrationInstruction,
    string CorrectiveRetryInstruction,
    string PromptedJsonInstruction,
    string OpeningSceneInstruction,
    string ContinueStoryInstruction);

public static class PromptTemplateDefaults
{
    public const string ValidationErrorPlaceholder = "{validationError}";
    public const string SchemaPlaceholder = "{schema}";
    public const string ExamplePlaceholder = "{example}";
    public const string MinSuggestedActionsPlaceholder = "{minSuggestedActions}";
    public const string MaxSuggestedActionsPlaceholder = "{maxSuggestedActions}";
    public const string MinParagraphsPlaceholder = "{minParagraphs}";
    public const string MaxParagraphsPlaceholder = "{maxParagraphs}";
    public const string MinSentencesPlaceholder = "{minSentences}";
    public const string MaxSentencesPlaceholder = "{maxSentences}";

    public static PromptTemplateSettings Create() => new(
        GeneratedPromptTemplates.StoryDefinitionInstruction,
        GeneratedPromptTemplates.StoryNarrationInstruction,
        GeneratedPromptTemplates.CorrectiveRetryInstruction,
        GeneratedPromptTemplates.PromptedJsonInstruction,
        GeneratedPromptTemplates.OpeningSceneInstruction,
        GeneratedPromptTemplates.ContinueStoryInstruction);
}

public sealed record StoryGenerationSettings(
    int RecentTurnCount,
    int MaxStoryBibleEntries,
    int MaxStoryBibleEntryCharacters,
    int MaxStoryBibleCharacters,
    int StoryBibleWarningPercent,
    int MaxPlannedEvents,
    int MaxPlannedEventCharacters,
    int MaxPlannedEventsCharacters,
    int PlannedEventsWarningPercent);

public sealed record RetrySettings(int MaxAutomaticRetries, TimeSpan InitialDelay, TimeSpan MaxDelay, TimeSpan MaxRetryAfter);

public sealed record ContentLimitSettings(
    int MaxStoryTitleCharacters,
    int MaxStoryLabelCharacters,
    int MaxStoryPromptCharacters,
    int MaxPlayerActionCharacters,
    int MaxNarrationCharacters,
    int MaxSuggestedActions,
    int MaxSuggestedActionCharacters,
    int MaxStoryBibleCategoryCharacters,
    int MaxStoryBibleNameCharacters,
    int MaxStoryBibleUpdatesPerResponse,
    int MaxPlannedEventDescriptionCharacters,
    int MaxPlannedEventConditionCharacters,
    int MaxPlannedEventUpdatesPerResponse,
    int MaxConditions,
    int MaxConditionDescriptionCharacters,
    int MaxResponseBodyBytes)
{
    public int MinSuggestedActions { get; init; } = 2;
    public int MinParagraphsPerResponse { get; init; } = 4;
    public int MaxParagraphsPerResponse { get; init; } = 6;
    public int MinSentencesPerParagraph { get; init; } = 2;
    public int MaxSentencesPerParagraph { get; init; } = 5;
}

public enum StructuredOutputTier { Untested, StrictJsonSchema, JsonMode, PromptedJson, Unsupported }
public enum OutputTokenParameter { MaxCompletionTokens, MaxTokens }
public enum InstructionMessageRole { Developer, System }

public sealed record ConnectionCapabilities(
    bool SupportsModelDiscovery,
    StructuredOutputTier StructuredOutputTier,
    string? TestedModelId,
    DateTimeOffset? TestedAtUtc)
{
    public OutputTokenParameter OutputTokenParameter { get; init; } = OutputTokenParameter.MaxCompletionTokens;
    public InstructionMessageRole InstructionMessageRole { get; init; } = InstructionMessageRole.Developer;
}

public sealed record ApiConnectionSettings(
    Uri? BaseUrl,
    string? ModelId,
    TimeSpan RequestTimeout,
    int MaxOutputTokens,
    ModelParameters Parameters,
    StoryGenerationSettings StoryGeneration,
    RetrySettings Retry,
    ContentLimitSettings ContentLimits,
    ConnectionCapabilities Capabilities)
{
    public LoggingSettings Logging { get; init; } = LoggingDefaults.Create();
}

public static class NarratorDefaults
{
    public static ApiConnectionSettings Create() => new(
        BaseUrl: null,
        ModelId: null,
        RequestTimeout: TimeSpan.FromSeconds(120),
        MaxOutputTokens: 4096,
        Parameters: new ModelParameters(Temperature: null, TopP: null, ReasoningEffort: null),
        StoryGeneration: new StoryGenerationSettings(
            RecentTurnCount: 8,
            MaxStoryBibleEntries: 200,
            MaxStoryBibleEntryCharacters: 4000,
            MaxStoryBibleCharacters: 60000,
            StoryBibleWarningPercent: 80,
            MaxPlannedEvents: 50,
            MaxPlannedEventCharacters: 2000,
            MaxPlannedEventsCharacters: 20000,
            PlannedEventsWarningPercent: 80),
        Retry: new RetrySettings(
            MaxAutomaticRetries: 2,
            InitialDelay: TimeSpan.FromSeconds(1),
            MaxDelay: TimeSpan.FromSeconds(10),
            MaxRetryAfter: TimeSpan.FromSeconds(60)),
        ContentLimits: new ContentLimitSettings(
            MaxStoryTitleCharacters: 200,
            MaxStoryLabelCharacters: 200,
            MaxStoryPromptCharacters: 20000,
            MaxPlayerActionCharacters: 4000,
            MaxNarrationCharacters: 20000,
            MaxSuggestedActions: 3,
            MaxSuggestedActionCharacters: 500,
            MaxStoryBibleCategoryCharacters: 100,
            MaxStoryBibleNameCharacters: 200,
            MaxStoryBibleUpdatesPerResponse: 100,
            MaxPlannedEventDescriptionCharacters: 1000,
            MaxPlannedEventConditionCharacters: 500,
            MaxPlannedEventUpdatesPerResponse: 50,
            MaxConditions: 20,
            MaxConditionDescriptionCharacters: 1000,
            MaxResponseBodyBytes: 2 * 1024 * 1024),
        Capabilities: new ConnectionCapabilities(
            SupportsModelDiscovery: false,
            StructuredOutputTier: StructuredOutputTier.Untested,
            TestedModelId: null,
            TestedAtUtc: null));
}

public static class SettingsValidator
{
    // Shared with NarratorApplication's sanity check on LLM-generated initial Story Bible entry counts,
    // so the two can't silently diverge.
    public const int MaxStoryBibleEntriesUpperBound = 2000;
    // Shared with NarratorApplication's sanity check on LLM-generated initial Planned Event counts.
    public const int MaxPlannedEventsUpperBound = 500;
    // Shared with NarratorApplication's sanity check on LLM-generated initial victory/loss condition counts.
    public const int MaxConditionsUpperBound = 200;

    public static IReadOnlyDictionary<string, string> Validate(ApiConnectionSettings value)
    {
        var errors = new Dictionary<string, string>();
        if (value.BaseUrl is { } baseUrl && (!baseUrl.IsAbsoluteUri || baseUrl.Scheme is not ("http" or "https")))
            errors[nameof(value.BaseUrl)] = "Must be an absolute http or https URL.";
        Range(errors, nameof(value.RequestTimeout), value.RequestTimeout.TotalSeconds, 10, 900);
        Range(errors, nameof(value.MaxOutputTokens), value.MaxOutputTokens, 256, 131072);
        OptionalRange(errors, "Temperature", value.Parameters.Temperature, 0, 2);
        OptionalRange(errors, "TopP", value.Parameters.TopP, 0, 1);
        Range(errors, "RecentTurnCount", value.StoryGeneration.RecentTurnCount, 0, 100);
        Range(errors, "MaxStoryBibleEntries", value.StoryGeneration.MaxStoryBibleEntries, 1, MaxStoryBibleEntriesUpperBound);
        Range(errors, "MaxStoryBibleEntryCharacters", value.StoryGeneration.MaxStoryBibleEntryCharacters, 100, 50000);
        Range(errors, "MaxStoryBibleCharacters", value.StoryGeneration.MaxStoryBibleCharacters, 1000, 1000000);
        if (value.StoryGeneration.MaxStoryBibleEntryCharacters > value.StoryGeneration.MaxStoryBibleCharacters)
            errors["MaxStoryBibleEntryCharacters"] = "Must not exceed the maximum total Story Bible characters.";
        Range(errors, "StoryBibleWarningPercent", value.StoryGeneration.StoryBibleWarningPercent, 50, 95);
        Range(errors, "MaxPlannedEvents", value.StoryGeneration.MaxPlannedEvents, 1, MaxPlannedEventsUpperBound);
        Range(errors, "MaxPlannedEventCharacters", value.StoryGeneration.MaxPlannedEventCharacters, 100, 50000);
        Range(errors, "MaxPlannedEventsCharacters", value.StoryGeneration.MaxPlannedEventsCharacters, 1000, 1000000);
        if (value.StoryGeneration.MaxPlannedEventCharacters > value.StoryGeneration.MaxPlannedEventsCharacters)
            errors["MaxPlannedEventCharacters"] = "Must not exceed the maximum total Planned Events characters.";
        Range(errors, "PlannedEventsWarningPercent", value.StoryGeneration.PlannedEventsWarningPercent, 50, 95);
        Range(errors, "MaxAutomaticRetries", value.Retry.MaxAutomaticRetries, 0, 5);
        Range(errors, "InitialDelay", value.Retry.InitialDelay.TotalSeconds, .25, 30);
        Range(errors, "MaxDelay", value.Retry.MaxDelay.TotalSeconds, 1, 120);
        Range(errors, "MaxRetryAfter", value.Retry.MaxRetryAfter.TotalSeconds, 1, 600);
        if (value.Retry.MaxDelay < value.Retry.InitialDelay)
            errors["MaxDelay"] = "Maximum retry delay must be at least the initial delay.";
        if (value.Logging is null)
            errors[nameof(value.Logging)] = "Logging settings are required.";
        else if (!Enum.IsDefined(value.Logging.MinimumLevel))
            errors[nameof(value.Logging.MinimumLevel)] = "Select a valid logging level.";

        var c = value.ContentLimits;
        Range(errors, nameof(c.MaxStoryTitleCharacters), c.MaxStoryTitleCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryLabelCharacters), c.MaxStoryLabelCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryPromptCharacters), c.MaxStoryPromptCharacters, 100, 200000);
        Range(errors, nameof(c.MaxPlayerActionCharacters), c.MaxPlayerActionCharacters, 1, 50000);
        Range(errors, nameof(c.MaxNarrationCharacters), c.MaxNarrationCharacters, 100, 200000);
        Range(errors, nameof(c.MinSuggestedActions), c.MinSuggestedActions, 1, 20);
        Range(errors, nameof(c.MaxSuggestedActions), c.MaxSuggestedActions, 1, 20);
        if (c.MinSuggestedActions > c.MaxSuggestedActions)
            errors[nameof(c.MinSuggestedActions)] = "Must not exceed the maximum suggested actions.";
        Range(errors, nameof(c.MaxSuggestedActionCharacters), c.MaxSuggestedActionCharacters, 1, 5000);
        Range(errors, nameof(c.MaxStoryBibleCategoryCharacters), c.MaxStoryBibleCategoryCharacters, 1, 1000);
        Range(errors, nameof(c.MaxStoryBibleNameCharacters), c.MaxStoryBibleNameCharacters, 1, 2000);
        Range(errors, nameof(c.MaxStoryBibleUpdatesPerResponse), c.MaxStoryBibleUpdatesPerResponse, 1, 1000);
        Range(errors, nameof(c.MaxPlannedEventDescriptionCharacters), c.MaxPlannedEventDescriptionCharacters, 1, 5000);
        Range(errors, nameof(c.MaxPlannedEventConditionCharacters), c.MaxPlannedEventConditionCharacters, 1, 5000);
        Range(errors, nameof(c.MaxPlannedEventUpdatesPerResponse), c.MaxPlannedEventUpdatesPerResponse, 1, 1000);
        Range(errors, nameof(c.MaxConditions), c.MaxConditions, 1, MaxConditionsUpperBound);
        Range(errors, nameof(c.MaxConditionDescriptionCharacters), c.MaxConditionDescriptionCharacters, 1, 5000);
        Range(errors, nameof(c.MaxResponseBodyBytes), c.MaxResponseBodyBytes, 64 * 1024, 16 * 1024 * 1024);
        Range(errors, nameof(c.MinParagraphsPerResponse), c.MinParagraphsPerResponse, 1, 20);
        Range(errors, nameof(c.MaxParagraphsPerResponse), c.MaxParagraphsPerResponse, 1, 20);
        if (c.MinParagraphsPerResponse > c.MaxParagraphsPerResponse)
            errors[nameof(c.MinParagraphsPerResponse)] = "Must not exceed the maximum paragraphs per response.";
        Range(errors, nameof(c.MinSentencesPerParagraph), c.MinSentencesPerParagraph, 1, 20);
        Range(errors, nameof(c.MaxSentencesPerParagraph), c.MaxSentencesPerParagraph, 1, 20);
        if (c.MinSentencesPerParagraph > c.MaxSentencesPerParagraph)
            errors[nameof(c.MinSentencesPerParagraph)] = "Must not exceed the maximum sentences per paragraph.";
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
