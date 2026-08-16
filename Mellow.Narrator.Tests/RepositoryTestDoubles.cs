using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

// Shared in-memory fakes for tests that need real read/write behavior (seed data, then observe what
// GenerateDefinitionAsync/StartStoryAsync/etc. actually persisted).
internal sealed class MemoryDefinitions : IStoryDefinitionRepository
{
    private readonly Dictionary<Guid, StoryDefinition> _values = [];
    public Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoryDefinitionSummary>>(_values.Values.Select(x => new StoryDefinitionSummary(x.Id, x.Title, x.SortOrder, x.UpdatedAtUtc, x.Description)).ToArray());
    public Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(id));
    public Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default)
    {
        _values[definition.Id] = definition;
        return Task.CompletedTask;
    }
    public Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException());
    public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException());
}

internal sealed class MemoryStates : IStoryStateRepository
{
    private readonly Dictionary<Guid, StoryState> _states = [];
    private readonly Dictionary<Guid, List<StoryTurn>> _turns = [];
    public MemoryStates() { }
    public MemoryStates(StoryState state, IReadOnlyList<StoryTurn> turns)
    {
        _states[state.Id] = state;
        _turns[state.Id] = turns.ToList();
    }
    public Task<IReadOnlyList<StoryStateSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoryStateSummary>>(_states.Values.Select(x => new StoryStateSummary(x.Id, x.Label, x.SortOrder, x.StartedAtUtc, x.LastActionAtUtc)).ToArray());
    public Task<StoryState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_states.GetValueOrDefault(id));
    public Task<IReadOnlyList<StoryTurn>> GetTurnsAsync(Guid id, int? takeLast = null, CancellationToken cancellationToken = default)
    {
        var values = _turns.GetValueOrDefault(id) ?? [];
        return Task.FromResult<IReadOnlyList<StoryTurn>>(takeLast is null ? values.ToArray() : values.TakeLast(takeLast.Value).ToArray());
    }
    public Task<StoryStateAggregateSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var state = _states.GetValueOrDefault(id);
        return Task.FromResult(state is null
            ? null
            : new StoryStateAggregateSnapshot(state, (_turns.GetValueOrDefault(id) ?? []).ToArray()));
    }
    public Task CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken = default)
    {
        _states[state.Id] = state;
        _turns[state.Id] = [openingTurn];
        return Task.CompletedTask;
    }
    public Task ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken = default)
    {
        _states[state.Id] = state;
        _turns[state.Id].Add(turn);
        return Task.CompletedTask;
    }
    public Task SaveAsync(StoryState state, CancellationToken cancellationToken = default)
    {
        _states[state.Id] = state;
        return Task.CompletedTask;
    }
    public Task UpdateLabelAsync(Guid id, string label, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<StoryState> CopyAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

// Shared always-empty fakes that throw on any mutation, for tests that assert a repository must never
// be touched at all (e.g. credential/settings tests with no definitions or story states in play).
internal sealed class NotSupportedDefinitions : IStoryDefinitionRepository
{
    public Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoryDefinitionSummary>>([]);
    public Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoryDefinition?>(null);
    public Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class NotSupportedStates : IStoryStateRepository
{
    public Task<IReadOnlyList<StoryStateSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoryStateSummary>>([]);
    public Task<StoryState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoryState?>(null);
    public Task<IReadOnlyList<StoryTurn>> GetTurnsAsync(Guid id, int? takeLast = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoryTurn>>([]);
    public Task<StoryStateAggregateSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoryStateAggregateSnapshot?>(null);
    public Task CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task SaveAsync(StoryState state, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task UpdateLabelAsync(Guid id, string label, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<StoryState> CopyAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
