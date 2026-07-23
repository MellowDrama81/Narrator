using System.Collections.ObjectModel;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryDefinitionListPage : ContentPage
{
    private readonly IStoryDefinitionRepository _repository;
    private readonly MainTabbedPage _tabs;
    private readonly ObservableCollection<StoryDefinitionSummary> _items = [];
    private readonly CollectionView _list;

    public StoryDefinitionListPage(IStoryDefinitionRepository repository, MainTabbedPage tabs)
    {
        _repository = repository;
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
                updated.SetBinding(Label.TextProperty, nameof(StoryDefinitionSummary.UpdatedAtUtc), stringFormat: "Updated {0:g}");
                return new VerticalStackLayout { Padding = new Thickness(4, 8), Children = { title, updated } };
            })
        };
        var grid = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children =
            {
                Ui.Heading("Story Definitions"),
                Ui.Buttons(
                    Ui.Button("New", (_, _) => _tabs.OpenPrompt()),
                    Ui.Button("Open", (_, _) => { if (Selected is { } x) _tabs.OpenDefinition(x.Id); }),
                    Ui.Button("Edit", (_, _) => { if (Selected is { } x) _tabs.OpenPrompt(x.Id); }),
                    Ui.Button("Start", (_, _) => { if (Selected is { } x) _tabs.OpenStart(x.Id); }),
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
            _items.Clear();
            foreach (var item in await _repository.ListAsync()) _items.Add(item);
        }
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
        if (!await DisplayAlertAsync("Delete Story Definition?", "Existing Story States will remain playable. The definition will move to Trash.", "Delete", "Cancel")) return;
        if (!await _tabs.CloseDefinitionTabsForDeletionAsync(selected.Id)) return;
        try { await _repository.MoveToTrashAsync(selected.Id); await Refresh(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Import(object? sender, EventArgs e)
    {
        try { var imported = await ImportExportService.ImportDefinitionAsync(_repository); await Refresh(); if (imported is not null) _tabs.OpenDefinition(imported.Id); }
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
