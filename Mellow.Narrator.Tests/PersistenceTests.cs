using Mellow.Narrator.Core;
using Mellow.Narrator.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task Definition_SwapSortOrderExchangesBothValues()
    {
        var repository = (IStoryDefinitionRepository)_store;
        var first = Definition() with { SortOrder = 0 };
        var second = Definition() with { SortOrder = 1 };
        await repository.SaveAsync(first);
        await repository.SaveAsync(second);

        await repository.SwapSortOrderAsync(first.Id, second.Id);

        var loadedFirst = await repository.GetAsync(first.Id);
        var loadedSecond = await repository.GetAsync(second.Id);
        Assert.Equal(1, loadedFirst!.SortOrder);
        Assert.Equal(0, loadedSecond!.SortOrder);
    }

    [Fact]
    public async Task Definition_LoadsPreExistingDocumentMissingPlannedEventFields()
    {
        var repository = (IStoryDefinitionRepository)_store;
        var definition = Definition();
        await repository.SaveAsync(definition);
        var file = Path.Combine(_root, "Mellow.Narrator", "story-definitions", $"{definition.Id:D}.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        var data = document["data"]!.AsObject();
        data.Remove("initialPlannedEvents");
        data.Remove("plannedEventMaintenanceHistory");
        await File.WriteAllTextAsync(file, document.ToJsonString());

        var loaded = await repository.GetAsync(definition.Id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.InitialPlannedEvents.Entries);
        Assert.Empty(loaded.PlannedEventMaintenanceHistory);
        Assert.True(PlannedEventProcessor.IsWithinLimits(loaded.InitialPlannedEvents, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public async Task State_LoadsPreExistingDocumentMissingPlannedEventFields()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var file = Path.Combine(_root, "Mellow.Narrator", "story-states", state.Id.ToString("D"), "state.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        var data = document["data"]!.AsObject();
        data.Remove("currentPlannedEvents");
        data.Remove("plannedEventMaintenanceHistory");
        data["setup"]!.AsObject()["definition"]!.AsObject().Remove("initialPlannedEvents");
        await File.WriteAllTextAsync(file, document.ToJsonString());

        var loaded = await repository.GetAsync(state.Id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.CurrentPlannedEvents.Entries);
        Assert.Empty(loaded.PlannedEventMaintenanceHistory);
        Assert.Empty(loaded.Setup.Definition.InitialPlannedEvents.Entries);
        Assert.True(PlannedEventProcessor.IsWithinLimits(loaded.CurrentPlannedEvents, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public async Task ConnectionCapabilities_RoundTripNegotiatedRequestContract()
    {
        var repository = (IApiConnectionSettingsStore)_store;
        var expected = NarratorDefaults.Create() with
        {
            Capabilities = new(false, StructuredOutputTier.JsonMode, "model", DateTimeOffset.UtcNow)
            {
                OutputTokenParameter = OutputTokenParameter.MaxTokens,
                InstructionMessageRole = InstructionMessageRole.System
            }
        };

        await repository.SaveAsync(expected);
        var actual = await repository.LoadAsync();

        Assert.Equal(OutputTokenParameter.MaxTokens, actual.Capabilities.OutputTokenParameter);
        Assert.Equal(InstructionMessageRole.System, actual.Capabilities.InstructionMessageRole);
    }

    [Fact]
    public async Task ConnectionSettingsWithoutNewOptionalSections_LoadDefaults()
    {
        var repository = (IApiConnectionSettingsStore)_store;
        await repository.SaveAsync(NarratorDefaults.Create());
        var path = Path.Combine(_root, "Mellow.Narrator", "settings", "api-connection.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["data"]!.AsObject().Remove("logging");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var loaded = await repository.LoadAsync();

        Assert.Equal(NarratorLogLevel.Information, loaded.Logging.MinimumLevel);
    }

    [Fact]
    public async Task ConnectionSettingsWithoutPlannedEventFields_LoadDefaultsAndPassValidation()
    {
        var repository = (IApiConnectionSettingsStore)_store;
        await repository.SaveAsync(NarratorDefaults.Create());
        var path = Path.Combine(_root, "Mellow.Narrator", "settings", "api-connection.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var data = document["data"]!.AsObject();
        var storyGeneration = data["storyGeneration"]!.AsObject();
        storyGeneration.Remove("maxPlannedEvents");
        storyGeneration.Remove("maxPlannedEventCharacters");
        storyGeneration.Remove("maxPlannedEventsCharacters");
        storyGeneration.Remove("plannedEventsWarningPercent");
        var contentLimits = data["contentLimits"]!.AsObject();
        contentLimits.Remove("maxPlannedEventDescriptionCharacters");
        contentLimits.Remove("maxPlannedEventUpdatesPerResponse");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var loaded = await repository.LoadAsync();

        var defaults = NarratorDefaults.Create();
        Assert.Equal(defaults.StoryGeneration.MaxPlannedEvents, loaded.StoryGeneration.MaxPlannedEvents);
        Assert.Equal(defaults.StoryGeneration.MaxPlannedEventCharacters, loaded.StoryGeneration.MaxPlannedEventCharacters);
        Assert.Equal(defaults.StoryGeneration.MaxPlannedEventsCharacters, loaded.StoryGeneration.MaxPlannedEventsCharacters);
        Assert.Equal(defaults.StoryGeneration.PlannedEventsWarningPercent, loaded.StoryGeneration.PlannedEventsWarningPercent);
        Assert.Equal(defaults.ContentLimits.MaxPlannedEventDescriptionCharacters, loaded.ContentLimits.MaxPlannedEventDescriptionCharacters);
        Assert.Equal(defaults.ContentLimits.MaxPlannedEventUpdatesPerResponse, loaded.ContentLimits.MaxPlannedEventUpdatesPerResponse);
        // The actual bug: without normalization these fields load as 0 (the int default), which fails
        // every one of their range checks, so re-saving unrelated settings changes was rejected citing
        // Planned Event fields the user never touched.
        Assert.Empty(SettingsValidator.Validate(loaded));
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
    public async Task StoryTurn_AllOrphansPastCommitBoundaryAreRolledBack()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var turns = Path.Combine(_root, "Mellow.Narrator", "story-states", state.Id.ToString("D"), "turns");
        await File.WriteAllTextAsync(Path.Combine(turns, $"00000001-{Guid.NewGuid():D}.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(turns, $"00000002-{Guid.NewGuid():D}.json"), "{}");

        var loaded = await repository.GetAsync(state.Id);

        Assert.Equal(0, loaded!.LastCommittedTurnSequence);
        Assert.Single(await repository.GetTurnsAsync(state.Id));
        Assert.Single(Directory.EnumerateFiles(turns, "*.json"));
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
    public async Task RecentTurns_DoNotReadOlderTurnDocuments()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var first = opening with { Id = Guid.NewGuid(), SequenceNumber = 1, PlayerAction = "first" };
        await repository.CommitTurnAsync(state = state with { LastCommittedTurnSequence = 1 }, first);
        var second = opening with { Id = Guid.NewGuid(), SequenceNumber = 2, PlayerAction = "second" };
        await repository.CommitTurnAsync(state = state with { LastCommittedTurnSequence = 2 }, second);
        var openingPath = Path.Combine(
            _root,
            "Mellow.Narrator",
            "story-states",
            state.Id.ToString("D"),
            "turns",
            $"00000000-{opening.Id:D}.json");
        await File.WriteAllTextAsync(openingPath, "{}");

        Assert.Contains(await repository.ListAsync(), x => x.Id == state.Id);
        var recent = await repository.GetTurnsAsync(state.Id, 1);

        Assert.Equal(2, Assert.Single(recent).SequenceNumber);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetTurnsAsync(state.Id));
    }

    [Fact]
    public async Task StaleWholeStateSaveIsRejectedAndMetadataUpdatePreservesCommittedTurn()
    {
        var repository = (IStoryStateRepository)_store;
        var (state, opening) = State();
        await repository.CreateAsync(state, opening);
        var stale = await repository.GetAsync(state.Id);
        var nextTurn = opening with { Id = Guid.NewGuid(), SequenceNumber = 1, PlayerAction = "advance" };
        await repository.CommitTurnAsync(state with { LastCommittedTurnSequence = 1 }, nextTurn);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(stale! with { Label = "Stale label" }));
        await repository.UpdateLabelAsync(state.Id, "Current label");

        var current = await repository.GetAsync(state.Id);
        Assert.Equal("Current label", current!.Label);
        Assert.Equal(1, current.LastCommittedTurnSequence);
        Assert.Equal(2, (await repository.GetSnapshotAsync(state.Id))!.Turns.Count);
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
    public async Task PermanentTrashDeletion_RejectsPathsOutsideTrash()
    {
        var settings = (IApiConnectionSettingsStore)_store;
        var trash = (ITrashStore)_store;
        var expected = NarratorDefaults.Create() with { ModelId = "preserve-me" };
        await settings.SaveAsync(expected);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            trash.DeletePermanentlyAsync(Path.Combine("..", "..", "settings", "api-connection.json")));

        Assert.Equal("preserve-me", (await settings.LoadAsync()).ModelId);
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
    public async Task NewerFormatPrimary_IsNotReplacedByOlderBackup()
    {
        var repository = (IStoryDefinitionRepository)_store;
        var definition = Definition();
        await repository.SaveAsync(definition);
        await repository.SaveAsync(definition with { Title = "Current" });
        var path = Path.Combine(_root, "Mellow.Narrator", "story-definitions", $"{definition.Id:D}.json");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new { formatVersion = 2, data = definition with { Title = "Newer format" } }, options));

        await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync(definition.Id));

        Assert.Contains("\"formatVersion\":2", (await File.ReadAllTextAsync(path)).Replace(" ", ""));
    }

    [Fact]
    public async Task NegativeFormatVersionPrimary_FallsBackToBackup()
    {
        var repository = (IStoryDefinitionRepository)_store;
        var definition = Definition();
        await repository.SaveAsync(definition);
        await repository.SaveAsync(definition with { Title = "Updated" });
        var path = Path.Combine(_root, "Mellow.Narrator", "story-definitions", $"{definition.Id:D}.json");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { formatVersion = -1, data = definition }, options));

        var loaded = await repository.GetAsync(definition.Id);

        Assert.Equal(definition.Id, loaded!.Id);
        Assert.Equal(definition.Title, loaded.Title);
    }

    [Fact]
    public void PersistenceRoot_MustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => new PersistenceOptions("relative-path").GetValidatedRoot());
    }

    [Fact]
    public void LegacyWindowsIdentityMigration_CopiesMissingStateAndSecureStorageWithoutOverwritingCurrentFiles()
    {
        var legacy = Path.Combine(_root, "legacy-package");
        var current = Path.Combine(_root, "current-package");
        var legacySettings = Path.Combine(legacy, "Data", "Mellow.Narrator", "settings");
        var legacySecureStorage = Path.Combine(legacy, "Settings");
        var legacyWorkspace = Path.Combine(legacy, "Data", "Mellow.Narrator", "workspace");
        var currentWorkspace = Path.Combine(current, "Data", "Mellow.Narrator", "workspace");
        Directory.CreateDirectory(legacySettings);
        Directory.CreateDirectory(legacySecureStorage);
        Directory.CreateDirectory(legacyWorkspace);
        Directory.CreateDirectory(currentWorkspace);
        File.WriteAllText(Path.Combine(legacySettings, "api-connection.json"), "legacy settings");
        File.WriteAllText(Path.Combine(legacySecureStorage, "securestorage.dat"), "legacy credential");
        File.WriteAllText(Path.Combine(currentWorkspace, "workspace.json"), "current workspace");
        File.WriteAllText(
            Path.Combine(legacyWorkspace, "workspace.json"),
            "legacy workspace");

        var migrated = ApplicationDataMigration.CopyMissingLegacyWindowsIdentityData(legacy, current);
        var repeated = ApplicationDataMigration.CopyMissingLegacyWindowsIdentityData(legacy, current);

        Assert.True(migrated);
        Assert.False(repeated);
        Assert.Equal(
            "legacy settings",
            File.ReadAllText(Path.Combine(current, "Data", "Mellow.Narrator", "settings", "api-connection.json")));
        Assert.Equal(
            "legacy credential",
            File.ReadAllText(Path.Combine(current, "Settings", "securestorage.dat")));
        Assert.Equal("current workspace", File.ReadAllText(Path.Combine(currentWorkspace, "workspace.json")));
        Assert.True(File.Exists(Path.Combine(legacySettings, "api-connection.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static StoryDefinition Definition()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "Definition", "Prompt", "", new([]), [], PlannedEvents.Empty, [], 0, now, now);
    }

    private static (StoryState, StoryTurn) State()
    {
        var id = Guid.NewGuid();
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", ["Content"], [], 3, 0);
        var bible = new StoryBible([entry]);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", "", bible, PlannedEvents.Empty);
        var now = DateTimeOffset.UtcNow;
        var state = new StoryState(id, "Story", null, new(definition), bible, [], PlannedEvents.Empty, [], 0, now, null, 0);
        var turn = new StoryTurn(Guid.NewGuid(), id, 0, null, "Opening", ["Continue"], [entry.Id], [], [], [], now, new("model", null, null, null));
        return (state, turn);
    }
}
