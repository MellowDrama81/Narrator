using System.Text.Json;

namespace Mellow.Narrator.Core;

public sealed record PlannedEventApplyResult(
    PlannedEvents Events,
    IReadOnlyList<Guid> RelevantEntryIds,
    IReadOnlyList<AppliedPlannedEventChange> Changes);

public static class PlannedEventProcessor
{
    // Importance is 1 through 5, same scale as the Story Bible; 5 is the maximum and marks a Planned
    // Event mandatory. Apply refuses to let a mandatory entry be removed except with outcome Fulfilled,
    // refuses to let a Replace quietly demote it out of mandatory status, and Cull never selects it as
    // an eviction candidate — the narrator has to actually work the event into the story to be rid of it.
    public const int MandatoryImportance = 5;

    public static PlannedEventApplyResult Apply(
        PlannedEvents current,
        IReadOnlyList<Guid> relevantIds,
        IReadOnlyList<ProposedPlannedEventUpdate> updates,
        int turnNumber,
        StoryGenerationSettings limits,
        Func<Guid>? createId = null)
    {
        createId ??= Guid.NewGuid;
        var entries = current.Entries.ToDictionary(x => x.Id);
        // The set of ids the model could actually have seen in this turn's plannedEvents context. New
        // prerequisite ids introduced by a proposal are checked against this snapshot, not the mutating
        // entries dictionary below, so a proposal can never "prerequire" a sibling added later in this
        // same batch - the model has no way to know that sibling's id yet, since ids for Add are always
        // assigned by us, never invented by the model.
        var knownIds = current.Entries.Select(x => x.Id).ToHashSet();
        var relevant = relevantIds.ToHashSet();
        if (relevant.Any(x => !entries.ContainsKey(x)))
            throw new NarratorException("The model marked an unknown Planned Event as relevant.");

        // Assign every Add a real id up front and collect a Key -> id map, so any proposal in this same
        // batch (including another Add) can reference a sibling Add's id via PrerequisiteKeys before the
        // loop below actually creates it - order within the batch doesn't matter, and the model still
        // never invents a real id itself. See the ProposedPlannedEvent comment in Models.cs.
        var addedIds = new Guid?[updates.Count];
        var keyToId = new Dictionary<string, Guid>();
        for (var i = 0; i < updates.Count; i++)
        {
            if (updates[i].Operation != PlannedEventOperation.Add || updates[i].Entry is null) continue;
            var id = createId();
            addedIds[i] = id;
            var key = updates[i].Entry!.Key;
            if (!string.IsNullOrWhiteSpace(key) && !keyToId.TryAdd(key, id))
                throw new NarratorException($"Duplicate Planned Event key '{key}' in the same batch.");
        }

        var touched = new HashSet<Guid>();
        var changes = new List<AppliedPlannedEventChange>();
        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i];
            if (update.Operation == PlannedEventOperation.Add)
            {
                ValidateProposal(update.Entry, before: null, knownIds);
                var id = addedIds[i]!.Value;
                var after = ToEntry(id, update.Entry!, turnNumber, keyToId);
                entries.Add(id, after);
                relevant.Add(id);
                changes.Add(new(PlannedEventOperation.Add, id, null, after, PlannedEventChangeSource.LlmUpdate, null));
                continue;
            }

            if (update.EntryId is null || !entries.TryGetValue(update.EntryId.Value, out var before))
                throw new NarratorException("A Planned Event update references an unknown entry.");
            if (!touched.Add(update.EntryId.Value))
                throw new NarratorException("A Planned Event was updated more than once.");

