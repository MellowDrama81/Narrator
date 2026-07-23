using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryDefinitionPage : ContentPage
{
    private readonly Guid _id;
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _application;
    private readonly MainTabbedPage _tabs;
    private readonly VerticalStackLayout _content = new() { Padding = 16, Spacing = 8 };

    public StoryDefinitionPage(
        Guid id,
        IStoryDefinitionRepository repository,
        INarratorApplication application,
        MainTabbedPage tabs)
    {
        _id = id;
        _repository = repository;
        _application = application;
        _tabs = tabs;
        Title = "Definition";
        Content = new ScrollView { Content = _content };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var value = await _repository.GetAsync(_id) ?? throw new NarratorException("Story Definition not found.");
            Title = value.Title;
            if (Parent is NavigationPage navigation) navigation.Title = Title;
            _content.Children.Clear();
            _content.Children.Add(Ui.Heading(value.Title));
            _content.Children.Add(new Label { Text = value.StoryPrompt });
            _content.Children.Add(Ui.Buttons(
                Ui.Button("Edit", async (_, _) => await _tabs.ReplaceCurrentWithPromptAsync(_id)),
                Ui.Button("Start Story", async (_, _) => await _tabs.ReplaceCurrentWithStartAsync(_id)),
                Ui.Button("Export", async (_, _) => await ImportExportService.ExportDefinitionAsync(value))));
            _content.Children.Add(Ui.Heading("Player Questions"));
            foreach (var question in value.PlayerQuestions.OrderBy(x => x.SortOrder))
                _content.Children.Add(new Label { Text = $"{question.Question}\nValidation: {question.ValidationInstruction}" });
            _content.Children.Add(Ui.Heading($"Initial Story Bible ({value.InitialStoryBible.Entries.Count})"));
            var settings = await _application.GetSettingsAsync();
            if (StoryBibleProcessor.IsApproachingLimits(value.InitialStoryBible, settings.StoryGeneration))
                _content.Children.Add(new Label { Text = "The Story Bible is approaching one or more configured limits.", TextColor = Colors.DarkOrange });
            _content.Children.Add(StoryBibleView.Create(value.InitialStoryBible));
            _content.Children.Add(Ui.Heading("Bible Maintenance History"));
            foreach (var record in value.StoryBibleMaintenanceHistory.OrderByDescending(x => x.CompletedAtUtc))
            {
                _content.Children.Add(new Label { Text = $"{record.CompletedAtUtc.ToLocalTime():g} — {record.Reason}", FontAttributes = FontAttributes.Bold });
                foreach (var change in record.Changes) _content.Children.Add(ChangeLabel(change));
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    internal static Label ChangeLabel(AppliedStoryBibleChange change) => new()
    {
        Text = $"{change.Operation}: {change.Before?.Name ?? change.After?.Name} ({change.Source})\nBefore: {change.Before?.Content ?? "—"}\nAfter: {change.After?.Content ?? "—"}"
    };
}

internal static class StoryBibleView
{
    public static View Create(StoryBible bible)
    {
        var body = new VerticalStackLayout { IsVisible = false, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };
        var search = new SearchBar { Placeholder = "Search name or content" };
        var categories = new Picker { Title = "All categories" };
        var importance = new Picker { Title = "All importance levels" };
        categories.ItemsSource = new[] { "All categories" }
            .Concat(bible.Entries.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            .ToArray();
        importance.ItemsSource = new[] { "All importance levels" }
            .Concat(bible.Entries.Select(x => x.Importance).Distinct().OrderDescending().Select(x => x.ToString()))
            .ToArray();
        categories.SelectedIndex = 0;
        importance.SelectedIndex = 0;

        void Render()
        {
            entries.Children.Clear();
            var query = search.Text?.Trim();
            var selectedCategory = categories.SelectedIndex > 0 ? categories.SelectedItem?.ToString() : null;
            var selectedImportance = importance.SelectedIndex > 0 && int.TryParse(importance.SelectedItem?.ToString(), out var parsed)
                ? parsed
                : (int?)null;
            var filtered = bible.Entries.Where(x =>
                (string.IsNullOrEmpty(query) ||
                    x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.Content.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                (selectedCategory is null || x.Category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase)) &&
                (selectedImportance is null || x.Importance == selectedImportance));

            foreach (var group in filtered.GroupBy(x => x.Category).OrderBy(x => x.Key))
            {
                entries.Children.Add(new Label { Text = group.Key, FontAttributes = FontAttributes.Bold });
                foreach (var entry in group.OrderBy(x => x.Name))
                {
                    var details = new VerticalStackLayout
                    {
                        IsVisible = false,
                        Children =
                        {
                            new Label { Text = entry.Content },
                            new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                        }
                    };
                    entries.Children.Add(new VerticalStackLayout
                    {
                        Children =
                        {
                            Ui.Button(
                                $"{entry.Name} [importance {entry.Importance}, relevant turn {entry.LastRelevantTurnNumber}]",
                                (_, _) => details.IsVisible = !details.IsVisible),
                            details
                        }
                    });
                }
            }
        }

        search.TextChanged += (_, _) => Render();
        categories.SelectedIndexChanged += (_, _) => Render();
        importance.SelectedIndexChanged += (_, _) => Render();
        Render();
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(bible).Length;
        body.Children.Add(new Label { Text = $"{bible.Entries.Count} active entries; {serializedBytes:N0} serialized bytes" });
        body.Children.Add(search);
        var filters = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) },
            Children = { categories, importance }
        };
        Grid.SetColumn(importance, 1);
        body.Children.Add(filters);
        body.Children.Add(entries);
        var toggle = Ui.Button("Show / hide Bible", (_, _) => body.IsVisible = !body.IsVisible);
        return new VerticalStackLayout { Children = { toggle, body } };
    }
}
