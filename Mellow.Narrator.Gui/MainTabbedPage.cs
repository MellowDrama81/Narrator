using Mellow.Narrator.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Mellow.Narrator.Gui;

public sealed class MainTabbedPage : TabbedPage
{
    private readonly IServiceProvider _services;
    private readonly IWorkspaceStateStore _workspace;
    private readonly SettingsPage _settingsPage;
    private bool _restored;
    private CancellationTokenSource? _saveDebounce;

    public MainTabbedPage(IServiceProvider services, IWorkspaceStateStore workspace)
    {
        _services = services;
        _workspace = workspace;
        Title = "Mellow Narrator";
        _settingsPage = new SettingsPage(
            _services.GetRequiredService<INarratorApplication>(),
            _services.GetRequiredService<ITrashStore>(),
            this);
        AddFixed(_settingsPage, TabType.Settings);
        AddFixed(new StoryDefinitionListPage(
            _services.GetRequiredService<IStoryDefinitionRepository>(),
            _services.GetRequiredService<INarratorApplication>(),
            this), TabType.StoryDefinitionList);
        AddFixed(new StoryStateListPage(
            _services.GetRequiredService<IStoryStateRepository>(),
            _services.GetRequiredService<INarratorApplication>(),
            this), TabType.PlayStoryList);
        CurrentPageChanged += async (_, _) => await SaveWorkspaceAsync();
        Loaded += async (_, _) => await RestoreAsync();
    }

    public void OpenDefinition(Guid id)
    {
        var existing = Find(TabType.StoryDefinition, id);
        if (existing is not null) { CurrentPage = existing; return; }
        AddDynamic(new StoryDefinitionPage(
            id,
            _services.GetRequiredService<IStoryDefinitionRepository>(),
            _services.GetRequiredService<INarratorApplication>(),
            this), TabType.StoryDefinition, id);
    }

    public void OpenSettings() => CurrentPage = Children.OfType<NarratorNavigationPage>().First(x => x.TabType == TabType.Settings);

    public async Task<bool> CloseDefinitionTabsForDeletionAsync(Guid definitionId)
    {
        var related = Children.OfType<NarratorNavigationPage>()
            .Where(x => x.RecordId == definitionId && x.TabType is TabType.StoryDefinition or TabType.StoryPrompt or TabType.StartStory)
            .ToArray();
        foreach (var temporary in related.Where(x => x.TabType is TabType.StoryPrompt or TabType.StartStory))
        {
            if (!await DisplayAlertAsync(
                    "Discard temporary progress?",
                    $"Close '{temporary.RootPage.Title}' and discard its temporary progress?",
                    "Discard",
                    "Cancel"))
                return false;
        }
        foreach (var requestPage in related.Select(x => x.RootPage).OfType<IInFlightRequestPage>())
            await requestPage.CancelInFlightRequestAsync();
        foreach (var page in related) Children.Remove(page);
        await SaveWorkspaceAsync();
        return true;
    }

    public async Task<bool> CloseStoryTabForDeletionAsync(Guid stateId)
    {
        var page = Find(TabType.PlayStory, stateId);
        if (page is null) return true;
        if (!await DisplayAlertAsync("Close open story?", "The Play Story tab must be closed before moving its durable state to Trash.", "Close", "Cancel"))
            return false;
        if (page.RootPage is IInFlightRequestPage requestPage)
            await requestPage.CancelInFlightRequestAsync();
        Children.Remove(page);
        await SaveWorkspaceAsync();
        return true;
    }

    public void OpenPrompt(
        Guid? id = null,
        StoryPromptDraft? restoredDraft = null,
        PendingOperationState? restoredOperation = null)
    {
        if (id is not null)
        {
            var existing = Find(TabType.StoryPrompt, id);
            if (existing is not null) { CurrentPage = existing; return; }
        }
        AddDynamic(new StoryPromptPage(id, _services.GetRequiredService<IStoryDefinitionRepository>(),
            _services.GetRequiredService<INarratorApplication>(), this, restoredDraft, restoredOperation), TabType.StoryPrompt, id);
    }

    public void OpenStart(
        Guid id,
        StartStoryDraft? restoredDraft = null,
        PendingOperationState? restoredOperation = null) =>
        AddDynamic(new StartStoryPage(id, _services.GetRequiredService<IStoryDefinitionRepository>(),
            _services.GetRequiredService<INarratorApplication>(), this, restoredDraft, restoredOperation), TabType.StartStory, id);

    public void OpenPlay(
        Guid id,
        PlayStoryTabState? restoredState = null,
        PendingOperationState? restoredOperation = null)
    {
        var existing = Find(TabType.PlayStory, id);
        if (existing is not null) { CurrentPage = existing; return; }
        AddDynamic(new PlayStoryPage(id, _services.GetRequiredService<IStoryStateRepository>(),
            _services.GetRequiredService<INarratorApplication>(), this, restoredState, restoredOperation), TabType.PlayStory, id);
    }

