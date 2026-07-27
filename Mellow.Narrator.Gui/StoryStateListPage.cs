using System.Collections.ObjectModel;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryStateListPage : ContentPage
{
    private readonly IStoryStateRepository _repository;
    private readonly INarratorApplication _application;
    private readonly MainTabbedPage _tabs;
    private readonly ObservableCollection<StoryStateSummary> _items = [];
    private readonly CollectionView _list;

    public StoryStateListPage(
        IStoryStateRepository repository,
        INarratorApplication application,
        MainTabbedPage tabs)
    {
        _repository = repository;
        _application = application;
        _tabs = tabs;
        Title = "Stories";
        _list = new CollectionView
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontAttributes = FontAttributes.Bold, FontSize = 18 };
                label.SetBinding(Label.TextProperty, nameof(StoryStateSummary.Label));
                var dates = new Label { FontSize = 12 };
                dates.SetBinding(Label.TextProperty, nameof(StoryStateSummary.StartedAtUtc),
                    converter: new LocalTimestampConverter(), stringFormat: "Started {0}");
                var lastAction = new Label { FontSize = 12 };
                lastAction.SetBinding(Label.TextProperty, new Binding(
                    nameof(StoryStateSummary.LastActionAtUtc),
                    stringFormat: "Last action: {0}",
                    converter: new LocalTimestampConverter(),
                    converterParameter: "No completed player action"));
                var open = new Label { TextColor = Colors.DarkGreen, FontSize = 12 };
                open.BindingContextChanged += (_, _) =>
                    open.Text = open.BindingContext is StoryStateSummary item && _tabs.IsStoryOpen(item.Id) ? "Open in a tab" : "";
                return new VerticalStackLayout { Padding = new Thickness(4, 8), Children = { label, dates, lastAction, open } };
            })
        };
        var empty = Ui.Empty("No Story States yet. Start a story from a Story Definition.");
        _items.CollectionChanged += (_, _) => empty.IsVisible = _items.Count == 0;
        var grid = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children =
            {
                Ui.Heading("Story States"),
                Ui.Buttons(
                    Ui.Button("Open", (_, _) => { if (Selected is { } x) _tabs.OpenPlay(x.Id); }),
                    Ui.SecondaryButton("Label", EditLabel),
                    Ui.SecondaryButton("Copy", Copy),
                    Ui.SecondaryButton("Import", Import),
                    Ui.SecondaryButton("Export", Export),
                    Ui.SecondaryButton("Earlier", async (_, _) => await Move(-1)),
                    Ui.SecondaryButton("Later", async (_, _) => await Move(1)),
                    Ui.DestructiveButton("Delete", Delete)),
                new Grid { Children = { _list, empty } }
            }
        };
        grid.SetRow(grid.Children[1], 1);
        grid.SetRow(grid.Children[2], 2);
        Content = grid;
    }

    private StoryStateSummary? Selected => _list.SelectedItem as StoryStateSummary;
    protected override async void OnAppearing() { base.OnAppearing(); await Refresh(); }

    private async Task Refresh()
    {
        try
        {
            _items.Clear();
            foreach (var item in await _repository.ListAsync()) _items.Add(item);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Copy(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        if (_tabs.IsStoryRequestInFlight(selected.Id))
        {
            await DisplayAlertAsync("Copy unavailable", "Wait for the current story request to finish or cancel it first.", "OK");
            return;
        }
        try { var copy = await _repository.CopyAsync(selected.Id); await Refresh(); _tabs.OpenPlay(copy.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void EditLabel(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        var value = await _repository.GetAsync(selected.Id);
        if (value is null) return;
        var settings = await _application.GetSettingsAsync();
        var label = await DisplayPromptAsync(
            "Story label",
            "Enter a label for this Story State.",
            initialValue: value.Label,
            maxLength: settings.ContentLimits.MaxStoryLabelCharacters);
        if (string.IsNullOrWhiteSpace(label)) return;
        try { await _repository.UpdateLabelAsync(value.Id, label); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task Move(int delta)
    {
        if (Selected is not { } selected) return;
        var index = _items.IndexOf(selected);
        var otherIndex = index + delta;
        if (otherIndex < 0 || otherIndex >= _items.Count) return;
        var other = _items[otherIndex];
        await _repository.SwapSortOrderAsync(selected.Id, other.Id);
        await Refresh();
    }

    private async void Delete(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        if (!await DisplayAlertAsync("Delete Story State?", "The complete story will move to Trash.", "Delete", "Cancel")) return;
        if (!await _tabs.CloseStoryTabForDeletionAsync(selected.Id)) return;
        try { await _repository.MoveToTrashAsync(selected.Id); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Import(object? sender, EventArgs e)
    {
        try { var imported = await ImportExportService.ImportStateAsync(_repository, _application); await Refresh(); if (imported is not null) _tabs.OpenPlay(imported.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Export(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(selected.Id)
                ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportStateAsync(snapshot.State, snapshot.Turns);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
