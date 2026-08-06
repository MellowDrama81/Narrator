using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class ImportExportProcessorTests
{
    [Fact]
    public void StateCopy_RemapsEntriesAndTurnsConsistently()
    {
        var (state, turn) = CreateState();

        var copy = ImportExportProcessor.CopyState(
            state,
            [turn],
            7,
            NarratorDefaults.Create().ContentLimits,
            NarratorDefaults.Create().StoryGeneration);

        Assert.NotEqual(state.Id, copy.State.Id);
        Assert.Equal(7, copy.State.SortOrder);
        var entry = Assert.Single(copy.State.CurrentStoryBible.Entries);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, entry.Id);
        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(copy.State.Id, copiedTurn.StoryStateId);
        Assert.Equal(entry.Id, Assert.Single(copiedTurn.RelevantStoryBibleEntryIds));
    }

    [Fact]
    public void StateCopy_RemapsPlannedEventIdsConsistentlyAndIndependentlyFromBibleIds()
    {
        var (state, turn) = CreateStateWithPlannedEvent();
        var originalPlannedEventId = state.CurrentPlannedEvents.Entries[0].Id;

        var copy = ImportExportProcessor.CopyState(
            state,
            [turn],
            7,
            NarratorDefaults.Create().ContentLimits,
            NarratorDefaults.Create().StoryGeneration);

        var plannedEvent = Assert.Single(copy.State.CurrentPlannedEvents.Entries);
        Assert.NotEqual(originalPlannedEventId, plannedEvent.Id);
        // The Planned Event id space is remapped independently of the Story Bible id space - the two
        // must not collide or accidentally share a mapping.
        Assert.NotEqual(copy.State.CurrentStoryBible.Entries[0].Id, plannedEvent.Id);

        var initialPlannedEvent = Assert.Single(copy.State.Setup.Definition.InitialPlannedEvents.Entries);
        Assert.Equal(plannedEvent.Id, initialPlannedEvent.Id);

        var maintenance = Assert.Single(copy.State.PlannedEventMaintenanceHistory);
        var maintenanceChange = Assert.Single(maintenance.Changes);
        Assert.Equal(plannedEvent.Id, maintenanceChange.EntryId);
        Assert.Equal(plannedEvent.Id, maintenanceChange.Before!.Id);
        Assert.Equal(plannedEvent.Id, maintenanceChange.After!.Id);

        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(plannedEvent.Id, Assert.Single(copiedTurn.RelevantPlannedEventIds));
        var turnChange = Assert.Single(copiedTurn.PlannedEventChanges);
        Assert.Equal(plannedEvent.Id, turnChange.EntryId);
    }

    [Fact]
    public void StateCopy_RemapsPrerequisiteReferenceToTheSiblingsNewId()
    {
        var (state, turn) = CreateStateWithPlannedEventPrerequisite();
        var prerequisiteId = state.CurrentPlannedEvents.Entries.Single(x => x.Description == "Prerequisite").Id;
        var dependentId = state.CurrentPlannedEvents.Entries.Single(x => x.Description == "Dependent").Id;

        var copy = ImportExportProcessor.CopyState(
            state, [turn], 7, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration);

        var copiedPrerequisite = copy.State.CurrentPlannedEvents.Entries.Single(x => x.Description == "Prerequisite");
        var copiedDependent = copy.State.CurrentPlannedEvents.Entries.Single(x => x.Description == "Dependent");
        Assert.NotEqual(prerequisiteId, copiedPrerequisite.Id);
        Assert.NotEqual(dependentId, copiedDependent.Id);
        Assert.Equal(copiedPrerequisite.Id, Assert.Single(copiedDependent.PrerequisiteEventIds));
    }

    [Fact]
    public void DefinitionCopy_RemapsPlannedEventIdsConsistently()
    {
        var now = DateTimeOffset.UtcNow;
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "The tower must fall.", 5, 3, [], 0);
        var change = new AppliedPlannedEventChange(
            PlannedEventOperation.Replace, plannedEvent.Id, plannedEvent, plannedEvent, PlannedEventChangeSource.ManualEdit, null);
        var maintenance = new PlannedEventMaintenanceRecord(
            Guid.NewGuid(), PlannedEventMaintenanceReason.ManualEdit, new(50, 2000, 20000), [change], now);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [maintenance], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        var copy = ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration);

        var copiedEvent = Assert.Single(copy.InitialPlannedEvents.Entries);
        Assert.NotEqual(plannedEvent.Id, copiedEvent.Id);
        var copiedMaintenance = Assert.Single(copy.PlannedEventMaintenanceHistory);
        var copiedChange = Assert.Single(copiedMaintenance.Changes);
        Assert.Equal(copiedEvent.Id, copiedChange.EntryId);
        Assert.Equal(copiedEvent.Id, copiedChange.Before!.Id);
        Assert.Equal(copiedEvent.Id, copiedChange.After!.Id);
    }

    [Fact]
    public void DefinitionCopy_RemapsConditionIdsConsistentlyAndPreservesDescriptionAndSecret()
    {
        var now = DateTimeOffset.UtcNow;
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var loss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            PlannedEvents.Empty, [], new([victory]), new([loss]), 0, now, now);

        var copy = ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration);

        var newVictory = Assert.Single(copy.InitialVictoryConditions.Entries);
        Assert.NotEqual(victory.Id, newVictory.Id);
        Assert.Equal(victory.Description, newVictory.Description);
        Assert.Equal(victory.Secret, newVictory.Secret);
        var newLoss = Assert.Single(copy.InitialLossConditions.Entries);
        Assert.NotEqual(loss.Id, newLoss.Id);
        Assert.Equal(loss.Description, newLoss.Description);
        Assert.Equal(loss.Secret, newLoss.Secret);
        // Victory and Loss Conditions are separate id spaces and must never collide with each other.
        Assert.NotEqual(newVictory.Id, newLoss.Id);
    }

    [Fact]
    public void DefinitionCopy_RejectsConditionWithEmptyDescription()
    {
        var now = DateTimeOffset.UtcNow;
        var invalid = new StoryCondition(Guid.NewGuid(), "   ", false);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            PlannedEvents.Empty, [], new([invalid]), StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsDuplicateVictoryConditionIds()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var first = new StoryCondition(id, "Defeat the dragon.", false);
        var second = new StoryCondition(id, "Escape the tower.", false);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            PlannedEvents.Empty, [], new([first, second]), StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsDuplicateLossConditionIds()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var first = new StoryCondition(id, "The kingdom falls.", true);
        var second = new StoryCondition(id, "The hero is captured.", false);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            PlannedEvents.Empty, [], StoryConditions.Empty, new([first, second]), 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsPlannedEventWithEmptyDescription()
    {
        var now = DateTimeOffset.UtcNow;
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "   ", 3, 3, [], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsPlannedEventWithInvalidImportance()
    {
        var now = DateTimeOffset.UtcNow;
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "The tower must fall.", 6, 3, [], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsDuplicatePlannedEventIds()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var first = new PlannedEvent(id, "First event.", 3, 3, [], 0);
        var second = new PlannedEvent(id, "Second event.", 3, 3, [], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([first, second]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsPlannedEventsExceedingStoryGenerationCountLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new PlannedEvent(Guid.NewGuid(), "First event.", 3, 3, [], 0);
        var second = new PlannedEvent(Guid.NewGuid(), "Second event.", 3, 3, [], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([first, second]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);
        var storyGeneration = NarratorDefaults.Create().StoryGeneration with { MaxPlannedEvents = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, storyGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsEmptyGuidPrerequisite()
    {
        var now = DateTimeOffset.UtcNow;
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "Event", 3, 3, [Guid.Empty], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsSelfReferencingPrerequisite()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var plannedEvent = new PlannedEvent(id, "Event", 3, 3, [id], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsALivePrerequisiteCycle()
    {
        var now = DateTimeOffset.UtcNow;
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = new PlannedEvent(aId, "A", 3, 3, [bId], 0);
        var b = new PlannedEvent(bId, "B", 3, 3, [aId], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([a, b]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_AcceptsADanglingPrerequisite()
    {
        // A prerequisite id that doesn't correspond to any entry in the collection represents a
        // prerequisite already resolved in earlier, unmodeled history - valid, not an error.
        var now = DateTimeOffset.UtcNow;
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "Event", 3, 3, [Guid.NewGuid()], 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            new([plannedEvent]), [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        var copy = ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration);

        Assert.Single(copy.InitialPlannedEvents.Entries);
    }

    [Fact]
    public void StateCopy_RejectsPlannedEventChangeWithInvalidBeforeAfterShape()
    {
        var (state, opening) = CreateStateWithPlannedEvent();
        var plannedEvent = state.CurrentPlannedEvents.Entries[0];
        // A Remove change must have After = null; supplying one makes the shape inconsistent.
        var invalidChange = new AppliedPlannedEventChange(
            PlannedEventOperation.Remove, plannedEvent.Id, plannedEvent, plannedEvent, PlannedEventChangeSource.LlmUpdate, PlannedEventOutcome.Fulfilled);
        var withChange = opening with { PlannedEventChanges = [invalidChange] };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withChange], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RemapsConditionIdsConsistentlyAndIndependentlyFromOtherIdSpaces()
    {
        var (state, turn) = CreateStateWithConditions();
        var originalVictoryId = state.CurrentVictoryConditions.Entries[0].Id;
        var originalLossId = state.CurrentLossConditions.Entries[0].Id;

        var copy = ImportExportProcessor.CopyState(
            state, [turn], 7, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration);

        var newVictory = Assert.Single(copy.State.CurrentVictoryConditions.Entries);
        Assert.NotEqual(originalVictoryId, newVictory.Id);
        Assert.Equal("Defeat the dragon.", newVictory.Description);
        Assert.False(newVictory.Secret);
        var newLoss = Assert.Single(copy.State.CurrentLossConditions.Entries);
        Assert.NotEqual(originalLossId, newLoss.Id);
        Assert.Equal("The kingdom falls.", newLoss.Description);
        Assert.True(newLoss.Secret);
        // Victory, Loss, and Story Bible ids are all separate id spaces and must never collide.
        Assert.NotEqual(newVictory.Id, newLoss.Id);
        Assert.NotEqual(copy.State.CurrentStoryBible.Entries[0].Id, newVictory.Id);
        Assert.NotEqual(copy.State.CurrentStoryBible.Entries[0].Id, newLoss.Id);

        // The snapshot embedded in Setup carries the same remapped ids as the live current conditions.
        Assert.Equal(newVictory.Id, Assert.Single(copy.State.Setup.Definition.InitialVictoryConditions.Entries).Id);
        Assert.Equal(newLoss.Id, Assert.Single(copy.State.Setup.Definition.InitialLossConditions.Entries).Id);

        // State-level revealed/met sets and the turn-level delta both follow the same remapping.
        Assert.Equal(newVictory.Id, Assert.Single(copy.State.RevealedVictoryConditionIds));
        Assert.Empty(copy.State.MetVictoryConditionIds);
        Assert.Empty(copy.State.RevealedLossConditionIds);
        Assert.Equal(newLoss.Id, Assert.Single(copy.State.MetLossConditionIds));

        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(newVictory.Id, Assert.Single(copiedTurn.RevealedVictoryConditionIds));
        Assert.Empty(copiedTurn.MetVictoryConditionIds);
        Assert.Empty(copiedTurn.RevealedLossConditionIds);
        Assert.Equal(newLoss.Id, Assert.Single(copiedTurn.MetLossConditionIds));
    }

    [Fact]
    public void StateCopy_RejectsConditionWithEmptyDescription()
    {
        var (state, turn) = CreateState();
        var invalid = new StoryCondition(Guid.NewGuid(), "   ", false);
        var withInvalid = state with
        {
            Setup = state.Setup with { Definition = state.Setup.Definition with { InitialVictoryConditions = new([invalid]) } },
            CurrentVictoryConditions = new([invalid])
        };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withInvalid, [turn], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsDanglingRevealedVictoryConditionId()
    {
        var (state, turn) = CreateState();
        var withDangling = state with { RevealedVictoryConditionIds = [Guid.NewGuid()] };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withDangling, [turn], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsDuplicateMetLossConditionId()
    {
        var (state, turn) = CreateStateWithConditions();
        var lossId = state.CurrentLossConditions.Entries[0].Id;
        var withDuplicate = state with { MetLossConditionIds = [lossId, lossId] };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withDuplicate, [turn], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsNonUtcTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            "A sufficiently long prompt for validation.",
            "",
            StoryBible.Empty,
            [],
            PlannedEvents.Empty,
            [],
            StoryConditions.Empty,
            StoryConditions.Empty,
            0,
            now.ToOffset(TimeSpan.FromHours(2)),
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition,
            1,
            NarratorDefaults.Create().ContentLimits,
            NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsEmptyId()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.Empty, "Story", "A sufficiently long prompt for validation.", "", StoryBible.Empty, [],
            PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsOversizedTitle()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = NarratorDefaults.Create().ContentLimits;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            new string('x', limits.MaxStoryTitleCharacters + 1),
            "A sufficiently long prompt for validation.",
            "",
            StoryBible.Empty,
            [],
            PlannedEvents.Empty,
            [],
            StoryConditions.Empty,
            StoryConditions.Empty,
            0,
            now,
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, limits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsOversizedStoryPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = NarratorDefaults.Create().ContentLimits;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            new string('x', limits.MaxStoryPromptCharacters + 1),
            "",
            StoryBible.Empty,
            [],
            PlannedEvents.Empty,
            [],
            StoryConditions.Empty,
            StoryConditions.Empty,
            0,
            now,
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, limits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleEntryWithNoFacts()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", [], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", new([entry]), [],
            PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleEntryWithInvalidImportance()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", ["Content"], [], 6, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", new([entry]), [],
            PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsDuplicateBibleEntryIds()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var first = new StoryBibleEntry(id, "fact", "First", ["Content"], [], 3, 0);
        var second = new StoryBibleEntry(id, "fact", "Second", ["Content"], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", new([first, second]), [],
            PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleExceedingStoryGenerationEntryLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new StoryBibleEntry(Guid.NewGuid(), "fact", "First", ["Content"], [], 3, 0);
        var second = new StoryBibleEntry(Guid.NewGuid(), "fact", "Second", ["Content"], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", "", new([first, second]), [],
            PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, 0, now, now);
        var storyGeneration = NarratorDefaults.Create().StoryGeneration with { MaxStoryBibleEntries = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, storyGeneration));
    }

    [Fact]
    public void StateCopy_RejectsNonContiguousTurnSequence()
    {
        var (state, opening) = CreateState();
        var second = opening with { Id = Guid.NewGuid(), SequenceNumber = 2, PlayerAction = "Continue" };
        var withLatestSequence = state with { LastCommittedTurnSequence = 2 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withLatestSequence, [opening, second], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsDuplicateTurnIds()
    {
        var (state, opening) = CreateState();
        var second = opening with { SequenceNumber = 1, PlayerAction = "Continue" };
        var withLatestSequence = state with { LastCommittedTurnSequence = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withLatestSequence, [opening, second], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsTurnWithEmptyId()
    {
        var (state, opening) = CreateState();
        var withEmptyId = opening with { Id = Guid.Empty };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withEmptyId], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsTurnWithMismatchedStoryStateId()
    {
        var (state, opening) = CreateState();
        var mismatched = opening with { StoryStateId = Guid.NewGuid() };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [mismatched], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsOpeningTurnWithPlayerAction()
    {
        var (state, opening) = CreateState();
        var withAction = opening with { PlayerAction = "Not allowed on the opening turn" };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withAction], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsChangeWithInvalidBeforeAfterShape()
    {
        var (state, opening) = CreateState();
        var entry = state.CurrentStoryBible.Entries[0];
        // An Add change must have Before = null; supplying one makes the shape inconsistent.
        var invalidChange = new AppliedStoryBibleChange(StoryBibleOperation.Add, entry.Id, entry, entry, StoryBibleChangeSource.LlmUpdate);
        var withChange = opening with { StoryBibleChanges = [invalidChange] };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withChange], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsBibleExceedingStoryGenerationCharacterLimit()
    {
        var (state, opening) = CreateState();
        var storyGeneration = NarratorDefaults.Create().StoryGeneration with { MaxStoryBibleEntryCharacters = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [opening], 1, NarratorDefaults.Create().ContentLimits, storyGeneration));
    }

    [Fact]
    public async Task ReadLimitedAsync_RejectsStreamExceedingMaximumImportBytes()
    {
        using var stream = new MemoryStream(new byte[ImportExportProcessor.MaximumImportBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportExportProcessor.ReadLimitedAsync(stream));
    }

    [Fact]
    public async Task ReadLimitedAsync_RejectsNonSeekableStreamExceedingMaximumImportBytesWhileReading()
    {
        await using var stream = new NonSeekableStream(new byte[ImportExportProcessor.MaximumImportBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportExportProcessor.ReadLimitedAsync(stream));
    }

    [Fact]
    public async Task ReadLimitedAsync_ReturnsExactBytesForAStreamWithinTheLimit()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(bytes);

        var result = await ImportExportProcessor.ReadLimitedAsync(stream);

        Assert.Equal(bytes, result);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static (StoryState State, StoryTurn Turn) CreateState()
    {
        var stateId = Guid.NewGuid();
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", ["The player is Alex."], [], 4, 0);
        var bible = new StoryBible([entry]);
        var snapshot = new StoryDefinitionSnapshot(
            "Story",
            "A sufficiently long prompt for validation.",
            "",
            bible,
            PlannedEvents.Empty,
            StoryConditions.Empty,
            StoryConditions.Empty);
        var now = DateTimeOffset.UtcNow;
        var state = new StoryState(stateId, "Story", null, new(snapshot), bible, [], PlannedEvents.Empty, [], StoryConditions.Empty, StoryConditions.Empty, [], [], [], [], 0, now, null, 0);
        var turn = new StoryTurn(
            Guid.NewGuid(),
            stateId,
            0,
            null,
            "Opening",
            ["Continue"],
            [entry.Id],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            now,
            new("model", null, null, null));
        return (state, turn);
    }

    private static (StoryState State, StoryTurn Turn) CreateStateWithPlannedEvent()
    {
        var (state, turn) = CreateState();
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "The tower must fall.", 5, 3, [], 0);
        var change = new AppliedPlannedEventChange(
            PlannedEventOperation.Replace, plannedEvent.Id, plannedEvent, plannedEvent, PlannedEventChangeSource.ManualEdit, null);
        var maintenance = new PlannedEventMaintenanceRecord(
            Guid.NewGuid(), PlannedEventMaintenanceReason.ManualEdit, new(50, 2000, 20000), [change], DateTimeOffset.UtcNow);
        var withPlannedEvents = state with
        {
            Setup = state.Setup with
            {
                Definition = state.Setup.Definition with { InitialPlannedEvents = new([plannedEvent]) }
            },
            CurrentPlannedEvents = new([plannedEvent]),
            PlannedEventMaintenanceHistory = [maintenance]
        };
        var turnWithPlannedEvents = turn with
        {
            RelevantPlannedEventIds = [plannedEvent.Id],
            PlannedEventChanges = [change]
        };
        return (withPlannedEvents, turnWithPlannedEvents);
    }

    private static (StoryState State, StoryTurn Turn) CreateStateWithPlannedEventPrerequisite()
    {
        var (state, turn) = CreateState();
        var prerequisite = new PlannedEvent(Guid.NewGuid(), "Prerequisite", 3, 3, [], 0);
        var dependent = new PlannedEvent(Guid.NewGuid(), "Dependent", 3, 3, [prerequisite.Id], 0);
        var withPlannedEvents = state with
        {
            Setup = state.Setup with
            {
                Definition = state.Setup.Definition with { InitialPlannedEvents = new([prerequisite, dependent]) }
            },
            CurrentPlannedEvents = new([prerequisite, dependent])
        };
        return (withPlannedEvents, turn);
    }

    private static (StoryState State, StoryTurn Turn) CreateStateWithConditions()
    {
        var (state, turn) = CreateState();
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var loss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var withConditions = state with
        {
            Setup = state.Setup with
            {
                Definition = state.Setup.Definition with
                {
                    InitialVictoryConditions = new([victory]),
                    InitialLossConditions = new([loss])
                }
            },
            CurrentVictoryConditions = new([victory]),
            CurrentLossConditions = new([loss]),
            RevealedVictoryConditionIds = [victory.Id],
            MetLossConditionIds = [loss.Id]
        };
        var turnWithConditions = turn with
        {
            RevealedVictoryConditionIds = [victory.Id],
            MetLossConditionIds = [loss.Id]
        };
        return (withConditions, turnWithConditions);
    }
}
