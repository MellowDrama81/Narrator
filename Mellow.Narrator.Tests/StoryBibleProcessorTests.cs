using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class StoryBibleProcessorTests
{
    [Fact]
    public void Apply_UpdatesRelevanceAndCullsDeterministically()
    {
        var first = Entry("00000000-0000-0000-0000-000000000001", "old", 1, 1);
        var second = Entry("00000000-0000-0000-0000-000000000002", "keep", 5, 2);
        var result = StoryBibleProcessor.Apply(
            new([first, second]),
            [second.Id],
            [new(StoryBibleOperation.Add, null, new("fact", "new", ["new fact"], [], 4))],
            3,
            new(8, 3, 4000, 60000, 80, 50, 2000, 20000, 80));

        Assert.Equal(3, result.Bible.Entries.Count);
        Assert.Equal(3, result.Bible.Entries.Single(x => x.Id == second.Id).LastRelevantTurnNumber);
        Assert.Contains(result.Changes, x => x.Operation == StoryBibleOperation.Add);
    }

    [Fact]
    public void CullToLimits_RemovesLowestImportanceThenOldestRelevant()
    {
        var oldest = Entry("00000000-0000-0000-0000-000000000001", "oldest", 1, 1);
        var newer = Entry("00000000-0000-0000-0000-000000000002", "newer", 1, 4);
        var important = Entry("00000000-0000-0000-0000-000000000003", "important", 5, 0);
        var result = StoryBibleProcessor.CullToLimits(new([oldest, newer, important]), new(8, 2, 4000, 60000, 80, 50, 2000, 20000, 80));
        Assert.DoesNotContain(result.Bible.Entries, x => x.Id == oldest.Id);
        Assert.Contains(result.Bible.Entries, x => x.Id == important.Id);
        Assert.Equal(StoryBibleChangeSource.AutomaticCull, Assert.Single(result.Changes).Source);
    }

    [Fact]
    public void Apply_RejectsDuplicateExistingUpdates()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "fact", 3, 0);
        var updates = new[]
        {
            new ProposedStoryBibleUpdate(StoryBibleOperation.Replace, entry.Id, new("fact", "x", ["x"], [], 3)),
            new ProposedStoryBibleUpdate(StoryBibleOperation.Remove, entry.Id, null)
        };
        Assert.Throws<NarratorException>(() => StoryBibleProcessor.Apply(new([entry]), [], updates, 1, new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_RejectsUnknownRelevantEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "fact", 3, 0);
        Assert.Throws<NarratorException>(() =>
            StoryBibleProcessor.Apply(new([entry]), [Guid.NewGuid()], [], 1, new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_RejectsRemovalOfRelevantEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "fact", 3, 0);
        Assert.Throws<NarratorException>(() => StoryBibleProcessor.Apply(
            new([entry]),
            [entry.Id],
            [new(StoryBibleOperation.Remove, entry.Id, null)],
            1,
            new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_RejectsUpdateReferencingUnknownEntry()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "fact", 3, 0);
        var updates = new[] { new ProposedStoryBibleUpdate(StoryBibleOperation.Replace, Guid.NewGuid(), new("fact", "x", ["x"], [], 3)) };
        Assert.Throws<NarratorException>(() => StoryBibleProcessor.Apply(new([entry]), [], updates, 1, new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_RejectsRemovalThatAlsoContainsAReplacement()
    {
        var entry = Entry("00000000-0000-0000-0000-000000000001", "fact", 3, 0);
        var updates = new[] { new ProposedStoryBibleUpdate(StoryBibleOperation.Remove, entry.Id, new("fact", "x", ["x"], [], 3)) };
        Assert.Throws<NarratorException>(() => StoryBibleProcessor.Apply(new([entry]), [], updates, 1, new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_RejectsIncompleteProposedEntry()
    {
        var updates = new[] { new ProposedStoryBibleUpdate(StoryBibleOperation.Add, null, new("fact", "", ["x"], [], 3)) };
        Assert.Throws<NarratorException>(() => StoryBibleProcessor.Apply(StoryBible.Empty, [], updates, 1, new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void Apply_AutomaticallyCullsAnEntryThatExceedsTheCharacterLimitInsteadOfThrowing()
    {
        var updates = new[]
        {
            new ProposedStoryBibleUpdate(StoryBibleOperation.Add, null, new("fact", "Large", [new string('x', 500)], [], 3))
        };
        var result = StoryBibleProcessor.Apply(StoryBible.Empty, [], updates, 1, new(8, 10, 200, 60000, 80, 50, 2000, 20000, 80));
        Assert.Empty(result.Bible.Entries);
        Assert.Equal(StoryBibleChangeSource.AutomaticCull, Assert.Single(result.Changes, x => x.Operation == StoryBibleOperation.Remove).Source);
    }

    [Fact]
    public void Apply_AutomaticallyCullsToStayWithinTheTotalCharacterLimitInsteadOfThrowing()
    {
        var existing = Entry("00000000-0000-0000-0000-000000000001", "old", 1, 0);
        var updates = new[]
        {
            new ProposedStoryBibleUpdate(StoryBibleOperation.Add, null, new("fact", "new", ["new fact"], [], 5))
        };
        var withNew = new StoryBibleEntry(Guid.NewGuid(), "fact", "new", ["new fact"], [], 5, 1);
        var combinedLength = JsonSerializer.Serialize(new StoryBible([existing, withNew])).Length;
        var newOnlyLength = JsonSerializer.Serialize(new StoryBible([withNew])).Length;
        var limits = new StoryGenerationSettings(8, 10, 4000, (combinedLength + newOnlyLength) / 2, 80, 50, 2000, 20000, 80);

        var result = StoryBibleProcessor.Apply(new([existing]), [], updates, 1, limits);

        Assert.DoesNotContain(result.Bible.Entries, x => x.Id == existing.Id);
        Assert.Contains(result.Bible.Entries, x => x.Name == "new");
    }

    [Fact]
    public void Apply_UsesInjectedIdAndMarksAdditionRelevant()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var result = StoryBibleProcessor.Apply(
            StoryBible.Empty,
            [],
            [new(StoryBibleOperation.Add, null, new("fact", "Fact", ["Content"], [], 3))],
            4,
            new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80),
            () => id);
        Assert.Equal(id, Assert.Single(result.Bible.Entries).Id);
        Assert.Contains(id, result.RelevantEntryIds);
        Assert.Equal(4, result.Bible.Entries[0].LastRelevantTurnNumber);
    }

    [Fact]
    public void CullToLimits_RemovesIndividuallyOversizedEntryFirst()
    {
        var oversized = new StoryBibleEntry(Guid.NewGuid(), "fact", "Large", [new string('x', 500)], [], 5, 10);
        var small = Entry("00000000-0000-0000-0000-000000000002", "small", 1, 0);
        var result = StoryBibleProcessor.CullToLimits(new([oversized, small]), new(8, 10, 200, 60000, 80, 50, 2000, 20000, 80));
        Assert.DoesNotContain(result.Bible.Entries, x => x.Id == oversized.Id);
        Assert.Contains(result.Bible.Entries, x => x.Id == small.Id);
    }

    [Fact]
    public void ApproachingLimits_UsesConfiguredPercentage()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(i => new StoryBibleEntry(Guid.NewGuid(), "fact", $"F{i}", ["x"], [], 3, 0)).ToArray();
        Assert.True(StoryBibleProcessor.IsApproachingLimits(new(entries), new(8, 10, 4000, 60000, 80, 50, 2000, 20000, 80)));
    }

    [Fact]
    public void StoryBibleEntry_TreatsContentIdenticalFactListsAsEqual()
    {
        var id = Guid.NewGuid();
        var first = new StoryBibleEntry(id, "fact", "Name", ["Known"], ["Secret"], 3, 1);
        // Deliberately fresh, non-reference-identical arrays with the same content - the default record
        // equality would compare these by reference and treat the entries as unequal.
        var second = new StoryBibleEntry(id, "fact", "Name", new List<string> { "Known" }.ToArray(), new List<string> { "Secret" }.ToArray(), 3, 1);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void StoryBibleEntry_TreatsDifferentFactsAsUnequal()
    {
        var id = Guid.NewGuid();
        var first = new StoryBibleEntry(id, "fact", "Name", ["Known"], [], 3, 1);
        var second = new StoryBibleEntry(id, "fact", "Name", ["Different"], [], 3, 1);

        Assert.NotEqual(first, second);
    }

    private static StoryBibleEntry Entry(string id, string name, int importance, int relevant) =>
        new(Guid.Parse(id), "fact", name, [$"{name} content"], [], importance, relevant);
}
