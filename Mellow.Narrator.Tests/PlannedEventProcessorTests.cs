using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class PlannedEventProcessorTests
{
    [Fact]
    public void Apply_UpdatesRelevanceAndAddsNewEntry()
    {
        var first = Entry("00000000-0000-0000-0000-000000000001", "Old event", 1, 3, 1);
        var second = Entry("00000000-0000-0000-0000-000000000002", "Kept event", 4, 3, 2);
        var result = PlannedEventProcessor.Apply(
            new([first, second]),
            [second.Id],
            [new(PlannedEventOperation.Add, null, new("New event", 3, 3, [], null, []), null)],
            3,
            Limits());

        Assert.Equal(3, result.Events.Entries.Count);
        Assert.Equal(3, result.Events.Entries.Single(x => x.Id == second.Id).LastRelevantTurnNumber);
        Assert.Contains(result.Changes, x => x.Operation == PlannedEventOperation.Add);
    }

    [Fact]
    public void Apply_RejectsRemovalWithNullOutcome()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, entry.Id, null, null) };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RemovesNonMandatoryEntryWithAbandonedOutcome()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, entry.Id, null, PlannedEventOutcome.Abandoned) };

        var result = PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits());

        Assert.Empty(result.Events.Entries);
        Assert.Equal(PlannedEventOutcome.Abandoned, Assert.Single(result.Changes).Outcome);
    }

    [Fact]
    public void Apply_RejectsMandatoryRemovalWithAbandonedOutcome()
    {
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, mandatory.Id, null, PlannedEventOutcome.Abandoned) };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsMandatoryRemovalWithAbandonedOutcomeEvenWithLowUrgency()
    {
        // Urgency is purely a narration-guidance signal and must have no bearing on the mandatory-removal
        // rule, which is keyed solely on Importance == MandatoryImportance.
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 1, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, mandatory.Id, null, PlannedEventOutcome.Abandoned) };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_AllowsMandatoryRemovalWithFulfilledOutcome()
    {
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, mandatory.Id, null, PlannedEventOutcome.Fulfilled) };

        var result = PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits());

        Assert.Empty(result.Events.Entries);
        Assert.Equal(PlannedEventOutcome.Fulfilled, Assert.Single(result.Changes).Outcome);
    }

    [Fact]
    public void Apply_RejectsReplaceThatLowersMandatoryImportance()
    {
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, mandatory.Id, new("Must happen, reworded", 4, 3, [], null, []), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_AllowsReplaceThatKeepsMandatoryImportanceAtMaximum()
    {
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, mandatory.Id, new("Must happen, reworded", PlannedEventProcessor.MandatoryImportance, 3, [], null, []), null)
        };

        var result = PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits());

        var updated = Assert.Single(result.Events.Entries);
        Assert.Equal("Must happen, reworded", updated.Description);
        Assert.Equal(PlannedEventProcessor.MandatoryImportance, updated.Importance);
    }

    [Fact]
    public void Apply_AllowsReplaceThatChangesUrgencyOnAMandatoryEntry()
    {
        // The mandatory-demotion guard fires only on Importance; Urgency can move freely in either
        // direction, even on a mandatory entry, without tripping it.
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 1, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, mandatory.Id, new("Must happen", PlannedEventProcessor.MandatoryImportance, 5, [], null, []), null)
        };

        var result = PlannedEventProcessor.Apply(new([mandatory]), [], updates, 1, Limits());

        var updated = Assert.Single(result.Events.Entries);
        Assert.Equal(PlannedEventProcessor.MandatoryImportance, updated.Importance);
        Assert.Equal(5, updated.Urgency);
    }

    [Fact]
    public void Apply_RejectsUnknownRelevantEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        Assert.Throws<NarratorException>(() =>
            PlannedEventProcessor.Apply(new([entry]), [Guid.NewGuid()], [], 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsRemovalOfRelevantEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(
            new([entry]),
            [entry.Id],
            [new(PlannedEventOperation.Remove, entry.Id, null, PlannedEventOutcome.Abandoned)],
            1,
            Limits()));
    }

    [Fact]
    public void Apply_RejectsUpdateReferencingUnknownEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, Guid.NewGuid(), new("Different", 3, 3, [], null, []), null) };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsSameEntryUpdatedMoreThanOnce()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, entry.Id, new("First edit", 3, 3, [], null, []), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, entry.Id, null, PlannedEventOutcome.Abandoned)
        };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsIncompleteProposedEntry()
    {
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("", 3, 3, [], null, []), null) };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsAddWithOutOfRangeUrgency()
    {
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Event", 3, 0, [], null, []), null) };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsReplaceWithOutOfRangeUrgency()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[] { new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, entry.Id, new("Event", 3, 6, [], null, []), null) };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_UsesInjectedIdAndMarksAdditionRelevant()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var result = PlannedEventProcessor.Apply(
            PlannedEvents.Empty,
            [],
            [new(PlannedEventOperation.Add, null, new("New event", 3, 3, [], null, []), null)],
            4,
            Limits(),
            () => id);

        Assert.Equal(id, Assert.Single(result.Events.Entries).Id);
        Assert.Contains(id, result.RelevantEntryIds);
        Assert.Equal(4, result.Events.Entries[0].LastRelevantTurnNumber);
    }

    [Fact]
    public void Cull_NeverSelectsAMandatoryEntryAsEvictionCandidate()
    {
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var minor = Entry("00000000-0000-0000-0000-000000000002", "Nice to have", 1, 3, 0);

        var result = PlannedEventProcessor.CullToLimits(new([mandatory, minor]), Limits(maxPlannedEvents: 1));

        Assert.Contains(result.Events.Entries, x => x.Id == mandatory.Id);
        Assert.DoesNotContain(result.Events.Entries, x => x.Id == minor.Id);
        Assert.Equal(PlannedEventChangeSource.AutomaticCull, Assert.Single(result.Changes).Source);
    }

    [Fact]
    public void Cull_StillEvictsAHighUrgencyEntryWhenNotMandatory()
    {
        // Urgency does not protect an entry from culling - only Importance == MandatoryImportance does.
        var mandatory = Entry("00000000-0000-0000-0000-000000000001", "Must happen", PlannedEventProcessor.MandatoryImportance, 1, 0);
        var urgentButNotMandatory = Entry("00000000-0000-0000-0000-000000000002", "Should happen soon", 1, 5, 0);

        var result = PlannedEventProcessor.CullToLimits(new([mandatory, urgentButNotMandatory]), Limits(maxPlannedEvents: 1));

        Assert.Contains(result.Events.Entries, x => x.Id == mandatory.Id);
        Assert.DoesNotContain(result.Events.Entries, x => x.Id == urgentButNotMandatory.Id);
    }

    [Fact]
    public void Apply_AllowsAddWithPrerequisiteReferencingAKnownEntry()
    {
        var prerequisite = Entry("00000000-0000-0000-0000-000000000001", "First event", 3, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Second event", 3, 3, [prerequisite.Id], null, []), null)
        };

        var result = PlannedEventProcessor.Apply(new([prerequisite]), [], updates, 1, Limits());

        var added = Assert.Single(result.Events.Entries, x => x.Description == "Second event");
        Assert.Equal(prerequisite.Id, Assert.Single(added.PrerequisiteEventIds));
    }

    [Fact]
    public void Apply_RejectsAddWithUnknownPrerequisite()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Event", 3, 3, [Guid.NewGuid()], null, []), null)
        };
        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_AllowsReplaceThatEchoesBackAnAlreadyDanglingPrerequisite()
    {
        // danglingPrerequisiteId was never present in `current` - it represents a prerequisite that was
        // resolved (fulfilled or abandoned) in some earlier turn this Apply call has no visibility into.
        // A Replace resends the full entry, so this must be echoable without tripping the unknown-id check.
        var danglingPrerequisiteId = Guid.NewGuid();
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0, [danglingPrerequisiteId]);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, entry.Id, new("Event, reworded", 3, 3, [danglingPrerequisiteId], null, []), null)
        };

        var result = PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits());

        var updated = Assert.Single(result.Events.Entries);
        Assert.Equal("Event, reworded", updated.Description);
        Assert.Equal(danglingPrerequisiteId, Assert.Single(updated.PrerequisiteEventIds));
    }

    [Fact]
    public void Apply_RejectsReplaceThatIntroducesANewUnknownPrerequisite()
    {
        var danglingPrerequisiteId = Guid.NewGuid();
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0, [danglingPrerequisiteId]);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(
                PlannedEventOperation.Replace, entry.Id,
                new("Event, reworded", 3, 3, [danglingPrerequisiteId, Guid.NewGuid()], null, []), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsReplaceThatMakesAnEntryReferenceItselfAsPrerequisite()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "Event", 3, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, entry.Id, new("Event", 3, 3, [entry.Id], null, []), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([entry]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsReplaceThatWouldCreateAPrerequisiteCycle()
    {
        var a = Entry("00000000-0000-0000-0000-000000000001", "A", 3, 3, 0);
        var b = Entry("00000000-0000-0000-0000-000000000002", "B", 3, 3, 0, [a.Id]);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Replace, a.Id, new("A", 3, 3, [b.Id], null, []), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(new([a, b]), [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_ResolvesAddPrerequisiteKeyReferencingALaterSiblingInTheSameBatch()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Dependent", 3, 3, [], null, ["prophecy"]), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Prerequisite", 3, 3, [], "prophecy", []), null)
        };

        var result = PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits());

        var dependent = Assert.Single(result.Events.Entries, x => x.Description == "Dependent");
        var prerequisite = Assert.Single(result.Events.Entries, x => x.Description == "Prerequisite");
        Assert.Equal(prerequisite.Id, Assert.Single(dependent.PrerequisiteEventIds));
    }

    [Fact]
    public void Apply_ResolvesAddPrerequisiteKeyReferencingAnEarlierSiblingInTheSameBatch()
    {
        // Same as the "later sibling" case above but with the Add order reversed, confirming resolution
        // doesn't depend on which order the batch lists Add operations in.
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Prerequisite", 3, 3, [], "prophecy", []), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Dependent", 3, 3, [], null, ["prophecy"]), null)
        };

        var result = PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits());

        var dependent = Assert.Single(result.Events.Entries, x => x.Description == "Dependent");
        var prerequisite = Assert.Single(result.Events.Entries, x => x.Description == "Prerequisite");
        Assert.Equal(prerequisite.Id, Assert.Single(dependent.PrerequisiteEventIds));
    }

    [Fact]
    public void Apply_RejectsDuplicateKeyAmongAddsInTheSameBatch()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("First", 3, 3, [], "shared", []), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Second", 3, 3, [], "shared", []), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsPrerequisiteKeyThatDoesNotMatchAnyBatchKey()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Dependent", 3, 3, [], null, ["does-not-exist"]), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsMutualPrerequisiteKeysBetweenTwoAddsAsACycle()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("A", 3, 3, [], "a", ["b"]), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("B", 3, 3, [], "b", ["a"]), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_RejectsAnAddWhosePrerequisiteKeysReferencesItsOwnKey()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Self", 3, 3, [], "self", ["self"]), null)
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits()));
    }

    [Fact]
    public void Apply_MergesPrerequisiteEventIdsAndPrerequisiteKeysOnTheSameAdd()
    {
        var existing = Entry("00000000-0000-0000-0000-000000000001", "Existing prerequisite", 3, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Sibling", 3, 3, [], "sibling", []), null),
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("Dependent", 3, 3, [existing.Id], null, ["sibling"]), null)
        };

        var result = PlannedEventProcessor.Apply(new([existing]), [], updates, 1, Limits());

        var sibling = Assert.Single(result.Events.Entries, x => x.Description == "Sibling");
        var dependent = Assert.Single(result.Events.Entries, x => x.Description == "Dependent");
        Assert.Equal(2, dependent.PrerequisiteEventIds.Count);
        Assert.Contains(existing.Id, dependent.PrerequisiteEventIds);
        Assert.Contains(sibling.Id, dependent.PrerequisiteEventIds);
    }

    [Fact]
    public void Apply_AllowsFulfillingAMandatoryEntryEvenWhileItsPrerequisiteIsStillLive()
    {
        // Apply does not mechanically gate removal on outstanding prerequisites - "don't pursue an event
        // until its prerequisites happen" is a prompt-level instruction to the model, not something this
        // method enforces.
        var prerequisite = Entry("00000000-0000-0000-0000-000000000001", "Prerequisite", 3, 3, 0);
        var mandatory = Entry(
            "00000000-0000-0000-0000-000000000002", "Must happen", PlannedEventProcessor.MandatoryImportance, 3, 0, [prerequisite.Id]);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Remove, mandatory.Id, null, PlannedEventOutcome.Fulfilled)
        };

        var result = PlannedEventProcessor.Apply(new([prerequisite, mandatory]), [], updates, 1, Limits());

        Assert.DoesNotContain(result.Events.Entries, x => x.Id == mandatory.Id);
        Assert.Contains(result.Events.Entries, x => x.Id == prerequisite.Id);
    }

    [Fact]
    public void ValidateRelationships_RejectsSelfReference()
    {
        var id = Guid.NewGuid();
        var entry = new PlannedEvent(id, "Event", 3, 3, [id], 0);
        Assert.NotNull(PlannedEventProcessor.ValidateRelationships(new([entry])));
    }

    [Fact]
    public void ValidateRelationships_RejectsACycleAmongLiveEntries()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = new PlannedEvent(aId, "A", 3, 3, [bId], 0);
        var b = new PlannedEvent(bId, "B", 3, 3, [aId], 0);
        Assert.NotNull(PlannedEventProcessor.ValidateRelationships(new([a, b])));
    }

    [Fact]
    public void ValidateRelationships_AcceptsAChainWithoutACycle()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var a = new PlannedEvent(aId, "A", 3, 3, [bId], 0);
        var b = new PlannedEvent(bId, "B", 3, 3, [cId], 0);
        var c = new PlannedEvent(cId, "C", 3, 3, [], 0);
        Assert.Null(PlannedEventProcessor.ValidateRelationships(new([a, b, c])));
    }

    [Fact]
    public void ValidateRelationships_TreatsADanglingPrerequisiteAsValidNotACycle()
    {
        var aId = Guid.NewGuid();
        var danglingId = Guid.NewGuid(); // no entry with this id exists in the collection
        var a = new PlannedEvent(aId, "A", 3, 3, [danglingId], 0);
        Assert.Null(PlannedEventProcessor.ValidateRelationships(new([a])));
    }

    [Fact]
    public void Apply_ThrowsWhenMandatoryEntriesAloneExceedTheCountLimit()
    {
        var first = Entry("00000000-0000-0000-0000-000000000001", "Must happen 1", PlannedEventProcessor.MandatoryImportance, 3, 0);
        var second = Entry("00000000-0000-0000-0000-000000000002", "Must happen 2", PlannedEventProcessor.MandatoryImportance, 3, 0);

        Assert.Throws<NarratorException>(() =>
            PlannedEventProcessor.Apply(new([first, second]), [], [], 1, Limits(maxPlannedEvents: 1)));
    }

    [Fact]
    public void Apply_AutomaticallyCullsAnEntryThatExceedsTheCharacterLimitInsteadOfThrowing()
    {
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new(new string('x', 500), 3, 3, [], null, []), null)
        };
        var result = PlannedEventProcessor.Apply(PlannedEvents.Empty, [], updates, 1, Limits(maxPlannedEventCharacters: 200));
        Assert.Empty(result.Events.Entries);
        Assert.Equal(PlannedEventChangeSource.AutomaticCull, Assert.Single(result.Changes, x => x.Operation == PlannedEventOperation.Remove).Source);
    }

    [Fact]
    public void Apply_AutomaticallyCullsToStayWithinTheTotalCharacterLimitInsteadOfThrowing()
    {
        var existing = Entry("00000000-0000-0000-0000-000000000001", "Old event", 1, 3, 0);
        var updates = new[]
        {
            new ProposedPlannedEventUpdate(PlannedEventOperation.Add, null, new("New event", 5, 3, [], null, []), null)
        };
        var withNew = new PlannedEvent(Guid.NewGuid(), "New event", 5, 3, [], 1);
        var combinedLength = JsonSerializer.Serialize(new PlannedEvents([existing, withNew])).Length;
        var newOnlyLength = JsonSerializer.Serialize(new PlannedEvents([withNew])).Length;
        var limits = Limits(maxPlannedEventsCharacters: (combinedLength + newOnlyLength) / 2);

        var result = PlannedEventProcessor.Apply(new([existing]), [], updates, 1, limits);

        Assert.DoesNotContain(result.Events.Entries, x => x.Id == existing.Id);
        Assert.Contains(result.Events.Entries, x => x.Description == "New event");
    }

    [Fact]
    public void ApproachingLimits_UsesConfiguredPercentage()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(i => new PlannedEvent(Guid.NewGuid(), $"Event {i}", 3, 3, [], 0)).ToArray();
        Assert.True(PlannedEventProcessor.IsApproachingLimits(new(entries), Limits(maxPlannedEvents: 10)));
    }

    [Fact]
    public void ValidateEntry_RejectsEmptyDescription()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("   ", 3, 3, 0, limits));
    }

    [Fact]
    public void ValidateEntry_RejectsDescriptionExceedingConfiguredLimit()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(PlannedEventProcessor.ValidateEntry(new string('x', limits.MaxPlannedEventDescriptionCharacters + 1), 3, 3, 0, limits));
    }

    [Fact]
    public void ValidateEntry_RejectsOutOfRangeImportance()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("Event", 0, 3, 0, limits));
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("Event", 6, 3, 0, limits));
    }

    [Fact]
    public void ValidateEntry_RejectsOutOfRangeUrgency()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("Event", 3, 0, 0, limits));
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("Event", 3, 6, 0, limits));
    }

    [Fact]
    public void ValidateEntry_RejectsNegativeLastRelevantTurnNumber()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(PlannedEventProcessor.ValidateEntry("Event", 3, 3, -1, limits));
    }

    [Fact]
    public void ValidateEntry_AcceptsAValidEntry()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.Null(PlannedEventProcessor.ValidateEntry("Event", 3, 3, 0, limits));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ValidateEntry_AcceptsEveryUrgencyInRange(int urgency)
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.Null(PlannedEventProcessor.ValidateEntry("Event", 3, urgency, 0, limits));
    }

    [Fact]
    public void ResolveInitialPlannedEvents_ResolvesAPrerequisiteKeyAmongProposals()
    {
        var proposals = new[]
        {
            new ProposedPlannedEvent("Dependent", 3, 3, [], null, ["prophecy"]),
            new ProposedPlannedEvent("Prerequisite", 3, 3, [], "prophecy", [])
        };

        var events = PlannedEventProcessor.ResolveInitialPlannedEvents(proposals, Guid.NewGuid);

        var dependent = Assert.Single(events.Entries, x => x.Description == "Dependent");
        var prerequisite = Assert.Single(events.Entries, x => x.Description == "Prerequisite");
        Assert.Equal(prerequisite.Id, Assert.Single(dependent.PrerequisiteEventIds));
    }

    [Fact]
    public void ResolveInitialPlannedEvents_RejectsDuplicateKeys()
    {
        var proposals = new[]
        {
            new ProposedPlannedEvent("First", 3, 3, [], "shared", []),
            new ProposedPlannedEvent("Second", 3, 3, [], "shared", [])
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.ResolveInitialPlannedEvents(proposals, Guid.NewGuid));
    }

    [Fact]
    public void ResolveInitialPlannedEvents_RejectsAnUnresolvableKey()
    {
        var proposals = new[]
        {
            new ProposedPlannedEvent("Dependent", 3, 3, [], null, ["does-not-exist"])
        };

        Assert.Throws<NarratorException>(() => PlannedEventProcessor.ResolveInitialPlannedEvents(proposals, Guid.NewGuid));
    }

    private static StoryGenerationSettings Limits(
        int maxPlannedEvents = 50,
        int maxPlannedEventCharacters = 2000,
        int maxPlannedEventsCharacters = 20000,
        int plannedEventsWarningPercent = 80) =>
        new(8, 200, 4000, 60000, 80, maxPlannedEvents, maxPlannedEventCharacters, maxPlannedEventsCharacters, plannedEventsWarningPercent);

    private static PlannedEvent Entry(
        string id, string description, int importance, int urgency, int relevant, IReadOnlyList<Guid>? prerequisiteEventIds = null) =>
        new(Guid.Parse(id), description, importance, urgency, prerequisiteEventIds ?? [], relevant);
}
