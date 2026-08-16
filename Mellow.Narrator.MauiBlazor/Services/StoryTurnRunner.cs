using Mellow.Narrator.Core;

namespace Mellow.Narrator.MauiBlazor.Services;

// Route components are short-lived: navigating away disposes the page, but must not dispose a story
// request. This singleton owns active turns until the application service has committed or failed them.
public sealed class StoryTurnRunner(NarratorWorkspace workspace, HybridWorkspace hybridWorkspace)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task> _active = [];
    private readonly Dictionary<Guid, string> _failures = [];

    public event Action<Guid>? Changed;
    public bool IsRunning(Guid storyId) { lock (_gate) return _active.ContainsKey(storyId); }
    public string? Failure(Guid storyId) { lock (_gate) return _failures.GetValueOrDefault(storyId); }

    public Task StartAsync(Guid storyId, string action, int? expectedTurn)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(storyId)) throw new InvalidOperationException("A turn is already being generated for this story.");
            _failures.Remove(storyId);
            var task = Task.Run(async () =>
            {
                try
                {
                    await hybridWorkspace.BeginAsync(TabType.PlayStory, storyId, PendingOperationType.GenerateStoryTurn, expectedTurn);
                    await workspace.PlayAsync(storyId, action);
                }
                catch (Exception ex)
                {
                    lock (_gate) _failures[storyId] = ex.Message;
                }
                finally
                {
                    await hybridWorkspace.CompleteAsync(TabType.PlayStory, storyId);
                    lock (_gate) _active.Remove(storyId);
                    Changed?.Invoke(storyId);
                }
            });
            _active.Add(storyId, task);
        }
        Changed?.Invoke(storyId);
        return Task.CompletedTask;
    }
}
