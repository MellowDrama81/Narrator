namespace Mellow.Narrator.Core;

public sealed record ModelParameters(double? Temperature, double? TopP, string? ReasoningEffort);

public enum NarratorLogLevel { Trace, Debug, Information, Warning, Error, Off }

public sealed record LoggingSettings(NarratorLogLevel MinimumLevel);

public static class LoggingDefaults
{
    public static LoggingSettings Create() => new(NarratorLogLevel.Information);
}

public sealed record PromptTemplateSettings(
    string PlayerAnswerValidationInstruction,
    string StoryDefinitionInstruction,
    string StoryNarrationInstruction,
    string CorrectiveRetryInstruction,
    string PromptedJsonInstruction,
    string OpeningSceneInstruction,
    string ContinueStoryInstruction);

public static class PromptTemplateDefaults
{
    public const int MaximumTemplateCharacters = 20000;
    public const string ValidationErrorPlaceholder = "{validationError}";
    public const string SchemaPlaceholder = "{schema}";

    public static PromptTemplateSettings Create() => new(
        "You validate one interactive-story setup answer. Apply the validation instruction in context. Return JSON only. A failed rule is advisory: set hasWarning true and explain concisely.",
        "Create the initial Story Bible for an interactive story. Include every durable fact required to narrate consistently. Keep entries concise, avoid duplicates, and assign importance 1 through 5. Return JSON only.",
        """
        You narrate an interactive story. Return JSON only. The Story Bible is authoritative and complete.
        Narrate the immediate scene, offer concise suggested actions, flag every existing Bible entry relevant now,
        and return only incremental Story Bible updates. Preserve durable facts, replace rather than duplicate,
        remove obsolete facts, and assign importance 1 through 5.
        """,
        $"Your previous response failed validation: {ValidationErrorPlaceholder}. Return a corrected JSON object only.",
        $"Return an object matching this JSON Schema exactly: {SchemaPlaceholder}",
        "Create the opening scene.",
        "Continue the story.");
}

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
    public PromptTemplateSettings PromptTemplates { get; init; } = PromptTemplateDefaults.Create();
    public LoggingSettings Logging { get; init; } = LoggingDefaults.Create();
}

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
        if (value.Logging is null)
            errors[nameof(value.Logging)] = "Logging settings are required.";
        else if (!Enum.IsDefined(value.Logging.MinimumLevel))
            errors[nameof(value.Logging.MinimumLevel)] = "Select a valid logging level.";
        ValidatePromptTemplates(errors, value.PromptTemplates);

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

    private static void ValidatePromptTemplates(
        IDictionary<string, string> errors,
        PromptTemplateSettings? templates)
    {
        if (templates is null)
        {
            errors[nameof(ApiConnectionSettings.PromptTemplates)] = "Prompt templates are required.";
            return;
        }

        Prompt(errors, nameof(templates.PlayerAnswerValidationInstruction), templates.PlayerAnswerValidationInstruction);
        Prompt(errors, nameof(templates.StoryDefinitionInstruction), templates.StoryDefinitionInstruction);
        Prompt(errors, nameof(templates.StoryNarrationInstruction), templates.StoryNarrationInstruction);
        Prompt(errors, nameof(templates.CorrectiveRetryInstruction), templates.CorrectiveRetryInstruction);
        Prompt(errors, nameof(templates.PromptedJsonInstruction), templates.PromptedJsonInstruction);
        Prompt(errors, nameof(templates.OpeningSceneInstruction), templates.OpeningSceneInstruction);
        Prompt(errors, nameof(templates.ContinueStoryInstruction), templates.ContinueStoryInstruction);
        if (!string.IsNullOrWhiteSpace(templates.CorrectiveRetryInstruction) &&
            !templates.CorrectiveRetryInstruction.Contains(PromptTemplateDefaults.ValidationErrorPlaceholder, StringComparison.Ordinal))
            errors[nameof(templates.CorrectiveRetryInstruction)] =
                $"Must contain {PromptTemplateDefaults.ValidationErrorPlaceholder}.";
        if (!string.IsNullOrWhiteSpace(templates.PromptedJsonInstruction) &&
            !templates.PromptedJsonInstruction.Contains(PromptTemplateDefaults.SchemaPlaceholder, StringComparison.Ordinal))
            errors[nameof(templates.PromptedJsonInstruction)] =
                $"Must contain {PromptTemplateDefaults.SchemaPlaceholder}.";
    }

    private static void Prompt(
        IDictionary<string, string> errors,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[name] = "Must not be empty.";
        else if (value.Length > PromptTemplateDefaults.MaximumTemplateCharacters)
            errors[name] = $"Must not exceed {PromptTemplateDefaults.MaximumTemplateCharacters} characters.";
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
