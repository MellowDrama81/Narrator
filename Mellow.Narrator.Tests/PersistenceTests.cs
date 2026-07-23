using Mellow.Narrator.Core;
using Mellow.Narrator.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mellow.Narrator.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mellow-narrator-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonNarratorStore _store;

    public PersistenceTests() => _store = new(new(_root));

    [Fact]
    public async Task Definition_RoundTripsAndCreatesBackup()
    {
        var repository = (IStoryDefinitionRepository)_store;
        var definition = Definition();
        await repository.SaveAsync(definition);
        await repository.SaveAsync(definition with { Title = "Updated", UpdatedAtUtc = DateTimeOffset.UtcNow });

        var loaded = await repository.GetAsync(definition.Id);
        Assert.Equal("Updated", loaded!.Title);
        var file = Path.Combine(_root, "Mellow.Narrator", "story-definitions", $"{definition.Id:D}.json");
        Assert.True(File.Exists(file + ".bak"));
    }

    [Fact]
    public async Task StoryTurn_RoundTripsAndOrphanIsRolledBack()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var orphan = opening with { Id = Guid.NewGuid(), SequenceNumber = 1, PlayerAction = "orphan" };
        var turns = Path.Combine(_root, "Mellow.Narrator", "story-states", state.Id.ToString("D"), "turns");
        await File.WriteAllTextAsync(Path.Combine(turns, $"00000001-{orphan.Id:D}.json"), "{}");

        var loaded = await repository.GetAsync(state.Id);
        Assert.Equal(0, loaded!.LastCommittedTurnSequence);
        Assert.Single(await repository.GetTurnsAsync(state.Id));
    }

    [Fact]
    public async Task Copy_ProducesIndependentTopLevelIdentity()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var copy = await repository.CopyAsync(state.Id);
        Assert.NotEqual(state.Id, copy.Id);
        Assert.Equal(state.Label, copy.Label);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, copy.CurrentStoryBible.Entries[0].Id);
        Assert.NotEqual(state.Setup.Definition.PlayerQuestions[0].Id, copy.Setup.Definition.PlayerQuestions[0].Id);
        Assert.Equal(copy.Setup.Definition.PlayerQuestions[0].Id, copy.Setup.PlayerResponses[0].QuestionId);
        var copiedTurn = Assert.Single(await repository.GetTurnsAsync(copy.Id));
        Assert.Equal(copy.Id, copiedTurn.StoryStateId);
        Assert.Equal(copy.CurrentStoryBible.Entries[0].Id, Assert.Single(copiedTurn.RelevantStoryBibleEntryIds));
    }

    [Fact]
    public async Task TrashRestore_WithStateIdentityCollision_DeepRemapsAggregate()
    {
        var repository = (IStoryStateRepository)_store;
        var trash = (ITrashStore)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        await repository.MoveToTrashAsync(state.Id);
        await repository.ImportAsync(state, [opening]);
        var item = Assert.Single(await trash.ListAsync());

        await trash.RestoreAsync(item.TrashId);

        var summaries = await repository.ListAsync();
        Assert.Equal(2, summaries.Count);
        var restoredId = Assert.Single(summaries, x => x.Id != state.Id).Id;
        var restored = await repository.GetAsync(restoredId);
        Assert.NotNull(restored);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, restored.CurrentStoryBible.Entries[0].Id);
        Assert.Equal(restored.Setup.Definition.PlayerQuestions[0].Id, restored.Setup.PlayerResponses[0].QuestionId);
        var restoredTurn = Assert.Single(await repository.GetTurnsAsync(restoredId));
        Assert.Equal(restoredId, restoredTurn.StoryStateId);
        Assert.Equal(restored.CurrentStoryBible.Entries[0].Id, Assert.Single(restoredTurn.RelevantStoryBibleEntryIds));
        Assert.Empty(await trash.ListAsync());
    }

    [Fact]
    public async Task MissingCommittedTurn_RestoresLastConsistentStateBackup()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var nextTurn = opening with { Id = Guid.NewGuid(), SequenceNumber = 1, PlayerAction = "advance", CompletedAtUtc = DateTimeOffset.UtcNow };
        var nextState = state with { LastCommittedTurnSequence = 1, LastActionAtUtc = nextTurn.CompletedAtUtc };
        await repository.CommitTurnAsync(nextState, nextTurn);
        var turns = Path.Combine(_root, "Mellow.Narrator", "story-states", state.Id.ToString("D"), "turns");
        File.Delete(Path.Combine(turns, $"00000001-{nextTurn.Id:D}.json"));

        var recovered = await repository.GetAsync(state.Id);

        Assert.Equal(0, recovered!.LastCommittedTurnSequence);
        Assert.Null(recovered.LastActionAtUtc);
    }

    [Fact]
    public async Task Import_PublishesCompleteAggregate()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.ImportAsync(state, [opening]);
        Assert.NotNull(await repository.GetAsync(state.Id));
        Assert.Single(await repository.GetTurnsAsync(state.Id));
    }

    [Fact]
    public async Task InvalidCommittedTurn_RestoresLastConsistentStateBackup()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var nextTurn = opening with { Id = Guid.NewGuid(), SequenceNumber = 1, PlayerAction = "advance" };
        await repository.CommitTurnAsync(state with { LastCommittedTurnSequence = 1 }, nextTurn);
        var turnPath = Path.Combine(
            _root,
            "Mellow.Narrator",
            "story-states",
            state.Id.ToString("D"),
            "turns",
            $"00000001-{nextTurn.Id:D}.json");
        await File.WriteAllTextAsync(turnPath, "{}");

        var recovered = await repository.GetAsync(state.Id);

        Assert.Equal(0, recovered!.LastCommittedTurnSequence);
        Assert.Single(await repository.GetTurnsAsync(state.Id));
        Assert.NotEmpty(await ((IRecoveryNoticeStore)_store).ConsumeAsync());
    }

    [Fact]
    public async Task PermanentTrashDeletion_RemovesDefinitionBackup()
    {
        var definitions = (IStoryDefinitionRepository)_store;
        var trash = (ITrashStore)_store;
        var definition = Definition();
        await definitions.SaveAsync(definition);
        await definitions.SaveAsync(definition with { Title = "Updated" });
        await definitions.MoveToTrashAsync(definition.Id);
        var item = Assert.Single(await trash.ListAsync());

        await trash.DeletePermanentlyAsync(item.TrashId);

        var trashFolder = Path.Combine(_root, "Mellow.Narrator", "trash", "story-definitions");
        Assert.Empty(Directory.EnumerateFiles(trashFolder));
    }

    [Fact]
    public async Task TrashRestore_AppendsAfterHighestSortOrder()
    {
        var definitions = (IStoryDefinitionRepository)_store;
        var trash = (ITrashStore)_store;
        var first = Definition() with { SortOrder = 3, Title = "First" };
        var second = Definition() with { SortOrder = 8, Title = "Second" };
        await definitions.SaveAsync(first);
        await definitions.SaveAsync(second);
        await definitions.MoveToTrashAsync(first.Id);
        var item = Assert.Single(await trash.ListAsync());

        await trash.RestoreAsync(item.TrashId);

        Assert.Equal(9, (await definitions.GetAsync(first.Id))!.SortOrder);
    }

    [Fact]
    public async Task TrashRetention_KeepsNewestTenItems()
    {
        var definitions = (IStoryDefinitionRepository)_store;
        var trash = (ITrashStore)_store;
        for (var index = 0; index < 11; index++)
        {
            var definition = Definition() with { Title = $"Definition {index}" };
            await definitions.SaveAsync(definition);
            await definitions.MoveToTrashAsync(definition.Id);
        }

        Assert.Equal(10, (await trash.ListAsync()).Count);
    }

    [Fact]
    public async Task VersionZeroDocument_IsMigratedAndRetainedAsBackup()
    {
        var definition = Definition();
        var path = Path.Combine(_root, "Mellow.Narrator", "story-definitions", $"{definition.Id:D}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { formatVersion = 0, data = definition }, options));

        var loaded = await ((IStoryDefinitionRepository)_store).GetAsync(definition.Id);

        Assert.Equal(definition.Id, loaded!.Id);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("\"formatVersion\": 1", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void PersistenceRoot_MustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => new PersistenceOptions("relative-path").GetValidatedRoot());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static StoryDefinition Definition()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "Definition", "Prompt", [], new([]), [], 0, now, now);
    }

    private static (StoryState, StoryTurn) State()
    {
        var id = Guid.NewGuid();
        var question = new PlayerQuestion(Guid.NewGuid(), "Name?", "Required", 0);
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", "Content", 3, 0);
        var bible = new StoryBible([entry]);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", [question], bible);
        var now = DateTimeOffset.UtcNow;
        var response = new PlayerResponse(question.Id, question.Question, question.ValidationInstruction, "Alex");
        var state = new StoryState(id, "Story", null, new(definition, [response]), bible, [], 0, now, null, 0);
        var turn = new StoryTurn(Guid.NewGuid(), id, 0, null, "Opening", ["Continue"], [entry.Id], [], now, new("model", null, null, null));
        return (state, turn);
    }
}