            if (update.Operation == PlannedEventOperation.Remove)
            {
                if (update.Entry is not null || relevant.Contains(before.Id))
                    throw new NarratorException("A removed Planned Event cannot also be relevant or contain a replacement.");
                if (update.Outcome is null)
                    throw new NarratorException("A Planned Event removal must state whether the event was fulfilled or abandoned.");
                if (before.Importance == MandatoryImportance && update.Outcome != PlannedEventOutcome.Fulfilled)
                    throw new NarratorException("A mandatory Planned Event can only be removed once it has occurred in the story.");
                entries.Remove(before.Id);
                changes.Add(new(PlannedEventOperation.Remove, before.Id, before, null, PlannedEventChangeSource.LlmUpdate, update.Outcome));
            }
            else
            {
                ValidateProposal(update.Entry, before, knownIds);
                if (before.Importance == MandatoryImportance && update.Entry!.Importance != MandatoryImportance)
                    throw new NarratorException("A mandatory Planned Event's importance cannot be reduced; remove it as fulfilled once it occurs.");
                var after = ToEntry(before.Id, update.Entry!, turnNumber, keyToId);
                entries[before.Id] = after;
                relevant.Add(before.Id);
                changes.Add(new(PlannedEventOperation.Replace, before.Id, before, after, PlannedEventChangeSource.LlmUpdate, null));
            }
        }

        foreach (var id in relevant)
        {
            if (!entries.TryGetValue(id, out var entry)) continue;
            entries[id] = entry with { LastRelevantTurnNumber = turnNumber };
        }

        Cull(entries, limits, changes);
        var events = new PlannedEvents(entries.Values
            .OrderByDescending(x => x.Importance)
            .ThenBy(x => x.LastRelevantTurnNumber)
            .ThenBy(x => x.Id)
            .ToArray());
        if (ValidateRelationships(events) is { } relationshipError)
            throw new NarratorException(relationshipError);
        ValidateSize(events, limits);
        return new(events, relevant.Where(entries.ContainsKey).Order().ToArray(), changes);
    }

    // Resolves a batch of brand-new proposals with no pre-existing entries and no updates - the initial
    // Planned Events proposed alongside a Story Definition - into real Planned Events, assigning each an
    // id via createId and resolving PrerequisiteKeys against each other's Key the same way Apply resolves
    // an Add's PrerequisiteKeys against its batch siblings. Throws if two proposals share a Key, if a
    // PrerequisiteKeys entry doesn't match any proposal's Key, or if the resolved result has a
    // self-reference or a cycle.
    public static PlannedEvents ResolveInitialPlannedEvents(IReadOnlyList<ProposedPlannedEvent> proposals, Func<Guid> createId)
    {
        var ids = proposals.Select(_ => createId()).ToArray();
        var keyToId = new Dictionary<string, Guid>();
        for (var i = 0; i < proposals.Count; i++)
        {
            var key = proposals[i].Key;
            if (!string.IsNullOrWhiteSpace(key) && !keyToId.TryAdd(key, ids[i]))
                throw new NarratorException($"Duplicate Planned Event key '{key}' in the same batch.");
        }
        var entries = proposals.Select((proposal, i) =>
            new PlannedEvent(ids[i], proposal.Description.Trim(), proposal.Importance, proposal.Urgency,
                ResolvePrerequisites(proposal, keyToId), 0)).ToArray();
        var events = new PlannedEvents(entries);
        if (ValidateRelationships(events) is { } error) throw new NarratorException(error);
        return events;
    }

    public static (PlannedEvents Events, IReadOnlyList<AppliedPlannedEventChange> Changes) CullToLimits(
        PlannedEvents current,
        StoryGenerationSettings limits)
    {
        var entries = current.Entries.ToDictionary(x => x.Id);
        var changes = new List<AppliedPlannedEventChange>();
        Cull(entries, limits, changes);
        var events = new PlannedEvents(entries.Values
            .OrderByDescending(x => x.Importance)
            .ThenBy(x => x.LastRelevantTurnNumber)
            .ThenBy(x => x.Id)
            .ToArray());
        return (events, changes);
    }

    public static bool IsWithinLimits(PlannedEvents events, StoryGenerationSettings limits) =>
        events.Entries.Count <= limits.MaxPlannedEvents
        && events.Entries.All(x => SerializedLength(x) <= limits.MaxPlannedEventCharacters)
        && SerializedLength(events) <= limits.MaxPlannedEventsCharacters;

    public static bool IsApproachingLimits(PlannedEvents events, StoryGenerationSettings limits)
    {
        var threshold = limits.PlannedEventsWarningPercent / 100d;
        var largest = events.Entries.Count == 0 ? 0 : events.Entries.Max(SerializedLength);
        return events.Entries.Count >= limits.MaxPlannedEvents * threshold ||
            largest >= limits.MaxPlannedEventCharacters * threshold ||
            SerializedLength(events) >= limits.MaxPlannedEventsCharacters * threshold;
    }

    // Shared field-level validation for a materialized Planned Event, used by both manual edits
    // (NarratorApplication) and import/copy (ImportExportProcessor) so the two paths can't drift out of
    // sync on what counts as a valid entry.
    public static string? ValidateEntry(
        string description,
        int importance,
        int urgency,
        int? lastRelevantTurnNumber,
        ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > limits.MaxPlannedEventDescriptionCharacters)
            return "A Planned Event description is empty or exceeds the configured limit.";
        if (importance is < 1 or > 5)
            return "Planned Event importance must be from 1 to 5.";
        if (urgency is < 1 or > 5)
            return "Planned Event urgency must be from 1 to 5.";
        if (lastRelevantTurnNumber < 0)
            return "A Planned Event's last-relevant turn number cannot be negative.";
        return null;
    }

    private static void Cull(IDictionary<Guid, PlannedEvent> entries, StoryGenerationSettings limits, ICollection<AppliedPlannedEventChange> changes)
    {
        foreach (var entry in entries.Values.Where(x => SerializedLength(x) > limits.MaxPlannedEventCharacters).ToArray())
            Remove(entries, entry, changes);
        while (entries.Count > limits.MaxPlannedEvents || SerializedLength(new PlannedEvents(entries.Values.ToArray())) > limits.MaxPlannedEventsCharacters)
        {
            var candidate = entries.Values
                .Where(x => x.Importance != MandatoryImportance)
                .OrderBy(x => x.Importance).ThenBy(x => x.LastRelevantTurnNumber).ThenBy(x => x.Id)
                .FirstOrDefault();
            if (candidate is null) break;
            Remove(entries, candidate, changes);
        }
    }

    private static void Remove(IDictionary<Guid, PlannedEvent> entries, PlannedEvent entry, ICollection<AppliedPlannedEventChange> changes)
    {
        entries.Remove(entry.Id);
        changes.Add(new(PlannedEventOperation.Remove, entry.Id, entry, null, PlannedEventChangeSource.AutomaticCull, null));
    }

    private static PlannedEvent ToEntry(Guid id, ProposedPlannedEvent value, int turn, IReadOnlyDictionary<string, Guid> keyToId) =>
        new(id, value.Description.Trim(), value.Importance, value.Urgency, ResolvePrerequisites(value, keyToId), turn);

    // Merges PrerequisiteEventIds (real ids, already validated against knownIds by ValidateProposal) with
    // PrerequisiteKeys resolved through the batch's Key -> id map. An unresolvable key means the model
    // referenced a label no proposal in this batch actually declared - a hallucinated/invented reference,
    // same failure mode ValidateProposal already guards against for real ids, so it's rejected the same way.
    private static IReadOnlyList<Guid> ResolvePrerequisites(ProposedPlannedEvent value, IReadOnlyDictionary<string, Guid> keyToId)
    {
        if (value.PrerequisiteKeys.Count == 0) return value.PrerequisiteEventIds.Distinct().ToArray();
        var resolved = new List<Guid>(value.PrerequisiteEventIds);
        foreach (var key in value.PrerequisiteKeys)
        {
            if (!keyToId.TryGetValue(key, out var id))
                throw new NarratorException($"A Planned Event references an unknown prerequisite key '{key}'.");
            resolved.Add(id);
        }
        return resolved.Distinct().ToArray();
    }

    // knownIds is the set of ids the model could have seen this turn (see the comment in Apply). before
    // is the entry being replaced, or null for an Add; any prerequisite id the entry already carried is
    // always allowed to be echoed back or dropped (it is the entry's own history), but a NEWLY introduced
    // prerequisite id must be one the model actually saw, to catch a hallucinated/invented reference that
    // could otherwise leave the entry blocked on something that never existed.
    private static void ValidateProposal(ProposedPlannedEvent? entry, PlannedEvent? before, IReadOnlySet<Guid> knownIds)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Description))
            throw new NarratorException("A Planned Event is incomplete.");
        if (entry.Importance is < 1 or > 5)
            throw new NarratorException("Planned Event importance must be from 1 to 5.");
        if (entry.Urgency is < 1 or > 5)
            throw new NarratorException("Planned Event urgency must be from 1 to 5.");
        var newPrerequisites = before is null
            ? entry.PrerequisiteEventIds
            : entry.PrerequisiteEventIds.Except(before.PrerequisiteEventIds);
        if (newPrerequisites.Any(id => !knownIds.Contains(id)))
            throw new NarratorException("A Planned Event lists an unknown prerequisite event.");
    }

    // Returns an error message if the collection has an invalid prerequisite relationship - a Planned
    // Event listing itself, or a cycle among currently-live entries - or null if it is valid. A
    // prerequisite id that no longer names a live entry is never an error; see the PlannedEvent comment
    // in Models.cs for why a dangling id is expected and harmless rather than something to reject.
    public static string? ValidateRelationships(PlannedEvents events)
    {
        foreach (var entry in events.Entries)
            if (entry.PrerequisiteEventIds.Contains(entry.Id))
                return "A Planned Event cannot list itself as a prerequisite.";
        return HasCycle(events) ? "Planned Events contain a prerequisite cycle." : null;
    }

    private static bool HasCycle(PlannedEvents events)
    {
        var entries = events.Entries.ToDictionary(x => x.Id);
        var state = new Dictionary<Guid, int>(); // 1 = on the current path (cycle if revisited), 2 = fully explored
        foreach (var id in entries.Keys)
            if (Visit(id)) return true;
        return false;

        bool Visit(Guid id)
        {
            if (state.TryGetValue(id, out var visited)) return visited == 1;
            state[id] = 1;
            if (entries.TryGetValue(id, out var entry))
                foreach (var prerequisiteId in entry.PrerequisiteEventIds)
                    if (entries.ContainsKey(prerequisiteId) && Visit(prerequisiteId))
                        return true;
            state[id] = 2;
            return false;
        }
    }

    private static void ValidateSize(PlannedEvents events, StoryGenerationSettings limits)
    {
        if (events.Entries.Any(x => SerializedLength(x) > limits.MaxPlannedEventCharacters))
            throw new NarratorException("A Planned Event exceeds the configured character limit.");
        if (SerializedLength(events) > limits.MaxPlannedEventsCharacters)
            throw new NarratorException("The Planned Events collection exceeds the configured total character limit.");
        // Unlike Story Bible entries, mandatory (importance 5) Planned Events are never auto-culled by
        // count, so a count overflow made up entirely of mandatory entries survives Cull unresolved and
        // must be surfaced here instead of silently exceeding the configured limit.
        if (events.Entries.Count > limits.MaxPlannedEvents)
            throw new NarratorException("There are too many mandatory Planned Events to fit within the configured limit.");
    }

    // Measures the JSON-serialized length (including property names, quoting, and escaping), not the
    // raw character count of the entry's text - the MaxPlannedEventCharacters/MaxPlannedEventsCharacters
    // settings are budgets against this serialized form, so the usable text budget is smaller than the
    // configured number suggests, and shifts if this record's shape ever changes.
    private static int SerializedLength<T>(T value) => JsonSerializer.Serialize(value).Length;
}
