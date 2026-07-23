using System.Collections.ObjectModel;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class TrashPage : ContentPage
{
    private readonly ITrashStore _trash;
    private readonly ObservableCollection<TrashItem> _items = [];
    private readonly CollectionView _list;

    public TrashPage(ITrashStore trash)
    {
        _trash = trash;
        Title = "Trash";
        _list = new CollectionView
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label();
                label.SetBinding(Label.TextProperty, nameof(TrashItem.DisplayName));
                var detail = new Label { FontSize = 12 };
                detail.SetBinding(Label.TextProperty, nameof(TrashItem.DeletedAtUtc), stringFormat: "Deleted {0:g}");
                return new VerticalStackLayout { Padding = 6, Children = { label, detail } };
            })
        };
        Content = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Children = { Ui.Buttons(Ui.Button("Restore", Restore), Ui.Button("Delete Permanently", Delete), Ui.Button("Empty Trash", Empty)), _list }
        };
        ((Grid)Content).SetRow(_list, 1);
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
    }

    protected override async void OnAppearing() { base.OnAppearing(); await Refresh(); }
    private TrashItem? Selected => _list.SelectedItem as TrashItem;

    private async Task Refresh()
    {
        _items.Clear();
        foreach (var item in await _trash.ListAsync()) _items.Add(item);
    }
    private async void Restore(object? sender, EventArgs e)
    {
        if (Selected is not { } item) return;
        try { await _trash.RestoreAsync(item.TrashId); await Refresh(); } catch (Exception ex) { await Ui.Error(this, ex); }
    }
    private async void Delete(object? sender, EventArgs e)
    {
        if (Selected is not { } item || !await DisplayAlertAsync("Delete permanently?", "This cannot be undone.", "Delete", "Cancel")) return;
        try { await _trash.DeletePermanentlyAsync(item.TrashId); await Refresh(); } catch (Exception ex) { await Ui.Error(this, ex); }
    }
    private async void Empty(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Empty Trash?", "All trashed stories and definitions will be permanently deleted.", "Empty", "Cancel")) return;
        try { await _trash.EmptyAsync(); await Refresh(); } catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
