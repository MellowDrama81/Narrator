using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

internal static class StoryBibleView
{
    public static View Create(Page page, StoryBible bible, ContentLimitSettings limits, int newEntryRelevantTurn, Func<StoryBible, Task<StoryBible?>> onSaveAsync, bool alwaysExpanded = false)
    {
        // Keep the latest persisted model locally. Rebuilding the parent page immediately after a
        // Save removes the focused native Button while WinUI is still raising its Click event,
        // which can surface as a COMException despite the save having succeeded.
        var currentBible = bible;
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };
        var summary = new Label();
        var search = new SearchBar { Placeholder = "Search name or content" };
        var categories = new Picker { Title = "All categories" };
        var importance = new Picker { Title = "All importance levels" };
        categories.ItemsSource = new[] { "All categories" }
            .Concat(currentBible.Entries.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            .ToArray();
        importance.ItemsSource = new[] { "All importance levels" }
            .Concat(currentBible.Entries.Select(x => x.Importance).Distinct().OrderDescending().Select(x => x.ToString()))
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
            var filtered = currentBible.Entries.Where(x =>
                (string.IsNullOrEmpty(query) ||
                    x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.KnownFacts.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    x.SecretFacts.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))) &&
                (selectedCategory is null || x.Category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase)) &&
                (selectedImportance is null || x.Importance == selectedImportance));

            foreach (var group in filtered.GroupBy(x => x.Category).OrderBy(x => x.Key))
            {
                entries.Children.Add(new Label { Text = group.Key, FontAttributes = FontAttributes.Bold });
                foreach (var entry in group.OrderBy(x => x.Name))
                {
                    var categoryInput = new Entry { Text = entry.Category, Placeholder = "Category", MaxLength = limits.MaxStoryBibleCategoryCharacters };
                    var nameInput = new Entry { Text = entry.Name, Placeholder = "Name", MaxLength = limits.MaxStoryBibleNameCharacters };
                    var knownInput = new Editor
                    {
                        Text = string.Join('\n', entry.KnownFacts),
                        Placeholder = "One known fact per line",
                        AutoSize = EditorAutoSizeOption.TextChanges,
                        MinimumHeightRequest = 80
                    };
                    var secretInput = new Editor
                    {
                        Text = string.Join('\n', entry.SecretFacts),
                        Placeholder = "One secret fact per line",
                        AutoSize = EditorAutoSizeOption.TextChanges,
                        MinimumHeightRequest = 80
                    };
                    var importanceInput = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" } };
                    importanceInput.SelectedIndex = Math.Clamp(entry.Importance, 1, 5) - 1;
                    var details = new VerticalStackLayout
                    {
                        IsVisible = false,
                        Spacing = 4,
                        Children =
                        {
                            new Label { Text = "Category" }, categoryInput,
                            new Label { Text = "Name" }, nameInput,
                            new Label { Text = "Known facts" }, knownInput,
                            new Label { Text = "Secret facts (not yet known to the player character)" }, secretInput,
                            new Label { Text = "Importance" }, importanceInput,
                            Ui.Buttons(
                                Ui.Button("Save", async (_, _) =>
                                {
                                    var name = (nameInput.Text ?? "").Trim();
                                    if (string.IsNullOrWhiteSpace(name))
                                    {
                                        await page.DisplayAlertAsync("Name required", "Enter a name for this Story Bible entry.", "OK");
                                        return;
                                    }
                                    var updated = entry with
                                    {
                                        Category = categoryInput.Text ?? "",
                                        Name = name,
                                        KnownFacts = SplitFacts(knownInput.Text),
                                        SecretFacts = SplitFacts(secretInput.Text),
                                        Importance = int.TryParse(importanceInput.SelectedItem?.ToString(), out var parsedImportance) ? parsedImportance : entry.Importance
                                    };
                                    var saved = await onSaveAsync(new StoryBible(currentBible.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                                    if (saved is not null)
                                    {
                                        currentBible = saved;
                                        RenderAfterSave();
                                    }
                                }),
                                Ui.DestructiveButton("Remove", async (_, _) =>
                                {
                                    if (!await page.DisplayAlertAsync("Remove entry?", $"Remove \"{entry.Name}\" from the Story Bible?", "Remove", "Cancel")) return;
                                    var saved = await onSaveAsync(new StoryBible(currentBible.Entries.Where(x => x.Id != entry.Id).ToArray()));
                                    if (saved is not null)
                                    {
                                        currentBible = saved;
                                        RenderAfterSave();
                                    }
                                })),
                            new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                        }
                    };
                    var secretTag = entry.SecretFacts.Count == 0 ? "" : $", {entry.SecretFacts.Count} secret";
                    entries.Children.Add(new VerticalStackLayout
                    {
                        Children =
                        {
                            Ui.Button(
                                $"{entry.Name} [importance {entry.Importance}, relevant turn {entry.LastRelevantTurnNumber}{secretTag}]",
                                (_, _) => details.IsVisible = !details.IsVisible),
                            details
                        }
                    });
                }
            }
        }

        void RenderAfterSave()
        {
            // Refresh only the Bible rows after the native Click dispatch completes; the parent
            // page remains intact, avoiding the WinUI COMException caused by a full page rebuild.
            page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
            {
                Render();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(currentBible).Length;
                summary.Text = $"{currentBible.Entries.Count} active entries; {bytes:N0} serialized bytes";
            });
        }

        search.TextChanged += (_, _) => Render();
        categories.SelectedIndexChanged += (_, _) => Render();
        importance.SelectedIndexChanged += (_, _) => Render();
        Render();

        var newCategory = new Entry { Placeholder = "Category", MaxLength = limits.MaxStoryBibleCategoryCharacters };
        var newName = new Entry { Placeholder = "Name", MaxLength = limits.MaxStoryBibleNameCharacters };
        var newKnownFacts = new Editor { Placeholder = "One known fact per line", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 };
        var newSecretFacts = new Editor { Placeholder = "One secret fact per line", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 };
        var newImportance = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" }, SelectedIndex = 2 };
        var addForm = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 4,
            Children =
            {
                new Label { Text = "Category" }, newCategory,
                new Label { Text = "Name" }, newName,
                new Label { Text = "Known facts" }, newKnownFacts,
                new Label { Text = "Secret facts (not yet known to the player character)" }, newSecretFacts,
                new Label { Text = "Importance" }, newImportance
            }
        };
        addForm.Children.Add(Ui.Buttons(
            Ui.Button("Add", async (_, _) =>
            {
                var name = (newName.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    await page.DisplayAlertAsync("Name required", "Enter a name for this Story Bible entry.", "OK");
                    return;
                }
                var added = new StoryBibleEntry(
                    Guid.Empty,
                    newCategory.Text ?? "",
                    name,
                    SplitFacts(newKnownFacts.Text),
                    SplitFacts(newSecretFacts.Text),
                    int.TryParse(newImportance.SelectedItem?.ToString(), out var parsedNewImportance) ? parsedNewImportance : 3,
                    newEntryRelevantTurn);
                var saved = await onSaveAsync(new StoryBible(currentBible.Entries.Append(added).ToArray()));
                if (saved is not null)
                {
                    currentBible = saved;
                    RenderAfterSave();
                }
            }),
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newCategory.Text = "";
                newName.Text = "";
                newKnownFacts.Text = "";
                newSecretFacts.Text = "";
                newImportance.SelectedIndex = 2;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(currentBible).Length;
        summary.Text = $"{currentBible.Entries.Count} active entries; {serializedBytes:N0} serialized bytes";
        body.Children.Add(summary);
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

    private static IReadOnlyList<string> SplitFacts(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
