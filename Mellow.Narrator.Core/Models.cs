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
// PrerequisiteEventIds references other Planned Events (by Id) that must occur before this one is
// pursued. An id that no longer corresponds to a live entry is not an error: it means that
// prerequisite has already been resolved (fulfilled or abandoned) and no longer blocks anything -
// PlannedEventProcessor never rewrites this list to prune such ids, it just stops treating them as
// blocking. Only an id that still names a live entry counts as an outstanding prerequisite.
public sealed record PlannedEvent(
    Guid Id,
    string Description,
    int Importance,
    int Urgency,
    IReadOnlyList<Guid> PrerequisiteEventIds,
    int LastRelevantTurnNumber)
{
    // Default record equality would compare PrerequisiteEventIds by reference (it's a list, not a
    // value type), so a freshly re-allocated but content-identical array would always compare unequal -
    // same issue StoryBibleEntry works around for its KnownFacts/SecretFacts lists.
    public bool Equals(PlannedEvent? other) =>
        other is not null &&
        Id == other.Id &&
        Description == other.Description &&
        Importance == other.Importance &&
        Urgency == other.Urgency &&
        LastRelevantTurnNumber == other.LastRelevantTurnNumber &&
        PrerequisiteEventIds.SequenceEqual(other.PrerequisiteEventIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Description);
        hash.Add(Importance);
        hash.Add(Urgency);
        hash.Add(LastRelevantTurnNumber);
        foreach (var id in PrerequisiteEventIds) hash.Add(id);
        return hash.ToHashCode();
    }
}

public sealed record PlannedEvents(IReadOnlyList<PlannedEvent> Entries)
{
    public static PlannedEvents Empty { get; } = new([]);
}

public sealed record PlannedEventLimitSnapshot(int MaxEntries, int MaxEntryCharacters, int MaxTotalCharacters);

public enum PlannedEventOperation { Add, Replace, Remove }
public enum PlannedEventOutcome { Fulfilled, Abandoned }
public enum PlannedEventChangeSource { LlmUpdate, AutomaticCull, ManualEdit }
public enum PlannedEventMaintenanceReason { GeneratedLimitCull, UserApprovedLimitCull, ManualEdit }

public sealed record ProposedPlannedEvent(string Description, int Importance, int Urgency, IReadOnlyList<Guid> PrerequisiteEventIds);

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

public sealed record StoryDefinition(
    Guid Id,
    string Title,
    string StoryPrompt,
    string InitialEventsPrompt,
    StoryBible InitialStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    PlannedEvents InitialPlannedEvents,
    IReadOnlyList<PlannedEventMaintenanceRecord> PlannedEventMaintenanceHistory,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record StoryDefinitionSnapshot(
    string Title,
    string StoryPrompt,
    string InitialEventsPrompt,
    StoryBible InitialStoryBible,
    PlannedEvents InitialPlannedEvents);

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
    DateTimeOffset CompletedAtUtc,
    GenerationMetadata Generation);

public sealed record StoryStateAggregateSnapshot(
    StoryState State,
    IReadOnlyList<StoryTurn> Turns);

public sealed record StoryDefinitionSummary(Guid Id, string Title, int SortOrder, DateTimeOffset UpdatedAtUtc);
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
