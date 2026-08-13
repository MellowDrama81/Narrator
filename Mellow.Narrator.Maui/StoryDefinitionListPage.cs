using System.Collections.ObjectModel;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Maui;

public sealed class StoryDefinitionListPage : ContentPage, IPendingOperationPage, IInFlightRequestPage
{
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _application;
    private readonly MainTabbedPage _tabs;
    private readonly ObservableCollection<StoryDefinitionSummary> _items = [];
    private readonly CollectionView _list;
    private readonly ActivityIndicator _startBusy = new();
    private readonly Label _status = new() { Text = "", TextColor = Colors.DarkOrange };
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;
    private bool _clearStatusOnNextRefresh;

    public StoryDefinitionListPage(
        IStoryDefinitionRepository repository,
        INarratorApplication application,
        MainTabbedPage tabs)
    {
        _repository = repository;
        _application = application;
        _tabs = tabs;
        Title = "Definitions";
        _list = new CollectionView
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var title = new Label { FontAttributes = FontAttributes.Bold, FontSize = 18 };
                title.SetBinding(Label.TextProperty, nameof(StoryDefinitionSummary.Title));
                var updated = new Label { FontSize = 12 };
                updated.SetBinding(Label.TextProperty, nameof(StoryDefinitionSummary.UpdatedAtUtc),
                    converter: new LocalTimestampConverter(), stringFormat: "Updated {0}");
                return new VerticalStackLayout { Padding = new Thickness(4, 8), Children = { title, updated } };
            })
        };
        var empty = Ui.Empty("No Story Definitions yet. Click New to create one.");
        _items.CollectionChanged += (_, _) => empty.IsVisible = _items.Count == 0;
        var buttons = Ui.Buttons(
            Ui.Button("New", (_, _) => _tabs.OpenPrompt()),
            Ui.SecondaryButton("Open", (_, _) => { if (Selected is { } x) _tabs.OpenDefinition(x.Id); }),
            Ui.Button("Start", async (_, _) => await StartAsync()),
            Ui.SecondaryButton("Import", Import),
            Ui.SecondaryButton("Export", Export),
            Ui.SecondaryButton("Earlier", async (_, _) => await Move(-1)),
            Ui.SecondaryButton("Later", async (_, _) => await Move(1)),
            Ui.DestructiveButton("Delete", Delete),
            Ui.Busy(_startBusy, "Startingâ€¦"));
        var listArea = new Grid { Children = { _list, empty } };
        var grid = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children = { Ui.Heading("Story Definitions"), buttons, _status, listArea }
        };
        grid.SetRow(buttons, 1);
        grid.SetRow(_status, 2);
        grid.SetRow(listArea, 3);
        Content = grid;
    }

    PendingOperationState? IPendingOperationPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        await Ui.WaitWhileAsync(() => _request is not null, TimeSpan.FromSeconds(5));
        if (marker is not null) _pendingOperation = marker;
    }

    internal void RestoreInterruptedOperation(PendingOperationState? operation)
    {
        if (operation?.Type is not PendingOperationType.GenerateOpeningScene) return;
        _pendingOperation = null;
        _status.Text = "The previous story start was interrupted. Select the Story Definition and choose Start to retry.";
        _clearStatusOnNextRefresh = false;
    }

    private StoryDefinitionSummary? Selected => _list.SelectedItem as StoryDefinitionSummary;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Refresh();
    }

    private async Task Refresh()
    {
        try
        {
            // The interrupted-operation notice must survive the very first Refresh() after it's set
            // (that's the appearance it's meant to be seen on), but must not linger forever across
            // later tab revisits - so it's cleared starting on the *second* Refresh() since it was set.
            if (_clearStatusOnNextRefresh) _status.Text = "";
            _clearStatusOnNextRefresh = !string.IsNullOrEmpty(_status.Text);
            var selectedId = Selected?.Id;
            _items.Clear();
            foreach (var item in await _repository.ListAsync()) _items.Add(item);
            if (selectedId is { } id) _list.SelectedItem = _items.FirstOrDefault(x => x.Id == id);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task StartAsync()
    {
        if (_request is not null || Selected is not { } selected) return;
        var retry = false;
        try
        {
            _request = new();
            _startBusy.IsRunning = true;
            _status.Text = "";
            var targetStateId = Guid.NewGuid();
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.GenerateOpeningScene, targetStateId, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            await _tabs.StartStoryAsync(selected.Id, targetStateId, replaceCurrent: false, _request.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            retry = await DisplayActionSheetAsync(
                $"Starting the story failed: {ex.Message}",
                "Cancel",
                null,
                "Retry") == "Retry";
        }
        finally
        {
            _pendingOperation = null;
            _startBusy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
        if (retry) await StartAsync();
    }

    private async Task Move(int delta)
    {
        if (Selected is not { } selected) return;
        var index = _items.IndexOf(selected);
        var otherIndex = index + delta;
        if (otherIndex < 0 || otherIndex >= _items.Count) return;
        var other = _items[otherIndex];
        try { await _repository.SwapSortOrderAsync(selected.Id, other.Id); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Delete(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        if (!await DisplayAlertAsync("Delete Story Definition?", "Existing Story States will remain playable. The definition will move to Trash.", "Delete", "Cancel")) return;
        if (!await _tabs.CloseDefinitionTabsForDeletionAsync(selected.Id)) return;
        try { await _repository.MoveToTrashAsync(selected.Id); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Import(object? sender, EventArgs e)
    {
        try { var imported = await ImportExportService.ImportDefinitionAsync(_repository, _application); await Refresh(); if (imported is not null) _tabs.OpenDefinition(imported.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Export(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        try
        {
            var definition = await _repository.GetAsync(selected.Id) ?? throw new NarratorException("Story Definition not found.");
            await ImportExportService.ExportDefinitionAsync(definition);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
