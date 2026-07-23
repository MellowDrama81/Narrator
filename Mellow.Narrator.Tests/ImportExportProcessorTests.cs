using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class ImportExportProcessorTests
{
    [Fact]
    public void StateCopy_RemapsQuestionsResponsesEntriesAndTurnsConsistently()
    {
        var (state, turn) = CreateState();

        var copy = ImportExportProcessor.CopyState(
            state,
            [turn],
            7,
            NarratorDefaults.Create().ContentLimits);

        Assert.NotEqual(state.Id, copy.State.Id);
        Assert.Equal(7, copy.State.SortOrder);
        var question = Assert.Single(copy.State.Setup.Definition.PlayerQuestions);
        Assert.NotEqual(state.Setup.Definition.PlayerQuestions[0].Id, question.Id);
        Assert.Equal(question.Id, Assert.Single(copy.State.Setup.PlayerResponses).QuestionId);
        var entry = Assert.Single(copy.State.CurrentStoryBible.Entries);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, entry.Id);
        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(copy.State.Id, copiedTurn.StoryStateId);
        Assert.Equal(entry.Id, Assert.Single(copiedTurn.RelevantStoryBibleEntryIds));
    }

    [Fact]
    public void StateCopy_RejectsResponseForUnknownQuestion()
    {
        var (state, turn) = CreateState();
        state = state with
        {
            Setup = state.Setup with
            {
                PlayerResponses = [state.Setup.PlayerResponses[0] with { QuestionId = Guid.NewGuid() }]
            }
        };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state,
            [turn],
            1,
            NarratorDefaults.Create().ContentLimits));
    }

    [Fact]
    public void StateCopy_RejectsMissingResponse()
    {
        var (state, turn) = CreateState();
        state = state with { Setup = state.Setup with { PlayerResponses = [] } };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state,
            [turn],
            1,
            NarratorDefaults.Create().ContentLimits));
    }

    [Fact]
    public void StateCopy_RejectsResponseTextThatDoesNotMatchQuestionSnapshot()
    {
        var (state, turn) = CreateState();
        state = state with
        {
            Setup = state.Setup with
            {
                PlayerResponses = [state.Setup.PlayerResponses[0] with { Question = "Different question?" }]
            }
        };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state,
            [turn],
            1,
            NarratorDefaults.Create().ContentLimits));
    }

    [Fact]
    public void DefinitionCopy_RejectsNonUtcTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            "A sufficiently long prompt for validation.",
            [],
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
        var question = new PlayerQuestion(Guid.NewGuid(), "Name?", "Required", 0);
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", "The player is Alex.", 4, 0);
        var bible = new StoryBible([entry]);
        var snapshot = new StoryDefinitionSnapshot(
            "Story",
            "A sufficiently long prompt for validation.",
            [question],
            bible);
        var response = new PlayerResponse(question.Id, question.Question, question.ValidationInstruction, "Alex");
        var now = DateTimeOffset.UtcNow;
        var state = new StoryState(stateId, "Story", null, new(snapshot, [response]), bible, [], 0, now, null, 0);
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
