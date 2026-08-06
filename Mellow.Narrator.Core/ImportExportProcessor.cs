namespace Mellow.Narrator.Core;

public sealed record ImportedStoryState(StoryState State, IReadOnlyList<StoryTurn> Turns);

public static class ImportExportProcessor
{
    public const int CurrentFormatVersion = 1;
    public const int MaximumImportBytes = 16 * 1024 * 1024;

    // Reads at most MaximumImportBytes from an import stream, rejecting anything larger instead of
    // buffering an arbitrarily large file into memory.
    public static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek && stream.Length > MaximumImportBytes)
            throw new InvalidDataException("The import file exceeds the maximum supported size.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumImportBytes)
                throw new InvalidDataException("The import file exceeds the maximum supported size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static StoryDefinition CopyDefinition(
        StoryDefinition source,
        int sortOrder,
        ContentLimitSettings limits,
        StoryGenerationSettings storyGeneration)
    {
        ValidateDefinition(source, limits, storyGeneration);
        var remap = new EntryIdRemapper();
        var plannedEventRemap = new PlannedEventIdRemapper();
        var victoryRemap = new ConditionIdRemapper();
        var lossRemap = new ConditionIdRemapper();
        return source with
        {
            Id = Guid.NewGuid(),
            InitialStoryBible = remap.MapBible(source.InitialStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(remap.MapChange).ToArray()
            }).ToArray(),
            InitialPlannedEvents = plannedEventRemap.MapEvents(source.InitialPlannedEvents),
            PlannedEventMaintenanceHistory = source.PlannedEventMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(plannedEventRemap.MapChange).ToArray()
            }).ToArray(),
            InitialVictoryConditions = victoryRemap.MapConditions(source.InitialVictoryConditions),
            InitialLossConditions = lossRemap.MapConditions(source.InitialLossConditions),
            SortOrder = sortOrder
        };
    }

    public static ImportedStoryState CopyState(
        StoryState source,
        IReadOnlyList<StoryTurn> turns,
        int sortOrder,
        ContentLimitSettings limits,
        StoryGenerationSettings storyGeneration)
    {
        ValidateState(source, turns, limits, storyGeneration);
        var newStateId = Guid.NewGuid();
        var remap = new EntryIdRemapper();
        var plannedEventRemap = new PlannedEventIdRemapper();
        var victoryRemap = new ConditionIdRemapper();
        var lossRemap = new ConditionIdRemapper();
        var state = source with
        {
            Id = newStateId,
            Setup = source.Setup with
            {
                Definition = source.Setup.Definition with
                {
                    InitialStoryBible = remap.MapBible(source.Setup.Definition.InitialStoryBible),
                    InitialPlannedEvents = plannedEventRemap.MapEvents(source.Setup.Definition.InitialPlannedEvents),
                    InitialVictoryConditions = victoryRemap.MapConditions(source.Setup.Definition.InitialVictoryConditions),
                    InitialLossConditions = lossRemap.MapConditions(source.Setup.Definition.InitialLossConditions)
                }
            },
            CurrentStoryBible = remap.MapBible(source.CurrentStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(remap.MapChange).ToArray()
            }).ToArray(),
            CurrentPlannedEvents = plannedEventRemap.MapEvents(source.CurrentPlannedEvents),
            PlannedEventMaintenanceHistory = source.PlannedEventMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(plannedEventRemap.MapChange).ToArray()
            }).ToArray(),
            CurrentVictoryConditions = victoryRemap.MapConditions(source.CurrentVictoryConditions),
            CurrentLossConditions = lossRemap.MapConditions(source.CurrentLossConditions),
            RevealedVictoryConditionIds = source.RevealedVictoryConditionIds.Select(victoryRemap.MapEntryId).ToArray(),
            MetVictoryConditionIds = source.MetVictoryConditionIds.Select(victoryRemap.MapEntryId).ToArray(),
            RevealedLossConditionIds = source.RevealedLossConditionIds.Select(lossRemap.MapEntryId).ToArray(),
            MetLossConditionIds = source.MetLossConditionIds.Select(lossRemap.MapEntryId).ToArray(),
            SortOrder = sortOrder
        };
        var mappedTurns = turns.OrderBy(x => x.SequenceNumber).Select(x => x with
        {
            Id = Guid.NewGuid(),
            StoryStateId = newStateId,
            RelevantStoryBibleEntryIds = x.RelevantStoryBibleEntryIds.Select(remap.MapEntryId).ToArray(),
            StoryBibleChanges = x.StoryBibleChanges.Select(remap.MapChange).ToArray(),
            RelevantPlannedEventIds = x.RelevantPlannedEventIds.Select(plannedEventRemap.MapEntryId).ToArray(),
            PlannedEventChanges = x.PlannedEventChanges.Select(plannedEventRemap.MapChange).ToArray(),
            RevealedVictoryConditionIds = x.RevealedVictoryConditionIds.Select(victoryRemap.MapEntryId).ToArray(),
            MetVictoryConditionIds = x.MetVictoryConditionIds.Select(victoryRemap.MapEntryId).ToArray(),
            RevealedLossConditionIds = x.RevealedLossConditionIds.Select(lossRemap.MapEntryId).ToArray(),
            MetLossConditionIds = x.MetLossConditionIds.Select(lossRemap.MapEntryId).ToArray()
        }).ToArray();
        return new(state, mappedTurns);
    }

    // Consistently remaps every Story Bible entry ID a single Copy call touches (bible entries,
    // maintenance-history change snapshots, turn relevant-entry lists) to a fresh ID, reusing the same
    // mapping wherever the same old ID recurs. Shared by CopyDefinition and CopyState so the two don't
    // duplicate identical remapping logic.
    private sealed class EntryIdRemapper
    {
        private readonly Dictionary<Guid, Guid> _entryIds = [];

        public Guid MapEntryId(Guid oldId) =>
            _entryIds.TryGetValue(oldId, out var mapped) ? mapped : _entryIds[oldId] = Guid.NewGuid();

        public StoryBibleEntry MapEntry(StoryBibleEntry entry) => entry with { Id = MapEntryId(entry.Id) };

        public StoryBible MapBible(StoryBible bible) => new(bible.Entries.Select(MapEntry).ToArray());

        public AppliedStoryBibleChange MapChange(AppliedStoryBibleChange change) => change with
        {
            EntryId = MapEntryId(change.EntryId),
            Before = change.Before is null ? null : MapEntry(change.Before),
            After = change.After is null ? null : MapEntry(change.After)
        };
    }

    // Parallel to EntryIdRemapper above, but for Planned Event IDs - a separate ID space with its own
    // mapping, kept in its own instance so a Copy call remaps initial/current Planned Events and their
    // maintenance history/turn references consistently, the same way EntryIdRemapper does for the bible.
    private sealed class PlannedEventIdRemapper
    {
        private readonly Dictionary<Guid, Guid> _entryIds = [];

        public Guid MapEntryId(Guid oldId) =>
            _entryIds.TryGetValue(oldId, out var mapped) ? mapped : _entryIds[oldId] = Guid.NewGuid();

        // A prerequisite id is remapped through the same dictionary as entry ids, so a reference to
        // another entry (live or already resolved and only surviving in maintenance-history snapshots)
        // keeps pointing at that same entry's new id after the copy.
        public PlannedEvent MapEntry(PlannedEvent entry) => entry with
        {
            Id = MapEntryId(entry.Id),
            PrerequisiteEventIds = entry.PrerequisiteEventIds.Select(MapEntryId).ToArray()
        };

        public PlannedEvents MapEvents(PlannedEvents events) => new(events.Entries.Select(MapEntry).ToArray());

        public AppliedPlannedEventChange MapChange(AppliedPlannedEventChange change) => change with
        {
            EntryId = MapEntryId(change.EntryId),
            Before = change.Before is null ? null : MapEntry(change.Before),
            After = change.After is null ? null : MapEntry(change.After)
        };
    }

    // Parallel to PlannedEventIdRemapper above, but for Story Condition IDs. Victory and Loss Conditions
    // are separate ID spaces (a definition's InitialVictoryConditions and InitialLossConditions never
    // reference each other), so CopyDefinition/CopyState use one instance per axis.
    private sealed class ConditionIdRemapper
    {
        private readonly Dictionary<Guid, Guid> _entryIds = [];

        public Guid MapEntryId(Guid oldId) =>
            _entryIds.TryGetValue(oldId, out var mapped) ? mapped : _entryIds[oldId] = Guid.NewGuid();

        public StoryConditions MapConditions(StoryConditions conditions) =>
            new(conditions.Entries.Select(x => x with { Id = MapEntryId(x.Id) }).ToArray());
    }

    private static void ValidateConditions(StoryConditions conditions, ContentLimitSettings limits)
    {
        if (conditions.Entries.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            conditions.Entries.Select(x => x.Id).Distinct().Count() != conditions.Entries.Count)
            throw new InvalidDataException("Condition IDs are invalid.");
        foreach (var entry in conditions.Entries)
            if (StoryConditionProcessor.ValidateEntry(entry.Description, limits) is { } error)
                throw new InvalidDataException(error);
    }

    private static void ValidateConditionIds(StoryConditions conditions, IReadOnlyList<Guid> ids, string name)
    {
        var known = conditions.Entries.Select(x => x.Id).ToHashSet();
        if (ids.Distinct().Count() != ids.Count || ids.Any(id => !known.Contains(id)))
            throw new InvalidDataException($"A {name} ID is invalid.");
    }

    public static void ValidateDefinition(StoryDefinition value, ContentLimitSettings limits, StoryGenerationSettings storyGeneration)
    {
        if (value.Id == Guid.Empty) throw new InvalidDataException("The Story Definition ID is invalid.");
        ValidateText(value.Title, limits.MaxStoryTitleCharacters, "Story Definition title");
        ValidateText(value.StoryPrompt, limits.MaxStoryPromptCharacters, "Story Prompt");
        ValidateOptionalText(value.InitialEventsPrompt, limits.MaxStoryPromptCharacters, "Initial Events prompt");
        ValidateBible(value.InitialStoryBible, limits, storyGeneration);
        ValidateMaintenance(value.StoryBibleMaintenanceHistory);
        ValidatePlannedEvents(value.InitialPlannedEvents, limits, storyGeneration);
        ValidatePlannedEventMaintenance(value.PlannedEventMaintenanceHistory);
        ValidateConditions(value.InitialVictoryConditions, limits);
        ValidateConditions(value.InitialLossConditions, limits);
        ValidateUtc(value.CreatedAtUtc, "created timestamp");
        ValidateUtc(value.UpdatedAtUtc, "updated timestamp");
    }

    public static void ValidateState(
        StoryState state,
        IReadOnlyList<StoryTurn> turns,
        ContentLimitSettings limits,
        StoryGenerationSettings storyGeneration)
    {
        if (state.Id == Guid.Empty) throw new InvalidDataException("The Story State ID is invalid.");
        ValidateText(state.Label, limits.MaxStoryLabelCharacters, "Story State label");
        ValidateText(state.Setup.Definition.Title, limits.MaxStoryTitleCharacters, "snapshot title");
        ValidateText(state.Setup.Definition.StoryPrompt, limits.MaxStoryPromptCharacters, "snapshot Story Prompt");
        ValidateOptionalText(state.Setup.Definition.InitialEventsPrompt, limits.MaxStoryPromptCharacters, "snapshot Initial Events prompt");
        ValidateBible(state.Setup.Definition.InitialStoryBible, limits, storyGeneration);
        ValidateBible(state.CurrentStoryBible, limits, storyGeneration);
        ValidateMaintenance(state.StoryBibleMaintenanceHistory);
        ValidatePlannedEvents(state.Setup.Definition.InitialPlannedEvents, limits, storyGeneration);
        ValidatePlannedEvents(state.CurrentPlannedEvents, limits, storyGeneration);
        ValidatePlannedEventMaintenance(state.PlannedEventMaintenanceHistory);
        ValidateConditions(state.Setup.Definition.InitialVictoryConditions, limits);
        ValidateConditions(state.Setup.Definition.InitialLossConditions, limits);
        ValidateConditions(state.CurrentVictoryConditions, limits);
        ValidateConditions(state.CurrentLossConditions, limits);
        ValidateConditionIds(state.CurrentVictoryConditions, state.RevealedVictoryConditionIds, "revealed Victory Condition");
        ValidateConditionIds(state.CurrentVictoryConditions, state.MetVictoryConditionIds, "met Victory Condition");
        ValidateConditionIds(state.CurrentLossConditions, state.RevealedLossConditionIds, "revealed Loss Condition");
        ValidateConditionIds(state.CurrentLossConditions, state.MetLossConditionIds, "met Loss Condition");
        ValidateUtc(state.StartedAtUtc, "started timestamp");
        if (state.LastActionAtUtc is { } lastAction) ValidateUtc(lastAction, "last-action timestamp");

        var ordered = turns.OrderBy(x => x.SequenceNumber).ToArray();
        if (ordered.Length == 0 || ordered[0].SequenceNumber != 0 ||
            ordered.Where((x, index) => x.SequenceNumber != index).Any() ||
            state.LastCommittedTurnSequence != ordered[^1].SequenceNumber)
            throw new InvalidDataException("Story turns are not contiguous or do not match the Story State.");
        if (ordered.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            ordered.Select(x => x.Id).Distinct().Count() != ordered.Length ||
            ordered.Any(x => x.StoryStateId != state.Id))
            throw new InvalidDataException("Story Turn identities are invalid.");
        if (ordered[0].PlayerAction is not null)
            throw new InvalidDataException("The opening turn must not contain a player action.");
        foreach (var turn in ordered)
        {
            ValidateText(turn.Narration, limits.MaxNarrationCharacters, "turn narration");
            if (turn.PlayerAction is { } action) ValidateText(action, limits.MaxPlayerActionCharacters, "player action");
            if (turn.SuggestedActions.Count > limits.MaxSuggestedActions)
                throw new InvalidDataException("A turn has too many suggested actions.");
            foreach (var suggestion in turn.SuggestedActions)
                ValidateText(suggestion, limits.MaxSuggestedActionCharacters, "suggested action");
            if (turn.RelevantStoryBibleEntryIds.Distinct().Count() != turn.RelevantStoryBibleEntryIds.Count)
                throw new InvalidDataException("A turn contains duplicate relevant-entry IDs.");
            ValidateChanges(turn.StoryBibleChanges);
            if (turn.RelevantPlannedEventIds.Distinct().Count() != turn.RelevantPlannedEventIds.Count)
                throw new InvalidDataException("A turn contains duplicate relevant Planned Event IDs.");
            ValidatePlannedEventChanges(turn.PlannedEventChanges);
            if (turn.RevealedVictoryConditionIds.Distinct().Count() != turn.RevealedVictoryConditionIds.Count ||
                turn.MetVictoryConditionIds.Distinct().Count() != turn.MetVictoryConditionIds.Count ||
                turn.RevealedLossConditionIds.Distinct().Count() != turn.RevealedLossConditionIds.Count ||
                turn.MetLossConditionIds.Distinct().Count() != turn.MetLossConditionIds.Count)
                throw new InvalidDataException("A turn contains duplicate condition IDs.");
            ValidateUtc(turn.CompletedAtUtc, "turn timestamp");
            ValidateText(turn.Generation.ModelId, 1000, "generation model ID");
        }
    }

    private static void ValidateBible(StoryBible bible, ContentLimitSettings limits, StoryGenerationSettings storyGeneration)
    {
        if (bible.Entries.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            bible.Entries.Select(x => x.Id).Distinct().Count() != bible.Entries.Count)
            throw new InvalidDataException("Story Bible entry IDs are invalid.");
        foreach (var entry in bible.Entries)
        {
            if (StoryBibleProcessor.ValidateEntry(
                    entry.Category, entry.Name, entry.KnownFacts, entry.SecretFacts,
                    entry.Importance, entry.LastRelevantTurnNumber, limits) is { } error)
                throw new InvalidDataException(error);
        }
        if (!StoryBibleProcessor.IsWithinLimits(bible, storyGeneration))
            throw new InvalidDataException("The Story Bible exceeds the configured Story Bible limits.");
    }

    private static void ValidateMaintenance(IReadOnlyList<StoryBibleMaintenanceRecord> records)
    {
        if (records.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            records.Select(x => x.Id).Distinct().Count() != records.Count)
            throw new InvalidDataException("Story Bible maintenance IDs are invalid.");
        foreach (var record in records)
        {
            if (record.Limits.MaxEntries <= 0 || record.Limits.MaxEntryCharacters <= 0 || record.Limits.MaxTotalCharacters <= 0)
                throw new InvalidDataException("A Story Bible maintenance limit snapshot is invalid.");
            ValidateChanges(record.Changes);
            ValidateUtc(record.CompletedAtUtc, "maintenance timestamp");
        }
    }

    private static void ValidateChanges(IReadOnlyList<AppliedStoryBibleChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.EntryId == Guid.Empty ||
                change.Before is not null && change.Before.Id != change.EntryId ||
                change.After is not null && change.After.Id != change.EntryId)
                throw new InvalidDataException("A Story Bible change has inconsistent IDs.");
            if (change.Operation == StoryBibleOperation.Add && (change.Before is not null || change.After is null) ||
                change.Operation == StoryBibleOperation.Replace && (change.Before is null || change.After is null) ||
                change.Operation == StoryBibleOperation.Remove && (change.Before is null || change.After is not null))
                throw new InvalidDataException("A Story Bible change has an invalid before/after shape.");
        }
    }

    private static void ValidatePlannedEvents(PlannedEvents events, ContentLimitSettings limits, StoryGenerationSettings storyGeneration)
    {
        if (events.Entries.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            events.Entries.Select(x => x.Id).Distinct().Count() != events.Entries.Count)
            throw new InvalidDataException("Planned Event IDs are invalid.");
        foreach (var entry in events.Entries)
        {
            if (PlannedEventProcessor.ValidateEntry(entry.Description, entry.Importance, entry.Urgency, entry.LastRelevantTurnNumber, limits) is { } error)
                throw new InvalidDataException(error);
            if (entry.PrerequisiteEventIds.Contains(Guid.Empty))
                throw new InvalidDataException("A Planned Event lists an empty prerequisite ID.");
        }
        if (PlannedEventProcessor.ValidateRelationships(events) is { } relationshipError)
            throw new InvalidDataException(relationshipError);
        if (!PlannedEventProcessor.IsWithinLimits(events, storyGeneration))
            throw new InvalidDataException("The Planned Events exceed the configured Planned Event limits.");
    }

    private static void ValidatePlannedEventMaintenance(IReadOnlyList<PlannedEventMaintenanceRecord> records)
    {
        if (records.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            records.Select(x => x.Id).Distinct().Count() != records.Count)
            throw new InvalidDataException("Planned Event maintenance IDs are invalid.");
        foreach (var record in records)
        {
            if (record.Limits.MaxEntries <= 0 || record.Limits.MaxEntryCharacters <= 0 || record.Limits.MaxTotalCharacters <= 0)
                throw new InvalidDataException("A Planned Event maintenance limit snapshot is invalid.");
            ValidatePlannedEventChanges(record.Changes);
            ValidateUtc(record.CompletedAtUtc, "maintenance timestamp");
        }
    }

    private static void ValidatePlannedEventChanges(IReadOnlyList<AppliedPlannedEventChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.EntryId == Guid.Empty ||
                change.Before is not null && change.Before.Id != change.EntryId ||
                change.After is not null && change.After.Id != change.EntryId)
                throw new InvalidDataException("A Planned Event change has inconsistent IDs.");
            if (change.Operation == PlannedEventOperation.Add && (change.Before is not null || change.After is null) ||
                change.Operation == PlannedEventOperation.Replace && (change.Before is null || change.After is null) ||
                change.Operation == PlannedEventOperation.Remove && (change.Before is null || change.After is not null))
                throw new InvalidDataException("A Planned Event change has an invalid before/after shape.");
        }
    }

    private static void ValidateText(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new InvalidDataException($"The {name} is empty or exceeds its configured limit.");
    }

    private static void ValidateOptionalText(string? value, int maximum, string name)
    {
        if (value is not null && value.Length > maximum)
            throw new InvalidDataException($"The {name} exceeds its configured limit.");
    }

    private static void ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new InvalidDataException($"The {name} must be UTC.");
    }
}
