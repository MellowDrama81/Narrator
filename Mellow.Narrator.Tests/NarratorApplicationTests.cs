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
            DefinitionResponse = new("Refined immutable prompt", "Suggested Title", "Initial events", [new("fact", "Fact", ["Content"], [], 3)])
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
    public async Task GenerateDefinition_RejectsOverwriteWhenSourceNoLongerExists()
    {
        var draft = new StoryPromptDraft(Guid.NewGuid(), "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", "", [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, true, Guid.NewGuid()));
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
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", ["Content"], [], 3, 0);
        var snapshot = new StoryDefinitionSnapshot("Snapshot title", "Snapshot prompt", new([entry]));
        var change = new AppliedStoryBibleChange(StoryBibleOperation.Replace, entry.Id, entry, entry, StoryBibleChangeSource.ManualEdit);
        var maintenance = new StoryBibleMaintenanceRecord(
            Guid.NewGuid(),
            StoryBibleMaintenanceReason.UserApprovedLimitCull,
            new(200, 4000, 60000),
            [change],
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
        var clonedEntry = Assert.Single(result.State.Setup.Definition.InitialStoryBible.Entries);
        Assert.NotEqual(entry.Id, clonedEntry.Id);
        Assert.Equal(clonedEntry.Id, Assert.Single(result.Opening.RelevantStoryBibleEntryIds));
        Assert.Equal("story-model", result.Opening.Generation.ModelId);

        // The carried-over maintenance history must reference the entry's *new* id, matching the
        // remapping applied to the live bible entry above, not the old id from the definition.
        var history = Assert.Single(result.State.StoryBibleMaintenanceHistory);
        Assert.Equal(maintenance.Id, history.Id);
        Assert.Equal(maintenance.Reason, history.Reason);
        var mappedChange = Assert.Single(history.Changes);
        Assert.Equal(clonedEntry.Id, mappedChange.EntryId);
        Assert.Equal(clonedEntry.Id, mappedChange.Before!.Id);
        Assert.Equal(clonedEntry.Id, mappedChange.After!.Id);
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
    public async Task PlayTurn_RejectsBlankAction()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("Next", ["Continue"], [], [], null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "   "));
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
    public async Task PlayTurn_RejectsEmptyNarration()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("   ", ["Continue"], [], [], null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsOversizedNarration()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var provider = new FakeProvider
        {
            StoryResponse = new(new string('x', limits.MaxNarrationCharacters + 1), ["Continue"], [], [], null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsEmptySuggestedAction()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("Next", ["   "], [], [], null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsOversizedSuggestedAction()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", [new string('x', limits.MaxSuggestedActionCharacters + 1)], [], [], null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsTooManyStoryBibleUpdates()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var updates = Enumerable.Range(0, limits.MaxStoryBibleUpdatesPerResponse + 1)
            .Select(_ => new ProposedStoryBibleUpdate(StoryBibleOperation.Remove, Guid.NewGuid(), null))
            .ToArray();
        var provider = new FakeProvider { StoryResponse = new("Next", ["Continue"], [], updates, null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task UpdateInitialStoryBible_AddsEditsAndRemovesEntries()
    {
        var keep = new StoryBibleEntry(Guid.NewGuid(), "fact", "Keep", ["Original content"], [], 3, 0);
        var remove = new StoryBibleEntry(Guid.NewGuid(), "fact", "Remove me", ["Content"], [], 2, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", new([keep, remove]), [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var edited = keep with { KnownFacts = ["Updated content"] };
        var added = new StoryBibleEntry(Guid.Empty, "fact", "New entry", ["New content"], [], 4, 0);
        var updated = await app.UpdateInitialStoryBibleAsync(definition.Id, new([edited, added]));

        Assert.Equal(2, updated.InitialStoryBible.Entries.Count);
        var keptEntry = Assert.Single(updated.InitialStoryBible.Entries, x => x.Id == keep.Id);
        Assert.Equal(["Updated content"], keptEntry.KnownFacts);
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

        var invalid = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", [], [], 3, 0);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialStoryBibleAsync(definition.Id, new([invalid])));
    }

    [Fact]
    public async Task UpdateInitialStoryBible_RejectsNegativeLastRelevantTurnNumber()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", StoryBible.Empty, [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var invalid = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", ["Content"], [], 3, -1);

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

        var first = new StoryBibleEntry(id, "fact", "First", ["Content"], [], 3, 0);
        var duplicate = new StoryBibleEntry(id, "fact", "Duplicate", ["Content"], [], 3, 0);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialStoryBibleAsync(definition.Id, new([first, duplicate])));
    }

    [Fact]
    public async Task UpdateCurrentStoryBible_PersistsManualEditForStoryState()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var existing = new StoryBibleEntry(Guid.NewGuid(), "fact", "Existing", ["Content"], [], 3, 2);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([existing]), [], 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var added = new StoryBibleEntry(Guid.Empty, "fact", "Added mid-play", ["Content"], [], 4, 2);
        var updated = await app.UpdateCurrentStoryBibleAsync(stateId, new([existing, added]));

        Assert.Equal(2, updated.CurrentStoryBible.Entries.Count);
        var history = Assert.Single(updated.StoryBibleMaintenanceHistory);
        Assert.Equal(StoryBibleMaintenanceReason.ManualEdit, history.Reason);
        var addedChange = Assert.Single(history.Changes);
        Assert.Equal(StoryBibleOperation.Add, addedChange.Operation);
        Assert.Equal("Added mid-play", addedChange.After!.Name);
    }

    [Fact]
    public async Task CullDefinition_RemovesLowestImportanceEntryAndRecordsHistory()
    {
        var lowImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "Low", ["Content"], [], 1, 0);
        var highImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "High", ["Content"], [], 5, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", new([lowImportance, highImportance]), [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var settings = ConfiguredSettings() with { StoryGeneration = ConfiguredSettings().StoryGeneration with { MaxStoryBibleEntries = 1 } };
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider(), settings);

        var updated = await app.CullDefinitionAsync(definition.Id);

        var remaining = Assert.Single(updated.InitialStoryBible.Entries);
        Assert.Equal(highImportance.Id, remaining.Id);
        var history = Assert.Single(updated.StoryBibleMaintenanceHistory);
        Assert.Equal(StoryBibleMaintenanceReason.UserApprovedLimitCull, history.Reason);
        Assert.Equal(StoryBibleOperation.Remove, Assert.Single(history.Changes).Operation);
    }

    [Fact]
    public async Task CullDefinition_ReturnsUnchangedWhenNothingExceedsLimits()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Entry", ["Content"], [], 3, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", new([entry]), [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var updated = await app.CullDefinitionAsync(definition.Id);

        Assert.Equal(definition, updated);
    }

    [Fact]
    public async Task CullStoryState_RemovesLowestImportanceEntryAndRecordsHistory()
    {
        var lowImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "Low", ["Content"], [], 1, 0);
        var highImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "High", ["Content"], [], 5, 0);
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([lowImportance, highImportance]), [], 0, now, null, 0);
        var states = new MemoryStates(state, []);
        var settings = ConfiguredSettings() with { StoryGeneration = ConfiguredSettings().StoryGeneration with { MaxStoryBibleEntries = 1 } };
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider(), settings);

        var updated = await app.CullStoryStateAsync(stateId);

        var remaining = Assert.Single(updated.CurrentStoryBible.Entries);
        Assert.Equal(highImportance.Id, remaining.Id);
        var history = Assert.Single(updated.StoryBibleMaintenanceHistory);
        Assert.Equal(StoryBibleMaintenanceReason.UserApprovedLimitCull, history.Reason);
    }

    [Fact]
    public async Task CullStoryState_ReturnsUnchangedWhenNothingExceedsLimits()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Entry", ["Content"], [], 3, 0);
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([entry]), [], 0, now, null, 0);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var updated = await app.CullStoryStateAsync(stateId);

        Assert.Equal(state, updated);
    }

    [Fact]
    public async Task SaveSettings_RejectsInvalidSettingsBeforeTouchingTheStore()
    {
        var original = ConfiguredSettings();
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), new FakeProvider(), original);
        var invalid = original with { MaxOutputTokens = 1 };

        await Assert.ThrowsAsync<NarratorException>(() => app.SaveSettingsAsync(invalid, null));

        Assert.Equal(original, await app.GetSettingsAsync());
    }

    [Fact]
    public async Task TestConnection_PersistsNegotiatedCapabilitiesWhenSettingsUnchanged()
    {
        var settings = ConfiguredSettings();
        var store = new MemorySettings(settings);
        var provider = new FakeProvider
        {
            TestConnectionResult = new(true, [], new(false, StructuredOutputTier.StrictJsonSchema, settings.ModelId, DateTimeOffset.UtcNow), null)
        };
        var app = new NarratorApplication(
            new MemoryDefinitions(), new MemoryStates(), store, new MemorySecureStorage(),
            provider, TimeProvider.System, new ApiConnectionCoordinator(), new StoryRequestCoordinator(), new SystemIdGenerator());

        await app.TestConnectionAsync();

        var saved = await app.GetSettingsAsync();
        Assert.Equal(StructuredOutputTier.StrictJsonSchema, saved.Capabilities.StructuredOutputTier);
    }

    [Fact]
    public async Task TestConnection_SkipsPersistingCapabilitiesWhenSettingsChangedDuringTest()
    {
        var original = ConfiguredSettings();
        var changed = original with { ModelId = "different-model" };
        var store = new SettingsThatChangeAfterFirstLoad(original, changed);
        var provider = new FakeProvider
        {
            TestConnectionResult = new(true, [], new(false, StructuredOutputTier.StrictJsonSchema, original.ModelId, DateTimeOffset.UtcNow), null)
        };
        var app = new NarratorApplication(
            new MemoryDefinitions(), new MemoryStates(), store, new MemorySecureStorage(),
            provider, TimeProvider.System, new ApiConnectionCoordinator(), new StoryRequestCoordinator(), new SystemIdGenerator());

        await app.TestConnectionAsync();

        Assert.False(store.SaveWasCalled);
    }

    [Fact]
    public async Task GenerateDefinition_RejectsBlankModelId()
    {
        var draft = new StoryPromptDraft(null, "Title", "Prompt");
        var settings = ConfiguredSettings() with { ModelId = null };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), new FakeProvider(), settings);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task DiscoverModels_RejectsMissingBaseUrl()
    {
        var settings = ConfiguredSettings() with { BaseUrl = null };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), new FakeProvider(), settings);

        await Assert.ThrowsAsync<NarratorException>(() => app.DiscoverModelsAsync());
    }

    [Fact]
    public async Task GetBibleLimitImpact_CountsOnlyDefinitionsAndStatesExceedingProposedLimits()
    {
        var withinLimits = new StoryBibleEntry(Guid.NewGuid(), "fact", "Small", ["Content"], [], 3, 0);
        var overLimits = new StoryBible(Enumerable.Range(0, 5)
            .Select(i => new StoryBibleEntry(Guid.NewGuid(), "fact", $"Entry {i}", ["Content"], [], 3, 0)).ToArray());

        var definitions = new MemoryDefinitions();
        var now = DateTimeOffset.UtcNow;
        await definitions.SaveAsync(new StoryDefinition(Guid.NewGuid(), "Fits", "Prompt", new([withinLimits]), [], 0, now, now));
        await definitions.SaveAsync(new StoryDefinition(Guid.NewGuid(), "TooBig", "Prompt", overLimits, [], 1, now, now));

        var fittingSnapshot = new StoryDefinitionSnapshot("Story", "Prompt", new([withinLimits]));
        var overSnapshot = new StoryDefinitionSnapshot("Story", "Prompt", overLimits);
        var states = new MemoryStates();
        await states.CreateAsync(
            new StoryState(Guid.NewGuid(), "Fits", null, new(fittingSnapshot), new([withinLimits]), [], 0, now, null, 0),
            OpeningTurn());
        await states.CreateAsync(
            new StoryState(Guid.NewGuid(), "TooBig", null, new(overSnapshot), overLimits, [], 1, now, null, 0),
            OpeningTurn());

        var app = CreateApplication(definitions, states, new FakeProvider());
        var proposed = ConfiguredSettings().StoryGeneration with { MaxStoryBibleEntries = 3 };

        var impact = await app.GetBibleLimitImpactAsync(proposed);

        Assert.Equal(1, impact.StoryDefinitionCount);
        Assert.Equal(1, impact.StoryStateCount);
    }

    [Fact]
    public async Task StartStory_RejectsConcurrentRequestForSameTargetState()
    {
        var coordinator = new StoryRequestCoordinator();
        var targetStateId = Guid.NewGuid();
        using var lease = coordinator.Enter(targetStateId);

        var draft = new StartStoryDraft(Guid.NewGuid(), new StoryDefinitionSnapshot("Story", "Prompt", StoryBible.Empty));
        var provider = new FakeProvider { StoryResponse = new("Opening", ["Continue"], [], [], "id", 10, 20) };
        var app = new NarratorApplication(
            new MemoryDefinitions(),
            new MemoryStates(),
            new MemorySettings(ConfiguredSettings()),
            new MemorySecureStorage(),
            provider,
            TimeProvider.System,
            new ApiConnectionCoordinator(),
            coordinator,
            new SystemIdGenerator());

        await Assert.ThrowsAsync<NarratorException>(() => app.StartStoryAsync(draft, targetStateId));
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

    private static StoryTurn OpeningTurn() => new(
        Guid.NewGuid(), Guid.NewGuid(), 0, null, "Opening", ["Continue"], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));

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

    // Simulates settings changing between TestConnectionAsync's initial read and its post-test
    // "are settings still what I tested" check: returns `first` on the first LoadAsync call (the
    // initial read) and `subsequent` on every call after (the post-test check).
    private sealed class SettingsThatChangeAfterFirstLoad(ApiConnectionSettings first, ApiConnectionSettings subsequent) : IApiConnectionSettingsStore
    {
        private int _loadCount;
        public bool SaveWasCalled { get; private set; }
        public Task<ApiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_loadCount++ == 0 ? first : subsequent);
        public Task SaveAsync(ApiConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            SaveWasCalled = true;
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
        public ConnectionTestResult? TestConnectionResult { get; set; }
        public Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(TestConnectionResult ?? throw new NotSupportedException());
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
