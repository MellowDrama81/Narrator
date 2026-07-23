namespace Mellow.Narrator.Core;

public interface IStoryDefinitionRepository
{
    Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default);
    Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IStoryStateRepository
{
    Task<IReadOnlyList<StoryStateSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoryState?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoryTurn>> GetTurnsAsync(Guid id, int? takeLast = null, CancellationToken cancellationToken = default);
    Task CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken = default);
    Task ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken = default);
    Task CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken = default);
    Task SaveAsync(StoryState state, CancellationToken cancellationToken = default);
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

public interface ISecureStorageService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record PlayerAnswerValidationResponse(bool HasWarning, string? Warning);
public sealed record StoryDefinitionGenerationResponse(IReadOnlyList<ProposedStoryBibleEntry> InitialStoryBibleEntries);

public sealed record StoryGenerationResponse(
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates,
    string? ProviderResponseId,
    int? InputTokens,
    int? OutputTokens);

public sealed record ConnectionTestResult(
    bool Success,
    IReadOnlyList<string> Models,
    ConnectionCapabilities Capabilities,
    string? Error);

public sealed record BibleLimitImpact(int StoryDefinitionCount, int StoryStateCount);

public sealed record GenerationContext(
    StoryDefinitionSnapshot Definition,
    IReadOnlyList<PlayerResponse> PlayerResponses,
    StoryBible StoryBible,
    IReadOnlyList<StoryTurn> RecentTurns,
    string? PlayerAction,
    int NextTurnNumber);

public interface ILanguageModelProvider
{
    Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default);
    Task<PlayerAnswerValidationResponse> ValidatePlayerAnswerAsync(ApiConnectionSettings settings, string? credential, PlayerQuestion question, string answer, IReadOnlyList<PlayerResponse> previousAnswers, CancellationToken cancellationToken = default);
    Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default);
    Task<StoryGenerationResponse> GenerateOpeningAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default);
    Task<StoryGenerationResponse> GenerateTurnAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default);
}

public interface INarratorApplication
{
    Task<ApiConnectionSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<BibleLimitImpact> GetBibleLimitImpactAsync(StoryGenerationSettings proposed, CancellationToken cancellationToken = default);
    Task<StoryDefinition> GenerateDefinitionAsync(StoryPromptDraft draft, bool overwrite, Guid targetId, CancellationToken cancellationToken = default);
    Task<StoryDefinition> CullDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);
    Task<StoryState> CullStoryStateAsync(Guid stateId, CancellationToken cancellationToken = default);
    Task<PlayerAnswerValidationResponse> ValidateAnswerAsync(Guid definitionId, PlayerQuestion question, string answer, IReadOnlyList<PlayerResponse> previousAnswers, CancellationToken cancellationToken = default);
    Task<(StoryState State, StoryTurn Opening)> StartStoryAsync(StartStoryDraft draft, Guid targetStateId, CancellationToken cancellationToken = default);
    Task<(StoryState State, StoryTurn Turn)> PlayTurnAsync(Guid stateId, string action, CancellationToken cancellationToken = default);
}

public sealed class NarratorException(string message, Exception? innerException = null) : Exception(message, innerException);
