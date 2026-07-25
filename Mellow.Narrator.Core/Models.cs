namespace Mellow.Narrator.Core;

public sealed record PlayerQuestion(Guid Id, string Question, string ValidationInstruction, int SortOrder);

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
    IReadOnlyList<PlayerQuestion> PlayerQuestions,
    StoryBible InitialStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record StoryDefinitionSnapshot(
    string Title,
    string StoryPrompt,
    IReadOnlyList<PlayerQuestion> PlayerQuestions,
    StoryBible InitialStoryBible);

public sealed record PlayerResponse(Guid QuestionId, string Question, string ValidationInstruction, string Answer);
public sealed record StorySetupSnapshot(StoryDefinitionSnapshot Definition, IReadOnlyList<PlayerResponse> PlayerResponses);

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
    string StoryPrompt,
    IReadOnlyList<PlayerQuestionDraft> PlayerQuestions);

public sealed record PlayerQuestionDraft(Guid Id, string Question, string ValidationInstruction, int SortOrder);
public enum PlayerAnswerValidationStatus { NotValidated, Valid, Warning, AcceptedWithWarning }
public sealed record PlayerAnswerDraft(Guid QuestionId, string Answer, PlayerAnswerValidationStatus ValidationStatus, string? ValidationWarning);

public sealed record StartStoryDraft(
    Guid SourceStoryDefinitionId,
    StoryDefinitionSnapshot Definition,
    int CurrentQuestionIndex,
    IReadOnlyList<PlayerAnswerDraft> PlayerAnswers)
{
    public IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory { get; init; } = [];
}

public enum TabType { Settings, StoryDefinitionList, PlayStoryList, StoryDefinition, StoryPrompt, StartStory, PlayStory }
public enum PendingOperationType { GenerateStoryDefinition, ValidatePlayerAnswer, GenerateOpeningScene, GenerateStoryTurn, DiscoverModels, TestApiConnection }

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
    StartStoryDraft? StartStoryDraft,
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
