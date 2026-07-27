using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class NarratorApplicationTests
{
    [Fact]
    public async Task GenerateDefinition_UsesRefinedStoryPromptInsteadOfDraftPrompt()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt mentioning today's mutable state");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined immutable prompt", "Suggested Title", "Initial events", [new("fact", "Fact", "Content", 3)])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        Assert.Equal("Refined immutable prompt", definition.StoryPrompt);
        Assert.Single(definition.InitialStoryBible.Entries);
    }

    [Fact]
    public async Task GenerateDefinition_RejectsEmptyRefinedStoryPrompt()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("   ", "Suggested Title", "", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task GenerateDefinition_UsesSuggestedTitleWhenDraftTitleIsBlank()
    {
        var draft = new StoryPromptDraft(null, "   ", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "A Generated Title", "", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        Assert.Equal("A Generated Title", definition.Title);
    }

    [Fact]
    public async Task GenerateDefinition_KeepsDraftTitleWhenProvided()
    {
        var draft = new StoryPromptDraft(null, "My Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "A Generated Title", "", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        Assert.Equal("My Title", definition.Title);
    }

    [Fact]
    public async Task GenerateDefinition_RejectsEmptySuggestedTitleWhenDraftTitleIsBlank()
    {
        var draft = new StoryPromptDraft(null, "", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "   ", "", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task GenerateDefinition_PersistsGeneratedInitialEventsPrompt()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", "The village is under curfew.", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        Assert.Equal("The village is under curfew.", definition.InitialEventsPrompt);
    }

    [Fact]
    public async Task GenerateDefinition_RejectsOversizedInitialEventsPrompt()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", new string('x', 20001), [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task StartStory_UsesTemporarySnapshotAndCarriesMaintenance()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", "Content", 3, 0);
        var snapshot = new StoryDefinitionSnapshot("Snapshot title", "Snapshot prompt", new([entry]));
        var maintenance = new StoryBibleMaintenanceRecord(
            Guid.NewGuid(),
            StoryBibleMaintenanceReason.UserApprovedLimitCull,
            new(200, 4000, 60000),
            [],
            DateTimeOffset.UtcNow);
        var draft = new StartStoryDraft(Guid.NewGuid(), snapshot)
        {
            StoryBibleMaintenanceHistory = [maintenance]
        };
        var provider = new FakeProvider
        {
            StoryResponse = new("Opening", ["Continue", "Wait"], [], [], "provider-id", 10, 20)
        };
        var states = new MemoryStates();
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        var result = await app.StartStoryAsync(draft, Guid.NewGuid());

        Assert.Equal("Snapshot title", result.State.Setup.Definition.Title);
        Assert.Equal(maintenance, Assert.Single(result.State.StoryBibleMaintenanceHistory));
        var clonedEntry = Assert.Single(result.State.Setup.Definition.InitialStoryBible.Entries);
        Assert.NotEqual(entry.Id, clonedEntry.Id);
        Assert.Equal(clonedEntry.Id, Assert.Single(result.Opening.RelevantStoryBibleEntryIds));
        Assert.Equal("story-model", result.Opening.Generation.ModelId);
    }

    [Fact]
    public async Task DiscoverModels_DoesNotRequireConfiguredModel()
    {
        var provider = new FakeProvider();
        var settings = ConfiguredSettings() with { ModelId = null };
        var store = new MemorySettings(settings);
        var app = new NarratorApplication(
            new MemoryDefinitions(),
            new MemoryStates(),
            store,
            new MemorySecureStorage(),
            provider,
            TimeProvider.System,
            new(),
            new(),
            new SystemIdGenerator());

        var models = await app.DiscoverModelsAsync();
        await app.SaveSettingsAsync(settings with { ModelId = "model-a" }, null);

        Assert.Equal(["model-a", "model-b"], models);
        Assert.Null(provider.DiscoverySettings!.ModelId);
        var saved = await store.LoadAsync();
        Assert.True(saved.Capabilities.SupportsModelDiscovery);
        Assert.Equal(StructuredOutputTier.Untested, saved.Capabilities.StructuredOutputTier);
    }

    [Fact]
    public async Task PlayTurn_UsesConfiguredRecentWindowAndCurrentModel()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 2);
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
            StoryResponse = new("Next", ["Continue", "Wait"], [], [], null, null, null)
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
    public async Task PlayTurn_AllowsFewerSuggestedActionsThanConfiguredMinimum()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["Only one"], [], [], null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        var result = await app.PlayTurnAsync(stateId, "Continue");

        Assert.Equal(["Only one"], result.Turn.SuggestedActions);
    }

    [Fact]
    public async Task PlayTurn_TruncatesSuggestedActionsToConfiguredMaximum()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["A", "B", "C", "D", "E"], [], [], null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        var result = await app.PlayTurnAsync(stateId, "Continue");

        Assert.Equal(["A", "B", "C"], result.Turn.SuggestedActions);
    }

    [Fact]
    public async Task UpdateInitialStoryBible_AddsEditsAndRemovesEntries()
    {
        var keep = new StoryBibleEntry(Guid.NewGuid(), "fact", "Keep", "Original content", 3, 0);
        var remove = new StoryBibleEntry(Guid.NewGuid(), "fact", "Remove me", "Content", 2, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", new([keep, remove]), [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var edited = keep with { Content = "Updated content" };
        var added = new StoryBibleEntry(Guid.Empty, "fact", "New entry", "New content", 4, 0);
        var updated = await app.UpdateInitialStoryBibleAsync(definition.Id, new([edited, added]));

        Assert.Equal(2, updated.InitialStoryBible.Entries.Count);
        var keptEntry = Assert.Single(updated.InitialStoryBible.Entries, x => x.Id == keep.Id);
        Assert.Equal("Updated content", keptEntry.Content);
        var newEntry = Assert.Single(updated.InitialStoryBible.Entries, x => x.Id != keep.Id);
        Assert.NotEqual(Guid.Empty, newEntry.Id);
        Assert.Equal("New entry", newEntry.Name);
        var history = Assert.Single(updated.StoryBibleMaintenanceHistory);
        Assert.Equal(StoryBibleMaintenanceReason.ManualEdit, history.Reason);
        Assert.Equal(3, history.Changes.Count);
        Assert.Contains(history.Changes, x => x.Operation == StoryBibleOperation.Replace && x.EntryId == keep.Id);
        Assert.Contains(history.Changes, x => x.Operation == StoryBibleOperation.Remove && x.EntryId == remove.Id);
        Assert.Contains(history.Changes, x => x.Operation == StoryBibleOperation.Add && x.EntryId == newEntry.Id);
    }

    [Fact]
    public async Task UpdateInitialStoryBible_RejectsEntryExceedingLimits()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", StoryBible.Empty, [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var invalid = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", "", 3, 0);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialStoryBibleAsync(definition.Id, new([invalid])));
    }

    [Fact]
    public async Task UpdateInitialStoryBible_RejectsDuplicateEntryIds()
    {
        var id = Guid.NewGuid();
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", StoryBible.Empty, [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var first = new StoryBibleEntry(id, "fact", "First", "Content", 3, 0);
        var duplicate = new StoryBibleEntry(id, "fact", "Duplicate", "Content", 3, 0);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialStoryBibleAsync(definition.Id, new([first, duplicate])));
    }

    [Fact]
    public async Task UpdateCurrentStoryBible_PersistsManualEditForStoryState()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var existing = new StoryBibleEntry(Guid.NewGuid(), "fact", "Existing", "Content", 3, 2);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([existing]), [], 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var added = new StoryBibleEntry(Guid.Empty, "fact", "Added mid-play", "Content", 4, 2);
        var updated = await app.UpdateCurrentStoryBibleAsync(stateId, new([existing, added]));

        Assert.Equal(2, updated.CurrentStoryBible.Entries.Count);
        var history = Assert.Single(updated.StoryBibleMaintenanceHistory);
        Assert.Equal(StoryBibleMaintenanceReason.ManualEdit, history.Reason);
        var addedChange = Assert.Single(history.Changes);
        Assert.Equal(StoryBibleOperation.Add, addedChange.Operation);
        Assert.Equal("Added mid-play", addedChange.After!.Name);
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
        public StoryDefinitionGenerationResponse DefinitionResponse { get; init; } = new("", "", "", []);
        public int OpeningCalls { get; private set; }
        public GenerationContext? LastContext { get; private set; }
        public ApiConnectionSettings? DiscoverySettings { get; private set; }
        public Task<IReadOnlyList<string>> DiscoverModelsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
        {
            DiscoverySettings = settings;
            return Task.FromResult<IReadOnlyList<string>>(["model-a", "model-b"]);
        }
        public Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(DefinitionResponse);
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
