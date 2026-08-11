using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

// Mirrors PlannedEventsView's structure/binding pattern, simplified: a Story Condition only has a
// description and a Secret toggle (no importance/urgency/prerequisites), so there is no filtering UI
// and no per-entry expand/collapse - just a flat list of description + secret checkbox rows.
internal static class ConditionsView
{
    public static View Create(Page page, StoryConditions conditions, ContentLimitSettings limits, Func<StoryConditions, Task<StoryConditions?>> onSaveAsync, bool alwaysExpanded = false)
    {
        // Retain the persisted state without replacing this view during the native button's Click
        // event; replacing the parent page at that point can cause a WinUI COMException.
        var currentConditions = conditions;
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };
        var summary = new Label();

        void Render()
        {
            entries.Children.Clear();
            foreach (var entry in currentConditions.Entries)
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
                                var saved = await onSaveAsync(new StoryConditions(currentConditions.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                                if (saved is not null)
                                {
                                    currentConditions = saved;
                                    RenderAfterSave();
                                }
                            }),
                            Ui.DestructiveButton("Remove", async (_, _) =>
                            {
                                if (!await page.DisplayAlertAsync("Remove condition?", $"Remove \"{entry.Description}\"?", "Remove", "Cancel")) return;
                                var saved = await onSaveAsync(new StoryConditions(currentConditions.Entries.Where(x => x.Id != entry.Id).ToArray()));
                                if (saved is not null)
                                {
                                    currentConditions = saved;
                                    RenderAfterSave();
                                }
                            })),
                        new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                    }
                });
            }
        }

        void RenderAfterSave()
        {
            // Refresh only these rows after the native Click dispatch completes. Rebuilding the
            // enclosing Story Definition page here can trigger a WinUI COMException.
            page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
            {
                Render();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(currentConditions).Length;
                summary.Text = $"{currentConditions.Entries.Count} conditions; {bytes:N0} serialized bytes";
            });
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
                var saved = await onSaveAsync(new StoryConditions(currentConditions.Entries.Append(added).ToArray()));
                if (saved is not null)
                {
                    currentConditions = saved;
                    newDescription.Text = "";
                    newSecret.IsChecked = true;
                    addForm.IsVisible = false;
                    RenderAfterSave();
                }
            }),
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newDescription.Text = "";
                newSecret.IsChecked = true;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(currentConditions).Length;
        summary.Text = $"{currentConditions.Entries.Count} conditions; {serializedBytes:N0} serialized bytes";
        body.Children.Add(summary);
        body.Children.Add(Ui.SecondaryButton("Add Condition", (_, _) => addForm.IsVisible = !addForm.IsVisible));
        body.Children.Add(addForm);
        body.Children.Add(entries);
        if (alwaysExpanded) return body;
        var toggle = Ui.SecondaryButton("Show / hide Conditions", (_, _) => body.IsVisible = !body.IsVisible);
        return new VerticalStackLayout { Children = { toggle, body } };
    }
}
