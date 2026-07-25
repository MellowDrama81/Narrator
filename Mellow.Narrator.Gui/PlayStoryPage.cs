using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class PlayStoryPage : ContentPage, IWorkspacePayloadPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid _stateId;
    private readonly IStoryStateRepository _repository;
    private readonly INarratorApplication _app;
    private readonly MainTabbedPage _tabs;
    private readonly VerticalStackLayout _narration = new() { Spacing = 12 };
    private readonly VerticalStackLayout _suggestions = new() { Spacing = 4 };
    private readonly Entry _action = new() { Placeholder = "What do you do?" };
    private readonly ActivityIndicator _busy = new();
    private readonly Button _copy;
    private readonly VerticalStackLayout _bible = new();
    private readonly VerticalStackLayout _history = new() { IsVisible = false, Spacing = 8 };
    private readonly Label _limitWarning = new() { TextColor = Colors.DarkOrange };
    private readonly Button _loadAllTurns;
    private readonly ScrollView _story;
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;
    private bool _historyLoaded;
    private bool _allTurnsLoaded;

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
        _copy = Ui.Button("Copy Story", Copy);
        _loadAllTurns = Ui.Button("Load complete narration history", async (_, _) =>
        {
            _allTurnsLoaded = true;
            await Refresh();
        });
        Title = "Play Story";
        _story = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    _limitWarning, _narration, _loadAllTurns, Ui.Heading("Suggested Actions"), _suggestions, _action,
                    Ui.Buttons(Ui.Button("Continue", Play), _copy, Ui.Button("Export", Export)),
                    _busy
                }
            }
        };
        var sidePanel = new ScrollView
        {
            IsVisible = false,
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Ui.Heading("Story Bible"), _bible,
                    Ui.Button("Show / hide Bible change history", async (_, _) => await ToggleHistoryAsync()), _history
                }
            }
        };
        var storyColumn = new ColumnDefinition(new GridLength(2, GridUnitType.Star));
        var sidePanelColumn = new ColumnDefinition(new GridLength(0, GridUnitType.Absolute));
        var columns = new Grid
        {
            ColumnSpacing = 0,
            ColumnDefinitions = { storyColumn, sidePanelColumn },
            Children = { _story, sidePanel }
        };
        Grid.SetColumn(sidePanel, 1);
        var sidePanelToggle = Ui.Button("Show / hide Story Bible", (_, _) =>
        {
            var show = !sidePanel.IsVisible;
            sidePanel.IsVisible = show;
            sidePanelColumn.Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0, GridUnitType.Absolute);
            columns.ColumnSpacing = show ? 16 : 0;
        });
        var layout = new Grid
        {
            Padding = 16,
            RowSpacing = 8,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Children = { sidePanelToggle, columns }
        };
        Grid.SetRow(columns, 1);
        Content = layout;
        _action.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
        _action.Completed += Play;
    }

    PlayStoryTabState? IWorkspacePayloadPage.PlayStoryTabState => new(_action.Text ?? "");
    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        while (_request is not null) await Task.Delay(20);
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
        if (_pendingOperation is not null)
        {
            _pendingOperation = null;
            await _tabs.SaveWorkspaceNowAsync();
            if (await DisplayActionSheetAsync(
                    "The previous turn was interrupted. Your player action is preserved.",
                    "Cancel",
                    null,
                    "Retry") == "Retry")
                Play(null, EventArgs.Empty);
        }
        await Refresh(scrollToLatestTurn: true);
    }

    private async Task Refresh(bool scrollToLatestTurn = false)
    {
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
            Title = state.Label;
            if (Parent is NavigationPage navigation) navigation.Title = Title;
            _limitWarning.Text = StoryBibleProcessor.IsApproachingLimits(state.CurrentStoryBible, settings.StoryGeneration)
                ? "The Story Bible is approaching one or more configured limits."
                : "";
            _narration.Children.Clear();
            var turns = await _repository.GetTurnsAsync(
                _stateId,
                _allTurnsLoaded ? null : Math.Max(1, settings.StoryGeneration.RecentTurnCount));
            _loadAllTurns.IsVisible = !_allTurnsLoaded && state.LastCommittedTurnSequence + 1 > turns.Count;
            View? latestTurnAnchor = null;
            foreach (var turn in turns)
            {
                View? anchor = null;
                if (turn.PlayerAction is not null)
                {
                    var actionLabel = new Label { Text = $"> {turn.PlayerAction}", FontAttributes = FontAttributes.Italic };
                    _narration.Children.Add(actionLabel);
                    anchor = actionLabel;
                }
                var narrationLabel = new Label { Text = turn.Narration, FontSize = 17 };
                _narration.Children.Add(narrationLabel);
                latestTurnAnchor = anchor ?? narrationLabel;
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
            _bible.Children.Clear();
            _bible.Children.Add(StoryBibleView.Create(this, state.CurrentStoryBible, settings.ContentLimits, state.LastCommittedTurnSequence, SaveBibleAsync));
            if (_historyLoaded) await LoadHistoryAsync(state);
            if (scrollToLatestTurn && latestTurnAnchor is not null)
                await ScrollToTopAsync(latestTurnAnchor);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

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
        await _story.ScrollToAsync(0, Math.Max(0, y), true);
    }

    private async Task SaveBibleAsync(StoryBible next)
    {
        try { await _app.UpdateCurrentStoryBibleAsync(_stateId, next); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task ToggleHistoryAsync()
    {
        _history.IsVisible = !_history.IsVisible;
        if (!_history.IsVisible || _historyLoaded) return;
        var state = await _repository.GetAsync(_stateId) ?? throw new NarratorException("Story State not found.");
        await LoadHistoryAsync(state);
    }

    private async Task LoadHistoryAsync(StoryState state)
    {
        _history.Children.Clear();
        var groups = new List<(DateTimeOffset At, string Header, IReadOnlyList<AppliedStoryBibleChange> Changes)>();
        groups.AddRange(state.StoryBibleMaintenanceHistory.Select(x =>
            (x.CompletedAtUtc, x.Reason.ToString(), x.Changes)));
        var allTurns = await _repository.GetTurnsAsync(_stateId);
        groups.AddRange(allTurns.Where(x => x.StoryBibleChanges.Count > 0).Select(x =>
            (x.CompletedAtUtc, $"Turn {x.SequenceNumber}", x.StoryBibleChanges)));
        foreach (var group in groups.OrderByDescending(x => x.At))
        {
            _history.Children.Add(new Label
            {
                Text = $"{group.Header} — {group.At.ToLocalTime():g}",
                FontAttributes = FontAttributes.Bold
            });
            foreach (var change in group.Changes) _history.Children.Add(StoryDefinitionPage.ChangeLabel(change));
        }
        _historyLoaded = true;
    }

    private async void Play(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_action.Text) || _request is not null) return;
        var retry = false;
        try
        {
            _request = new();
            _busy.IsRunning = true;
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
            await _app.PlayTurnAsync(_stateId, action, _request.Token);
            _action.Text = "";
            await Refresh(scrollToLatestTurn: true);
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
            _copy.IsEnabled = true;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
        if (retry) Play(null, EventArgs.Empty);
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
}