    public async Task CloseCurrentAsync()
    {
        if (CurrentPage is not NarratorNavigationPage page || page.IsFixed) return;
        if (page.RootPage is ICloseGuardPage guard && !await guard.CanCloseAsync()) return;
        Children.Remove(page);
        await SaveWorkspaceAsync();
    }

    public async Task ReplaceCurrentWithDefinitionAsync(Guid id)
    {
        var previous = CurrentPage;
        if (previous is NarratorNavigationPage page && !page.IsFixed) Children.Remove(previous);
        OpenDefinition(id);
        await SaveWorkspaceAsync();
    }

    public async Task ReplaceCurrentWithPlayAsync(Guid id)
    {
        var previous = CurrentPage;
        if (previous is NarratorNavigationPage page && !page.IsFixed) Children.Remove(previous);
        OpenPlay(id);
        await SaveWorkspaceAsync();
    }

    public async Task ReplaceCurrentWithStartAsync(Guid id)
    {
        var previous = CurrentPage;
        if (previous is NarratorNavigationPage page && !page.IsFixed) Children.Remove(previous);
        OpenStart(id);
        await SaveWorkspaceAsync();
    }

    public Task ShowManageTabsAsync() => Navigation.PushModalAsync(new NavigationPage(new ManageTabsPage(this)));

    internal IReadOnlyList<NarratorNavigationPage> UnlockedTabs =>
        Children.OfType<NarratorNavigationPage>().Where(x => !x.IsFixed).ToArray();

    internal async Task MoveAsync(NarratorNavigationPage page, int delta)
    {
        var oldIndex = Children.IndexOf(page);
        var newIndex = Math.Clamp(oldIndex + delta, 3, Children.Count - 1);
        if (newIndex == oldIndex) return;
        var current = CurrentPage;
        Children.RemoveAt(oldIndex);
        Children.Insert(newIndex, page);
        CurrentPage = current;
        await SaveWorkspaceAsync();
    }

    internal void ScheduleWorkspaceSave()
    {
        _saveDebounce?.Cancel();
        _saveDebounce = new();
        _ = SaveAfterDelayAsync(_saveDebounce.Token);
    }

    internal Task SaveWorkspaceNowAsync() => SaveWorkspaceAsync();

    internal bool IsStoryRequestInFlight(Guid stateId) =>
        Find(TabType.PlayStory, stateId)?.RootPage is IInFlightRequestPage { HasInFlightRequest: true };

    internal bool IsStoryOpen(Guid stateId) => Find(TabType.PlayStory, stateId) is not null;

    internal async Task CancelInFlightRequestsAsync()
    {
        foreach (var page in Children.OfType<NarratorNavigationPage>())
        {
            if (page.RootPage is IInFlightRequestPage request)
                await request.CancelInFlightRequestAsync(true);
        }
        await SaveWorkspaceAsync();
    }

    private void AddFixed(Page page, TabType type) => AddPage(page, Guid.NewGuid(), type, null, false);
    private void AddDynamic(Page page, TabType type, Guid? recordId) => AddPage(page, Guid.NewGuid(), type, recordId, true);

    private void AddPage(Page page, Guid tabId, TabType type, Guid? recordId, bool activate)
    {
        var nav = new NarratorNavigationPage(page, tabId, type, recordId);
        page.ToolbarItems.Add(new ToolbarItem("Manage Tabs", null, async () => await ShowManageTabsAsync()));
        if (!nav.IsFixed) page.ToolbarItems.Add(new ToolbarItem("Close", null, async () => await CloseCurrentAsync()));
        Children.Add(nav);
        if (activate) CurrentPage = nav;
    }

    private NarratorNavigationPage? Find(TabType type, Guid? id) =>
        Children.OfType<NarratorNavigationPage>().FirstOrDefault(x => x.TabType == type && x.RecordId == id);

