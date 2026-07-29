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
        return source with
        {
            Id = Guid.NewGuid(),
            InitialStoryBible = remap.MapBible(source.InitialStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(remap.MapChange).ToArray()
            }).ToArray(),
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
        var state = source with
        {
            Id = newStateId,
            Setup = source.Setup with
            {
                Definition = source.Setup.Definition with
                {
                    InitialStoryBible = remap.MapBible(source.Setup.Definition.InitialStoryBible)
                }
            },
            CurrentStoryBible = remap.MapBible(source.CurrentStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(remap.MapChange).ToArray()
            }).ToArray(),
            SortOrder = sortOrder
        };
        var mappedTurns = turns.OrderBy(x => x.SequenceNumber).Select(x => x with
        {
            Id = Guid.NewGuid(),
            StoryStateId = newStateId,
            RelevantStoryBibleEntryIds = x.RelevantStoryBibleEntryIds.Select(remap.MapEntryId).ToArray(),
            StoryBibleChanges = x.StoryBibleChanges.Select(remap.MapChange).ToArray()
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

    public static void ValidateDefinition(StoryDefinition value, ContentLimitSettings limits, StoryGenerationSettings storyGeneration)
    {
        if (value.Id == Guid.Empty) throw new InvalidDataException("The Story Definition ID is invalid.");
        ValidateText(value.Title, limits.MaxStoryTitleCharacters, "Story Definition title");
        ValidateText(value.StoryPrompt, limits.MaxStoryPromptCharacters, "Story Prompt");
        ValidateOptionalText(value.InitialEventsPrompt, limits.MaxStoryPromptCharacters, "Initial Events prompt");
        ValidateBible(value.InitialStoryBible, limits, storyGeneration);
        ValidateMaintenance(value.StoryBibleMaintenanceHistory);
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
