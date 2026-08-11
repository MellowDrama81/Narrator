using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class NarratorApplicationTests
{
    [Fact]
    public async Task CreateBlankDefinition_PersistsAnEmptyDefinitionWithoutCallingProvider()
    {
        var definitions = new MemoryDefinitions();
        var provider = new FakeProvider();
        var app = CreateApplication(definitions, new MemoryStates(), provider);

        var definition = await app.CreateBlankDefinitionAsync("   ");

        Assert.Equal("Untitled Story Definition", definition.Title);
        Assert.Empty(definition.StoryPrompt);
        Assert.Empty(definition.InitialEventsPrompt);
        Assert.Empty(definition.InitialStoryBible.Entries);
        Assert.Empty(definition.InitialPlannedEvents.Entries);
        Assert.Empty(definition.InitialVictoryConditions.Entries);
        Assert.Empty(definition.InitialLossConditions.Entries);
        Assert.Equal(definition, await definitions.GetAsync(definition.Id));
        Assert.Equal(0, provider.DefinitionCalls);
    }

    [Fact]
    public async Task CreateBlankDefinition_TrimsProvidedTitleAndAppendsSortOrder()
    {
        var definitions = new MemoryDefinitions();
        var now = DateTimeOffset.UtcNow;
        await definitions.SaveAsync(new StoryDefinition(
            Guid.NewGuid(), "Existing", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, StoryConditions.Empty, 7, now, now));
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var definition = await app.CreateBlankDefinitionAsync("  Hand-authored world  ");

        Assert.Equal("Hand-authored world", definition.Title);
        Assert.Equal(8, definition.SortOrder);
    }

    [Fact]
    public async Task GenerateDefinition_UsesRefinedStoryPromptInsteadOfDraftPrompt()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt mentioning today's mutable state");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined immutable prompt", "Suggested Title", "Initial events", [new("fact", "Fact", ["Content"], [], 3)], [], [], [])
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
            DefinitionResponse = new("   ", "Suggested Title", "", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "Suggested Title", "", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "A Generated Title", "", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "A Generated Title", "", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "   ", "", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "Suggested Title", "The village is under curfew.", [], [], [], [])
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
            DefinitionResponse = new("Refined prompt", "Suggested Title", new string('x', 20001), [], [], [], [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task GenerateDefinition_PersistsGeneratedInitialPlannedEvents()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", "", [], [new("The tower must fall.", 5, 4, null)], [], [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        var plannedEvent = Assert.Single(definition.InitialPlannedEvents.Entries);
        Assert.Equal("The tower must fall.", plannedEvent.Description);
        Assert.Equal(5, plannedEvent.Importance);
        Assert.Equal(4, plannedEvent.Urgency);
    }

    [Fact]
    public async Task GenerateDefinition_PersistsGeneratedInitialPlannedEventCondition()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", "", [],
            [
                new("The hero learns the prophecy.", 4, 3, null),
                new("The hero confronts the villain.", 5, 3, "The prophecy must already be known.")
            ], [], [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        var prophecy = Assert.Single(definition.InitialPlannedEvents.Entries, x => x.Description == "The hero learns the prophecy.");
        var confrontation = Assert.Single(definition.InitialPlannedEvents.Entries, x => x.Description == "The hero confronts the villain.");
        Assert.Null(prophecy.Condition);
        Assert.Equal("The prophecy must already be known.", confrontation.Condition);
    }

    [Fact]
    public async Task GenerateDefinition_PersistsGeneratedInitialVictoryAndLossConditions()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new(
                "Refined prompt", "Suggested Title", "", [], [],
                [new("Defeat the dragon.", false)],
                [new("The kingdom falls.", true)])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var definition = await app.GenerateDefinitionAsync(draft, false, Guid.NewGuid());

        var victory = Assert.Single(definition.InitialVictoryConditions.Entries);
        Assert.Equal("Defeat the dragon.", victory.Description);
        Assert.False(victory.Secret);
        Assert.NotEqual(Guid.Empty, victory.Id);
        var loss = Assert.Single(definition.InitialLossConditions.Entries);
        Assert.Equal("The kingdom falls.", loss.Description);
        Assert.True(loss.Secret);
        Assert.NotEqual(victory.Id, loss.Id);
    }

    [Fact]
    public async Task GenerateDefinition_RejectsInvalidGeneratedCondition()
    {
        var draft = new StoryPromptDraft(null, "Title", "Raw prompt");
        var provider = new FakeProvider
        {
            DefinitionResponse = new("Refined prompt", "Suggested Title", "", [], [], [new("   ", false)], [])
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.GenerateDefinitionAsync(draft, false, Guid.NewGuid()));
    }

    [Fact]
    public async Task StartStory_RemapsVictoryAndLossConditionIdsFreshWithNoCollisionAgainstTheSource()
    {
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var loss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var snapshot = new StoryDefinitionSnapshot(
            "Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, new([victory]), new([loss]));
        var draft = new StartStoryDraft(Guid.NewGuid(), snapshot);
        var provider = new FakeProvider
        {
            StoryResponse = new("Opening", ["Continue", "Wait"], [], [], [], [], [], [], [], [], "", "provider-id", 10, 20)
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var result = await app.StartStoryAsync(draft, Guid.NewGuid());

        var newVictory = Assert.Single(result.State.CurrentVictoryConditions.Entries);
        Assert.NotEqual(victory.Id, newVictory.Id);
        Assert.Equal(victory.Description, newVictory.Description);
        Assert.Equal(victory.Secret, newVictory.Secret);
        var newLoss = Assert.Single(result.State.CurrentLossConditions.Entries);
        Assert.NotEqual(loss.Id, newLoss.Id);
        Assert.Equal(loss.Description, newLoss.Description);
        Assert.Equal(loss.Secret, newLoss.Secret);
        Assert.NotEqual(newVictory.Id, newLoss.Id);

        // The snapshot carried in the new state's Setup must reference the same freshly remapped ids,
        // not the original definition's ids - matching the pattern already used for Story Bible/Planned
        // Event ids in StartStory_UsesTemporarySnapshotAndCarriesMaintenance above.
        Assert.Equal(newVictory.Id, Assert.Single(result.State.Setup.Definition.InitialVictoryConditions.Entries).Id);
        Assert.Equal(newLoss.Id, Assert.Single(result.State.Setup.Definition.InitialLossConditions.Entries).Id);
    }

    [Fact]
    public async Task StartStory_RejectsBlankStoryPromptBeforeCallingProvider()
    {
        var provider = new FakeProvider();
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);
        var snapshot = new StoryDefinitionSnapshot(
            "Untitled Story Definition", "   ", "", StoryBible.Empty, PlannedEvents.Empty,
            StoryConditions.Empty, StoryConditions.Empty);

        var error = await Assert.ThrowsAsync<NarratorException>(() =>
            app.StartStoryAsync(new StartStoryDraft(Guid.NewGuid(), snapshot), Guid.NewGuid()));

        Assert.Contains("Story Prompt", error.Message);
        Assert.Equal(0, provider.OpeningCalls);
    }

    [Fact]
    public async Task StartStory_AppliesInitialRevealedAndMetConditionSetsFromTheOpeningResponse()
    {
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var loss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var snapshot = new StoryDefinitionSnapshot(
            "Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, new([victory]), new([loss]));
        var draft = new StartStoryDraft(Guid.NewGuid(), snapshot);
        var provider = new FakeProvider
        {
            // The response can only reference the conditions' NEW ids, which are assigned during this
            // very call - StoryResponseFactory reads them back off the context NarratorApplication built.
            StoryResponseFactory = context => new(
                "Opening",
                ["Continue", "Wait"],
                [], [], [], [],
                [context.VictoryConditions.Conditions.Entries[0].Id],
                [],
                [],
                [context.LossConditions.Conditions.Entries[0].Id],
                "",
                "provider-id", 10, 20)
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var result = await app.StartStoryAsync(draft, Guid.NewGuid());

        var newVictory = Assert.Single(result.State.CurrentVictoryConditions.Entries);
        var newLoss = Assert.Single(result.State.CurrentLossConditions.Entries);

        Assert.Equal(newVictory.Id, Assert.Single(result.State.RevealedVictoryConditionIds));
        Assert.Empty(result.State.MetVictoryConditionIds);
        Assert.Empty(result.State.RevealedLossConditionIds);
        Assert.Equal(newLoss.Id, Assert.Single(result.State.MetLossConditionIds));

        // The opening StoryTurn only ever carries this turn's own delta - for the very first turn that
        // happens to equal the state's freshly-initialized cumulative totals.
        Assert.Equal(newVictory.Id, Assert.Single(result.Opening.RevealedVictoryConditionIds));
        Assert.Empty(result.Opening.MetVictoryConditionIds);
        Assert.Empty(result.Opening.RevealedLossConditionIds);
        Assert.Equal(newLoss.Id, Assert.Single(result.Opening.MetLossConditionIds));
    }

    [Fact]
    public async Task PlayTurn_AccumulatesRevealedAndMetConditionIdsCumulativelyWhileEachTurnKeepsOnlyItsOwnDelta()
    {
        var condition = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot(
            "Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, new([condition]), StoryConditions.Empty);
        var state = new StoryState(
            stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [],
            new([condition]), StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Turn one", ["Continue"], [], [], [], [], [condition.Id], [], [], [], "", null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        var firstTurn = await app.PlayTurnAsync(stateId, "Approach the dragon");

        Assert.Equal(condition.Id, Assert.Single(firstTurn.Turn.RevealedVictoryConditionIds));
        Assert.Empty(firstTurn.Turn.MetVictoryConditionIds);
        Assert.Equal(condition.Id, Assert.Single(firstTurn.State.RevealedVictoryConditionIds));
        Assert.Empty(firstTurn.State.MetVictoryConditionIds);

        // A condition already revealed must not be reported as revealed again - only the new "met" delta
        // is reported this turn.
        provider.StoryResponse = new("Turn two", ["Continue"], [], [], [], [], [], [condition.Id], [], [], "", null, null, null);

        var secondTurn = await app.PlayTurnAsync(stateId, "Strike the dragon");

        Assert.Empty(secondTurn.Turn.RevealedVictoryConditionIds);
        Assert.Equal(condition.Id, Assert.Single(secondTurn.Turn.MetVictoryConditionIds));
        // The state's cumulative totals now include both turns' contributions.
        Assert.Equal(condition.Id, Assert.Single(secondTurn.State.RevealedVictoryConditionIds));
        Assert.Equal(condition.Id, Assert.Single(secondTurn.State.MetVictoryConditionIds));
    }

    [Fact]
    public async Task StartStory_UsesTemporarySnapshotAndCarriesMaintenance()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", ["Content"], [], 3, 0);
        var snapshot = new StoryDefinitionSnapshot("Snapshot title", "Snapshot prompt", "", new([entry]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
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
            StoryResponse = new("Opening", ["Continue", "Wait"], [], [], [], [], [], [], [], [], "", "provider-id", 10, 20)
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
    public async Task StartStory_SetsStorySummaryFromOpeningResponseAndSendsEmptySummaryInTheRequest()
    {
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var draft = new StartStoryDraft(Guid.NewGuid(), snapshot);
        var provider = new FakeProvider
        {
            StoryResponse = new("Opening", ["Continue", "Wait"], [], [], [], [], [], [], [], [], "A hero arrives in a quiet village.", "provider-id", 10, 20)
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var result = await app.StartStoryAsync(draft, Guid.NewGuid());

        Assert.Equal("", provider.LastContext!.StorySummary);
        Assert.Equal("A hero arrives in a quiet village.", result.State.StorySummary);
    }

    [Fact]
    public async Task PlayTurn_ReplacesStorySummaryRatherThanMergingWithThePreviousValue()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "The hero left the village.", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["Continue"], [], [], [], [], [], [], [], [], "The hero reached the forest and lost the map.", null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        Assert.Equal("The hero left the village.", provider.LastContext?.StorySummary ?? state.StorySummary);
        var result = await app.PlayTurnAsync(stateId, "Continue");

        Assert.Equal("The hero left the village.", provider.LastContext!.StorySummary);
        Assert.Equal("The hero reached the forest and lost the map.", result.State.StorySummary);
    }

    [Fact]
    public async Task PlayTurn_RejectsOversizedStorySummary()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["Continue"], [], [], [], [], [], [], [], [], new string('x', limits.MaxStorySummaryCharacters + 1), null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
        var turns = Enumerable.Range(0, 3).Select(sequence => new StoryTurn(
            Guid.NewGuid(),
            stateId,
            sequence,
            sequence == 0 ? null : $"Action {sequence}",
            $"Narration {sequence}",
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            now,
            new("old-model", null, null, null))).ToArray();
        var states = new MemoryStates(state, turns);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["Continue", "Wait"], [], [], [], [], [], [], [], [], "", null, null, null)
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("Next", ["Continue"], [], [], [], [], [], [], [], [], "", null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "   "));
    }

    [Fact]
    public async Task PlayTurn_AllowsFewerSuggestedActionsThanConfiguredMinimum()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["Only one"], [], [], [], [], [], [], [], [], "", null, null, null)
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", ["A", "B", "C", "D", "E"], [], [], [], [], [], [], [], [], "", null, null, null)
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("   ", ["Continue"], [], [], [], [], [], [], [], [], "", null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsOversizedNarration()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var provider = new FakeProvider
        {
            StoryResponse = new(new string('x', limits.MaxNarrationCharacters + 1), ["Continue"], [], [], [], [], [], [], [], [], "", null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsEmptySuggestedAction()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var provider = new FakeProvider { StoryResponse = new("Next", ["   "], [], [], [], [], [], [], [], [], "", null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsOversizedSuggestedAction()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var provider = new FakeProvider
        {
            StoryResponse = new("Next", [new string('x', limits.MaxSuggestedActionCharacters + 1)], [], [], [], [], [], [], [], [], "", null, null, null)
        };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task PlayTurn_RejectsTooManyStoryBibleUpdates()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 0);
        var states = new MemoryStates(state, []);
        var limits = ConfiguredSettings().ContentLimits;
        var updates = Enumerable.Range(0, limits.MaxStoryBibleUpdatesPerResponse + 1)
            .Select(_ => new ProposedStoryBibleUpdate(StoryBibleOperation.Remove, Guid.NewGuid(), null))
            .ToArray();
        var provider = new FakeProvider { StoryResponse = new("Next", ["Continue"], [], updates, [], [], [], [], [], [], "", null, null, null) };
        var app = CreateApplication(new MemoryDefinitions(), states, provider);

        await Assert.ThrowsAsync<NarratorException>(() => app.PlayTurnAsync(stateId, "Continue"));
    }

    [Fact]
    public async Task StartStoryThenPlayTurn_RoundTripsPlannedEventsThroughGenerationContext()
    {
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "The bridge collapses.", 3, 3, "The scouts must have crossed first.", 0);
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, new([plannedEvent]), StoryConditions.Empty, StoryConditions.Empty);
        var draft = new StartStoryDraft(Guid.NewGuid(), snapshot);
        var provider = new FakeProvider
        {
            StoryResponse = new("Opening", ["Continue", "Wait"], [], [], [], [], [], [], [], [], "", "provider-id", 10, 20)
        };
        var app = CreateApplication(new MemoryDefinitions(), new MemoryStates(), provider);

        var started = await app.StartStoryAsync(draft, Guid.NewGuid());

        var startedPlannedEvent = Assert.Single(started.State.CurrentPlannedEvents.Entries);
        // Planned Event ids are remapped on start the same way Story Bible entry ids are.
        Assert.NotEqual(plannedEvent.Id, startedPlannedEvent.Id);
        Assert.Equal(startedPlannedEvent.Id, Assert.Single(provider.LastContext!.PlannedEvents.Entries).Id);
        // The Condition text carries over unchanged during the id remap.
        Assert.Equal("The scouts must have crossed first.", startedPlannedEvent.Condition);

        provider.StoryResponse = new(
            "Next scene",
            ["Continue"],
            [],
            [],
            [],
            [
                new(PlannedEventOperation.Remove, startedPlannedEvent.Id, null, PlannedEventOutcome.Fulfilled),
                new(PlannedEventOperation.Add, null, new("A new complication arises.", 2, 3, null), null)
            ],
            [],
            [],
            [],
            [],
            "",
            null,
            null,
            null);

        var turnResult = await app.PlayTurnAsync(started.State.Id, "Cross the bridge");

        // The turn's context carried forward the planned events from the story state that StartStoryAsync produced.
        Assert.Equal(startedPlannedEvent.Id, Assert.Single(provider.LastContext!.PlannedEvents.Entries).Id);
        var remaining = Assert.Single(turnResult.State.CurrentPlannedEvents.Entries);
        Assert.Equal("A new complication arises.", remaining.Description);
        Assert.DoesNotContain(turnResult.State.CurrentPlannedEvents.Entries, x => x.Id == startedPlannedEvent.Id);
        Assert.Contains(turnResult.Turn.PlannedEventChanges,
            x => x.Operation == PlannedEventOperation.Remove && x.Outcome == PlannedEventOutcome.Fulfilled);
    }

    [Fact]
    public async Task UpdateInitialPlannedEvents_ManualEditIsNotSubjectToTheMandatoryRemovalRule()
    {
        var mandatory = new PlannedEvent(Guid.NewGuid(), "Must happen", PlannedEventProcessor.MandatoryImportance, 3, null, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], new([mandatory]), [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        // A human editing Planned Events directly can freely remove a mandatory (importance 5) entry -
        // unlike the LLM Apply path, no outcome is required and the mandatory-removal rule is not enforced.
        var updated = await app.UpdateInitialPlannedEventsAsync(definition.Id, PlannedEvents.Empty);

        Assert.Empty(updated.InitialPlannedEvents.Entries);
        var history = Assert.Single(updated.PlannedEventMaintenanceHistory);
        Assert.Equal(PlannedEventMaintenanceReason.ManualEdit, history.Reason);
        Assert.Equal(PlannedEventOperation.Remove, Assert.Single(history.Changes).Operation);
    }

    [Fact]
    public async Task UpdateCurrentPlannedEvents_PersistsManualEditForStoryState()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var existing = new PlannedEvent(Guid.NewGuid(), "Existing event", 3, 3, null, 2);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], new([existing]), [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var added = new PlannedEvent(Guid.Empty, "Added mid-play", 4, 3, null, 2);
        var updated = await app.UpdateCurrentPlannedEventsAsync(stateId, new([existing, added]));

        Assert.Equal(2, updated.CurrentPlannedEvents.Entries.Count);
        var history = Assert.Single(updated.PlannedEventMaintenanceHistory);
        Assert.Equal(PlannedEventMaintenanceReason.ManualEdit, history.Reason);
        var addedChange = Assert.Single(history.Changes);
        Assert.Equal(PlannedEventOperation.Add, addedChange.Operation);
        Assert.Equal("Added mid-play", addedChange.After!.Description);
    }

    [Fact]
    public async Task UpdateCurrentPlannedEvents_RoundTripsAndTrimsAConditionOnAManualEdit()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var added = new PlannedEvent(Guid.Empty, "Added mid-play", 4, 3, "  Needs the tower to have fallen.  ", 2);
        var updated = await app.UpdateCurrentPlannedEventsAsync(stateId, new([added]));

        var persisted = Assert.Single(updated.CurrentPlannedEvents.Entries);
        Assert.Equal("Needs the tower to have fallen.", persisted.Condition);
    }

    [Fact]
    public async Task UpdateInitialPlannedEvents_RoundTripsANullCondition()
    {
        var definitions = new MemoryDefinitions();
        var withCondition = new PlannedEvent(Guid.NewGuid(), "Must happen", 3, 3, "Some condition.", 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], new([withCondition]), [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var withoutCondition = withCondition with { Condition = null };
        var updated = await app.UpdateInitialPlannedEventsAsync(definition.Id, new([withoutCondition]));

        Assert.Null(Assert.Single(updated.InitialPlannedEvents.Entries).Condition);
    }

    [Fact]
    public async Task UpdateInitialPlannedEvents_RejectsAConditionExceedingConfiguredLimit()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var settings = ConfiguredSettings();
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider(), settings);

        var oversized = new PlannedEvent(
            Guid.NewGuid(), "Event", 3, 3, new string('x', settings.ContentLimits.MaxPlannedEventConditionCharacters + 1), 0);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialPlannedEventsAsync(definition.Id, new([oversized])));
    }

    [Fact]
    public async Task UpdateInitialVictoryConditions_ManualEditRoundTripsAssigningFreshIdsToNewEntries()
    {
        var existing = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            new([existing]), StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var added = new StoryCondition(Guid.Empty, "Escape the tower.", true);
        var updated = await app.UpdateInitialVictoryConditionsAsync(definition.Id, new([existing, added]));

        Assert.Equal(2, updated.InitialVictoryConditions.Entries.Count);
        Assert.Contains(updated.InitialVictoryConditions.Entries, x => x.Id == existing.Id);
        var newEntry = Assert.Single(updated.InitialVictoryConditions.Entries, x => x.Id != existing.Id);
        Assert.NotEqual(Guid.Empty, newEntry.Id);
        Assert.Equal("Escape the tower.", newEntry.Description);
        Assert.True(newEntry.Secret);
        Assert.Empty(updated.InitialLossConditions.Entries);
    }

    [Fact]
    public async Task UpdateInitialVictoryConditions_RejectsWhenExceedingConfiguredLimits()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var settings = ConfiguredSettings() with
        {
            ContentLimits = ConfiguredSettings().ContentLimits with { MaxConditions = 1 }
        };
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider(), settings);

        var tooMany = new StoryConditions([
            new(Guid.Empty, "Defeat the dragon.", false),
            new(Guid.Empty, "Escape the tower.", false)
        ]);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialVictoryConditionsAsync(definition.Id, tooMany));
    }

    [Fact]
    public async Task UpdateInitialLossConditions_ManualEditRoundTripsAssigningFreshIdsToNewEntries()
    {
        var existing = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, new([existing]), 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var added = new StoryCondition(Guid.Empty, "The hero is captured.", false);
        var updated = await app.UpdateInitialLossConditionsAsync(definition.Id, new([existing, added]));

        Assert.Equal(2, updated.InitialLossConditions.Entries.Count);
        Assert.Contains(updated.InitialLossConditions.Entries, x => x.Id == existing.Id);
        var newEntry = Assert.Single(updated.InitialLossConditions.Entries, x => x.Id != existing.Id);
        Assert.NotEqual(Guid.Empty, newEntry.Id);
        Assert.Equal("The hero is captured.", newEntry.Description);
        Assert.False(newEntry.Secret);
        Assert.Empty(updated.InitialVictoryConditions.Entries);
    }

    [Fact]
    public async Task UpdateInitialLossConditions_RejectsWhenExceedingConfiguredLimits()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var settings = ConfiguredSettings() with
        {
            ContentLimits = ConfiguredSettings().ContentLimits with { MaxConditions = 1 }
        };
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider(), settings);

        var tooMany = new StoryConditions([
            new(Guid.Empty, "The hero is captured.", false),
            new(Guid.Empty, "The kingdom falls.", true)
        ]);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialLossConditionsAsync(definition.Id, tooMany));
    }

    [Fact]
    public async Task UpdateInitialVictoryConditions_RejectsEntryExceedingDescriptionLimit()
    {
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var invalid = new StoryConditions([new(Guid.NewGuid(), "   ", false)]);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialVictoryConditionsAsync(definition.Id, invalid));
    }

    [Fact]
    public async Task UpdateInitialVictoryConditions_RejectsDuplicateConditionIds()
    {
        var id = Guid.NewGuid();
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [],
            StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider());

        var duplicate = new StoryConditions([
            new(id, "Defeat the dragon.", false),
            new(id, "Escape the tower.", false)
        ]);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateInitialVictoryConditionsAsync(definition.Id, duplicate));
    }

    [Fact]
    public async Task CullDefinition_AlsoCullsPlannedEventsExceedingLimits()
    {
        var lowImportance = new PlannedEvent(Guid.NewGuid(), "Low", 1, 3, null, 0);
        var highImportance = new PlannedEvent(Guid.NewGuid(), "High", 5, 3, null, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], new([lowImportance, highImportance]), [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await definitions.SaveAsync(definition);
        var settings = ConfiguredSettings() with { StoryGeneration = ConfiguredSettings().StoryGeneration with { MaxPlannedEvents = 1 } };
        var app = CreateApplication(definitions, new MemoryStates(), new FakeProvider(), settings);

        var updated = await app.CullDefinitionAsync(definition.Id);

        var remaining = Assert.Single(updated.InitialPlannedEvents.Entries);
        Assert.Equal(highImportance.Id, remaining.Id);
        var history = Assert.Single(updated.PlannedEventMaintenanceHistory);
        Assert.Equal(PlannedEventMaintenanceReason.UserApprovedLimitCull, history.Reason);
    }

    [Fact]
    public async Task CullStoryState_AlsoCullsPlannedEventsExceedingLimits()
    {
        var lowImportance = new PlannedEvent(Guid.NewGuid(), "Low", 1, 3, null, 0);
        var highImportance = new PlannedEvent(Guid.NewGuid(), "High", 5, 3, null, 0);
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], new([lowImportance, highImportance]), [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, null, 0);
        var states = new MemoryStates(state, []);
        var settings = ConfiguredSettings() with { StoryGeneration = ConfiguredSettings().StoryGeneration with { MaxPlannedEvents = 1 } };
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider(), settings);

        var updated = await app.CullStoryStateAsync(stateId);

        var remaining = Assert.Single(updated.CurrentPlannedEvents.Entries);
        Assert.Equal(highImportance.Id, remaining.Id);
        var history = Assert.Single(updated.PlannedEventMaintenanceHistory);
        Assert.Equal(PlannedEventMaintenanceReason.UserApprovedLimitCull, history.Reason);
    }

    [Fact]
    public async Task UpdateInitialStoryBible_AddsEditsAndRemovesEntries()
    {
        var keep = new StoryBibleEntry(Guid.NewGuid(), "fact", "Keep", ["Original content"], [], 3, 0);
        var remove = new StoryBibleEntry(Guid.NewGuid(), "fact", "Remove me", ["Content"], [], 2, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", new([keep, remove]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
            Guid.NewGuid(), "Story", "Prompt", "", StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var existing = new StoryBibleEntry(Guid.NewGuid(), "fact", "Existing", ["Content"], [], 3, 2);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([existing]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
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
    public async Task UpdateStorySummary_RoundTripsAndTrimsWhitespace()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider());

        var updated = await app.UpdateStorySummaryAsync(stateId, "  The hero left the village.  ");

        Assert.Equal("The hero left the village.", updated.StorySummary);
        var saved = await states.GetAsync(stateId);
        Assert.Equal("The hero left the village.", saved!.StorySummary);
    }

    [Fact]
    public async Task UpdateStorySummary_RejectsSummaryExceedingConfiguredLimit()
    {
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), StoryBible.Empty, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, now, 2);
        var states = new MemoryStates(state, []);
        var settings = ConfiguredSettings();
        var app = CreateApplication(new MemoryDefinitions(), states, new FakeProvider(), settings);

        var oversized = new string('x', settings.ContentLimits.MaxStorySummaryCharacters + 1);

        await Assert.ThrowsAsync<NarratorException>(() => app.UpdateStorySummaryAsync(stateId, oversized));
    }

    [Fact]
    public async Task CullDefinition_RemovesLowestImportanceEntryAndRecordsHistory()
    {
        var lowImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "Low", ["Content"], [], 1, 0);
        var highImportance = new StoryBibleEntry(Guid.NewGuid(), "fact", "High", ["Content"], [], 5, 0);
        var definitions = new MemoryDefinitions();
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "Prompt", "", new([lowImportance, highImportance]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
            Guid.NewGuid(), "Story", "Prompt", "", new([entry]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([lowImportance, highImportance]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, null, 0);
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
        var snapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var state = new StoryState(stateId, "Story", null, new(snapshot), new([entry]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, null, 0);
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
        await definitions.SaveAsync(new StoryDefinition(Guid.NewGuid(), "Fits", "Prompt", "", new([withinLimits]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now));
        await definitions.SaveAsync(new StoryDefinition(Guid.NewGuid(), "TooBig", "Prompt", "", overLimits, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 1, now, now));

        var fittingSnapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", new([withinLimits]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var overSnapshot = new StoryDefinitionSnapshot("Story", "Prompt", "", overLimits, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var states = new MemoryStates();
        await states.CreateAsync(
            new StoryState(Guid.NewGuid(), "Fits", null, new(fittingSnapshot), new([withinLimits]), [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 0, now, null, 0),
            OpeningTurn());
        await states.CreateAsync(
            new StoryState(Guid.NewGuid(), "TooBig", null, new(overSnapshot), overLimits, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], "", 1, now, null, 0),
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

        var draft = new StartStoryDraft(Guid.NewGuid(), new StoryDefinitionSnapshot("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty));
        var provider = new FakeProvider { StoryResponse = new("Opening", ["Continue"], [], [], [], [], [], [], [], [], "", "id", 10, 20) };
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
        Guid.NewGuid(), Guid.NewGuid(), 0, null, "Opening", ["Continue"], [], [], [], [], [], [], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));

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
        // Settable, not init-only: some tests need to change the canned response between two calls
        // made through the same app/provider instance (e.g. StartStoryAsync then PlayTurnAsync).
        public StoryGenerationResponse StoryResponse { get; set; } = new("", [], [], [], [], [], [], [], [], [], "", null, null, null);
        public StoryDefinitionGenerationResponse DefinitionResponse { get; set; } = new("", "", "", [], [], [], []);
        // Used instead of StoryResponse when a test needs to reference ids that only exist once
        // NarratorApplication has remapped them (e.g. Story Condition ids freshly assigned during
        // StartStoryAsync) - the context passed to Generate*Async carries those new ids.
        public Func<GenerationContext, StoryGenerationResponse>? StoryResponseFactory { get; set; }
        public int DefinitionCalls { get; private set; }
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
        public Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default)
        {
            DefinitionCalls++;
            return Task.FromResult(DefinitionResponse);
        }
        public Task<StoryGenerationResponse> GenerateOpeningAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default)
        {
            OpeningCalls++;
            LastContext = context;
            return Task.FromResult(StoryResponseFactory?.Invoke(context) ?? StoryResponse);
        }
        public Task<StoryGenerationResponse> GenerateTurnAsync(ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(StoryResponseFactory?.Invoke(context) ?? StoryResponse);
        }
    }
}
