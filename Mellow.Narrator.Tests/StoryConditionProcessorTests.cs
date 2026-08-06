using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class StoryConditionProcessorTests
{
    [Fact]
    public void ValidateEntry_RejectsEmptyDescription()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(StoryConditionProcessor.ValidateEntry("   ", limits));
    }

    [Fact]
    public void ValidateEntry_RejectsDescriptionExceedingConfiguredLimit()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.NotNull(StoryConditionProcessor.ValidateEntry(new string('x', limits.MaxConditionDescriptionCharacters + 1), limits));
    }

    [Fact]
    public void ValidateEntry_AcceptsAValidDescription()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        Assert.Null(StoryConditionProcessor.ValidateEntry("Defeat the dragon.", limits));
    }

    [Fact]
    public void ResolveInitial_AssignsIdsAndTrimsDescriptions()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var proposals = new[]
        {
            new ProposedStoryCondition("  Defeat the dragon.  ", false),
            new ProposedStoryCondition("The betrayal is discovered.", true)
        };

        var conditions = StoryConditionProcessor.ResolveInitial(proposals, Guid.NewGuid, limits);

        Assert.Equal(2, conditions.Entries.Count);
        var visible = Assert.Single(conditions.Entries, x => !x.Secret);
        Assert.Equal("Defeat the dragon.", visible.Description);
        Assert.NotEqual(Guid.Empty, visible.Id);
        var secret = Assert.Single(conditions.Entries, x => x.Secret);
        Assert.Equal("The betrayal is discovered.", secret.Description);
    }

    [Fact]
    public void ResolveInitial_UsesInjectedIdFactory()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var proposals = new[] { new ProposedStoryCondition("Defeat the dragon.", false) };

        var conditions = StoryConditionProcessor.ResolveInitial(proposals, () => id, limits);

        Assert.Equal(id, Assert.Single(conditions.Entries).Id);
    }

    [Fact]
    public void ResolveInitial_ThrowsOnInvalidProposal()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var proposals = new[] { new ProposedStoryCondition("   ", false) };

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ResolveInitial(proposals, Guid.NewGuid, limits));
    }

    [Fact]
    public void IsWithinLimits_RejectsTooManyEntries()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var conditions = new StoryConditions(Enumerable.Range(0, limits.MaxConditions + 1)
            .Select(i => new StoryCondition(Guid.NewGuid(), $"Condition {i}", false)).ToArray());

        Assert.False(StoryConditionProcessor.IsWithinLimits(conditions, limits));
    }

    [Fact]
    public void IsWithinLimits_RejectsEntryExceedingDescriptionLimit()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var conditions = new StoryConditions([new(Guid.NewGuid(), new string('x', limits.MaxConditionDescriptionCharacters + 1), false)]);

        Assert.False(StoryConditionProcessor.IsWithinLimits(conditions, limits));
    }

    [Fact]
    public void IsWithinLimits_AcceptsConditionsWithinBothLimits()
    {
        var limits = NarratorDefaults.Create().ContentLimits;
        var conditions = new StoryConditions([new(Guid.NewGuid(), "Defeat the dragon.", false)]);

        Assert.True(StoryConditionProcessor.IsWithinLimits(conditions, limits));
    }

    [Fact]
    public void ApplyTurn_RevealsAndMeetsConditionsInOneTurn()
    {
        var visible = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var secret = new StoryCondition(Guid.NewGuid(), "The betrayal is discovered.", true);
        var conditions = new StoryConditions([visible, secret]);

        var (revealed, met) = StoryConditionProcessor.ApplyTurn(conditions, [], [], [visible.Id], [visible.Id, secret.Id]);

        Assert.Equal(visible.Id, Assert.Single(revealed));
        Assert.Equal(2, met.Count);
        Assert.Contains(visible.Id, met);
        Assert.Contains(secret.Id, met);
    }

    [Fact]
    public void ApplyTurn_AllowsMeetingASecretConditionWithoutRevealingIt()
    {
        var secret = new StoryCondition(Guid.NewGuid(), "The betrayal is discovered.", true);
        var conditions = new StoryConditions([secret]);

        var (revealed, met) = StoryConditionProcessor.ApplyTurn(conditions, [], [], [], [secret.Id]);

        Assert.Empty(revealed);
        Assert.Equal(secret.Id, Assert.Single(met));
    }

    [Fact]
    public void ApplyTurn_ReturnsEmptyWhenNothingIsReportedThisTurn()
    {
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        var (revealed, met) = StoryConditionProcessor.ApplyTurn(conditions, [], [], [], []);

        Assert.Empty(revealed);
        Assert.Empty(met);
    }

    [Fact]
    public void ApplyTurn_ThrowsOnUnknownRevealedId()
    {
        var conditions = StoryConditions.Empty;

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [], [Guid.NewGuid()], []));
    }

    [Fact]
    public void ApplyTurn_ThrowsOnUnknownMetId()
    {
        var conditions = StoryConditions.Empty;

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [], [], [Guid.NewGuid()]));
    }

    [Fact]
    public void ApplyTurn_ThrowsWhenTryingToRevealASecretCondition()
    {
        var secret = new StoryCondition(Guid.NewGuid(), "The betrayal is discovered.", true);
        var conditions = new StoryConditions([secret]);

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [], [secret.Id], []));
    }

    [Fact]
    public void ApplyTurn_ThrowsOnDuplicateRevealedIdWithinTheSameCall()
    {
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [], [entry.Id, entry.Id], []));
    }

    [Fact]
    public void ApplyTurn_ThrowsOnDuplicateMetIdWithinTheSameCall()
    {
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [], [], [entry.Id, entry.Id]));
    }

    [Fact]
    public void ApplyTurn_ThrowsWhenReRevealingAnAlreadyRevealedCondition()
    {
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [entry.Id], [], [entry.Id], []));
    }

    [Fact]
    public void ApplyTurn_ThrowsWhenReReportingAnAlreadyMetCondition()
    {
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        Assert.Throws<NarratorException>(() => StoryConditionProcessor.ApplyTurn(conditions, [], [entry.Id], [], [entry.Id]));
    }

    [Fact]
    public void ApplyTurn_AllowsMeetingAConditionThatWasRevealedInAnEarlierTurn()
    {
        // Being already-revealed only blocks re-revealing; it must not block later meeting the same
        // condition - that is the normal lifecycle (reveal now, meet later).
        var entry = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var conditions = new StoryConditions([entry]);

        var (revealed, met) = StoryConditionProcessor.ApplyTurn(conditions, [entry.Id], [], [], [entry.Id]);

        Assert.Empty(revealed);
        Assert.Equal(entry.Id, Assert.Single(met));
    }
}
