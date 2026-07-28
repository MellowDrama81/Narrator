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
    public const string MinSuggestedActionsPlaceholder = "{minSuggestedActions}";
    public const string MaxSuggestedActionsPlaceholder = "{maxSuggestedActions}";

    public static PromptTemplateSettings Create() => new(
        """
        Refine the Story Prompt and create the initial Story Bible for an interactive story.
        The Story Prompt is sent verbatim with every request for the entire story, so it must contain only
        immutable facts and instructions that will never change: setting, premise, tone, and narration rules.
        Anything that can change over the course of the story — character states, locations, relationships,
        inventory, objectives, or any other mutable detail — must not remain in the Story Prompt; move it into
        Story Bible entries instead. Rewrite the Story Prompt to keep only what is truly immutable, moving
        everything else into Story Bible entries. Also write an Initial Events prompt describing the starting
        state of the story and anything that should happen in the first few scenes. Unlike the Story Prompt,
        the Initial Events prompt is supplied only for the earliest turns and is dropped once enough real
        history has accumulated, so anything that must be remembered later belongs in the Story Bible instead,
        not there. Leave it empty if the opening needs no guidance beyond the Story Prompt and Story Bible.
        Each entry has a name and two lists of short, concise fact strings instead of one block of text:
        knownFacts holds everything the player character already knows or could plainly observe, and
        secretFacts holds hidden facts the character does not yet know — schemes, true motives, or facts
        only other characters or the narrator are aware of. A single entry (for example one character) can
        and often should have both known and secret facts about the same subject; do not split them into
        separate entries. Either list may be empty, but not both. Include every durable fact required to
        narrate consistently, avoid duplicate entries for the same subject, and assign importance 1 through 5.
        Also propose a concise, evocative title for the story; it is used only if the user did not already
        provide one. Return JSON only.
        """,
        $"""
        You narrate an interactive story. Return JSON only. The Story Bible is authoritative and complete.
        Narrate in second person and present tense, as though it is happening to the player right now
        (for example, "You push open the door and the room falls silent," not "She pushed open the door"
        or "You will push open the door"). Narrate the immediate scene in 4 to 6 short paragraphs of no more
        than 2 to 5 sentences each, separating every paragraph from the next with a blank line by embedding a
        literal double newline character (\n\n) between them inside the narration string; never write a long
        paragraph, and never return the scene as one unbroken block of text,
        offer between {MinSuggestedActionsPlaceholder} and {MaxSuggestedActionsPlaceholder} concise suggested actions, flag every existing Bible entry relevant now,
        and return only incremental Story Bible updates. The narration string must contain only prose describing
        the scene; never list, number, or otherwise embed the suggested actions or choices within it — they
        belong solely in the suggestedActions field. Resolve the current player action from the final request,
        advance beyond the most recent narration, and never answer an older action or repeat an earlier scene.
        If the player's action is passive, hesitant, or leaves no clear direction, take the initiative yourself:
        introduce a complication, event, or NPC action that pushes the plot forward instead of letting the scene idle.
        Stop narrating the moment the player character reaches an important decision; never narrate past it or
        resolve it yourself, and make the suggested actions represent the distinct choices available at that point.
        Narrate strictly from the player character's own awareness: never reveal a fact, motive, or hidden
        scheme the character has no way of knowing, even if the Story Bible records it for continuity. Each
        entry's secretFacts are things the character does not yet know, and their content must never appear
        in or be implied directly by the narration. At most, narrate what the character could actually
        perceive, such as suspicious behavior or an odd detail that hints at something being wrong, without
        stating what that something is. When story events genuinely make the character become aware of a
        secret fact, issue a replace update for that entry moving the fact's substance from secretFacts into
        knownFacts (rewording it as needed, and removing it from secretFacts); when adding a new fact the
        character does not yet know, place it in secretFacts instead of knownFacts.
        For an add update, always set entryId to null because the application assigns the ID. Never invent IDs.
        For replace and remove updates, use only an existing Story Bible entry ID supplied in the request.
        In relevantStoryBibleEntryIds, use only IDs copied exactly from the current Story Bible. Never invent IDs.
        Preserve durable facts, replace rather than duplicate, remove obsolete facts, and assign importance 1 through 5.
        A message with contextType "initialEvents", when present, describes the intended starting state and
        early scenes; it is only supplied for the earliest turns and will silently stop appearing once enough
        real history has accumulated, so never treat its absence as something having changed.
        """,
        $"Your previous response failed validation: {ValidationErrorPlaceholder}. Return a corrected JSON object only.",
        $"Return an object matching this JSON Schema exactly: {SchemaPlaceholder}",
        "Create the opening scene. Narrate entirely in second person present tense, addressing the player " +
        "character as \"you\" throughout; never refer to them in third person (for example \"she\", \"he\", " +
        "\"they\", or by name) even though no prior narration exists yet to anchor the pattern.",
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
    int MaxPlayerActionCharacters,
    int MaxNarrationCharacters,
    int MaxSuggestedActions,
    int MaxSuggestedActionCharacters,
    int MaxStoryBibleCategoryCharacters,
    int MaxStoryBibleNameCharacters,
    int MaxStoryBibleUpdatesPerResponse,
    int MaxResponseBodyBytes)
{
    public int MinSuggestedActions { get; init; } = 2;
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
        null,
        null,
        TimeSpan.FromSeconds(120),
        4096,
        new(null, null, null),
        new(8, 200, 4000, 60000, 80),
        new(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)),
        new(200, 200, 20000, 4000, 20000, 3, 500, 100, 200, 100, 2 * 1024 * 1024),
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
