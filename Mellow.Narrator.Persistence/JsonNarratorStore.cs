using System.Collections.Concurrent;
using Mellow.Narrator.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mellow.Narrator.Persistence;

public sealed class JsonNarratorStore :
    IStoryDefinitionRepository,
    IStoryStateRepository,
    IWorkspaceStateStore,
    IApiConnectionSettingsStore,
    ITrashStore,
    IRecoveryNoticeStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RecoveryNotice> _recoveryNotices = new();
    private readonly INarratorLogLevelSwitch? _logLevelSwitch;
    private readonly ILogger<JsonNarratorStore> _logger;

    public JsonNarratorStore(
        PersistenceOptions options,
        INarratorLogLevelSwitch? logLevelSwitch = null,
        ILogger<JsonNarratorStore>? logger = null)
    {
        _root = Path.Combine(options.GetValidatedRoot(), "Mellow.Narrator");
        _logLevelSwitch = logLevelSwitch;
        _logger = logger ?? NullLogger<JsonNarratorStore>.Instance;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(DefinitionsPath);
        Directory.CreateDirectory(StatesPath);
        Directory.CreateDirectory(TrashDefinitionsPath);
        Directory.CreateDirectory(TrashStatesPath);
        Directory.CreateDirectory(StagingPath);
        CleanupStaging();
    }

    private string DefinitionsPath => Path.Combine(_root, "story-definitions");
    private string StatesPath => Path.Combine(_root, "story-states");
    private string TrashPath => Path.Combine(_root, "trash");
    private string TrashDefinitionsPath => Path.Combine(TrashPath, "story-definitions");
    private string TrashStatesPath => Path.Combine(TrashPath, "story-states");
    private string StagingPath => Path.Combine(_root, "staging");
    private string SettingsPath => Path.Combine(_root, "settings", "api-connection.json");
    private string WorkspacePath => Path.Combine(_root, "workspace", "workspace.json");
    private static string DefinitionFile(string root, Guid id) => Path.Combine(root, $"{id:D}.json");
    private static string StateFolder(string root, Guid id) => Path.Combine(root, id.ToString("D"));
    private static string StateFile(string folder) => Path.Combine(folder, "state.json");
    private static string TurnsFolder(string folder) => Path.Combine(folder, "turns");
    private static string TurnFile(string folder, StoryTurn turn) => Path.Combine(TurnsFolder(folder), $"{turn.SequenceNumber:D8}-{turn.Id:D}.json");

    async Task<IReadOnlyList<StoryDefinitionSummary>> IStoryDefinitionRepository.ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<StoryDefinitionSummary>();
        foreach (var file in Directory.EnumerateFiles(DefinitionsPath, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var item = await LockedAsync($"definition:{id}",
                async () => Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(file, cancellationToken, ReportRecovery)), cancellationToken);
            if (item is not null) result.Add(new(item.Id, item.Title, item.SortOrder, item.UpdatedAtUtc, item.Description));
        }
        return result.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToArray();
    }

    Task<StoryDefinition?> IStoryDefinitionRepository.GetAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync($"definition:{id}",
            async () => Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(DefinitionFile(DefinitionsPath, id), cancellationToken, ReportRecovery)), cancellationToken);

    async Task IStoryDefinitionRepository.SaveAsync(StoryDefinition definition, CancellationToken cancellationToken)
    {
        ValidateId(definition.Id);
        await LockedAsync($"definition:{definition.Id}", () =>
            JsonFileStore.WriteAsync(DefinitionFile(DefinitionsPath, definition.Id), definition, cancellationToken), cancellationToken);
    }

    async Task IStoryDefinitionRepository.SwapSortOrderAsync(
        Guid firstId,
        Guid secondId,
        CancellationToken cancellationToken)
    {
        ValidateId(firstId);
        ValidateId(secondId);
        if (firstId == secondId) return;
        await LockedManyAsync([$"definition:{firstId}", $"definition:{secondId}"], async () =>
        {
            var firstFile = DefinitionFile(DefinitionsPath, firstId);
            var secondFile = DefinitionFile(DefinitionsPath, secondId);
            var first = Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(firstFile, cancellationToken, ReportRecovery))
                ?? throw new FileNotFoundException("First Story Definition not found.");
            var second = Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(secondFile, cancellationToken, ReportRecovery))
                ?? throw new FileNotFoundException("Second Story Definition not found.");
            await JsonFileStore.WriteAsync(firstFile, first with { SortOrder = second.SortOrder }, cancellationToken);
            await JsonFileStore.WriteAsync(secondFile, second with { SortOrder = first.SortOrder }, cancellationToken);
        }, cancellationToken);
    }

    async Task IStoryDefinitionRepository.MoveToTrashAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        await LockedAsync($"definition:{id}", async () =>
        {
            var source = DefinitionFile(DefinitionsPath, id);
            if (!File.Exists(source)) throw new FileNotFoundException("Story Definition not found.", source);
            var target = Path.Combine(TrashDefinitionsPath, $"{Stamp()}-{id:D}.json");
            File.Move(source, target);
            if (File.Exists(source + ".bak")) File.Move(source + ".bak", target + ".bak");
            await PurgeTrashAsync(cancellationToken);
        }, cancellationToken);
    }

    async Task<IReadOnlyList<StoryStateSummary>> IStoryStateRepository.ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<StoryStateSummary>();
        foreach (var folder in Directory.EnumerateDirectories(StatesPath))
        {
            var id = Path.GetFileName(folder);
            var item = await LockedAsync($"state:{id}", () => LoadStateAndRecoverAsync(folder, cancellationToken), cancellationToken);
            if (item is not null) result.Add(new(item.Id, item.Label, item.SortOrder, item.StartedAtUtc, item.LastActionAtUtc));
        }
        return result.OrderBy(x => x.SortOrder).ThenBy(x => x.Label).ToArray();
    }

    async Task<StoryState?> IStoryStateRepository.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        return await LockedAsync($"state:{id}", () => LoadStateAndRecoverAsync(StateFolder(StatesPath, id), cancellationToken), cancellationToken);
    }

    async Task<IReadOnlyList<StoryTurn>> IStoryStateRepository.GetTurnsAsync(Guid id, int? takeLast, CancellationToken cancellationToken)
    {
        ValidateId(id);
        return await LockedAsync($"state:{id}", async () =>
        {
            var folder = StateFolder(StatesPath, id);
            var state = await LoadStateAndRecoverAsync(folder, cancellationToken);
            if (state is null) return [];
            return await ReadTurnsAsync(folder, state, takeLast, cancellationToken);
        }, cancellationToken);
    }

    async Task<StoryStateAggregateSnapshot?> IStoryStateRepository.GetSnapshotAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        return await LockedAsync($"state:{id}", async () =>
        {
            var folder = StateFolder(StatesPath, id);
            var state = await LoadStateAndRecoverAsync(folder, cancellationToken);
            if (state is null) return null;
            return new StoryStateAggregateSnapshot(
                state,
                await ReadTurnsAsync(folder, state, null, cancellationToken));
        }, cancellationToken);
    }

    async Task IStoryStateRepository.CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken)
    {
        ValidateId(state.Id);
        await LockedAsync($"state:{state.Id}", async () =>
        {
            var destination = StateFolder(StatesPath, state.Id);
            if (Directory.Exists(destination)) throw new IOException("The Story State already exists.");
            var staging = StateFolder(StagingPath, state.Id);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(TurnsFolder(staging));
            try
            {
                await JsonFileStore.WriteImmutableAsync(TurnFile(staging, openingTurn), openingTurn, cancellationToken);
                await JsonFileStore.WriteAsync(StateFile(staging), state, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(staging, destination);
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                throw;
            }
        }, cancellationToken);
    }

    async Task IStoryStateRepository.CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken)
    {
        ValidateId(state.Id);
        await LockedAsync($"state:{state.Id}", async () =>
        {
            var folder = StateFolder(StatesPath, state.Id);
            var current = await LoadStateAndRecoverAsync(folder, cancellationToken) ?? throw new FileNotFoundException("Story State not found.");
            if (turn.SequenceNumber != current.LastCommittedTurnSequence + 1 || state.LastCommittedTurnSequence != turn.SequenceNumber)
                throw new InvalidOperationException("Story turn sequence is not contiguous.");
            var turnPath = TurnFile(folder, turn);
            await JsonFileStore.WriteImmutableAsync(turnPath, turn, cancellationToken);
            try { await JsonFileStore.WriteAsync(StateFile(folder), state, cancellationToken); }
            catch
            {
                if (File.Exists(turnPath)) File.Delete(turnPath);
                throw;
            }
        }, cancellationToken);
    }

    async Task IStoryStateRepository.ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken)
    {
        ValidateId(state.Id);
        if (turns.Count == 0 || turns[0].SequenceNumber != 0 ||
            turns.Select(x => x.SequenceNumber).Where((x, i) => x != i).Any() ||
            state.LastCommittedTurnSequence != turns[^1].SequenceNumber)
            throw new InvalidDataException("Imported Story Turns must be contiguous from zero and match the Story State.");
        if (turns.Any(x => x.StoryStateId != state.Id))
            throw new InvalidDataException("An imported Story Turn references the wrong Story State.");
        await LockedAsync($"state:{state.Id}", () => CreateAggregateAsync(state, turns, cancellationToken), cancellationToken);
    }

    async Task IStoryStateRepository.SaveAsync(StoryState state, CancellationToken cancellationToken)
    {
        ValidateId(state.Id);
        await LockedAsync($"state:{state.Id}", async () =>
        {
            var folder = StateFolder(StatesPath, state.Id);
            var current = await LoadStateAndRecoverAsync(folder, cancellationToken)
                ?? throw new FileNotFoundException("Story State not found.");
            if (current.LastCommittedTurnSequence != state.LastCommittedTurnSequence)
                throw new InvalidOperationException("The Story State changed after it was loaded. Reload it and try again.");
            await JsonFileStore.WriteAsync(StateFile(folder), state, cancellationToken);
        }, cancellationToken);
    }

    async Task IStoryStateRepository.UpdateLabelAsync(Guid id, string label, CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A Story State label is required.", nameof(label));
        await LockedAsync($"state:{id}", async () =>
        {
            var folder = StateFolder(StatesPath, id);
            var current = await LoadStateAndRecoverAsync(folder, cancellationToken)
                ?? throw new FileNotFoundException("Story State not found.");
            await JsonFileStore.WriteAsync(StateFile(folder), current with { Label = label }, cancellationToken);
        }, cancellationToken);
    }

    async Task IStoryStateRepository.SwapSortOrderAsync(
        Guid firstId,
        Guid secondId,
        CancellationToken cancellationToken)
    {
        ValidateId(firstId);
        ValidateId(secondId);
        if (firstId == secondId) return;
        await LockedManyAsync([$"state:{firstId}", $"state:{secondId}"], async () =>
        {
            var firstFolder = StateFolder(StatesPath, firstId);
            var secondFolder = StateFolder(StatesPath, secondId);
            var first = await LoadStateAndRecoverAsync(firstFolder, cancellationToken)
                ?? throw new FileNotFoundException("First Story State not found.");
            var second = await LoadStateAndRecoverAsync(secondFolder, cancellationToken)
                ?? throw new FileNotFoundException("Second Story State not found.");
            await JsonFileStore.WriteAsync(
                StateFile(firstFolder),
                first with { SortOrder = second.SortOrder },
                cancellationToken);
            await JsonFileStore.WriteAsync(
                StateFile(secondFolder),
                second with { SortOrder = first.SortOrder },
                cancellationToken);
        }, cancellationToken);
    }

    async Task<StoryState> IStoryStateRepository.CopyAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var (source, turns) = await LockedAsync($"state:{id}", async () =>
        {
            var folder = StateFolder(StatesPath, id);
            var state = await LoadStateAndRecoverAsync(folder, cancellationToken)
                ?? throw new FileNotFoundException("Story State not found.");
            var snapshotTurns = await ReadTurnsFromFolderAsync(folder, state.LastCommittedTurnSequence, cancellationToken);
            return (state, snapshotTurns);
        }, cancellationToken);
        var summaries = await ((IStoryStateRepository)this).ListAsync(cancellationToken);
        var (copy, mappedTurns) = RemapStateAggregate(
            source,
            turns,
            summaries.Count == 0 ? 0 : summaries.Max(x => x.SortOrder) + 1);
        await CreateAggregateAsync(copy, mappedTurns, cancellationToken);
        return copy;
    }

    async Task IStoryStateRepository.MoveToTrashAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        await LockedAsync($"state:{id}", async () =>
        {
            var source = StateFolder(StatesPath, id);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Story State not found.");
            Directory.Move(source, Path.Combine(TrashStatesPath, $"{Stamp()}-{id:D}"));
            await PurgeTrashAsync(cancellationToken);
        }, cancellationToken);
    }

    async Task<WorkspaceState> IWorkspaceStateStore.LoadAsync(CancellationToken cancellationToken) =>
        await LockedAsync("workspace",
            () => JsonFileStore.ReadAsync<WorkspaceState>(WorkspacePath, cancellationToken, ReportRecovery), cancellationToken)
        ?? WorkspaceState.Empty;

    Task IWorkspaceStateStore.SaveAsync(WorkspaceState state, CancellationToken cancellationToken) =>
        LockedAsync("workspace", () => JsonFileStore.WriteAsync(WorkspacePath, state, cancellationToken), cancellationToken);

    async Task<ApiConnectionSettings> IApiConnectionSettingsStore.LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await LockedAsync("settings",
            () => JsonFileStore.ReadAsync<ApiConnectionSettings>(SettingsPath, cancellationToken, ReportRecovery), cancellationToken);
        var normalized = loaded ?? NarratorDefaults.Create();
        if (normalized.Logging is null)
            normalized = normalized with { Logging = LoggingDefaults.Create() };
        normalized = NormalizePlannedEventSettings(normalized);
        if (_logLevelSwitch is not null) _logLevelSwitch.MinimumLevel = normalized.Logging.MinimumLevel;
        return normalized;
    }

    // The Planned Event limit fields on StoryGenerationSettings/ContentLimitSettings were added after
    // ApiConnectionSettings first shipped, as required constructor parameters with no C# default -
    // unlike Logging above, these are value types, so a settings document saved before they existed
    // deserializes them as 0 rather than null. 0 is outside every one of their valid ranges (each has a
    // minimum of at least 1), so it can never be a legitimately saved value; SettingsValidator would
    // reject it on the very next save, citing fields the user never touched. Treat a 0 as "missing from
    // an older document" and backfill it from defaults, the same way Logging is backfilled above.
    private static ApiConnectionSettings NormalizePlannedEventSettings(ApiConnectionSettings settings)
    {
        var defaults = NarratorDefaults.Create();
        var generation = settings.StoryGeneration with
        {
            MaxPlannedEvents = settings.StoryGeneration.MaxPlannedEvents == 0
                ? defaults.StoryGeneration.MaxPlannedEvents : settings.StoryGeneration.MaxPlannedEvents,
            MaxPlannedEventCharacters = settings.StoryGeneration.MaxPlannedEventCharacters == 0
                ? defaults.StoryGeneration.MaxPlannedEventCharacters : settings.StoryGeneration.MaxPlannedEventCharacters,
            MaxPlannedEventsCharacters = settings.StoryGeneration.MaxPlannedEventsCharacters == 0
                ? defaults.StoryGeneration.MaxPlannedEventsCharacters : settings.StoryGeneration.MaxPlannedEventsCharacters,
            PlannedEventsWarningPercent = settings.StoryGeneration.PlannedEventsWarningPercent == 0
                ? defaults.StoryGeneration.PlannedEventsWarningPercent : settings.StoryGeneration.PlannedEventsWarningPercent
        };
        var contentLimits = settings.ContentLimits with
        {
            MaxPlannedEventDescriptionCharacters = settings.ContentLimits.MaxPlannedEventDescriptionCharacters == 0
                ? defaults.ContentLimits.MaxPlannedEventDescriptionCharacters : settings.ContentLimits.MaxPlannedEventDescriptionCharacters,
            MaxPlannedEventUpdatesPerResponse = settings.ContentLimits.MaxPlannedEventUpdatesPerResponse == 0
                ? defaults.ContentLimits.MaxPlannedEventUpdatesPerResponse : settings.ContentLimits.MaxPlannedEventUpdatesPerResponse,
            MaxConditions = settings.ContentLimits.MaxConditions == 0
                ? defaults.ContentLimits.MaxConditions : settings.ContentLimits.MaxConditions,
            MaxConditionDescriptionCharacters = settings.ContentLimits.MaxConditionDescriptionCharacters == 0
                ? defaults.ContentLimits.MaxConditionDescriptionCharacters : settings.ContentLimits.MaxConditionDescriptionCharacters,
            MaxStorySummaryCharacters = settings.ContentLimits.MaxStorySummaryCharacters == 0
                ? defaults.ContentLimits.MaxStorySummaryCharacters : settings.ContentLimits.MaxStorySummaryCharacters
        };
        return settings with { StoryGeneration = generation, ContentLimits = contentLimits };
    }

    async Task IApiConnectionSettingsStore.SaveAsync(ApiConnectionSettings settings, CancellationToken cancellationToken)
    {
        await LockedAsync("settings", () => JsonFileStore.WriteAsync(SettingsPath, settings, cancellationToken), cancellationToken);
        if (_logLevelSwitch is not null) _logLevelSwitch.MinimumLevel = settings.Logging.MinimumLevel;
    }

    Task<IReadOnlyList<RecoveryNotice>> IRecoveryNoticeStore.ConsumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notices = new List<RecoveryNotice>();
        while (_recoveryNotices.TryDequeue(out var notice)) notices.Add(notice);
        return Task.FromResult<IReadOnlyList<RecoveryNotice>>(notices);
    }

    async Task<IReadOnlyList<TrashItem>> ITrashStore.ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<TrashItem>();
        foreach (var file in Directory.EnumerateFiles(TrashDefinitionsPath, "*.json")
                     .Where(x => !x.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
        {
            var definition = Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(file, cancellationToken));
            items.Add(ToTrashItem(file, TrashItemType.StoryDefinition, definition?.Title));
        }
        foreach (var folder in Directory.EnumerateDirectories(TrashStatesPath))
        {
            var state = await LoadStateAndRecoverAsync(folder, cancellationToken);
            items.Add(ToTrashItem(folder, TrashItemType.StoryState, state?.Label));
        }
        return items.OrderByDescending(x => x.DeletedAtUtc).ToArray();
    }

    Task ITrashStore.RestoreAsync(string trashId, CancellationToken cancellationToken) =>
        LockedAsync("trash", () => RestoreCoreAsync(trashId, cancellationToken), cancellationToken);

    private async Task RestoreCoreAsync(string trashId, CancellationToken cancellationToken)
    {
        var item = (await ((ITrashStore)this).ListAsync(cancellationToken)).SingleOrDefault(x => x.TrashId == trashId)
            ?? throw new FileNotFoundException("Trash item not found.");
        var source = Path.Combine(item.Type == TrashItemType.StoryDefinition ? TrashDefinitionsPath : TrashStatesPath, trashId);
        if (item.Type == TrashItemType.StoryDefinition)
        {
            var definition = Normalize(await JsonFileStore.ReadAsync<StoryDefinition>(source, cancellationToken)) ?? throw new InvalidDataException();
            var destination = DefinitionFile(DefinitionsPath, definition.Id);
            if (!File.Exists(destination))
            {
                var summaries = await ((IStoryDefinitionRepository)this).ListAsync(cancellationToken);
                definition = definition with { SortOrder = summaries.Count == 0 ? 0 : summaries.Max(x => x.SortOrder) + 1 };
                await JsonFileStore.WriteAsync(source, definition, cancellationToken);
                File.Move(source, destination);
                if (File.Exists(source + ".bak")) File.Move(source + ".bak", destination + ".bak");
            }
            else
            {
                var restored = RemapDefinition(
                    definition,
                    NextDefinitionSortOrder(await ((IStoryDefinitionRepository)this).ListAsync(cancellationToken)));
                await JsonFileStore.WriteAsync(DefinitionFile(DefinitionsPath, restored.Id), restored, cancellationToken);
                File.Delete(source);
                if (File.Exists(source + ".bak")) File.Delete(source + ".bak");
            }
        }
        else
        {
            var destination = StateFolder(StatesPath, item.OriginalId);
            if (!Directory.Exists(destination))
            {
                var state = await LoadStateAndRecoverAsync(source, cancellationToken) ?? throw new InvalidDataException();
                var summaries = await ((IStoryStateRepository)this).ListAsync(cancellationToken);
                state = state with { SortOrder = summaries.Count == 0 ? 0 : summaries.Max(x => x.SortOrder) + 1 };
                await JsonFileStore.WriteAsync(StateFile(source), state, cancellationToken);
                Directory.Move(source, destination);
            }
            else
            {
                var state = await LoadStateAndRecoverAsync(source, cancellationToken) ?? throw new InvalidDataException();
                var turns = await ReadTurnsFromFolderAsync(source, state.LastCommittedTurnSequence, cancellationToken);
                var (restored, mappedTurns) = RemapStateAggregate(
                    state,
                    turns,
                    NextStateSortOrder(await ((IStoryStateRepository)this).ListAsync(cancellationToken)));
                await CreateAggregateAsync(restored, mappedTurns, cancellationToken);
                Directory.Delete(source, true);
            }
        }
    }

    Task ITrashStore.DeletePermanentlyAsync(string trashId, CancellationToken cancellationToken) =>
        LockedAsync("trash", () => DeletePermanentlyCoreAsync(trashId, cancellationToken), cancellationToken);

    private async Task DeletePermanentlyCoreAsync(string trashId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = (await ((ITrashStore)this).ListAsync(cancellationToken))
            .SingleOrDefault(x => string.Equals(x.TrashId, trashId, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("Trash item not found.");
        DeleteTrashItemFiles(item);
    }

    private void DeleteTrashItemFiles(TrashItem item)
    {
        var path = Path.Combine(
            item.Type == TrashItemType.StoryDefinition ? TrashDefinitionsPath : TrashStatesPath,
            item.TrashId);
        if (item.Type == TrashItemType.StoryDefinition)
        {
            File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
        else
        {
            Directory.Delete(path, true);
        }
    }

    Task ITrashStore.EmptyAsync(CancellationToken cancellationToken) =>
        LockedAsync("trash", () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(TrashDefinitionsPath)) File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(TrashStatesPath)) Directory.Delete(directory, true);
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task<StoryState?> LoadStateAndRecoverAsync(string folder, CancellationToken cancellationToken)
    {
        var path = StateFile(folder);
        var state = Normalize(await JsonFileStore.ReadAsync<StoryState>(path, cancellationToken, ReportRecovery));
        if (state is null) return null;
        if (!await IsCommitBoundaryConsistentAsync(folder, state, cancellationToken))
        {
            var backup = Normalize(await JsonFileStore.ReadExactAsync<StoryState>(path + ".bak", cancellationToken));
            if (backup is null || !await IsCommitBoundaryConsistentAsync(folder, backup, cancellationToken))
            {
                JsonFileStore.Quarantine(path);
                JsonFileStore.Quarantine(path + ".bak");
                throw new InvalidDataException("The Story State is inconsistent and no valid backup exists.");
            }
            File.Delete(path);
            await JsonFileStore.WriteAsync(path, backup, CancellationToken.None);
            ReportRecovery($"Restored Story State '{state.Id}' to its last internally consistent commit.");
            state = backup;
        }
        foreach (var orphan in Directory.EnumerateFiles(TurnsFolder(folder), "*.json"))
        {
            var sequenceText = Path.GetFileName(orphan).Split('-', 2)[0];
            if (int.TryParse(sequenceText, out var sequence) && sequence > state.LastCommittedTurnSequence)
                File.Delete(orphan);
        }
        return state;
    }

    private async Task CreateAggregateAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken)
    {
        var staging = StateFolder(StagingPath, state.Id);
        var destination = StateFolder(StatesPath, state.Id);
        Directory.CreateDirectory(TurnsFolder(staging));
        try
        {
            foreach (var turn in turns) await JsonFileStore.WriteImmutableAsync(TurnFile(staging, turn), turn, cancellationToken);
            await JsonFileStore.WriteAsync(StateFile(staging), state, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    private static StoryDefinition RemapDefinition(StoryDefinition source, int sortOrder)
    {
        var entryIds = new Dictionary<Guid, Guid>();
        Guid MapEntryId(Guid oldId)
        {
            if (!entryIds.TryGetValue(oldId, out var mapped)) entryIds[oldId] = mapped = Guid.NewGuid();
            return mapped;
        }
        StoryBibleEntry MapEntry(StoryBibleEntry entry) => entry with { Id = MapEntryId(entry.Id) };
        AppliedStoryBibleChange MapChange(AppliedStoryBibleChange change) => change with
        {
            EntryId = MapEntryId(change.EntryId),
            Before = change.Before is null ? null : MapEntry(change.Before),
            After = change.After is null ? null : MapEntry(change.After)
        };

        return source with
        {
            Id = Guid.NewGuid(),
            InitialStoryBible = new(source.InitialStoryBible.Entries.Select(MapEntry).ToArray()),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = sortOrder
        };
    }

    private static (StoryState State, StoryTurn[] Turns) RemapStateAggregate(
        StoryState source,
        IReadOnlyList<StoryTurn> turns,
        int sortOrder)
    {
        var newStateId = Guid.NewGuid();
        var entryIds = new Dictionary<Guid, Guid>();
        Guid MapEntryId(Guid oldId)
        {
            if (!entryIds.TryGetValue(oldId, out var mapped)) entryIds[oldId] = mapped = Guid.NewGuid();
            return mapped;
        }
        StoryBibleEntry MapEntry(StoryBibleEntry entry) => entry with { Id = MapEntryId(entry.Id) };
        StoryBible MapBible(StoryBible bible) => new(bible.Entries.Select(MapEntry).ToArray());
        AppliedStoryBibleChange MapChange(AppliedStoryBibleChange change) => change with
        {
            EntryId = MapEntryId(change.EntryId),
            Before = change.Before is null ? null : MapEntry(change.Before),
            After = change.After is null ? null : MapEntry(change.After)
        };

        var copy = source with
        {
            Id = newStateId,
            Setup = source.Setup with
            {
                Definition = source.Setup.Definition with
                {
                    InitialStoryBible = MapBible(source.Setup.Definition.InitialStoryBible)
                }
            },
            CurrentStoryBible = MapBible(source.CurrentStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = sortOrder
        };
        var mappedTurns = turns.Select(x => x with
        {
            Id = Guid.NewGuid(),
            StoryStateId = newStateId,
            RelevantStoryBibleEntryIds = x.RelevantStoryBibleEntryIds.Select(MapEntryId).ToArray(),
            StoryBibleChanges = x.StoryBibleChanges.Select(MapChange).ToArray()
        }).ToArray();
        return (copy, mappedTurns);
    }

    private static async Task<StoryTurn[]> ReadTurnsFromFolderAsync(
        string folder,
        int lastCommittedSequence,
        CancellationToken cancellationToken)
    {
        var state = Normalize(await JsonFileStore.ReadExactAsync<StoryState>(StateFile(folder), cancellationToken))
            ?? throw new InvalidDataException("The Story State document is unavailable.");
        if (state.LastCommittedTurnSequence != lastCommittedSequence)
            throw new InvalidDataException("The Story State changed while its turns were being read.");
        return (await ReadTurnsAsync(folder, state, null, cancellationToken)).ToArray();
    }

    private static async Task<IReadOnlyList<StoryTurn>> ReadTurnsAsync(
        string folder,
        StoryState state,
        int? takeLast,
        CancellationToken cancellationToken)
    {
        if (takeLast is <= 0) return [];
        var firstSequence = takeLast is null
            ? 0
            : Math.Max(0, state.LastCommittedTurnSequence - takeLast.Value + 1);
        var result = new List<StoryTurn>(state.LastCommittedTurnSequence - firstSequence + 1);
        for (var sequence = firstSequence; sequence <= state.LastCommittedTurnSequence; sequence++)
            result.Add(await ReadTurnAsync(folder, state.Id, sequence, cancellationToken));
        return result;
    }

    private static async Task<StoryTurn> ReadTurnAsync(
        string folder,
        Guid stateId,
        int sequence,
        CancellationToken cancellationToken)
    {
        var matches = Directory.EnumerateFiles(TurnsFolder(folder), $"{sequence:D8}-*.json").ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"Story Turn {sequence} is missing or duplicated.");
        var turn = Normalize(await JsonFileStore.ReadExactAsync<StoryTurn>(matches[0], cancellationToken))
            ?? throw new InvalidDataException($"Story Turn {sequence} is invalid.");
        var idText = Path.GetFileNameWithoutExtension(matches[0]).AsSpan(9);
        if (turn.StoryStateId != stateId ||
            turn.SequenceNumber != sequence ||
            !Guid.TryParse(idText, out var fileId) ||
            fileId != turn.Id)
            throw new InvalidDataException($"Story Turn {sequence} has inconsistent identity data.");
        return turn;
    }

    private static async Task<bool> IsCommitBoundaryConsistentAsync(
        string folder,
        StoryState state,
        CancellationToken cancellationToken)
    {
        if (state.Id == Guid.Empty || state.LastCommittedTurnSequence < 0) return false;
        var turnsFolder = TurnsFolder(folder);
        if (!Directory.Exists(turnsFolder)) return false;
        try
        {
            _ = await ReadTurnAsync(folder, state.Id, state.LastCommittedTurnSequence, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private Task PurgeTrashAsync(CancellationToken cancellationToken) =>
        LockedAsync("trash", async () =>
        {
            var items = await ((ITrashStore)this).ListAsync(cancellationToken);
            var total = items.Sum(x => x.SizeBytes);
            foreach (var item in items.OrderBy(x => x.DeletedAtUtc).Take(Math.Max(0, items.Count - 1)))
            {
                if (items.Count <= 10 && total <= 100L * 1024 * 1024) break;
                DeleteTrashItemFiles(item);
                total -= item.SizeBytes;
                items = items.Where(x => x.TrashId != item.TrashId).ToArray();
            }
        }, cancellationToken);

    private static TrashItem ToTrashItem(string path, TrashItemType type, string? displayName)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('-', 2);
        _ = DateTimeOffset.TryParseExact(parts[0], "yyyyMMdd'T'HHmmssfff'Z'", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var deleted);
        var idText = Path.GetFileNameWithoutExtension(parts.ElementAtOrDefault(1) ?? string.Empty);
        _ = Guid.TryParse(idText, out var id);
        var size = File.Exists(path)
            ? new FileInfo(path).Length + (File.Exists(path + ".bak") ? new FileInfo(path + ".bak").Length : 0)
            :
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        return new(name, type, id, string.IsNullOrWhiteSpace(displayName) ? id.ToString("D") : displayName, deleted, size);
    }

    // Fields added to StoryDefinition/StoryState/StoryTurn/PlannedEvent after they first shipped (the
    // Planned Events feature itself, then Urgency, then Victory/Loss Conditions) are required constructor
    // parameters in code, so every in-memory construction path is compiler-checked to supply them. A
    // document written to disk before one of those fields existed has no matching JSON property for it
    // though, and System.Text.Json leaves a missing reference-type constructor parameter as null rather
    // than failing - so loading old data can hand the rest of the app a StoryDefinition/StoryState/
    // StoryTurn whose newer collection-typed properties are null despite the compile-time guarantee
    // everywhere else. Backfilling those nulls here, once, at the single boundary where on-disk data
    // re-enters the app, keeps that guarantee true in practice for callers like
    // PlannedEventProcessor.IsWithinLimits that dereference these collections unconditionally.
    private static StoryDefinition? Normalize(StoryDefinition? value) => value is null ? null : value with
    {
        Description = value.Description ?? "",
        InitialEventsPrompt = value.InitialEventsPrompt ?? "",
        InitialPlannedEvents = NormalizePlannedEvents(value.InitialPlannedEvents),
        PlannedEventMaintenanceHistory = value.PlannedEventMaintenanceHistory ?? [],
        InitialVictoryConditions = NormalizeConditions(value.InitialVictoryConditions),
        InitialLossConditions = NormalizeConditions(value.InitialLossConditions)
    };

    private static StoryState? Normalize(StoryState? value) => value is null ? null : value with
    {
        Setup = value.Setup with { Definition = NormalizeSnapshot(value.Setup.Definition) },
        CurrentPlannedEvents = NormalizePlannedEvents(value.CurrentPlannedEvents),
        PlannedEventMaintenanceHistory = value.PlannedEventMaintenanceHistory ?? [],
        CurrentVictoryConditions = NormalizeConditions(value.CurrentVictoryConditions),
        CurrentLossConditions = NormalizeConditions(value.CurrentLossConditions),
        RevealedVictoryConditionIds = value.RevealedVictoryConditionIds ?? [],
        MetVictoryConditionIds = value.MetVictoryConditionIds ?? [],
        RevealedLossConditionIds = value.RevealedLossConditionIds ?? [],
        MetLossConditionIds = value.MetLossConditionIds ?? [],
        StorySummary = value.StorySummary ?? ""
    };

    private static StoryTurn? Normalize(StoryTurn? value) => value is null ? null : value with
    {
        RelevantPlannedEventIds = value.RelevantPlannedEventIds ?? [],
        PlannedEventChanges = value.PlannedEventChanges ?? [],
        RevealedVictoryConditionIds = value.RevealedVictoryConditionIds ?? [],
        MetVictoryConditionIds = value.MetVictoryConditionIds ?? [],
        RevealedLossConditionIds = value.RevealedLossConditionIds ?? [],
        MetLossConditionIds = value.MetLossConditionIds ?? []
    };

    private static StoryDefinitionSnapshot NormalizeSnapshot(StoryDefinitionSnapshot value) => value with
    {
        InitialEventsPrompt = value.InitialEventsPrompt ?? "",
        InitialPlannedEvents = NormalizePlannedEvents(value.InitialPlannedEvents),
        InitialVictoryConditions = NormalizeConditions(value.InitialVictoryConditions),
        InitialLossConditions = NormalizeConditions(value.InitialLossConditions)
    };

    private static PlannedEvents NormalizePlannedEvents(PlannedEvents? value) => new((value?.Entries ?? []).ToArray());

    private static StoryConditions NormalizeConditions(StoryConditions? value) => new((value?.Entries ?? []).ToArray());

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
    private static void ValidateId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("ID cannot be empty."); }
    private static int NextDefinitionSortOrder(IReadOnlyList<StoryDefinitionSummary> values) =>
        values.Count == 0 ? 0 : values.Max(x => x.SortOrder) + 1;
    private static int NextStateSortOrder(IReadOnlyList<StoryStateSummary> values) =>
        values.Count == 0 ? 0 : values.Max(x => x.SortOrder) + 1;

    private void ReportRecovery(string message)
    {
        _recoveryNotices.Enqueue(new(message, DateTimeOffset.UtcNow));
        _logger.LogWarning("{RecoveryMessage}", message);
    }

    private async Task LockedAsync(string key, Func<Task> action, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await action(); } finally { gate.Release(); }
    }

    private async Task<T> LockedAsync<T>(string key, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await action(); } finally { gate.Release(); }
    }

    private async Task LockedManyAsync(
        IReadOnlyList<string> keys,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var gates = keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => _locks.GetOrAdd(x, _ => new(1, 1)))
            .ToArray();
        var acquired = new List<SemaphoreSlim>(gates.Length);
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync(cancellationToken);
                acquired.Add(gate);
            }
            await action();
        }
        finally
        {
            for (var index = acquired.Count - 1; index >= 0; index--)
                acquired[index].Release();
        }
    }

    private void CleanupStaging()
    {
        var directories = Directory.EnumerateDirectories(StagingPath).ToArray();
        var files = Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).ToArray();
        foreach (var directory in directories) Directory.Delete(directory, true);
        foreach (var file in files) File.Delete(file);
        if (directories.Length + files.Length > 0)
            ReportRecovery($"Removed {directories.Length + files.Length} incomplete staged persistence items.");
    }
}
