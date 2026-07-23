namespace Mellow.Narrator.Core;

public sealed record ImportedStoryState(StoryState State, IReadOnlyList<StoryTurn> Turns);

public static class ImportExportProcessor
{
    public const int CurrentFormatVersion = 1;
    public const int MaximumImportBytes = 16 * 1024 * 1024;

    public static StoryDefinition CopyDefinition(
        StoryDefinition source,
        int sortOrder,
        ContentLimitSettings limits)
    {
        ValidateDefinition(source, limits);
        var entryIds = new Dictionary<Guid, Guid>();
        Guid MapEntryId(Guid oldId) =>
            entryIds.TryGetValue(oldId, out var mapped) ? mapped : entryIds[oldId] = Guid.NewGuid();
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
            PlayerQuestions = source.PlayerQuestions.Select(x => x with { Id = Guid.NewGuid() }).ToArray(),
            InitialStoryBible = new(source.InitialStoryBible.Entries.Select(MapEntry).ToArray()),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = sortOrder
        };
    }

    public static ImportedStoryState CopyState(
        StoryState source,
        IReadOnlyList<StoryTurn> turns,
        int sortOrder,
        ContentLimitSettings limits)
    {
        ValidateState(source, turns, limits);
        var newStateId = Guid.NewGuid();
        var entryIds = new Dictionary<Guid, Guid>();
        var questionIds = new Dictionary<Guid, Guid>();
        Guid MapEntryId(Guid oldId) =>
            entryIds.TryGetValue(oldId, out var mapped) ? mapped : entryIds[oldId] = Guid.NewGuid();
        Guid MapQuestionId(Guid oldId) =>
            questionIds.TryGetValue(oldId, out var mapped) ? mapped : questionIds[oldId] = Guid.NewGuid();
        StoryBibleEntry MapEntry(StoryBibleEntry entry) => entry with { Id = MapEntryId(entry.Id) };
        StoryBible MapBible(StoryBible bible) => new(bible.Entries.Select(MapEntry).ToArray());
        AppliedStoryBibleChange MapChange(AppliedStoryBibleChange change) => change with
        {
            EntryId = MapEntryId(change.EntryId),
            Before = change.Before is null ? null : MapEntry(change.Before),
            After = change.After is null ? null : MapEntry(change.After)
        };
        var state = source with
        {
            Id = newStateId,
            Setup = source.Setup with
            {
                Definition = source.Setup.Definition with
                {
                    PlayerQuestions = source.Setup.Definition.PlayerQuestions
                        .Select(x => x with { Id = MapQuestionId(x.Id) }).ToArray(),
                    InitialStoryBible = MapBible(source.Setup.Definition.InitialStoryBible)
                },
                PlayerResponses = source.Setup.PlayerResponses.Select(x => x with
                {
                    QuestionId = MapQuestionId(x.QuestionId)
                }).ToArray()
            },
            CurrentStoryBible = MapBible(source.CurrentStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = sortOrder
        };
        var mappedTurns = turns.OrderBy(x => x.SequenceNumber).Select(x => x with
        {
            Id = Guid.NewGuid(),
            StoryStateId = newStateId,
            RelevantStoryBibleEntryIds = x.RelevantStoryBibleEntryIds.Select(MapEntryId).ToArray(),
            StoryBibleChanges = x.StoryBibleChanges.Select(MapChange).ToArray()
        }).ToArray();
        return new(state, mappedTurns);
    }

    public static void ValidateDefinition(StoryDefinition value, ContentLimitSettings limits)
    {
        if (value.Id == Guid.Empty) throw new InvalidDataException("The Story Definition ID is invalid.");
        ValidateText(value.Title, limits.MaxStoryTitleCharacters, "Story Definition title");
        ValidateText(value.StoryPrompt, limits.MaxStoryPromptCharacters, "Story Prompt");
        ValidateQuestions(value.PlayerQuestions, limits);
        ValidateBible(value.InitialStoryBible, limits);
        ValidateMaintenance(value.StoryBibleMaintenanceHistory);
        ValidateUtc(value.CreatedAtUtc, "created timestamp");
        ValidateUtc(value.UpdatedAtUtc, "updated timestamp");
    }

    public static void ValidateState(
        StoryState state,
        IReadOnlyList<StoryTurn> turns,
        ContentLimitSettings limits)
    {
        if (state.Id == Guid.Empty) throw new InvalidDataException("The Story State ID is invalid.");
        ValidateText(state.Label, limits.MaxStoryLabelCharacters, "Story State label");
        ValidateText(state.Setup.Definition.Title, limits.MaxStoryTitleCharacters, "snapshot title");
        ValidateText(state.Setup.Definition.StoryPrompt, limits.MaxStoryPromptCharacters, "snapshot Story Prompt");
        ValidateQuestions(state.Setup.Definition.PlayerQuestions, limits);
        ValidateBible(state.Setup.Definition.InitialStoryBible, limits);
        ValidateBible(state.CurrentStoryBible, limits);
        ValidateMaintenance(state.StoryBibleMaintenanceHistory);
        var questionsById = state.Setup.Definition.PlayerQuestions.ToDictionary(x => x.Id);
        if (state.Setup.PlayerResponses.Select(x => x.QuestionId).Distinct().Count() != state.Setup.PlayerResponses.Count ||
            state.Setup.PlayerResponses.Count != questionsById.Count ||
            state.Setup.PlayerResponses.Any(response =>
                !questionsById.TryGetValue(response.QuestionId, out var question) ||
                !string.Equals(response.Question, question.Question, StringComparison.Ordinal) ||
                !string.Equals(response.ValidationInstruction, question.ValidationInstruction, StringComparison.Ordinal)))
            throw new InvalidDataException("Player responses do not match the snapshot questions.");
        foreach (var response in state.Setup.PlayerResponses)
            ValidateText(response.Answer, limits.MaxPlayerAnswerCharacters, "player answer");
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

    private static void ValidateQuestions(IReadOnlyList<PlayerQuestion> questions, ContentLimitSettings limits)
    {
        if (questions.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            questions.Select(x => x.Id).Distinct().Count() != questions.Count ||
            questions.Select(x => x.SortOrder).Distinct().Count() != questions.Count)
            throw new InvalidDataException("Player question identities or ordering are invalid.");
        foreach (var question in questions)
        {
            ValidateText(question.Question, limits.MaxPlayerQuestionCharacters, "player question");
            ValidateText(question.ValidationInstruction, limits.MaxValidationInstructionCharacters, "validation instruction");
        }
    }

    private static void ValidateBible(StoryBible bible, ContentLimitSettings limits)
    {
        if (bible.Entries.Select(x => x.Id).Any(x => x == Guid.Empty) ||
            bible.Entries.Select(x => x.Id).Distinct().Count() != bible.Entries.Count)
            throw new InvalidDataException("Story Bible entry IDs are invalid.");
        foreach (var entry in bible.Entries)
        {
            ValidateText(entry.Category, limits.MaxStoryBibleCategoryCharacters, "Story Bible category");
            ValidateText(entry.Name, limits.MaxStoryBibleNameCharacters, "Story Bible entry name");
            if (string.IsNullOrWhiteSpace(entry.Content))
                throw new InvalidDataException("A Story Bible entry has empty content.");
            if (entry.Importance is < 1 or > 5 || entry.LastRelevantTurnNumber < 0)
                throw new InvalidDataException("Story Bible metadata is invalid.");
        }
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

    private static void ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new InvalidDataException($"The {name} must be UTC.");
    }
}
