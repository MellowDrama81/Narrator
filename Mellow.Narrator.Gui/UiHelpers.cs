using Mellow.Narrator.Core;
using Microsoft.Maui.Layouts;

namespace Mellow.Narrator.Gui;

internal static class Ui
{
    public static Label Heading(string text) => new()
    {
        Text = text,
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 8)
    };

    public static Button Button(string text, EventHandler clicked)
    {
        var button = new Button { Text = text };
        button.Clicked += clicked;
        return button;
    }

    public static FlexLayout Buttons(params View[] children)
    {
        var layout = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            JustifyContent = FlexJustify.Start,
            AlignItems = FlexAlignItems.Center
        };
        foreach (var child in children) layout.Children.Add(child);
        return layout;
    }

    public static Task Error(Page page, Exception ex) => page.DisplayAlertAsync("Mellow Narrator", ex.Message, "OK");
}

public sealed class NarratorNavigationPage : NavigationPage
{
    public NarratorNavigationPage(Page root, Guid tabId, TabType type, Guid? recordId = null) : base(root)
    {
        TabId = tabId;
        TabType = type;
        RecordId = recordId;
        Title = root.Title;
    }

    public Guid TabId { get; }
    public TabType TabType { get; }
    public Guid? RecordId { get; }
    public bool IsFixed => TabType is TabType.Settings or TabType.StoryDefinitionList or TabType.PlayStoryList;
}
