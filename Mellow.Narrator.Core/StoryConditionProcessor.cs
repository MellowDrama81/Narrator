namespace Mellow.Narrator.Core;

// Victory/Loss Conditions are fixed at the Story Definition (see the StoryCondition comment in
// Models.cs) - unlike Story Bible entries and Planned Events, the narrator never adds, replaces, or
// removes one during play. Each turn it only reports which of the still-unmet conditions it revealed
// (wove into the narration - non-secret only) or actually met, so this processor has no Add/Replace/
// Remove/Cull machinery, just validation of those two turn-level id lists.
public static class StoryConditionProcessor
{
    public static string? ValidateEntry(string description, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > limits.MaxConditionDescriptionCharacters)
            return "A condition description is empty or exceeds the configured limit.";
        return null;
    }

    // Resolves a batch of brand-new proposals - the initial victory/loss conditions proposed alongside a
    // Story Definition - into real Story Conditions, assigning each an id via createId. No key/prerequisite
    // resolution is needed here (unlike Planned Events): conditions never reference each other.
    public static StoryConditions ResolveInitial(
        IReadOnlyList<ProposedStoryCondition> proposals, Func<Guid> createId, ContentLimitSettings limits)
    {
        foreach (var proposal in proposals)
            if (ValidateEntry(proposal.Description, limits) is { } error)
                throw new NarratorException(error);
        return new StoryConditions(proposals.Select(p => new StoryCondition(createId(), p.Description.Trim(), p.Secret)).ToArray());
    }

    public static bool IsWithinLimits(StoryConditions conditions, ContentLimitSettings limits) =>
        conditions.Entries.Count <= limits.MaxConditions
        && conditions.Entries.All(x => x.Description.Length <= limits.MaxConditionDescriptionCharacters);

    // Validates and returns this turn's newly revealed/met ids for one condition list (victory or loss).
    // alreadyRevealedIds/alreadyMetIds are the running totals from before this turn; proposedRevealedIds/
    // proposedMetIds are exactly what the model reported this turn. Throws on any rule violation: an
    // unknown id, a duplicate mention (within this turn or against the running totals), or revealing a
    // secret condition - secret ones may only ever be reported met, never revealed.
    public static (IReadOnlyList<Guid> Revealed, IReadOnlyList<Guid> Met) ApplyTurn(
        StoryConditions conditions,
        IReadOnlyList<Guid> alreadyRevealedIds,
        IReadOnlyList<Guid> alreadyMetIds,
        IReadOnlyList<Guid> proposedRevealedIds,
        IReadOnlyList<Guid> proposedMetIds)
    {
        var byId = conditions.Entries.ToDictionary(x => x.Id);
        var alreadyRevealed = alreadyRevealedIds.ToHashSet();
        var alreadyMet = alreadyMetIds.ToHashSet();

        var revealed = new List<Guid>();
        var seenRevealed = new HashSet<Guid>();
        foreach (var id in proposedRevealedIds)
        {
            if (!byId.TryGetValue(id, out var entry))
                throw new NarratorException("An unknown condition was marked revealed.");
            if (entry.Secret)
                throw new NarratorException("A secret condition cannot be marked revealed.");
            if (!seenRevealed.Add(id) || alreadyRevealed.Contains(id))
                throw new NarratorException("A condition was marked revealed more than once.");
            revealed.Add(id);
        }

        var met = new List<Guid>();
        var seenMet = new HashSet<Guid>();
        foreach (var id in proposedMetIds)
        {
            if (!byId.ContainsKey(id))
                throw new NarratorException("An unknown condition was marked met.");
            if (!seenMet.Add(id) || alreadyMet.Contains(id))
                throw new NarratorException("A condition was marked met more than once.");
            met.Add(id);
        }

        return (revealed, met);
    }
}
