using Mellow.Narrator.Core;

namespace Mellow.Narrator.Services;

// The Hybrid UI uses route documents rather than native tabs, but persists the same durable workspace
// concepts: recent documents, unsent drafts, and an interruption marker for a request that could not
// survive application shutdown.
public sealed class HybridWorkspace(IWorkspaceStateStore store)
{
    private WorkspaceState _state = WorkspaceState.Empty;
    private bool _initialized;
    public IReadOnlyList<OpenTabState> Recent => _state.Tabs.OrderBy(x => x.Position).ToArray();
    public string? InterruptedMessage { get; private set; }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _state = await store.LoadAsync();
        var interrupted = _state.Tabs.Select(x => x.PendingOperation).FirstOrDefault(x => x is not null);
        if (interrupted is not null)
        {
            InterruptedMessage = $"The previous {Name(interrupted.Type)} was interrupted. Open the affected document and retry.";
            await ClearPendingAsync(interrupted.OperationId);
        }
    }

    public StoryPromptDraft? StoryPromptDraft => _state.Tabs.FirstOrDefault(x => x.Type == TabType.StoryPrompt)?.StoryPromptDraft;
    public PlayStoryTabState? PlayDraft(Guid storyId) => _state.Tabs.FirstOrDefault(x => x.Type == TabType.PlayStory && x.DurableRecordId == storyId)?.PlayStoryTabState;
    public Task SaveStoryPromptDraftAsync(StoryPromptDraft draft) => TrackAsync(TabType.StoryPrompt, null, draft);
    public Task SavePlayDraftAsync(Guid storyId, string action) => TrackAsync(TabType.PlayStory, storyId, playDraft: new(action));

    public async Task TrackAsync(TabType type, Guid? recordId, StoryPromptDraft? draft = null, PlayStoryTabState? playDraft = null)
    {
        var existing = _state.Tabs.FirstOrDefault(x => x.Type == type && x.DurableRecordId == recordId);
        var tab = new OpenTabState(existing?.TabId ?? Guid.NewGuid(), type, 0, recordId, draft, playDraft, existing?.PendingOperation);
        // Open stories are durable user documents. Do not evict or reorder them merely because the
        // player revisits one: preserve an existing story's position and append a newly opened story.
        var tabs = _state.Tabs.OrderBy(x => x.Position).Where(x => x.TabId != tab.TabId).ToList();
        if (type == TabType.PlayStory)
        {
            var position = existing is null ? tabs.Count : Math.Min(existing.Position, tabs.Count);
            tabs.Insert(position, tab);
        }
        else
        {
            tabs.Insert(0, tab);
        }
        var orderedTabs = tabs.Select((item, index) => item with { Position = index }).ToArray();
        _state = new(tab.TabId, orderedTabs);
        await store.SaveAsync(_state);
    }

    public async Task BeginAsync(TabType type, Guid? recordId, PendingOperationType operation, int? expectedTurn = null)
    {
        await TrackAsync(type, recordId);
        var tabs = _state.Tabs.Select(x => x.Type == type && x.DurableRecordId == recordId
            ? x with { PendingOperation = new(Guid.NewGuid(), operation, recordId, expectedTurn, DateTimeOffset.UtcNow) }
            : x).ToArray();
        _state = _state with { Tabs = tabs };
        await store.SaveAsync(_state);
    }

    public async Task CompleteAsync(TabType type, Guid? recordId)
    {
        var tabs = _state.Tabs.Select(x => x.Type == type && x.DurableRecordId == recordId ? x with { PendingOperation = null } : x).ToArray();
        _state = _state with { Tabs = tabs };
        await store.SaveAsync(_state);
    }

    public async Task CloseStoryAsync(Guid storyId)
    {
        var closingActiveStory = _state.Tabs.Any(x => x.TabId == _state.ActiveTabId && x.Type == TabType.PlayStory && x.DurableRecordId == storyId);
        var tabs = _state.Tabs
            .OrderBy(x => x.Position)
            .Where(x => x.Type != TabType.PlayStory || x.DurableRecordId != storyId)
            .Select((item, index) => item with { Position = index })
            .ToArray();
        _state = new(closingActiveStory ? tabs.FirstOrDefault()?.TabId ?? Guid.Empty : _state.ActiveTabId, tabs);
        await store.SaveAsync(_state);
    }

    private async Task ClearPendingAsync(Guid id)
    {
        _state = _state with { Tabs = _state.Tabs.Select(x => x.PendingOperation?.OperationId == id ? x with { PendingOperation = null } : x).ToArray() };
        await store.SaveAsync(_state);
    }
    private static string Name(PendingOperationType value) => string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
}
