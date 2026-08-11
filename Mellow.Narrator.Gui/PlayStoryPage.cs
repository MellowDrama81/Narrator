using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class PlayStoryPage : ContentPage, IPlayStoryTabStatePage, IPendingOperationPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid _stateId;
    private readonly IStoryStateRepository _repository;
    private readonly INarratorApplication _app;
    private readonly MainTabbedPage _tabs;
    private readonly VerticalStackLayout _narration = new() { Spacing = 12 };
    private readonly VerticalStackLayout _suggestions = new() { Spacing = 4 };
    private readonly Entry _action = new() { Placeholder = "What do you do?" };
    private readonly ActivityIndicator _busy = new();
    private readonly Grid _busyOverlay;
    private readonly Button _submit;
    private readonly Button _copy;
    private readonly VerticalStackLayout _bible = new();
    private readonly VerticalStackLayout _plannedEvents = new();
    private readonly VerticalStackLayout _summary = new();
    private readonly Label _limitWarning = new() { TextColor = Colors.DarkOrange };
    private readonly ScrollView _story;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;
    private bool _loaded;

    public PlayStoryPage(
        Guid stateId,
        IStoryStateRepository repository,
        INarratorApplication app,
        MainTabbedPage tabs,
        PlayStoryTabState? restoredState = null,
        PendingOperationState? restoredOperation = null)
    {
        _stateId = stateId;
        _repository = repository;
        _app = app;
        _tabs = tabs;
        _pendingOperation = restoredOperation;
        _action.Text = restoredState?.PendingPlayerAction ?? "";
        _copy = Ui.SecondaryButton("Copy Story", Copy);
        _submit = Ui.Button("Submit", Play);
        Title = "Play Story";
        var actionRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            Children = { _action, _submit }
        };
        Grid.SetColumn(_submit, 1);
        var interactionArea = new VerticalStackLayout { Spacing = 8, Children = { _suggestions, actionRow } };
        _busy.Color = Colors.White;
        _busyOverlay = new Grid
        {
            IsVisible = false,
            BackgroundColor = Color.FromArgb("#AA000000"),
            Children =
            {
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _busy, new Label { Text = "Writing…", TextColor = Colors.White } }
                }
            }
        };
        var interactionStack = new Grid { Children = { interactionArea, _busyOverlay } };
        _story = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Padding = new Thickness(0, 0, 16, 0),
                Children =
                {
                    _limitWarning, _narration, interactionStack,
                    Ui.Buttons(_copy, Ui.SecondaryButton("Export", Export), Ui.SecondaryButton("Export Full History", ExportHistory))
                }
            }
        };
        var sidePanelContent = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(0, 0, 16, 0),
            Children =
            {
                Ui.Heading("Story So Far"), _summary,
                Ui.Heading("Story Bible"), _bible,
                Ui.SecondaryButton("Export Bible History", ExportBibleHistory),
                Ui.Heading("Planned Events"), _plannedEvents,
                Ui.SecondaryButton("Export Planned Event History", ExportPlannedEventHistory)
            }
        };
        var sidePanel = new ScrollView { IsVisible = false, Content = sidePanelContent };
        var storyColumn = new ColumnDefinition(new GridLength(2, GridUnitType.Star));
        var sidePanelColumn = new ColumnDefinition(new GridLength(0, GridUnitType.Absolute));
        var sidePanelToggle = new Button
        {
            Text = "\U0001F4D6",
            FontSize = 18,
            Padding = new Thickness(0),
            WidthRequest = 36,
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 8, 6, 0)
        };
        var storyPane = new Grid { Children = { _story, sidePanelToggle } };
        var columns = new Grid
        {
            ColumnSpacing = 0,
            ColumnDefinitions = { storyColumn, sidePanelColumn },
            Children = { storyPane, sidePanel }
        };
        Grid.SetColumn(sidePanel, 1);
        _story.Margin = new Thickness(40, 12, 40, 12);
        sidePanelToggle.Clicked += (_, _) =>
        {
            var show = !sidePanel.IsVisible;
            sidePanel.IsVisible = show;
            sidePanelColumn.Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0, GridUnitType.Absolute);
            columns.ColumnSpacing = show ? 16 : 0;
        };
        Content = columns;
        _action.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
        _action.Completed += Play;
    }

    PlayStoryTabState? IPlayStoryTabStatePage.PlayStoryTabState => new(_action.Text ?? "");
    PendingOperationState? IPendingOperationPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        await Ui.WaitWhileAsync(() => _request is not null, TimeSpan.FromSeconds(5));
        if (marker is not null) _pendingOperation = marker;
    }

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel request?", "A story request is still in progress.", "Cancel and Close", "Keep Open")) return false;
            await ((IInFlightRequestPage)this).CancelInFlightRequestAsync();
        }
        if (string.IsNullOrWhiteSpace(_action.Text)) return true;
        return await DisplayAlertAsync("Discard pending action?", "The pending player action will be discarded. The durable Story State will remain.", "Discard", "Keep Open");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (_pendingOperation is not null && _request is null)
            {
                _pendingOperation = null;
                await _tabs.SaveWorkspaceNowAsync();
                if (await DisplayActionSheetAsync(
                        "The previous turn was interrupted. Your player action is preserved.",
                        "Cancel",
                        null,
                        "Retry") == "Retry")
                {
                    Play(null, EventArgs.Empty);
                    return;
                }
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
        // Only force-scroll to the latest turn the first time this page is shown - TabbedPage raises
        // OnAppearing on every tab switch, and a plain revisit must still refresh the content (in case
        // it changed elsewhere) without discarding the reader's scroll position every time.
        await Refresh(scrollToLatestTurn: !_loaded);
        _loaded = true;
    }

    private async Task Refresh(bool scrollToLatestTurn = false)
    {
        // Several independent callers (OnAppearing, SaveBibleAsync, Play() itself) can trigger a
        // refresh; without serializing them, two overlapping rebuilds of the same collections can
        // interleave and leave the busy overlay/controls reconciled against a stale snapshot.
        await _refreshGate.WaitAsync();
        try { await RefreshCoreAsync(scrollToLatestTurn); }
        finally { _refreshGate.Release(); }
    }

    private async Task RefreshCoreAsync(bool scrollToLatestTurn)
    {
        View? latestTurnAnchor = null;
        var completed = false;
        try
        {
            var state = await _repository.GetAsync(_stateId) ?? throw new NarratorException("Story State not found.");
            var settings = await _app.GetSettingsAsync();
            if (!StoryBibleProcessor.IsWithinLimits(state.CurrentStoryBible, settings.StoryGeneration))
            {
                var choice = await DisplayActionSheetAsync("Story Bible exceeds current limits", "Cancel", null, "Increase Limits", "Automatically Cull");
                if (choice == "Increase Limits") { _tabs.OpenSettings(); return; }
                if (choice != "Automatically Cull") return;
                var preview = StoryBibleProcessor.CullToLimits(state.CurrentStoryBible, settings.StoryGeneration);
                var names = string.Join(Environment.NewLine, preview.Changes.Select(x => $"• {x.Before?.Name}"));
                if (!await DisplayAlertAsync("Cull Story Bible?", $"These entries will be removed:\n{names}", "Cull", "Cancel")) return;
                state = await _app.CullStoryStateAsync(_stateId);
            }
            if (!PlannedEventProcessor.IsWithinLimits(state.CurrentPlannedEvents, settings.StoryGeneration))
            {
                var choice = await DisplayActionSheetAsync("Planned Events exceed current limits", "Cancel", null, "Increase Limits", "Automatically Cull");
                if (choice == "Increase Limits") { _tabs.OpenSettings(); return; }
                if (choice != "Automatically Cull") return;
                var preview = PlannedEventProcessor.CullToLimits(state.CurrentPlannedEvents, settings.StoryGeneration);
                var descriptions = string.Join(Environment.NewLine, preview.Changes.Select(x => $"• {x.Before?.Description}"));
                if (!await DisplayAlertAsync("Cull Planned Events?", $"These events will be removed:\n{descriptions}", "Cull", "Cancel")) return;
                state = await _app.CullStoryStateAsync(_stateId);
            }
            Title = state.Label;
            if (Parent is NavigationPage navigation) navigation.Title = Title;
            _limitWarning.Text = StoryBibleProcessor.IsApproachingLimits(state.CurrentStoryBible, settings.StoryGeneration)
                ? "The Story Bible is approaching one or more configured limits."
                : PlannedEventProcessor.IsApproachingLimits(state.CurrentPlannedEvents, settings.StoryGeneration)
                    ? "Planned Events are approaching one or more configured limits."
                    : "";
            _narration.Children.Clear();
            var turns = await _repository.GetTurnsAsync(
                _stateId,
                Math.Max(1, settings.StoryGeneration.RecentTurnCount));
            foreach (var turn in turns)
            {
                View? anchor = null;
                if (turn.PlayerAction is not null)
                {
                    var actionLabel = new Label { Text = $"> {turn.PlayerAction}", FontAttributes = FontAttributes.Italic };
                    _narration.Children.Add(actionLabel);
                    anchor = actionLabel;
                }
                foreach (var paragraph in SplitParagraphs(turn.Narration))
                {
                    var narrationLabel = new Label { Text = paragraph, FontSize = 17 };
                    _narration.Children.Add(narrationLabel);
                    anchor ??= narrationLabel;
                }
                latestTurnAnchor = anchor;
            }
            var last = turns.LastOrDefault();
            _suggestions.Children.Clear();
            foreach (var suggestion in last?.SuggestedActions ?? [])
            {
                var button = Ui.Button(suggestion, (_, _) =>
                {
                    _action.Text = suggestion;
                    Play(null, EventArgs.Empty);
                });
                _suggestions.Children.Add(button);
            }
            _summary.Children.Clear();
            _summary.Children.Add(BuildSummaryEditor(state.StorySummary, settings.ContentLimits.MaxStorySummaryCharacters));
            _bible.Children.Clear();
            _bible.Children.Add(StoryBibleView.Create(this, state.CurrentStoryBible, settings.ContentLimits, state.LastCommittedTurnSequence, SaveBibleAsync, alwaysExpanded: true));
            _plannedEvents.Children.Clear();
            _plannedEvents.Children.Add(PlannedEventsView.Create(this, state.CurrentPlannedEvents, settings.ContentLimits, state.LastCommittedTurnSequence, SavePlannedEventsAsync, alwaysExpanded: true));
            completed = true;
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
        finally
        {
            var inFlight = _request is not null;
            _busyOverlay.IsVisible = inFlight;
            _action.IsEnabled = !inFlight;
            _submit.IsEnabled = !inFlight;
            SetSuggestionsEnabled(!inFlight);
        }
        if (completed && scrollToLatestTurn && latestTurnAnchor is not null)
            await ScrollToTopAsync(latestTurnAnchor);
    }

    private static string[] SplitParagraphs(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task ScrollToTopAsync(VisualElement anchor)
    {
        // Height stays 0 until the platform has actually arranged this element with the newly
        // added siblings above it; IsLoaded/Loaded fire on attachment, before that arrange pass runs.
        for (var attempt = 0; attempt < 20 && anchor.Height <= 0; attempt++)
            await Task.Delay(15);
        var y = 0.0;
        Element? current = anchor;
        while (current is VisualElement element && current != _story)
        {
            y += element.Y;
            current = current.Parent;
        }
        var scroll = _story.ScrollToAsync(0, Math.Max(0, y), false);
        try
        {
            await scroll.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            // Auto-scrolling is presentational only. A platform scroll task must never retain the
            // refresh gate or keep a completed story request looking active indefinitely.
            Ui.Warning("Timed out while scrolling to the latest story turn.");
            _ = ObserveLateScrollFailureAsync(scroll);
        }
        catch (Exception ex)
        {
            Ui.Warning(ex, "Could not scroll to the latest story turn.");
        }
    }

    private static async Task ObserveLateScrollFailureAsync(Task scroll)
    {
        try { await scroll; }
        catch (Exception ex) { Ui.Warning(ex, "The delayed story scroll failed."); }
    }

    // The narrator rewrites this every turn (see the StoryState.StorySummary comment in Models.cs), so
    // this editor exists mainly as a safety valve: a human can read and, if it has drifted from the
    // actual story, correct it directly, the same manual-override pattern already used for the Story
    // Bible and Planned Events.
    private View BuildSummaryEditor(string summary, int maxLength)
    {
        var editor = new Editor
        {
            Text = summary,
            Placeholder = "(empty until the opening scene establishes it)",
            MaxLength = maxLength,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 80
        };
        return new VerticalStackLayout
        {
            Spacing = 4,
            Children = { editor, Ui.SecondaryButton("Save Summary", async (_, _) => await SaveStorySummaryAsync(editor.Text ?? "")) }
        };
    }

    private async Task SaveStorySummaryAsync(string next)
    {
        try { await _app.UpdateStorySummaryAsync(_stateId, next); RefreshAfterEditorClick(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveBibleAsync(StoryBible next)
    {
        try { await _app.UpdateCurrentStoryBibleAsync(_stateId, next); RefreshAfterEditorClick(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SavePlannedEventsAsync(PlannedEvents next)
    {
        try { await _app.UpdateCurrentPlannedEventsAsync(_stateId, next); RefreshAfterEditorClick(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private void RefreshAfterEditorClick()
    {
        // The editor being saved is part of the collections Refresh replaces. Let WinUI finish the
        // current Click event before clearing those controls; removing the focused button during event
        // dispatch can otherwise throw a COMException after the data has already been persisted.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1), async () => await Refresh());
    }

    private async void Play(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_action.Text) || _request is not null) return;
        var retry = false;
        var committed = false;
        StoryState? resultState = null;
        StoryTurn? resultTurn = null;
        try
        {
            _request = new();
            _busy.IsRunning = true;
            _busyOverlay.IsVisible = true;
            _action.IsEnabled = false;
            _submit.IsEnabled = false;
            SetSuggestionsEnabled(false);
            _copy.IsEnabled = false;
            var action = _action.Text;
            var state = await _repository.GetAsync(_stateId) ?? throw new NarratorException("Story State not found.");
            _pendingOperation = new(
                Guid.NewGuid(),
                PendingOperationType.GenerateStoryTurn,
                _stateId,
                state.LastCommittedTurnSequence + 1,
                DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            (resultState, resultTurn) = await _app.PlayTurnAsync(_stateId, action, _request.Token);
            _action.Text = "";
            committed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            retry = await DisplayActionSheetAsync(
                $"Story request failed: {ex.Message}",
                "Cancel",
                null,
                "Retry") == "Retry";
        }
        finally
        {
            _pendingOperation = null;
            _busy.IsRunning = false;
            _busyOverlay.IsVisible = false;
            _action.IsEnabled = true;
            _submit.IsEnabled = true;
            SetSuggestionsEnabled(true);
            _copy.IsEnabled = true;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
        // The request is complete as soon as the turn is durably committed. Rebuilding and scrolling
        // the page is presentation work, so it runs only after the busy/request state has been cleared.
        if (committed) await Refresh(scrollToLatestTurn: true);
        if (committed && resultState is not null && resultTurn is not null) await ShowMetConditionsAsync(resultState, resultTurn);
        if (retry) Play(null, EventArgs.Empty);
    }

    // Only this turn's own MetVictoryConditionIds/MetLossConditionIds (the delta - see StoryTurn) are
    // considered, never the state's cumulative totals, so a condition met in an earlier turn is never
    // re-announced here.
    private async Task ShowMetConditionsAsync(StoryState state, StoryTurn turn)
    {
        var metVictories = ResolveDescriptions(state.CurrentVictoryConditions, turn.MetVictoryConditionIds);
        var metLosses = ResolveDescriptions(state.CurrentLossConditions, turn.MetLossConditionIds);
        if (metVictories.Count == 0 && metLosses.Count == 0) return;
        var lines = new List<string>();
        if (metVictories.Count > 0) lines.Add("Victory condition(s) met:" + Environment.NewLine + string.Join(Environment.NewLine, metVictories.Select(x => $"• {x}")));
        if (metLosses.Count > 0) lines.Add("Loss condition(s) met:" + Environment.NewLine + string.Join(Environment.NewLine, metLosses.Select(x => $"• {x}")));
        var title = metVictories.Count > 0 && metLosses.Count > 0
            ? "Victory and loss conditions met"
            : metVictories.Count > 0 ? "Victory condition met" : "Loss condition met";
        await DisplayAlertAsync(title, string.Join(Environment.NewLine + Environment.NewLine, lines), "Keep Playing");
    }

    private static List<string> ResolveDescriptions(StoryConditions conditions, IReadOnlyList<Guid> ids) =>
        ids.Select(id => conditions.Entries.FirstOrDefault(x => x.Id == id)?.Description)
            .Where(description => description is not null)
            .Select(description => description!)
            .ToList();

    private void SetSuggestionsEnabled(bool enabled)
    {
        foreach (var button in _suggestions.Children.OfType<Button>()) button.IsEnabled = enabled;
    }

    private async void Copy(object? sender, EventArgs e)
    {
        if (_request is not null) return;
        try { var copy = await _repository.CopyAsync(_stateId); _tabs.OpenPlay(copy.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Export(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(_stateId)
                ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportStateAsync(snapshot.State, snapshot.Turns);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void ExportHistory(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(_stateId)
                ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportNarrationHistoryAsync(snapshot.State, snapshot.Turns);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void ExportBibleHistory(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(_stateId)
                ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportBibleHistoryAsync(snapshot.State, snapshot.Turns);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void ExportPlannedEventHistory(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(_stateId)
                ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportPlannedEventHistoryAsync(snapshot.State, snapshot.Turns);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