    private async Task RestoreAsync()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            var state = await _workspace.LoadAsync();
            var restoredPages = new Dictionary<Guid, NarratorNavigationPage>();
            foreach (var fixedTab in state.Tabs.Where(x => x.Type is TabType.Settings or TabType.StoryDefinitionList or TabType.PlayStoryList))
            {
                var fixedPage = Children.OfType<NarratorNavigationPage>()
                    .FirstOrDefault(x => x.IsFixed && x.TabType == fixedTab.Type);
                if (fixedPage is not null) restoredPages[fixedTab.TabId] = fixedPage;
            }
            _settingsPage.RestoreInterruptedOperation(
                state.Tabs.FirstOrDefault(x => x.Type == TabType.Settings)?.PendingOperation);
            foreach (var tab in state.Tabs.OrderBy(x => x.Position).Where(x => x.Position >= 3))
            {
                if (tab.Type is TabType.StoryDefinition or TabType.StartStory or TabType.StoryPrompt &&
                    tab.DurableRecordId is { } referencedDefinition &&
                    await _services.GetRequiredService<IStoryDefinitionRepository>().GetAsync(referencedDefinition) is null)
                    continue;
                if (tab.Type == TabType.PlayStory &&
                    (tab.DurableRecordId is not { } referencedState ||
                     await _services.GetRequiredService<IStoryStateRepository>().GetAsync(referencedState) is null))
                    continue;
                if (tab.PendingOperation is { Type: PendingOperationType.GenerateStoryDefinition, TargetRecordId: { } definitionId } definitionOperation &&
                    await _services.GetRequiredService<IStoryDefinitionRepository>().GetAsync(definitionId) is { } completedDefinition &&
                    (tab.StoryPromptDraft?.SourceStoryDefinitionId != definitionId ||
                        completedDefinition.UpdatedAtUtc > definitionOperation.StartedAtUtc))
                {
                    OpenDefinition(definitionId);
                    if (CurrentPage is NarratorNavigationPage completedPage)
                        restoredPages[tab.TabId] = completedPage;
                    continue;
                }
                if (tab.PendingOperation is { Type: PendingOperationType.GenerateOpeningScene, TargetRecordId: { } stateId } &&
                    await _services.GetRequiredService<IStoryStateRepository>().GetAsync(stateId) is not null)
                {
                    OpenPlay(stateId);
                    if (CurrentPage is NarratorNavigationPage completedPage)
                        restoredPages[tab.TabId] = completedPage;
                    continue;
                }
                var playState = tab.PlayStoryTabState;
                var pending = tab.PendingOperation;
                if (tab.Type == TabType.PlayStory &&
                    tab.DurableRecordId is { } pendingStateId &&
                    pending is { Type: PendingOperationType.GenerateStoryTurn, ExpectedTurnSequence: { } expected } &&
                    await _services.GetRequiredService<IStoryStateRepository>().GetAsync(pendingStateId) is { } durable &&
                    durable.LastCommittedTurnSequence >= expected)
                {
                    playState = new("");
                    pending = null;
                }
                var restored = false;
                if (tab.Type == TabType.StoryPrompt)
                {
                    OpenPrompt(tab.DurableRecordId, tab.StoryPromptDraft, pending);
                    restored = true;
                }
                else if (tab.DurableRecordId is { } id && tab.Type == TabType.StoryDefinition)
                {
                    OpenDefinition(id);
                    restored = true;
                }
                else if (tab.DurableRecordId is { } playId && tab.Type == TabType.PlayStory)
                {
                    OpenPlay(playId, playState, pending);
                    restored = true;
                }
                else if (tab.DurableRecordId is { } startId && tab.Type == TabType.StartStory)
                {
                    OpenStart(startId, tab.StartStoryDraft, pending);
                    restored = true;
                }
                if (restored && CurrentPage is NarratorNavigationPage restoredPage)
                    restoredPages[tab.TabId] = restoredPage;
            }
            var activeTabId = WorkspaceRestoration.SelectActiveTabId(
                state,
                restoredPages.Keys.ToHashSet());
            if (activeTabId is { } restoredActiveId &&
                restoredPages.TryGetValue(restoredActiveId, out var activePage))
                CurrentPage = activePage;
            else
                CurrentPage = Children.OfType<NarratorNavigationPage>().First(x => x.TabType == TabType.Settings);
            await SaveWorkspaceAsync();
            var notices = await _services.GetRequiredService<IRecoveryNoticeStore>().ConsumeAsync();
            if (notices.Count > 0)
                await DisplayAlertAsync(
                    "Data recovery completed",
                    string.Join(Environment.NewLine, notices.Select(x => $"• {x.Message}")),
                    "OK");
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveWorkspaceAsync()
    {
        if (!_restored) return;
        var tabs = Children.OfType<NarratorNavigationPage>().Select((x, i) =>
        {
            var payload = x.RootPage as IWorkspacePayloadPage;
            return new OpenTabState(x.TabId, x.TabType, i, x.RecordId,
                payload?.StoryPromptDraft, payload?.StartStoryDraft, payload?.PlayStoryTabState, payload?.PendingOperation);
        }).ToArray();
        await _workspace.SaveAsync(new((CurrentPage as NarratorNavigationPage)?.TabId ?? Guid.Empty, tabs));
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await SaveWorkspaceAsync();
        }
        catch (OperationCanceledException) { }
    }
}
