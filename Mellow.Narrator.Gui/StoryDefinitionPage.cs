using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryDefinitionPage : ContentPage, IWorkspacePayloadPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid _id;
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _application;
    private readonly MainTabbedPage _tabs;
    private readonly VerticalStackLayout _content = new() { Padding = 16, Spacing = 8 };
    private readonly ActivityIndicator _startBusy = new();
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;

    public StoryDefinitionPage(
        Guid id,
        IStoryDefinitionRepository repository,
        INarratorApplication application,
        MainTabbedPage tabs,
        PendingOperationState? restoredOperation = null)
    {
        _id = id;
        _repository = repository;
        _application = application;
        _tabs = tabs;
        _pendingOperation = restoredOperation;
        Title = "Definition";
        Content = new ScrollView { Content = _content };
    }

    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        while (_request is not null) await Task.Delay(20);
        if (marker is not null) _pendingOperation = marker;
    }

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is null) return true;
        if (!await DisplayAlertAsync("Cancel starting the story?", "Starting the story is still in progress.", "Cancel and Close", "Keep Open")) return false;
        await ((IInFlightRequestPage)this).CancelInFlightRequestAsync();
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_pendingOperation is not null)
        {
            _pendingOperation = null;
            await _tabs.SaveWorkspaceNowAsync();
            if (await DisplayActionSheetAsync(
                    "Starting the story was interrupted.",
                    "Cancel",
                    null,
                    "Retry") == "Retry")
            {
                await RefreshAsync();
                await StartStoryAsync();
                return;
            }
        }
        await RefreshAsync();
    }

    private async Task StartStoryAsync()
    {
        if (_request is not null) return;
        var retry = false;
        try
        {
            _request = new();
            _startBusy.IsRunning = true;
            var targetStateId = Guid.NewGuid();
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.GenerateOpeningScene, targetStateId, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            await _tabs.StartStoryAsync(_id, targetStateId, replaceCurrent: true, _request.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            retry = await DisplayActionSheetAsync(
                $"Starting the story failed: {ex.Message}",
                "Cancel",
                null,
                "Retry") == "Retry";
        }
        finally
        {
            _pendingOperation = null;
            _startBusy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
        if (retry) await StartStoryAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var value = await _repository.GetAsync(_id) ?? throw new NarratorException("Story Definition not found.");
            var settings = await _application.GetSettingsAsync();
            Title = value.Title;
            if (Parent is NavigationPage navigation) navigation.Title = Title;
            _content.Children.Clear();
            var titleEntry = new Entry { Text = value.Title, MaxLength = settings.ContentLimits.MaxStoryTitleCharacters, FontSize = 24, FontAttributes = FontAttributes.Bold };
            _content.Children.Add(titleEntry);
            var promptEditor = new Editor
            {
                Text = value.StoryPrompt,
                AutoSize = EditorAutoSizeOption.TextChanges,
                MinimumHeightRequest = 160
            };
            _content.Children.Add(promptEditor);
            _content.Children.Add(Ui.Heading("Initial Events"));
            _content.Children.Add(new Label
            {
                Text = "Sent to the LLM only for the earliest turns, then dropped once enough real history has accumulated. " +
                    "Use it to describe the starting state and what should happen in the first few scenes. Anything that " +
                    "must be remembered later belongs in the Story Bible instead.",
                FontSize = 12
            });
            var initialEventsEditor = new Editor
            {
                Text = value.InitialEventsPrompt,
                Placeholder = "No special guidance for the opening scenes.",
                AutoSize = EditorAutoSizeOption.TextChanges,
                MinimumHeightRequest = 120
            };
            _content.Children.Add(initialEventsEditor);
            _content.Children.Add(Ui.Buttons(
                Ui.Button("Save Definition", async (_, _) => await SaveDefinitionAsync(value, titleEntry.Text, promptEditor.Text, initialEventsEditor.Text)),
                Ui.Button("Start Story", async (_, _) => await StartStoryAsync()),
                Ui.SecondaryButton("Export", async (_, _) => await ExportAsync(value))));
            _content.Children.Add(Ui.Busy(_startBusy, "Starting…"));
            _content.Children.Add(Ui.Heading($"Initial Story Bible ({value.InitialStoryBible.Entries.Count})"));
            if (StoryBibleProcessor.IsApproachingLimits(value.InitialStoryBible, settings.StoryGeneration))
                _content.Children.Add(new Label { Text = "The Story Bible is approaching one or more configured limits.", TextColor = Colors.DarkOrange });
            _content.Children.Add(StoryBibleView.Create(this, value.InitialStoryBible, settings.ContentLimits, 0, SaveBibleAsync, alwaysExpanded: true));
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveDefinitionAsync(StoryDefinition value, string? title, string? prompt, string? initialEventsPrompt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title)) throw new NarratorException("Enter a title.");
            if (string.IsNullOrWhiteSpace(prompt)) throw new NarratorException("Enter a Story Prompt.");
            await _repository.SaveAsync(value with
            {
                Title = title,
                StoryPrompt = prompt,
                InitialEventsPrompt = initialEventsPrompt ?? "",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await RefreshAsync();
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
}

internal static class StoryBibleView
{
    public static View Create(Page page, StoryBible bible, ContentLimitSettings limits, int newEntryRelevantTurn, Func<StoryBible, Task> onSaveAsync, bool alwaysExpanded = false)
    {
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
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
                                Ui.DestructiveButton("Remove", async (_, _) =>
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
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newCategory.Text = "";
                newName.Text = "";
                newContent.Text = "";
                newImportance.SelectedIndex = 2;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(bible).Length;
        body.Children.Add(new Label { Text = $"{bible.Entries.Count} active entries; {serializedBytes:N0} serialized bytes" });
        body.Children.Add(Ui.SecondaryButton("Add Entry", (_, _) => addForm.IsVisible = !addForm.IsVisible));
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
        if (alwaysExpanded) return body;
        var toggle = Ui.SecondaryButton("Show / hide Bible", (_, _) => body.IsVisible = !body.IsVisible);
        return new VerticalStackLayout { Children = { toggle, body } };
    }
}
