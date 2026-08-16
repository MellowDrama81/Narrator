namespace Mellow.Narrator.Core;

public sealed record StoryBible(IReadOnlyList<StoryBibleEntry> Entries)
{
    public static StoryBible Empty { get; } = new([]);
}

public sealed record StoryBibleEntry(
    Guid Id,
    string Category,
    string Name,
    IReadOnlyList<string> KnownFacts,
    IReadOnlyList<string> SecretFacts,
    int Importance,
    int LastRelevantTurnNumber)
{
    // The default record equality compares KnownFacts/SecretFacts by reference (they're lists, not
    // value types), so a freshly re-allocated but content-identical array would always compare unequal.
    public bool Equals(StoryBibleEntry? other) =>
        other is not null &&
        Id == other.Id &&
        Category == other.Category &&
        Name == other.Name &&
        Importance == other.Importance &&
        LastRelevantTurnNumber == other.LastRelevantTurnNumber &&
        KnownFacts.SequenceEqual(other.KnownFacts) &&
        SecretFacts.SequenceEqual(other.SecretFacts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Category);
        hash.Add(Name);
        hash.Add(Importance);
        hash.Add(LastRelevantTurnNumber);
        foreach (var fact in KnownFacts) hash.Add(fact);
        foreach (var fact in SecretFacts) hash.Add(fact);
        return hash.ToHashCode();
    }
}

public sealed record StoryBibleLimitSnapshot(int MaxEntries, int MaxEntryCharacters, int MaxTotalCharacters);

public enum StoryBibleOperation { Add, Replace, Remove }
public enum StoryBibleChangeSource { LlmUpdate, AutomaticCull, ManualEdit }
public enum StoryBibleMaintenanceReason { GeneratedBibleLimitCull, UserApprovedLimitCull, ManualEdit }

public sealed record ProposedStoryBibleEntry(
    string Category,
    string Name,
    IReadOnlyList<string> KnownFacts,
    IReadOnlyList<string> SecretFacts,
    int Importance);

public sealed record ProposedStoryBibleUpdate(
    StoryBibleOperation Operation,
    Guid? EntryId,
    ProposedStoryBibleEntry? Entry);

public sealed record AppliedStoryBibleChange(
    StoryBibleOperation Operation,
    Guid EntryId,
    StoryBibleEntry? Before,
    StoryBibleEntry? After,
    StoryBibleChangeSource Source);

public sealed record StoryBibleMaintenanceRecord(
    Guid Id,
    StoryBibleMaintenanceReason Reason,
    StoryBibleLimitSnapshot Limits,
    IReadOnlyList<AppliedStoryBibleChange> Changes,
    DateTimeOffset CompletedAtUtc);

// A Planned Event is a future plot point the LLM is steering the story toward. Unlike Story Bible
// entries (durable facts about the current state of the world), Planned Events describe something
// that has not happened yet and are always kept secret from the player. Importance and Urgency are
// independent axes: Importance 5 (the maximum) marks the event mandatory - PlannedEventProcessor
// refuses to let it be removed except with outcome Fulfilled, and never auto-culls it, so the
// narrator is forced to work it into the story rather than letting it quietly drop. Urgency (1
// through 5) instead tells the narrator how directly and soon to steer scenes toward the event: 1
// means let it emerge naturally whenever the story happens to head that way, 5 means actively work
// it into the very next scene(s). A mandatory event can still have low urgency (inevitable, but not
// due yet) or a minor event can have high urgency (small, but should happen very soon if at all).
// Condition is an optional freeform description of what must happen, or what state the story must be
// in, before this event can be pursued - narrative prose the narrator interprets each turn, not a
// structured reference to another entry. Null or empty means the event has no prerequisite and is
// pursuable immediately according to its own importance and urgency.
public sealed record PlannedEvent(
    Guid Id,
    string Description,
    int Importance,
    int Urgency,
    string? Condition,
    int LastRelevantTurnNumber);

public sealed record PlannedEvents(IReadOnlyList<PlannedEvent> Entries)
{
    public static PlannedEvents Empty { get; } = new([]);
}

