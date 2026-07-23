namespace Mellow.Narrator.Gui;

public sealed class ManageTabsPage : ContentPage
{
    private readonly MainTabbedPage _owner;
    private readonly VerticalStackLayout _rows = new() { Spacing = 8 };

    public ManageTabsPage(MainTabbedPage owner)
    {
        _owner = owner;
        Title = "Manage Tabs";
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 16, Children = { Ui.Heading("Manage Tabs"), _rows } } };
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        var tabs = _owner.UnlockedTabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var earlier = Ui.Button("Earlier", async (_, _) => { await _owner.MoveAsync(tab, -1); Render(); });
            var later = Ui.Button("Later", async (_, _) => { await _owner.MoveAsync(tab, 1); Render(); });
            earlier.IsEnabled = i > 0;
            later.IsEnabled = i < tabs.Count - 1;
            _rows.Children.Add(new VerticalStackLayout
            {
                Children = { new Label { Text = tab.Title, FontAttributes = FontAttributes.Bold }, Ui.Buttons(earlier, later) }
            });
        }
    }
}
