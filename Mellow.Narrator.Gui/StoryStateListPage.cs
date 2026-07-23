using System.Collections.ObjectModel;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryStateListPage : ContentPage
{
    private readonly IStoryStateRepository _repository;
    private readonly MainTabbedPage _tabs;
    private readonly ObservableCollection<StoryStateSummary> _items = [];
    private readonly CollectionView _list;

    public StoryStateListPage(IStoryStateRepository repository, MainTabbedPage tabs)
    {
        _repository = repository;
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
                dates.SetBinding(Label.TextProperty, nameof(StoryStateSummary.StartedAtUtc), stringFormat: "Started {0:g}");
                var lastAction = new Label { FontSize = 12 };
                lastAction.SetBinding(Label.TextProperty, nameof(StoryStateSummary.LastActionAtUtc), stringFormat: "Last action {0:g}");
                return new VerticalStackLayout { Padding = new Thickness(4, 8), Children = { label, dates, lastAction } };
            })
        };
        var grid = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children =
            {
                Ui.Heading("Story States"),
                Ui.Buttons(
                    Ui.Button("Open", (_, _) => { if (Selected is { } x) _tabs.OpenPlay(x.Id); }),
                    Ui.Button("Label", EditLabel),
                    Ui.Button("Copy", Copy),
                    Ui.Button("Import", Import),
                    Ui.Button("Export", Export),
                    Ui.Button("Earlier", async (_, _) => await Move(-1)),
                    Ui.Button("Later", async (_, _) => await Move(1)),
                    Ui.Button("Delete", Delete)),
                _list
            }
        };
        grid.SetRow(grid.Children[1], 1);
        grid.SetRow(_list, 2);
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
        try { var copy = await _repository.CopyAsync(selected.Id); await Refresh(); _tabs.OpenPlay(copy.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void EditLabel(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        var value = await _repository.GetAsync(selected.Id);
        if (value is null) return;
        var label = await DisplayPromptAsync("Story label", "Enter a label for this Story State.", initialValue: value.Label, maxLength: 200);
        if (string.IsNullOrWhiteSpace(label)) return;
        try { await _repository.SaveAsync(value with { Label = label.Trim() }); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task Move(int delta)
    {
        if (Selected is not { } selected) return;
        var index = _items.IndexOf(selected);
        var otherIndex = index + delta;
        if (otherIndex < 0 || otherIndex >= _items.Count) return;
        var other = _items[otherIndex];
        var first = await _repository.GetAsync(selected.Id);
        var second = await _repository.GetAsync(other.Id);
        if (first is null || second is null) return;
        await _repository.SaveAsync(first with { SortOrder = second.SortOrder });
        await _repository.SaveAsync(second with { SortOrder = first.SortOrder });
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
        try { var imported = await ImportExportService.ImportStateAsync(_repository); await Refresh(); if (imported is not null) _tabs.OpenPlay(imported.Id); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Export(object? sender, EventArgs e)
    {
        if (Selected is not { } selected) return;
        try
        {
            var state = await _repository.GetAsync(selected.Id) ?? throw new NarratorException("Story State not found.");
            await ImportExportService.ExportStateAsync(state, await _repository.GetTurnsAsync(state.Id));
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