public sealed record PlannedEventLimitSnapshot(int MaxEntries, int MaxEntryCharacters, int MaxTotalCharacters);

public enum PlannedEventOperation { Add, Replace, Remove }
public enum PlannedEventOutcome { Fulfilled, Abandoned }
public enum PlannedEventChangeSource { LlmUpdate, AutomaticCull, ManualEdit }
public enum PlannedEventMaintenanceReason { GeneratedLimitCull, UserApprovedLimitCull, ManualEdit }

public sealed record ProposedPlannedEvent(
    string Description,
    int Importance,
    int Urgency,
    string? Condition);

public sealed record ProposedPlannedEventUpdate(
    PlannedEventOperation Operation,
    Guid? EntryId,
    ProposedPlannedEvent? Entry,
    PlannedEventOutcome? Outcome);

public sealed record AppliedPlannedEventChange(
    PlannedEventOperation Operation,
    Guid EntryId,
    PlannedEvent? Before,
    PlannedEvent? After,
    PlannedEventChangeSource Source,
    PlannedEventOutcome? Outcome);

public sealed record PlannedEventMaintenanceRecord(
    Guid Id,
    PlannedEventMaintenanceReason Reason,
    PlannedEventLimitSnapshot Limits,
    IReadOnlyList<AppliedPlannedEventChange> Changes,
    DateTimeOffset CompletedAtUtc);

// A Story Condition is a fixed victory or loss condition defined on the Story Definition and copied
// verbatim (with remapped ids, same as Story Bible/Planned Events - see NarratorApplication.StartStoryAsync)
// into every Story State started from it. Unlike Planned Events, the set never grows or shrinks during
// play - the narrator only ever reports a condition as revealed and/or met, never adds, replaces, or
// removes one - so no maintenance/cull machinery exists for it. Secret controls whether the narrator may
// ever state the condition's content directly in narration: a secret condition must stay implied only
// through the ordinary events that satisfy it, exactly like a Planned Event, while a non-secret one
// should be woven into the prose once something in the story makes it relevant (never as an upfront
// list) and is then tracked as "revealed". Both secret and non-secret conditions are tracked as "met"
// once actually satisfied; a condition, once met, stays met for the rest of the story even though the
// player may choose to keep playing past it.
public sealed record StoryCondition(Guid Id, string Description, bool Secret);

public sealed record StoryConditions(IReadOnlyList<StoryCondition> Entries)
{
    public static StoryConditions Empty { get; } = new([]);
}

public sealed record ProposedStoryCondition(string Description, bool Secret);

public sealed record StoryDefinition(
    Guid Id,
    string Title,
    string StoryPrompt,
    string InitialEventsPrompt,
    StoryBible InitialStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    PlannedEvents InitialPlannedEvents,
    IReadOnlyList<PlannedEventMaintenanceRecord> PlannedEventMaintenanceHistory,
    StoryConditions InitialVictoryConditions,
    StoryConditions InitialLossConditions,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Description = "");

public sealed record StoryDefinitionSnapshot(
    string Title,
    string StoryPrompt,
    string InitialEventsPrompt,
    StoryBible InitialStoryBible,
    PlannedEvents InitialPlannedEvents,
    StoryConditions InitialVictoryConditions,
    StoryConditions InitialLossConditions,
    string Description = "");

public sealed record StorySetupSnapshot(StoryDefinitionSnapshot Definition);

public sealed record StoryState(
    Guid Id,
    string Label,
    Guid? SourceStoryDefinitionId,
    StorySetupSnapshot Setup,
    StoryBible CurrentStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    PlannedEvents CurrentPlannedEvents,
    IReadOnlyList<PlannedEventMaintenanceRecord> PlannedEventMaintenanceHistory,
    StoryConditions CurrentVictoryConditions,
    StoryConditions CurrentLossConditions,
    IReadOnlyList<Guid> RevealedVictoryConditionIds,
    IReadOnlyList<Guid> MetVictoryConditionIds,
    IReadOnlyList<Guid> RevealedLossConditionIds,
    IReadOnlyList<Guid> MetLossConditionIds,
    // A compact, narrator-maintained prose recap of everything about the story so far that doesn't fit
    // the Story Bible's atomic facts - the only memory of anything that has scrolled out of the raw
    // recent-turn history sent with each request. Empty until the opening turn establishes it; the
    // narrator is expected to rewrite (not just append to) this every turn, so it stays roughly constant
    // in length rather than growing without bound. See StoryGenerationResponse.StorySummary.
    string StorySummary,
    int SortOrder,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActionAtUtc,
    int LastCommittedTurnSequence);

