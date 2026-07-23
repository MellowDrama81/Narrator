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
    private readonly VerticalStackLayout _bible = new();
    private readonly VerticalStackLayout _history = new() { IsVisible = false, Spacing = 8 };
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;

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
        Title = "Play Story";
        var bibleBody = new VerticalStackLayout { IsVisible = false, Children = { _bible } };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 8,
                Children =
                {
                    _narration, Ui.Heading("Suggested Actions"), _suggestions, _action,
                    Ui.Buttons(Ui.Button("Continue", Play), Ui.Button("Copy Story", Copy), Ui.Button("Export", Export)),
                    _busy, Ui.Button("Show / hide Story Bible", (_, _) => bibleBody.IsVisible = !bibleBody.IsVisible), bibleBody,
                    Ui.Button("Show / hide Bible change history", (_, _) => _history.IsVisible = !_history.IsVisible), _history
                }
            }
        };
        _action.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
    }

    PlayStoryTabState? IWorkspacePayloadPage.PlayStoryTabState => new(_action.Text ?? "");
    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    void IInFlightRequestPage.CancelInFlightRequest() => _request?.Cancel();

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel request?", "A story request is still in progress.", "Cancel and Close", "Keep Open")) return false;
            _request.Cancel();
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
            await DisplayAlertAsync("Turn interrupted", "The incomplete turn was rolled back. Your player action is preserved; choose Continue to retry, or clear it to cancel.", "OK");
        }
        await Refresh();
    }

    private async Task Refresh()
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
            _narration.Children.Clear();
            var turns = await _repository.GetTurnsAsync(_stateId);
            foreach (var turn in turns)
            {
                if (turn.PlayerAction is not null) _narration.Children.Add(new Label { Text = $"> {turn.PlayerAction}", FontAttributes = FontAttributes.Italic });
                _narration.Children.Add(new Label { Text = turn.Narration, FontSize = 17 });
            }
            var last = turns.LastOrDefault();
            _suggestions.Children.Clear();
            foreach (var suggestion in last?.SuggestedActions ?? [])
            {
                var button = Ui.Button(suggestion, (_, _) => _action.Text = suggestion);
                _suggestions.Children.Add(button);
            }
            _bible.Children.Clear();
            _bible.Children.Add(StoryBibleView.Create(state.CurrentStoryBible));
            _history.Children.Clear();
            foreach (var record in state.StoryBibleMaintenanceHistory.OrderByDescending(x => x.CompletedAtUtc))
            {
                _history.Children.Add(new Label { Text = $"{record.CompletedAtUtc:g} — {record.Reason}", FontAttributes = FontAttributes.Bold });
                foreach (var change in record.Changes) _history.Children.Add(StoryDefinitionPage.ChangeLabel(change));
            }
            foreach (var turn in turns.OrderByDescending(x => x.SequenceNumber))
            {
                if (turn.StoryBibleChanges.Count == 0) continue;
                _history.Children.Add(new Label { Text = $"Turn {turn.SequenceNumber} — {turn.CompletedAtUtc:g}", FontAttributes = FontAttributes.Bold });
                foreach (var change in turn.StoryBibleChanges) _history.Children.Add(StoryDefinitionPage.ChangeLabel(change));
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Play(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_action.Text) || _request is not null) return;
        try
        {
            _request = new();
            _busy.IsRunning = true;
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
            await Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Ui.Error(this, ex); }
        finally
        {
            _pendingOperation = null;
            _busy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
    }

    private async void Copy(object? sender, EventArgs e)
    {
        try { var copy = await _repository.CopyAsync(_stateId); _tabs.OpenPlay(copy.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Export(object? sender, EventArgs e)
    {
        try
        {
            var state = await _repository.GetAsync(_stateId) ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportStateAsync(state, await _repository.GetTurnsAsync(_stateId));
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
