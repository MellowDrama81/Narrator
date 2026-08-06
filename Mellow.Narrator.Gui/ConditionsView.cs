using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

// Mirrors PlannedEventsView's structure/binding pattern, simplified: a Story Condition only has a
// description and a Secret toggle (no importance/urgency/prerequisites), so there is no filtering UI
// and no per-entry expand/collapse - just a flat list of description + secret checkbox rows.
internal static class ConditionsView
{
    public static View Create(Page page, StoryConditions conditions, ContentLimitSettings limits, Func<StoryConditions, Task> onSaveAsync, bool alwaysExpanded = false)
    {
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };

        void Render()
        {
            entries.Children.Clear();
            foreach (var entry in conditions.Entries)
            {
                var descriptionInput = new Editor
                {
                    Text = entry.Description,
                    Placeholder = "Description",
                    MaxLength = limits.MaxConditionDescriptionCharacters,
                    AutoSize = EditorAutoSizeOption.TextChanges,
                    MinimumHeightRequest = 80
                };
                var secretInput = new CheckBox { IsChecked = entry.Secret };
                entries.Children.Add(new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = "Description" }, descriptionInput,
                        new HorizontalStackLayout
                        {
                            Spacing = 6,
                            Children = { secretInput, new Label { Text = "Secret (never stated directly in narration)", VerticalOptions = LayoutOptions.Center } }
                        },
                        Ui.Buttons(
                            Ui.Button("Save", async (_, _) =>
                            {
                                var description = (descriptionInput.Text ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(description))
                                {
                                    await page.DisplayAlertAsync("Description required", "Enter a description for this condition.", "OK");
                                    return;
                                }
                                var updated = entry with { Description = description, Secret = secretInput.IsChecked };
                                await onSaveAsync(new StoryConditions(conditions.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                            }),
                            Ui.DestructiveButton("Remove", async (_, _) =>
                            {
                                if (!await page.DisplayAlertAsync("Remove condition?", $"Remove \"{entry.Description}\"?", "Remove", "Cancel")) return;
                                await onSaveAsync(new StoryConditions(conditions.Entries.Where(x => x.Id != entry.Id).ToArray()));
                            })),
                        new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                    }
                });
            }
        }

        Render();

        var newDescription = new Editor
        {
            Placeholder = "Description",
            MaxLength = limits.MaxConditionDescriptionCharacters,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 80
        };
        var newSecret = new CheckBox { IsChecked = true };
        var addForm = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 4,
            Children =
            {
                new Label { Text = "Description" }, newDescription,
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children = { newSecret, new Label { Text = "Secret (never stated directly in narration)", VerticalOptions = LayoutOptions.Center } }
                }
            }
        };
        addForm.Children.Add(Ui.Buttons(
            Ui.Button("Add", async (_, _) =>
            {
                var description = (newDescription.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(description))
                {
                    await page.DisplayAlertAsync("Description required", "Enter a description for this condition.", "OK");
                    return;
                }
                var added = new StoryCondition(Guid.Empty, description, newSecret.IsChecked);
                await onSaveAsync(new StoryConditions(conditions.Entries.Append(added).ToArray()));
            }),
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newDescription.Text = "";
                newSecret.IsChecked = true;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(conditions).Length;
        body.Children.Add(new Label { Text = $"{conditions.Entries.Count} conditions; {serializedBytes:N0} serialized bytes" });
        body.Children.Add(Ui.SecondaryButton("Add Condition", (_, _) => addForm.IsVisible = !addForm.IsVisible));
        body.Children.Add(addForm);
        body.Children.Add(entries);
        if (alwaysExpanded) return body;
        var toggle = Ui.SecondaryButton("Show / hide Conditions", (_, _) => body.IsVisible = !body.IsVisible);
        return new VerticalStackLayout { Children = { toggle, body } };
    }
}