public sealed record GenerationMetadata(
    string ModelId,
    string? ProviderResponseId,
    int? InputTokens,
    int? OutputTokens);

public sealed record StoryTurn(
    Guid Id,
    Guid StoryStateId,
    int SequenceNumber,
    string? PlayerAction,
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<AppliedStoryBibleChange> StoryBibleChanges,
    IReadOnlyList<Guid> RelevantPlannedEventIds,
    IReadOnlyList<AppliedPlannedEventChange> PlannedEventChanges,
    // Newly revealed/met this turn only - not cumulative (see StoryState for the running totals). A
    // condition already revealed or met in an earlier turn is never repeated here.
    IReadOnlyList<Guid> RevealedVictoryConditionIds,
    IReadOnlyList<Guid> MetVictoryConditionIds,
    IReadOnlyList<Guid> RevealedLossConditionIds,
    IReadOnlyList<Guid> MetLossConditionIds,
    DateTimeOffset CompletedAtUtc,
    GenerationMetadata Generation);

public sealed record StoryStateAggregateSnapshot(
    StoryState State,
    IReadOnlyList<StoryTurn> Turns);

public sealed record StoryDefinitionSummary(Guid Id, string Title, int SortOrder, DateTimeOffset UpdatedAtUtc, string Description = "");
public sealed record StoryStateSummary(Guid Id, string Label, int SortOrder, DateTimeOffset StartedAtUtc, DateTimeOffset? LastActionAtUtc);

public sealed record StoryPromptDraft(
    Guid? SourceStoryDefinitionId,
    string Title,
    string StoryPrompt);

public sealed record StartStoryDraft(
    Guid SourceStoryDefinitionId,
    StoryDefinitionSnapshot Definition)
{
    public IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory { get; init; } = [];
    public IReadOnlyList<PlannedEventMaintenanceRecord> PlannedEventMaintenanceHistory { get; init; } = [];
}

public enum TabType { Settings, StoryDefinitionList, PlayStoryList, StoryDefinition, StoryPrompt, PlayStory }
public enum PendingOperationType { GenerateStoryDefinition, GenerateOpeningScene, GenerateStoryTurn, DiscoverModels, TestApiConnection }

public sealed record PendingOperationState(
    Guid OperationId,
    PendingOperationType Type,
    Guid? TargetRecordId,
    int? ExpectedTurnSequence,
    DateTimeOffset StartedAtUtc);

public sealed record PlayStoryTabState(string PendingPlayerAction);

public sealed record OpenTabState(
    Guid TabId,
    TabType Type,
    int Position,
    Guid? DurableRecordId,
    StoryPromptDraft? StoryPromptDraft,
    PlayStoryTabState? PlayStoryTabState,
    PendingOperationState? PendingOperation);

public sealed record WorkspaceState(Guid ActiveTabId, IReadOnlyList<OpenTabState> Tabs)
{
    public static WorkspaceState Empty { get; } = new(Guid.Empty, []);
}

public sealed record TrashItem(
    string TrashId,
    TrashItemType Type,
    Guid OriginalId,
    string DisplayName,
    DateTimeOffset DeletedAtUtc,
    long SizeBytes);

public enum TrashItemType { StoryDefinition, StoryState }

public sealed record RecoveryNotice(string Message, DateTimeOffset OccurredAtUtc);

public sealed record StoryDefinitionExport(int FormatVersion, DateTimeOffset ExportedAtUtc, StoryDefinition Definition);
public sealed record StoryStateExport(int FormatVersion, DateTimeOffset ExportedAtUtc, StoryState State, IReadOnlyList<StoryTurn> Turns);
