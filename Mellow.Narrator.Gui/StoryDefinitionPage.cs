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
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
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
                Ui.Button("Export", async (_, _) => await ExportAsync(value))));
            _content.Children.Add(Ui.Heading("Player Questions"));
            foreach (var question in value.PlayerQuestions.OrderBy(x => x.SortOrder))
                _content.Children.Add(new Label { Text = $"{question.Question}\nValidation: {question.ValidationInstruction}" });
            _content.Children.Add(Ui.Heading($"Initial Story Bible ({value.InitialStoryBible.Entries.Count})"));
            var settings = await _application.GetSettingsAsync();
            if (StoryBibleProcessor.IsApproachingLimits(value.InitialStoryBible, settings.StoryGeneration))
                _content.Children.Add(new Label { Text = "The Story Bible is approaching one or more configured limits.", TextColor = Colors.DarkOrange });
            _content.Children.Add(StoryBibleView.Create(this, value.InitialStoryBible, settings.ContentLimits, 0, SaveBibleAsync));
            _content.Children.Add(Ui.Heading("Bible Maintenance History"));
            foreach (var record in value.StoryBibleMaintenanceHistory.OrderByDescending(x => x.CompletedAtUtc))
            {
                _content.Children.Add(new Label { Text = $"{record.CompletedAtUtc.ToLocalTime():g} — {record.Reason}", FontAttributes = FontAttributes.Bold });
                foreach (var change in record.Changes) _content.Children.Add(ChangeLabel(change));
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveBibleAsync(StoryBible next)
    {
        try { await _application.UpdateInitialStoryBibleAsync(_id, next); await RefreshAsync(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task ExportAsync(StoryDefinition definition)
    {
        try { await ImportExportService.ExportDefinitionAsync(definition); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    internal static Label ChangeLabel(AppliedStoryBibleChange change) => new()
    {
        Text = $"{change.Operation}: {change.Before?.Name ?? change.After?.Name} ({change.Source})\nBefore: {change.Before?.Content ?? "—"}\nAfter: {change.After?.Content ?? "—"}"
    };
}

internal static class StoryBibleView
{
    public static View Create(Page page, StoryBible bible, ContentLimitSettings limits, int newEntryRelevantTurn, Func<StoryBible, Task> onSaveAsync)
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
            var selectedImportance = importance.SelectedIndex > 0 && int.TryParse(importance.SelectedItem?.ToString(), out var parsedFilter)
                ? parsedFilter
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
                    var categoryInput = new Entry { Text = entry.Category, Placeholder = "Category", MaxLength = limits.MaxStoryBibleCategoryCharacters };
                    var nameInput = new Entry { Text = entry.Name, Placeholder = "Name", MaxLength = limits.MaxStoryBibleNameCharacters };
                    var contentInput = new Editor { Text = entry.Content, Placeholder = "Content", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 };
                    var importanceInput = new Picker { Title = "Importance", ItemsSource = new[] { "1", "2", "3", "4", "5" } };
                    importanceInput.SelectedIndex = Math.Clamp(entry.Importance, 1, 5) - 1;
                    var details = new VerticalStackLayout
                    {
                        IsVisible = false,
                        Spacing = 4,
                        Children =
                        {
                            new Label { Text = "Category" }, categoryInput,
                            new Label { Text = "Name" }, nameInput,
                            new Label { Text = "Content" }, contentInput,
                            new Label { Text = "Importance" }, importanceInput,
                            Ui.Buttons(
                                Ui.Button("Save", async (_, _) =>
                                {
                                    var updated = entry with
                                    {
                                        Category = categoryInput.Text ?? "",
                                        Name = nameInput.Text ?? "",
                                        Content = contentInput.Text ?? "",
                                        Importance = int.TryParse(importanceInput.SelectedItem?.ToString(), out var parsedImportance) ? parsedImportance : entry.Importance
                                    };
                                    await onSaveAsync(new StoryBible(bible.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                                }),
                                Ui.Button("Remove", async (_, _) =>
                                {
                                    if (!await page.DisplayAlertAsync("Remove entry?", $"Remove \"{entry.Name}\" from the Story Bible?", "Remove", "Cancel")) return;
                                    await onSaveAsync(new StoryBible(bible.Entries.Where(x => x.Id != entry.Id).ToArray()));
                                })),
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

        var newCategory = new Entry { Placeholder = "Category", MaxLength = limits.MaxStoryBibleCategoryCharacters };
        var newName = new Entry { Placeholder = "Name", MaxLength = limits.MaxStoryBibleNameCharacters };
        var newContent = new Editor { Placeholder = "Content", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 };
        var newImportance = new Picker { Title = "Importance", ItemsSource = new[] { "1", "2", "3", "4", "5" }, SelectedIndex = 2 };
        var addForm = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 4,
            Children =
            {
                new Label { Text = "Category" }, newCategory,
                new Label { Text = "Name" }, newName,
                new Label { Text = "Content" }, newContent,
                new Label { Text = "Importance" }, newImportance
            }
        };
        addForm.Children.Add(Ui.Buttons(
            Ui.Button("Add", async (_, _) =>
            {
                var added = new StoryBibleEntry(
                    Guid.Empty,
                    newCategory.Text ?? "",
                    newName.Text ?? "",
                    newContent.Text ?? "",
                    int.TryParse(newImportance.SelectedItem?.ToString(), out var parsedNewImportance) ? parsedNewImportance : 3,
                    newEntryRelevantTurn);
                await onSaveAsync(new StoryBible(bible.Entries.Append(added).ToArray()));
            }),
            Ui.Button("Cancel", (_, _) =>
            {
                newCategory.Text = "";
                newName.Text = "";
                newContent.Text = "";
                newImportance.SelectedIndex = 2;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(bible).Length;
        body.Children.Add(new Label { Text = $"{bible.Entries.Count} active entries; {serializedBytes:N0} serialized bytes" });
        body.Children.Add(Ui.Button("Add Entry", (_, _) => addForm.IsVisible = !addForm.IsVisible));
        body.Children.Add(addForm);
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
