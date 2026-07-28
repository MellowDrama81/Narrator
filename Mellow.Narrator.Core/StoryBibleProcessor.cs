using System.Text.Json;

namespace Mellow.Narrator.Core;

public sealed record StoryBibleApplyResult(
    StoryBible Bible,
    IReadOnlyList<Guid> RelevantEntryIds,
    IReadOnlyList<AppliedStoryBibleChange> Changes);

public static class StoryBibleProcessor
{
    public static StoryBibleApplyResult Apply(
        StoryBible current,
        IReadOnlyList<Guid> relevantIds,
        IReadOnlyList<ProposedStoryBibleUpdate> updates,
        int turnNumber,
        StoryGenerationSettings limits,
        Func<Guid>? createId = null)
    {
        createId ??= Guid.NewGuid;
        var entries = current.Entries.ToDictionary(x => x.Id);
        var relevant = relevantIds.ToHashSet();
        if (relevant.Any(x => !entries.ContainsKey(x)))
            throw new NarratorException("The model marked an unknown Story Bible entry as relevant.");

        var touched = new HashSet<Guid>();
        var changes = new List<AppliedStoryBibleChange>();
        foreach (var update in updates)
        {
            if (update.Operation == StoryBibleOperation.Add)
            {
                ValidateProposal(update.Entry);
                var id = createId();
                var after = ToEntry(id, update.Entry!, turnNumber);
                entries.Add(id, after);
                relevant.Add(id);
                changes.Add(new(StoryBibleOperation.Add, id, null, after, StoryBibleChangeSource.LlmUpdate));
                continue;
            }

            if (update.EntryId is null || !entries.TryGetValue(update.EntryId.Value, out var before))
                throw new NarratorException("A Story Bible update references an unknown entry.");
            if (!touched.Add(update.EntryId.Value))
                throw new NarratorException("A Story Bible entry was updated more than once.");

            if (update.Operation == StoryBibleOperation.Remove)
            {
                if (update.Entry is not null || relevant.Contains(before.Id))
                    throw new NarratorException("A removed Story Bible entry cannot also be relevant or contain a replacement.");
                entries.Remove(before.Id);
                changes.Add(new(StoryBibleOperation.Remove, before.Id, before, null, StoryBibleChangeSource.LlmUpdate));
            }
            else
            {
                ValidateProposal(update.Entry);
                var after = ToEntry(before.Id, update.Entry!, turnNumber);
                entries[before.Id] = after;
                relevant.Add(before.Id);
                changes.Add(new(StoryBibleOperation.Replace, before.Id, before, after, StoryBibleChangeSource.LlmUpdate));
            }
        }

        foreach (var id in relevant)
        {
            if (!entries.TryGetValue(id, out var entry)) continue;
            entries[id] = entry with { LastRelevantTurnNumber = turnNumber };
        }

        CullCount(entries, limits.MaxStoryBibleEntries, changes);
        var bible = new StoryBible(entries.Values.OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray());
        ValidateSize(bible, limits);
        return new(bible, relevant.Where(entries.ContainsKey).Order().ToArray(), changes);
    }

    public static (StoryBible Bible, IReadOnlyList<AppliedStoryBibleChange> Changes) CullToLimits(
        StoryBible current,
        StoryGenerationSettings limits)
    {
        var entries = current.Entries.ToDictionary(x => x.Id);
        var changes = new List<AppliedStoryBibleChange>();
        foreach (var entry in entries.Values.Where(x => SerializedLength(x) > limits.MaxStoryBibleEntryCharacters).ToArray())
            Remove(entries, entry, changes);
        while (entries.Count > limits.MaxStoryBibleEntries || SerializedLength(new StoryBible(entries.Values.ToArray())) > limits.MaxStoryBibleCharacters)
        {
            var candidate = entries.Values.OrderBy(x => x.Importance).ThenBy(x => x.LastRelevantTurnNumber).ThenBy(x => x.Id).FirstOrDefault();
            if (candidate is null) break;
            Remove(entries, candidate, changes);
        }
        return (new(entries.Values.OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray()), changes);
    }

    public static bool IsWithinLimits(StoryBible bible, StoryGenerationSettings limits) =>
        bible.Entries.Count <= limits.MaxStoryBibleEntries
        && bible.Entries.All(x => SerializedLength(x) <= limits.MaxStoryBibleEntryCharacters)
        && SerializedLength(bible) <= limits.MaxStoryBibleCharacters;

    public static bool IsApproachingLimits(StoryBible bible, StoryGenerationSettings limits)
    {
        var threshold = limits.StoryBibleWarningPercent / 100d;
        var largest = bible.Entries.Count == 0 ? 0 : bible.Entries.Max(SerializedLength);
        return bible.Entries.Count >= limits.MaxStoryBibleEntries * threshold ||
            largest >= limits.MaxStoryBibleEntryCharacters * threshold ||
            SerializedLength(bible) >= limits.MaxStoryBibleCharacters * threshold;
    }

    private static void CullCount(IDictionary<Guid, StoryBibleEntry> entries, int max, ICollection<AppliedStoryBibleChange> changes)
    {
        while (entries.Count > max)
        {
            var candidate = entries.Values.OrderBy(x => x.Importance).ThenBy(x => x.LastRelevantTurnNumber).ThenBy(x => x.Id).First();
            Remove(entries, candidate, changes);
        }
    }

    private static void Remove(IDictionary<Guid, StoryBibleEntry> entries, StoryBibleEntry entry, ICollection<AppliedStoryBibleChange> changes)
    {
        entries.Remove(entry.Id);
        changes.Add(new(StoryBibleOperation.Remove, entry.Id, entry, null, StoryBibleChangeSource.AutomaticCull));
    }

    private static StoryBibleEntry ToEntry(Guid id, ProposedStoryBibleEntry value, int turn) =>
        new(
            id,
            value.Category.Trim(),
            value.Name.Trim(),
            TrimFacts(value.KnownFacts),
            TrimFacts(value.SecretFacts),
            value.Importance,
            turn);

    private static IReadOnlyList<string> TrimFacts(IReadOnlyList<string> facts) =>
        facts.Select(x => x.Trim()).ToArray();

    private static void ValidateProposal(ProposedStoryBibleEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Category) || string.IsNullOrWhiteSpace(entry.Name))
            throw new NarratorException("A Story Bible entry is incomplete.");
        if (entry.KnownFacts.Count == 0 && entry.SecretFacts.Count == 0)
            throw new NarratorException("A Story Bible entry must have at least one known or secret fact.");
        if (entry.KnownFacts.Any(string.IsNullOrWhiteSpace) || entry.SecretFacts.Any(string.IsNullOrWhiteSpace))
            throw new NarratorException("A Story Bible entry has an empty fact.");
        if (entry.Importance is < 1 or > 5)
            throw new NarratorException("Story Bible importance must be from 1 to 5.");
    }

    private static void ValidateSize(StoryBible bible, StoryGenerationSettings limits)
    {
        if (bible.Entries.Any(x => SerializedLength(x) > limits.MaxStoryBibleEntryCharacters))
            throw new NarratorException("A Story Bible entry exceeds the configured character limit.");
        if (SerializedLength(bible) > limits.MaxStoryBibleCharacters)
            throw new NarratorException("The Story Bible exceeds the configured total character limit.");
    }

    private static int SerializedLength<T>(T value) => JsonSerializer.Serialize(value).Length;
}
