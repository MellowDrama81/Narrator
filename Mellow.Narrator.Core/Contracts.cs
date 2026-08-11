namespace Mellow.Narrator.Core;

public interface IStoryDefinitionRepository
{
    Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default);
    Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default);
    Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IStoryStateRepository
{
    Task<IReadOnlyList<StoryStateSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoryState?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoryTurn>> GetTurnsAsync(Guid id, int? takeLast = null, CancellationToken cancellationToken = default);
    Task<StoryStateAggregateSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default);
    Task CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken = default);
    Task ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken = default);
    Task CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken = default);
    Task SaveAsync(StoryState state, CancellationToken cancellationToken = default);
    Task UpdateLabelAsync(Guid id, string label, CancellationToken cancellationToken = default);
    Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default);
    Task<StoryState> CopyAsync(Guid id, CancellationToken cancellationToken = default);
    Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IWorkspaceStateStore
{
    Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default);
}

public interface IApiConnectionSettingsStore
{
    Task<ApiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApiConnectionSettings settings, CancellationToken cancellationToken = default);
}

public interface ITrashStore
{
    Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(string trashId, CancellationToken cancellationToken = default);
    Task DeletePermanentlyAsync(string trashId, CancellationToken cancellationToken = default);
    Task EmptyAsync(CancellationToken cancellationToken = default);
}

public interface IRecoveryNoticeStore
{
    Task<IReadOnlyList<RecoveryNotice>> ConsumeAsync(CancellationToken cancellationToken = default);
}

public interface INarratorLogLevelSwitch
{
    NarratorLogLevel MinimumLevel { get; set; }
}

public interface ISecureStorageService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record StoryDefinitionGenerationResponse(
    string RefinedStoryPrompt,
    string SuggestedTitle,
    string InitialEventsPrompt,
    IReadOnlyList<ProposedStoryBibleEntry> InitialStoryBibleEntries,
    IReadOnlyList<ProposedPlannedEvent> InitialPlannedEvents,
    IReadOnlyList<ProposedStoryCondition> InitialVictoryConditions,
    IReadOnlyList<ProposedStoryCondition> InitialLossConditions);

public sealed record StoryGenerationResponse(
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates,
    IReadOnlyList<Guid> RelevantPlannedEventIds,
    IReadOnlyList<ProposedPlannedEventUpdate> PlannedEventUpdates,
    IReadOnlyList<Guid> RevealedVictoryConditionIds,
    IReadOnlyList<Guid> MetVictoryConditionIds,
    IReadOnlyList<Guid> RevealedLossConditionIds,
    IReadOnlyList<Guid> MetLossConditionIds,
    // The full replacement value for StoryState.StorySummary - always returned, never a delta. See the
    // StoryState.StorySummary comment in Models.cs.
    string StorySummary,
    string? ProviderResponseId,
    int? InputTokens,
    int? OutputTokens);

public sealed record ConnectionTestResult(
    bool Success,
    IReadOnlyList<string> Models,
    ConnectionCapabilities Capabilities,
    string? Error);

public sealed record BibleLimitImpact(
    int StoryDefinitionCount,
    int StoryStateCount,
    int PlannedEventDefinitionCount,
    int PlannedEventStateCount);

// Groups a condition list (victory or loss) with the ids already revealed/met so far, for the provider
// to filter and annotate when building the request - see OpenAiCompatibleProvider.BuildStoryMessages.
// AlreadyMetIds conditions are dropped entirely from what's sent (nothing left to evaluate); the rest are
// sent with a revealed flag so the model never re-reveals one already established.
public sealed record ConditionsContext(StoryConditions Conditions, IReadOnlyList<Guid> RevealedIds, IReadOnlyList<Guid> MetIds);

public sealed record GenerationContext(
    StoryDefinitionSnapshot Definition,
    StoryBible StoryBible,
    PlannedEvents PlannedEvents,
    ConditionsContext VictoryConditions,
    ConditionsContext LossConditions,
    // The current StoryState.StorySummary value, sent as-is; empty for the opening scene.
    string StorySummary,
    IReadOnlyList<StoryTurn> RecentTurns,
    string? PlayerAction,
    int NextTurnNumber);

public interface ILanguageModelProvider
{
    Task<IReadOnlyList<string>> DiscoverModelsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default);
    Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(ApiConnectionSettings settings, string? credential, string storyDefinitionPrompt, CancellationToken cancellationToken = default);
    Task<StoryGenerationResponse> GenerateOpeningAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default);
    Task<StoryGenerationResponse> GenerateTurnAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default);
}

public interface INarratorApplication
{
    Task<ApiConnectionSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> HasApiCredentialAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> DiscoverModelsAsync(CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<BibleLimitImpact> GetBibleLimitImpactAsync(StoryGenerationSettings proposed, CancellationToken cancellationToken = default);
    Task<StoryDefinition> CreateBlankDefinitionAsync(string? title = null, CancellationToken cancellationToken = default);
    Task<StoryDefinition> GenerateDefinitionAsync(StoryPromptDraft draft, bool overwrite, Guid targetId, CancellationToken cancellationToken = default);
    Task<StoryDefinition> CullDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);
    Task<StoryState> CullStoryStateAsync(Guid stateId, CancellationToken cancellationToken = default);
    Task<StoryDefinition> UpdateInitialStoryBibleAsync(Guid definitionId, StoryBible bible, CancellationToken cancellationToken = default);
    Task<StoryState> UpdateCurrentStoryBibleAsync(Guid stateId, StoryBible bible, CancellationToken cancellationToken = default);
    Task<StoryDefinition> UpdateInitialPlannedEventsAsync(Guid definitionId, PlannedEvents events, CancellationToken cancellationToken = default);
    Task<StoryState> UpdateCurrentPlannedEventsAsync(Guid stateId, PlannedEvents events, CancellationToken cancellationToken = default);
    Task<StoryDefinition> UpdateInitialVictoryConditionsAsync(Guid definitionId, StoryConditions conditions, CancellationToken cancellationToken = default);
    Task<StoryDefinition> UpdateInitialLossConditionsAsync(Guid definitionId, StoryConditions conditions, CancellationToken cancellationToken = default);
    Task<StoryState> UpdateStorySummaryAsync(Guid stateId, string summary, CancellationToken cancellationToken = default);
    Task<(StoryState State, StoryTurn Opening)> StartStoryAsync(StartStoryDraft draft, Guid targetStateId, CancellationToken cancellationToken = default);
    Task<(StoryState State, StoryTurn Turn)> PlayTurnAsync(Guid stateId, string action, CancellationToken cancellationToken = default);
}

public sealed class NarratorException(string message, Exception? innerException = null) : Exception(message, innerException);
