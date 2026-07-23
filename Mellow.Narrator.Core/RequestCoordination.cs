using System.Collections.Concurrent;

namespace Mellow.Narrator.Core;

public interface IIdGenerator
{
    Guid NewId();
}

public sealed class SystemIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

public sealed class ApiConnectionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool RequiresCredentialReentry { get; private set; }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            await action();
            return true;
        }, cancellationToken);

    public void MarkCredentialReentryRequired() => RequiresCredentialReentry = true;
    public void MarkCredentialHealthy() => RequiresCredentialReentry = false;
}

public sealed class StoryRequestCoordinator
{
    private readonly ConcurrentDictionary<Guid, byte> _active = [];

    public IDisposable Enter(Guid storyStateId)
    {
        if (!_active.TryAdd(storyStateId, 0))
            throw new NarratorException("A request is already in progress for this Story State.");
        return new Lease(_active, storyStateId);
    }

    private sealed class Lease(ConcurrentDictionary<Guid, byte> active, Guid id) : IDisposable
    {
        public void Dispose() => active.TryRemove(id, out _);
    }
}
