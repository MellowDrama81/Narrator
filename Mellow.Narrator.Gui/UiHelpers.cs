using System.Globalization;
using Mellow.Narrator.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Layouts;

namespace Mellow.Narrator.Gui;

internal static class Ui
{
    private static ILogger _logger = NullLogger.Instance;
    private static INarratorLogLevelSwitch? _logLevelSwitch;

    public static void ConfigureLogging(ILogger logger, INarratorLogLevelSwitch logLevelSwitch)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logLevelSwitch = logLevelSwitch ?? throw new ArgumentNullException(nameof(logLevelSwitch));
    }

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

    public static Button SecondaryButton(string text, EventHandler clicked)
    {
        var button = Button(text, clicked);
        button.BackgroundColor = Colors.Transparent;
        button.TextColor = Color.FromArgb("#512BD4");
        button.BorderColor = Color.FromArgb("#512BD4");
        button.BorderWidth = 1;
        return button;
    }

    public static Button DestructiveButton(string text, EventHandler clicked)
    {
        var button = Button(text, clicked);
        button.BackgroundColor = Color.FromArgb("#C0392B");
        button.TextColor = Colors.White;
        return button;
    }

    public static Label Empty(string text) => new()
    {
        Text = text,
        FontSize = 14,
        TextColor = Colors.Gray,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Margin = new Thickness(0, 32)
    };

    public static View Busy(ActivityIndicator indicator, string text = "Working…")
    {
        var label = new Label { Text = text, VerticalOptions = LayoutOptions.Center };
        label.SetBinding(Label.IsVisibleProperty, new Binding(nameof(ActivityIndicator.IsRunning), source: indicator));
        return new HorizontalStackLayout { Spacing = 8, Children = { indicator, label } };
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

    // Shared by every page's CancelInFlightRequestAsync: polls stillPending until it returns false or
    // the timeout elapses, so a request that never observes cancellation can't hang the caller (e.g.
    // app shutdown) indefinitely.
    public static async Task WaitWhileAsync(Func<bool> stillPending, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (stillPending() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    public static Task Error(Page page, Exception ex)
    {
        _logger.LogError("A GUI operation failed with {ErrorType}.", ex.GetType().FullName);
        if (_logLevelSwitch?.MinimumLevel == NarratorLogLevel.Trace)
            _logger.LogTrace(ex, "GUI failure details.");
        // Only NarratorException messages are written for the user to read; every other exception
        // type is an unexpected .NET failure (NullReferenceException, raw IOException, etc.) whose
        // message is an implementation detail, not something a user should see.
        var message = ex is NarratorException ? ex.Message : "Something went wrong. Check the logs for details.";
        return page.DisplayAlertAsync("Mellow Narrator", message, "OK");
    }

    public static void Warning(string message) => _logger.LogWarning("{WarningMessage}", message);

    public static void Warning(Exception ex, string message) =>
        _logger.LogWarning(ex, "{WarningMessage}", message);
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
    public bool IsFixed => FixedTabTypes.Types.Contains(TabType);
}

// Single source of truth for which tabs are fixed (always present, never closable/reorderable), so
// MainTabbedPage's tab layout can't silently drift out of sync with NarratorNavigationPage.IsFixed.
internal static class FixedTabTypes
{
    public static readonly IReadOnlySet<TabType> Types = new HashSet<TabType>
    {
        TabType.Settings, TabType.StoryDefinitionList, TabType.PlayStoryList
    };
}

internal sealed class LocalTimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset timestamp
            ? timestamp.ToLocalTime().ToString("g", culture)
            : parameter?.ToString() ?? "Not yet";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
