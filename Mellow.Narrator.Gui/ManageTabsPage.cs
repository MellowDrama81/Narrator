using System.Collections.ObjectModel;

namespace Mellow.Narrator.Gui;

public sealed class ManageTabsPage : ContentPage
{
    private readonly MainTabbedPage _owner;
    private readonly ObservableCollection<NarratorNavigationPage> _tabs = [];

    public ManageTabsPage(MainTabbedPage owner)
    {
        _owner = owner;
        Title = "Manage Tabs";
        var list = new CollectionView
        {
            ItemsSource = _tabs,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var title = new Label { FontAttributes = FontAttributes.Bold };
                title.SetBinding(Label.TextProperty, nameof(Page.Title));
                var earlier = Ui.SecondaryButton("Earlier", async (sender, _) => await MoveAsync(sender, -1));
                var later = Ui.SecondaryButton("Later", async (sender, _) => await MoveAsync(sender, 1));
                earlier.BindingContextChanged += (_, _) => UpdateButtons(earlier, later);
                later.BindingContextChanged += (_, _) => UpdateButtons(earlier, later);
                return new VerticalStackLayout
                {
                    Padding = 8,
                    Children = { title, Ui.Buttons(earlier, later) }
                };
            })
        };
        Content = new Grid
        {
            Padding = 16,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Children = { Ui.Heading("Manage Tabs"), list }
        };
        ((Grid)Content).SetRow(list, 1);
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        Refresh();
    }

    private async Task MoveAsync(object? sender, int delta)
    {
        if (sender is not Button { BindingContext: NarratorNavigationPage page }) return;
        await _owner.MoveAsync(page, delta);
        Refresh();
    }

    private void UpdateButtons(Button earlier, Button later)
    {
        if (earlier.BindingContext is not NarratorNavigationPage page) return;
        var index = _owner.UnlockedTabs.ToList().IndexOf(page);
        earlier.IsEnabled = index > 0;
        later.IsEnabled = index >= 0 && index < _owner.UnlockedTabs.Count - 1;
    }

    private void Refresh()
    {
        _tabs.Clear();
        foreach (var tab in _owner.UnlockedTabs) _tabs.Add(tab);
    }
}
