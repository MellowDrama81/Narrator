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
    string ContinueStoryInstruction)
{
    public string TurnAdjudicationInstruction => GeneratedPromptTemplates.TurnAdjudicationInstruction;
    public string NarrationOnlyInstruction => GeneratedPromptTemplates.NarrationOnlyInstruction;
    public string ScenePlanInstruction => GeneratedPromptTemplates.ScenePlanInstruction;
    public string NarrationFromAdjudicationInstruction => GeneratedPromptTemplates.NarrationFromAdjudicationInstruction;
    public string NarrationFromPlanInstruction => GeneratedPromptTemplates.NarrationFromPlanInstruction;
    public string NarrationFromCritiqueInstruction => GeneratedPromptTemplates.NarrationFromCritiqueInstruction;
    public string PlanCriticInstruction => GeneratedPromptTemplates.PlanCriticInstruction;
    public string StoryBibleAnalysisInstruction => GeneratedPromptTemplates.StoryBibleAnalysisInstruction;
    public string PlannedEventAnalysisInstruction => GeneratedPromptTemplates.PlannedEventAnalysisInstruction;
    public string ConditionSummaryAnalysisInstruction => GeneratedPromptTemplates.ConditionSummaryAnalysisInstruction;
    public string StateExtractionInstruction => GeneratedPromptTemplates.StateExtractionInstruction;
    public string StateExtractionFromAnalysesInstruction => GeneratedPromptTemplates.StateExtractionFromAnalysesInstruction;
    public string ProseRevisionInstruction => GeneratedPromptTemplates.ProseRevisionInstruction;
}

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
    int MaxStorySummaryCharacters,
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
public enum TurnPipelineMode { OneCall, TwoCalls, ThreeCalls, FourCalls, FiveCalls, SevenCalls, SevenCallsParallel, EightCalls }
public enum GenerationCall { StoryDefinition, Turn, Adjudication, ScenePlan, PlanCritic, Narration, StoryBibleAnalysis, PlannedEventAnalysis, ConditionSummaryAnalysis, StateExtraction, ProseRevision }

public static class TurnPipelineCalls
{
    public static IReadOnlyList<GenerationCall> For(TurnPipelineMode pipeline) => pipeline switch
    {
        TurnPipelineMode.OneCall => [GenerationCall.StoryDefinition, GenerationCall.Turn],
        TurnPipelineMode.TwoCalls => [GenerationCall.StoryDefinition, GenerationCall.Narration, GenerationCall.StateExtraction],
        TurnPipelineMode.ThreeCalls => [GenerationCall.StoryDefinition, GenerationCall.Adjudication, GenerationCall.Narration, GenerationCall.StateExtraction],
        TurnPipelineMode.FourCalls => [GenerationCall.StoryDefinition, GenerationCall.Adjudication, GenerationCall.ScenePlan, GenerationCall.Narration, GenerationCall.StateExtraction],
        TurnPipelineMode.FiveCalls => [GenerationCall.StoryDefinition, GenerationCall.Adjudication, GenerationCall.ScenePlan, GenerationCall.PlanCritic, GenerationCall.Narration, GenerationCall.StateExtraction],
        TurnPipelineMode.SevenCalls or TurnPipelineMode.SevenCallsParallel => [GenerationCall.StoryDefinition, GenerationCall.Adjudication, GenerationCall.ScenePlan, GenerationCall.Narration, GenerationCall.StoryBibleAnalysis, GenerationCall.PlannedEventAnalysis, GenerationCall.ConditionSummaryAnalysis, GenerationCall.StateExtraction],
        TurnPipelineMode.EightCalls => [GenerationCall.StoryDefinition, GenerationCall.Adjudication, GenerationCall.ScenePlan, GenerationCall.Narration, GenerationCall.StoryBibleAnalysis, GenerationCall.PlannedEventAnalysis, GenerationCall.ConditionSummaryAnalysis, GenerationCall.StateExtraction, GenerationCall.ProseRevision],
        _ => throw new ArgumentOutOfRangeException(nameof(pipeline), pipeline, "Unknown turn pipeline mode.")
    };
}

