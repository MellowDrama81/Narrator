using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class RequestCoordinationTests
{
    [Fact]
    public void StoryRequestCoordinator_RejectsConcurrentRequestForSameState()
    {
        var coordinator = new StoryRequestCoordinator();
        var id = Guid.NewGuid();
        using var first = coordinator.Enter(id);
        Assert.Throws<NarratorException>(() => coordinator.Enter(id));
        using var other = coordinator.Enter(Guid.NewGuid());
    }

    [Fact]
    public void StoryRequestCoordinator_AllowsReentryAfterTheLeaseIsDisposed()
    {
        var coordinator = new StoryRequestCoordinator();
        var id = Guid.NewGuid();
        using (coordinator.Enter(id)) { }

        using var second = coordinator.Enter(id);
    }
}
