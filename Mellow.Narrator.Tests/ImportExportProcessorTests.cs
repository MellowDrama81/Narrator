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
            NarratorDefaults.Create().ContentLimits);

        Assert.NotEqual(state.Id, copy.State.Id);
        Assert.Equal(7, copy.State.SortOrder);
        var entry = Assert.Single(copy.State.CurrentStoryBible.Entries);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, entry.Id);
        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(copy.State.Id, copiedTurn.StoryStateId);
        Assert.Equal(entry.Id, Assert.Single(copiedTurn.RelevantStoryBibleEntryIds));
    }

    [Fact]
    public void DefinitionCopy_RejectsNonUtcTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            "A sufficiently long prompt for validation.",
            StoryBible.Empty,
            [],
            0,
            now.ToOffset(TimeSpan.FromHours(2)),
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition,
            1,
            NarratorDefaults.Create().ContentLimits));
    }

    private static (StoryState State, StoryTurn Turn) CreateState()
    {
        var stateId = Guid.NewGuid();
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", "The player is Alex.", 4, 0);
        var bible = new StoryBible([entry]);
        var snapshot = new StoryDefinitionSnapshot(
            "Story",
            "A sufficiently long prompt for validation.",
            bible);
        var now = DateTimeOffset.UtcNow;
        var state = new StoryState(stateId, "Story", null, new(snapshot), bible, [], 0, now, null, 0);
        var turn = new StoryTurn(
            Guid.NewGuid(),
            stateId,
            0,
            null,
            "Opening",
            ["Continue"],
            [entry.Id],
            [],
            now,
            new("model", null, null, null));
        return (state, turn);
    }
}