// A connection is intentionally credentials-free. MAUI stores each profile's API key in platform
// secure storage; the web app stores it in its local IndexedDB profile record.
public sealed record ApiConnectionProfile(Guid Id, string Name, Uri? BaseUrl)
{
    public ConnectionCapabilities Capabilities { get; init; } = new(false, StructuredOutputTier.Untested, null, null);
    // Structured-output support varies by model, even when models share an endpoint. The legacy
    // Capabilities value is retained for settings-file compatibility and endpoint-wide discovery.
    public IReadOnlyDictionary<string, ConnectionCapabilities> ModelCapabilities { get; init; } =
        new Dictionary<string, ConnectionCapabilities>(StringComparer.Ordinal);
}

// A call can choose its connection, model, and HTTP request settings independently. Null values
// inherit the legacy/default values so existing settings documents remain compatible.
public sealed record GenerationCallRoute(Guid? ConnectionId, string? ModelId)
{
    public TimeSpan? RequestTimeout { get; init; }
    public int? MaxOutputTokens { get; init; }
    public ModelParameters? Parameters { get; init; }
    public RetrySettings? Retry { get; init; }
}

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
    // Experimental turn generation pipelines, retained side-by-side so providers/models can be compared.
    public TurnPipelineMode TurnPipeline { get; init; } = TurnPipelineMode.FourCalls;
    public IReadOnlyList<ApiConnectionProfile> Connections { get; init; } = [];
    public IReadOnlyDictionary<GenerationCall, GenerationCallRoute> GenerationCallRoutes { get; init; } =
        new Dictionary<GenerationCall, GenerationCallRoute>();
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
            MaxStorySummaryCharacters: 3000,
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
        var duplicateConnectionNames = value.Connections
            .Where(connection => !string.IsNullOrWhiteSpace(connection.Name))
            .GroupBy(connection => connection.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
        if (duplicateConnectionNames) errors[nameof(value.Connections)] = "Connection names must be unique.";
        foreach (var connection in value.Connections)
        {
            if (connection.Id == Guid.Empty || string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 100)
                errors[nameof(value.Connections)] = "Each connection needs a unique name of at most 100 characters.";
            if (connection.BaseUrl is not null && (!connection.BaseUrl.IsAbsoluteUri || connection.BaseUrl.Scheme is not ("http" or "https")))
                errors[nameof(value.Connections)] = "Each connection URL must be an absolute http or https URL.";
        }
        var connectionIds = value.Connections.Select(connection => connection.Id).ToHashSet();
        if (value.GenerationCallRoutes.Values.Any(route => route.ConnectionId is { } id && !connectionIds.Contains(id)))
            errors[nameof(value.GenerationCallRoutes)] = "A call is assigned to a connection that no longer exists.";
        foreach (var route in value.GenerationCallRoutes.Values)
        {
            if (route.RequestTimeout is { } timeout) Range(errors, "CallTimeout", timeout.TotalSeconds, 10, 900);
            if (route.MaxOutputTokens is { } tokens) Range(errors, "CallMaxOutputTokens", tokens, 256, 131072);
            if (route.Parameters is { } parameters)
            {
                OptionalRange(errors, "CallTemperature", parameters.Temperature, 0, 2);
                OptionalRange(errors, "CallTopP", parameters.TopP, 0, 1);
            }
            if (route.Retry is { } retry)
            {
                Range(errors, "CallMaxAutomaticRetries", retry.MaxAutomaticRetries, 0, 5);
                Range(errors, "CallInitialDelay", retry.InitialDelay.TotalSeconds, .25, 30);
                Range(errors, "CallMaxDelay", retry.MaxDelay.TotalSeconds, 1, 120);
                Range(errors, "CallMaxRetryAfter", retry.MaxRetryAfter.TotalSeconds, 1, 600);
                if (retry.MaxDelay < retry.InitialDelay) errors["CallMaxDelay"] = "Must be at least the initial retry delay.";
            }
        }
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
        Range(errors, nameof(c.MaxStorySummaryCharacters), c.MaxStorySummaryCharacters, 500, 20000);
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
    public static string ApiCredentialForConnection(Guid connectionId) => $"mellow-narrator.api-credential.{connectionId:N}";
}
