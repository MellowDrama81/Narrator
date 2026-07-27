namespace Mellow.Narrator.Core;

public sealed record StoryBible(IReadOnlyList<StoryBibleEntry> Entries)
{
    public static StoryBible Empty { get; } = new([]);
}

public sealed record StoryBibleEntry(
    Guid Id,
    string Category,
    string Name,
    string Content,
    int Importance,
    int LastRelevantTurnNumber);

public sealed record StoryBibleLimitSnapshot(int MaxEntries, int MaxEntryCharacters, int MaxTotalCharacters);

public enum StoryBibleOperation { Add, Replace, Remove }
public enum StoryBibleChangeSource { LlmUpdate, AutomaticCull, ManualEdit }
public enum StoryBibleMaintenanceReason { GeneratedBibleLimitCull, UserApprovedLimitCull, ManualEdit }

public sealed record ProposedStoryBibleEntry(string Category, string Name, string Content, int Importance);

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

public sealed record StoryDefinition(
    Guid Id,
    string Title,
    string StoryPrompt,
    StoryBible InitialStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string InitialEventsPrompt { get; init; } = "";
}

public sealed record StoryDefinitionSnapshot(
    string Title,
    string StoryPrompt,
    StoryBible InitialStoryBible)
{
    public string InitialEventsPrompt { get; init; } = "";
}

public sealed record StorySetupSnapshot(StoryDefinitionSnapshot Definition);

public sealed record StoryState(
    Guid Id,
    string Label,
    Guid? SourceStoryDefinitionId,
    StorySetupSnapshot Setup,
    StoryBible CurrentStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
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
