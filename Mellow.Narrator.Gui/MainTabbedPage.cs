using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class MainTabbedPage : TabbedPage
{
    private readonly INarratorApplication _app;
    private readonly ITrashStore _trash;
    private readonly IStoryDefinitionRepository _definitions;
    private readonly IStoryStateRepository _states;
    private readonly IRecoveryNoticeStore _recoveryNotices;
    private readonly IWorkspaceStateStore _workspace;
    private readonly SettingsPage _settingsPage;
    private readonly StoryDefinitionListPage _definitionListPage;
    private bool _restored;
    private CancellationTokenSource? _saveDebounce;

    public MainTabbedPage(
        INarratorApplication app,
        ITrashStore trash,
        IStoryDefinitionRepository definitions,
        IStoryStateRepository states,
        IRecoveryNoticeStore recoveryNotices,
        IWorkspaceStateStore workspace)
    {
        _app = app;
        _trash = trash;
        _definitions = definitions;
        _states = states;
        _recoveryNotices = recoveryNotices;
        _workspace = workspace;
        Title = "Mellow Narrator";
        _settingsPage = new SettingsPage(_app, _trash, this);
        AddFixed(_settingsPage, TabType.Settings);
        _definitionListPage = new StoryDefinitionListPage(_definitions, _app, this);
        AddFixed(_definitionListPage, TabType.StoryDefinitionList);
        AddFixed(new StoryStateListPage(_states, _app, this), TabType.PlayStoryList);
        CurrentPageChanged += async (_, _) => await SaveWorkspaceAsync();
        Loaded += async (_, _) => await RestoreAsync();
    }

    public void OpenDefinition(Guid id, PendingOperationState? restoredOperation = null)
    {
        var existing = Find(TabType.StoryDefinition, id);
        if (existing is not null) { CurrentPage = existing; return; }
        AddDynamic(new StoryDefinitionPage(id, _definitions, _app, this, restoredOperation), TabType.StoryDefinition, id);
    }

    public void OpenSettings() => CurrentPage = Children.OfType<NarratorNavigationPage>().First(x => x.TabType == TabType.Settings);

    public async Task<bool> CloseDefinitionTabsForDeletionAsync(Guid definitionId)
    {
        var related = Children.OfType<NarratorNavigationPage>()
            .Where(x => x.RecordId == definitionId && x.TabType is TabType.StoryDefinition or TabType.StoryPrompt)
            .ToArray();
        foreach (var temporary in related.Where(x => x.TabType is TabType.StoryPrompt))
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
        AddDynamic(new StoryPromptPage(id, _definitions, _app, this, restoredDraft, restoredOperation), TabType.StoryPrompt, id);
    }

    /// <summary>
    /// Resolves a Story Definition's Story Bible against current limits (offering a cull if needed),
    /// generates the opening scene, and navigates to the resulting Play Story tab. Returns null if the
    /// user backed out of a limits/cull prompt; throws on a genuine failure so the caller can offer retry.
    /// </summary>
    public async Task<StoryState?> StartStoryAsync(Guid definitionId, Guid targetStateId, bool replaceCurrent, CancellationToken cancellationToken = default)
    {
        var source = await _definitions.GetAsync(definitionId) ?? throw new NarratorException("Story Definition not found.");
        var settings = await _app.GetSettingsAsync();
        if (!StoryBibleProcessor.IsWithinLimits(source.InitialStoryBible, settings.StoryGeneration))
        {
            var choice = await DisplayActionSheetAsync(
                "The Story Bible exceeds current limits.",
                "Cancel",
                null,
                "Increase Limits",
                "Automatically Cull");
            if (choice == "Increase Limits") { OpenSettings(); return null; }
            if (choice != "Automatically Cull") return null;
            var preview = StoryBibleProcessor.CullToLimits(source.InitialStoryBible, settings.StoryGeneration);
            var names = string.Join(Environment.NewLine, preview.Changes.Select(x => $"• {x.Before?.Name}"));
            if (!await DisplayAlertAsync("Cull Story Bible?", $"These entries will be removed:\n{names}", "Cull", "Cancel")) return null;
            source = await _app.CullDefinitionAsync(definitionId);
        }
        if (!PlannedEventProcessor.IsWithinLimits(source.InitialPlannedEvents, settings.StoryGeneration))
        {
            var choice = await DisplayActionSheetAsync(
                "Planned Events exceed current limits.",
                "Cancel",
                null,
                "Increase Limits",
                "Automatically Cull");
            if (choice == "Increase Limits") { OpenSettings(); return null; }
            if (choice != "Automatically Cull") return null;
            var preview = PlannedEventProcessor.CullToLimits(source.InitialPlannedEvents, settings.StoryGeneration);
            var descriptions = string.Join(Environment.NewLine, preview.Changes.Select(x => $"• {x.Before?.Description}"));
            if (!await DisplayAlertAsync("Cull Planned Events?", $"These events will be removed:\n{descriptions}", "Cull", "Cancel")) return null;
            source = await _app.CullDefinitionAsync(definitionId);
        }
        var definition = new StoryDefinitionSnapshot(source.Title, source.StoryPrompt, source.InitialEventsPrompt, source.InitialStoryBible, source.InitialPlannedEvents);
        var draft = new StartStoryDraft(definitionId, definition);
        var result = await _app.StartStoryAsync(draft, targetStateId, cancellationToken);
        if (replaceCurrent) await ReplaceCurrentWithPlayAsync(result.State.Id);
        else OpenPlay(result.State.Id);
        return result.State;
    }

    public void OpenPlay(
        Guid id,
        PlayStoryTabState? restoredState = null,
        PendingOperationState? restoredOperation = null)
    {
        var existing = Find(TabType.PlayStory, id);
        if (existing is not null) { CurrentPage = existing; return; }
        AddDynamic(new PlayStoryPage(id, _states, _app, this, restoredState, restoredOperation), TabType.PlayStory, id);
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

    public Task ShowManageTabsAsync() => Navigation.PushModalAsync(new NavigationPage(new ManageTabsPage(this)));

    internal IReadOnlyList<NarratorNavigationPage> UnlockedTabs =>
        Children.OfType<NarratorNavigationPage>().Where(x => !x.IsFixed).ToArray();

    internal async Task MoveAsync(NarratorNavigationPage page, int delta)
    {
        var oldIndex = Children.IndexOf(page);
        var newIndex = Math.Clamp(oldIndex + delta, FixedTabTypes.Types.Count, Children.Count - 1);
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
            foreach (var fixedTab in state.Tabs.Where(x => FixedTabTypes.Types.Contains(x.Type)))
            {
                var fixedPage = Children.OfType<NarratorNavigationPage>()
                    .FirstOrDefault(x => x.IsFixed && x.TabType == fixedTab.Type);
                if (fixedPage is not null) restoredPages[fixedTab.TabId] = fixedPage;
            }
            _settingsPage.RestoreInterruptedOperation(
                state.Tabs.FirstOrDefault(x => x.Type == TabType.Settings)?.PendingOperation);
            var listPending = state.Tabs.FirstOrDefault(x => x.Type == TabType.StoryDefinitionList)?.PendingOperation;
            if (listPending is { Type: PendingOperationType.GenerateOpeningScene, TargetRecordId: { } listStateId } &&
                await _states.GetAsync(listStateId) is not null)
                OpenPlay(listStateId);
            else
                _definitionListPage.RestoreInterruptedOperation(listPending);
            foreach (var tab in state.Tabs.OrderBy(x => x.Position).Where(x => x.Position >= FixedTabTypes.Types.Count))
            {
                if (tab.Type is TabType.StoryDefinition or TabType.StoryPrompt &&
                    tab.DurableRecordId is { } referencedDefinition &&
                    await _definitions.GetAsync(referencedDefinition) is null)
                    continue;
                if (tab.Type == TabType.PlayStory &&
                    (tab.DurableRecordId is not { } referencedState ||
                     await _states.GetAsync(referencedState) is null))
                    continue;
                if (tab.PendingOperation is { Type: PendingOperationType.GenerateStoryDefinition, TargetRecordId: { } definitionId } definitionOperation &&
                    await _definitions.GetAsync(definitionId) is { } completedDefinition &&
                    (tab.StoryPromptDraft?.SourceStoryDefinitionId != definitionId ||
                        completedDefinition.UpdatedAtUtc > definitionOperation.StartedAtUtc))
                {
                    OpenDefinition(definitionId);
                    if (CurrentPage is NarratorNavigationPage completedPage)
                        restoredPages[tab.TabId] = completedPage;
                    continue;
                }
                if (tab.PendingOperation is { Type: PendingOperationType.GenerateOpeningScene, TargetRecordId: { } openedStateId } &&
                    await _states.GetAsync(openedStateId) is not null)
                {
                    OpenPlay(openedStateId);
                    if (CurrentPage is NarratorNavigationPage completedPlayPage)
                        restoredPages[tab.TabId] = completedPlayPage;
                    continue;
                }
                var playState = tab.PlayStoryTabState;
                var pending = tab.PendingOperation;
                if (tab.Type == TabType.PlayStory &&
                    tab.DurableRecordId is { } pendingStateId &&
                    pending is { Type: PendingOperationType.GenerateStoryTurn, ExpectedTurnSequence: { } expected } &&
                    await _states.GetAsync(pendingStateId) is { } durable &&
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
                    OpenDefinition(id, pending);
                    restored = true;
                }
                else if (tab.DurableRecordId is { } playId && tab.Type == TabType.PlayStory)
                {
                    OpenPlay(playId, playState, pending);
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
            var notices = await _recoveryNotices.ConsumeAsync();
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
        try
        {
            var tabs = Children.OfType<NarratorNavigationPage>().Select((x, i) =>
                new OpenTabState(x.TabId, x.TabType, i, x.RecordId,
                    (x.RootPage as IStoryPromptDraftPage)?.StoryPromptDraft,
                    (x.RootPage as IPlayStoryTabStatePage)?.PlayStoryTabState,
                    (x.RootPage as IPendingOperationPage)?.PendingOperation)).ToArray();
            await _workspace.SaveAsync(new((CurrentPage as NarratorNavigationPage)?.TabId ?? Guid.Empty, tabs));
        }
        // Every caller - CurrentPageChanged, the debounced SaveAfterDelayAsync, and every page's
        // SaveWorkspaceNowAsync call in their own finally blocks - relies on this never throwing, so a
        // single try/catch here protects all of them instead of needing one at each call site.
        catch (Exception ex) { await Ui.Error(this, ex); }
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
