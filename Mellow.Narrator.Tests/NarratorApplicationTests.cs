using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class NarratorApplicationTests
{
    [Fact]
    public async Task StartStory_UsesTemporarySnapshotAndCarriesMaintenance()
    {
        var question = new PlayerQuestion(Guid.NewGuid(), "Name?", "Required", 0);
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", "Content", 3, 0);
        var snapshot = new StoryDefinitionSnapshot("Snapshot title", "Snapshot prompt", [question], new([entry]));
        var maintenance = new StoryBibleMaintenanceRecord(
            Guid.NewGuid(),
            StoryBibleMaintenanceReason.UserApprovedLimitCull,
            new(200, 4000, 60000),
            [],
            DateTimeOffset.UtcNow);
        var draft = new StartStoryDraft(
            Guid.NewGuid(),
            snapshot,
            1,
            [new(question.Id, "Alex", PlayerAnswerValidationStatus.AcceptedWithWarning, "Unusual")])
        {
            StoryBibleMaintenanceHistory = [maintenance]
        };
        var provider = new FakeProvider
        {
            StoryResponse = new("Opening", ["Continue"], [], [], "provider-id", 10, 20)
        };
        var states = new MemoryStates();
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        var result = await app.StartStoryAsync(draft, Guid.NewGuid());

        Assert.Equal("Snapshot title", result.State.Setup.Definition.Title);
        Assert.Equal("Alex", Assert.Single(result.State.Setup.PlayerResponses).Answer);
        Assert.Equal(maintenance, Assert.Single(result.State.StoryBibleMaintenanceHistory));
        var clonedEntry = Assert.Single(result.State.Setup.Definition.InitialStoryBible.Entries);
        Assert.NotEqual(entry.Id, clonedEntry.Id);
        Assert.Equal(clonedEntry.Id, Assert.Single(result.Opening.RelevantStoryBibleEntryIds));
        Assert.Equal("story-model", result.Opening.Generation.ModelId);
    }

    [Fact]
    public async Task StartStory_RejectsUnvalidatedAnswerWithoutCallingProvider()
    {
        var question = new PlayerQuestion(Guid.NewGuid(), "Name?", "Required", 0);
        var draft = new StartStoryDraft(
            Guid.NewGuid(),
            new("Story", "Prompt", [question], StoryBible.Empty),
            0,
            [new(question.Id, "Alex", PlayerAnswerValidationStatus.NotValidated, null)]);
        var provider = new FakeProvider();
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.StartStoryAsync(draft, Guid.NewGuid()));

        Assert.Equal(0, provider.OpeningCalls);
    }

    [Fact]
    public async Task PlayTurn_UsesConfiguredRecentWindowAndCurrentModel()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", [], StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot, []), StoryBible.Empty, [], 0, now, now, 2);
        var turns = Enumerable.Range(0, 3).Select(sequence => new StoryTurn(
            Guid.NewGuid(),
            stateId,
            sequence,
            sequence == 0 ? null : $"Action {sequence}",
            $"Narration {sequence}",
            [],
            [],
            [],
            now,
            new("old-model", null, null, null))).ToArray();
        var states = new MemoryStates(state, turns);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", [], [], [], null, null, null)
        };
        var settings = ConfiguredSettings() with
        {
            ModelId = "new-model",
            StoryGeneration = ConfiguredSettings().StoryGeneration with { RecentTurnCount = 2 }
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider, settings);

        var result = await app.PlayTurnAsync(stateId, "Continue");

        Assert.Equal(2, provider.LastContext!.RecentTurns.Count);
        Assert.Equal([1, 2], provider.LastContext.RecentTurns.Select(x => x.SequenceNumber));
        Assert.Equal("new-model", result.Turn.Generation.ModelId);
    }

    [Fact]
    public void StoryRequestCoordinator_RejectsConcurrentRequestForSameState()
    {
        var coordinator = new StoryRequestCoordinator();
        var id = Guid.NewGuid();
        using var first = coordinator.Enter(id);
        Assert.Throws<NarratorException>(() => coordinator.Enter(id));
        using var other = coordinator.Enter(Guid.NewGuid());
    }

    private static NarratorApplication CreateApplication(
        IStoryDefinitionRepository definitions,
        IStoryStateRepository states,
        ILanguageModelProvider provider,
        ApiConnectionSettings? settings = null) =>
        new(
            definitions,
            states,
            new MemorySettings(settings ?? ConfiguredSettings()),
            new MemorySecureStorage(),
            provider,
            TimeProvider.System,
            new(),
            new(),
            new SystemIdGenerator());

    private static ApiConnectionSettings ConfiguredSettings() => NarratorDefaults.Create() with
    {
        BaseUrl = new("https://example.test/v1"),
        ModelId = "story-model",
        Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow)
    };

    private sealed class MemoryDefinitions : IStoryDefinitionRepository
    {
        private readonly Dictionary<Guid, StoryDefinition> _values = [];
        public Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoryDefinitionSummary>>(_values.Values.Select(x => new StoryDefinitionSummary(x.Id, x.Title, x.SortOrder, x.UpdatedAtUtc)).ToArray());
        public Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(id));
        public Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default)
        {
            _values[definition.Id] = definition;
            return Task.CompletedTask;
        }
        public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());
    }

    private sealed class MemoryStates : IStoryStateRepository
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
        public Task SaveAsync(StoryState state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoryState> CopyAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemorySettings(ApiConnectionSettings value) : IApiConnectionSettingsStore
    {
        private ApiConnectionSettings _value = value;
        public Task<ApiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
        public Task SaveAsync(ApiConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            _value = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecureStorage : ISecureStorageService
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeProvider : ILanguageModelProvider
    {
        public StoryGenerationResponse StoryResponse { get; init; } = new("", [], [], [], null, null, null);
        public int OpeningCalls { get; private set; }
        public GenerationContext? LastContext { get; private set; }
        public Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PlayerAnswerValidationResponse> ValidatePlayerAnswerAsync(ApiConnectionSettings settings, string? credential, PlayerQuestion question, string answer, IReadOnlyList<PlayerResponse> previousAnswers, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoryGenerationResponse> GenerateOpeningAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default)
        {
            OpeningCalls++;
            LastContext = context;
            return Task.FromResult(StoryResponse);
        }
        public Task<StoryGenerationResponse> GenerateTurnAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(StoryResponse);
        }
    }
}
