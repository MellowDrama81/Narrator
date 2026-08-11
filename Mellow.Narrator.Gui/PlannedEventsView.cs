using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

internal static class PlannedEventsView
{
    public static View Create(Page page, PlannedEvents events, ContentLimitSettings limits, int newEntryRelevantTurn, Func<PlannedEvents, Task<PlannedEvents?>> onSaveAsync, bool alwaysExpanded = false)
    {
        // Keep the latest persisted model locally. Rebuilding the parent page while its native Save
        // Button is raising Click can surface as a WinUI COMException even after persistence succeeds.
        var currentEvents = events;
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };
        var summary = new Label();
        var search = new SearchBar { Placeholder = "Search description" };
        var importance = new Picker { Title = "All importance levels" };
        importance.ItemsSource = new[] { "All importance levels" }
            .Concat(currentEvents.Entries.Select(x => x.Importance).Distinct().OrderDescending().Select(x => x.ToString()))
            .ToArray();
        importance.SelectedIndex = 0;
        var urgency = new Picker { Title = "All urgency levels" };
        urgency.ItemsSource = new[] { "All urgency levels" }
            .Concat(currentEvents.Entries.Select(x => x.Urgency).Distinct().OrderDescending().Select(x => x.ToString()))
            .ToArray();
        urgency.SelectedIndex = 0;

        void Render()
        {
            entries.Children.Clear();
            var query = search.Text?.Trim();
            var selectedImportance = importance.SelectedIndex > 0 && int.TryParse(importance.SelectedItem?.ToString(), out var parsedImportanceFilter)
                ? parsedImportanceFilter
                : (int?)null;
            var selectedUrgency = urgency.SelectedIndex > 0 && int.TryParse(urgency.SelectedItem?.ToString(), out var parsedUrgencyFilter)
                ? parsedUrgencyFilter
                : (int?)null;
            var filtered = currentEvents.Entries.Where(x =>
                (string.IsNullOrEmpty(query) || x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                (selectedImportance is null || x.Importance == selectedImportance) &&
                (selectedUrgency is null || x.Urgency == selectedUrgency));

            foreach (var entry in filtered.OrderByDescending(x => x.Importance).ThenByDescending(x => x.Urgency).ThenBy(x => x.LastRelevantTurnNumber))
            {
                var descriptionInput = new Editor
                {
                    Text = entry.Description,
                    Placeholder = "Description",
                    MaxLength = limits.MaxPlannedEventDescriptionCharacters,
                    AutoSize = EditorAutoSizeOption.TextChanges,
                    MinimumHeightRequest = 80
                };
                var importanceInput = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" } };
                importanceInput.SelectedIndex = Math.Clamp(entry.Importance, 1, 5) - 1;
                var urgencyInput = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" } };
                urgencyInput.SelectedIndex = Math.Clamp(entry.Urgency, 1, 5) - 1;
                var mandatory = entry.Importance == PlannedEventProcessor.MandatoryImportance;
                var conditionInput = new Editor
                {
                    Text = entry.Condition ?? "",
                    Placeholder = "None - pursuable immediately",
                    MaxLength = limits.MaxPlannedEventConditionCharacters,
                    AutoSize = EditorAutoSizeOption.TextChanges,
                    MinimumHeightRequest = 60
                };
                var details = new VerticalStackLayout
                {
                    IsVisible = false,
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = "Description" }, descriptionInput,
                        new Label { Text = "Importance (5 is mandatory: the narrator must force it to happen)" }, importanceInput,
                        new Label { Text = "Urgency (5 = steer toward it now; 1 = let it emerge naturally)" }, urgencyInput,
                        new Label { Text = "Condition (what must happen, or what state the story must be in, first)" }, conditionInput,
                        Ui.Buttons(
                            Ui.Button("Save", async (_, _) =>
                            {
                                var description = (descriptionInput.Text ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(description))
                                {
                                    await page.DisplayAlertAsync("Description required", "Enter a description for this Planned Event.", "OK");
                                    return;
                                }
                                var condition = (conditionInput.Text ?? "").Trim();
                                var updated = entry with
                                {
                                    Description = description,
                                    Importance = int.TryParse(importanceInput.SelectedItem?.ToString(), out var parsedImportance) ? parsedImportance : entry.Importance,
                                    Urgency = int.TryParse(urgencyInput.SelectedItem?.ToString(), out var parsedUrgency) ? parsedUrgency : entry.Urgency,
                                    Condition = string.IsNullOrWhiteSpace(condition) ? null : condition
                                };
                                var saved = await onSaveAsync(new PlannedEvents(currentEvents.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                                if (saved is not null)
                                {
                                    currentEvents = saved;
                                    RenderAfterSave();
                                }
                            }),
                            Ui.DestructiveButton("Remove", async (_, _) =>
                            {
                                var prompt = mandatory
                                    ? $"\"{entry.Description}\" is a mandatory Planned Event. Remove it anyway?"
                                    : $"Remove \"{entry.Description}\" from Planned Events?";
                                if (!await page.DisplayAlertAsync("Remove Planned Event?", prompt, "Remove", "Cancel")) return;
                                var saved = await onSaveAsync(new PlannedEvents(currentEvents.Entries.Where(x => x.Id != entry.Id).ToArray()));
                                if (saved is not null)
                                {
                                    currentEvents = saved;
                                    RenderAfterSave();
                                }
                            })),
                        new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                    }
                };
                var tag = mandatory ? "mandatory" : $"importance {entry.Importance}";
                var conditionTag = string.IsNullOrWhiteSpace(entry.Condition) ? "" : ", conditional";
                entries.Children.Add(new VerticalStackLayout
                {
                    Children =
                    {
                        Ui.Button(
                            $"{Summarize(entry.Description)} [{tag}, urgency {entry.Urgency}, relevant turn {entry.LastRelevantTurnNumber}{conditionTag}]",
                            (_, _) => details.IsVisible = !details.IsVisible),
                        details
                    }
                });
            }
        }

        void RenderAfterSave()
        {
            // Replace only the event rows after the native Click dispatch has completed. This makes
            // successful Add/Remove actions visible without rebuilding the Story Definition page.
            page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
            {
                Render();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(currentEvents).Length;
                summary.Text = $"{currentEvents.Entries.Count} active Planned Events; {bytes:N0} serialized bytes";
            });
        }

        search.TextChanged += (_, _) => Render();
        importance.SelectedIndexChanged += (_, _) => Render();
        urgency.SelectedIndexChanged += (_, _) => Render();
        Render();

        var newDescription = new Editor
        {
            Placeholder = "Description",
            MaxLength = limits.MaxPlannedEventDescriptionCharacters,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 80
        };
        var newImportance = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" }, SelectedIndex = 2 };
        var newUrgency = new Picker { ItemsSource = new[] { "1", "2", "3", "4", "5" }, SelectedIndex = 2 };
        var newCondition = new Editor
        {
            Placeholder = "None - pursuable immediately",
            MaxLength = limits.MaxPlannedEventConditionCharacters,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 60
        };
        var addForm = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 4,
            Children =
            {
                new Label { Text = "Description" }, newDescription,
                new Label { Text = "Importance (5 is mandatory: the narrator must force it to happen)" }, newImportance,
                new Label { Text = "Urgency (5 = steer toward it now; 1 = let it emerge naturally)" }, newUrgency,
                new Label { Text = "Condition (what must happen, or what state the story must be in, first)" }, newCondition
            }
        };
        addForm.Children.Add(Ui.Buttons(
            Ui.Button("Add", async (_, _) =>
            {
                var description = (newDescription.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(description))
                {
                    await page.DisplayAlertAsync("Description required", "Enter a description for this Planned Event.", "OK");
                    return;
                }
                var condition = (newCondition.Text ?? "").Trim();
                var added = new PlannedEvent(
                    Guid.Empty,
                    description,
                    int.TryParse(newImportance.SelectedItem?.ToString(), out var parsedNewImportance) ? parsedNewImportance : 3,
                    int.TryParse(newUrgency.SelectedItem?.ToString(), out var parsedNewUrgency) ? parsedNewUrgency : 3,
                    string.IsNullOrWhiteSpace(condition) ? null : condition,
                    newEntryRelevantTurn);
                var saved = await onSaveAsync(new PlannedEvents(currentEvents.Entries.Append(added).ToArray()));
                if (saved is not null)
                {
                    currentEvents = saved;
                    newDescription.Text = "";
                    newImportance.SelectedIndex = 2;
                    newUrgency.SelectedIndex = 2;
                    newCondition.Text = "";
                    addForm.IsVisible = false;
                    RenderAfterSave();
                }
            }),
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newDescription.Text = "";
                newImportance.SelectedIndex = 2;
                newUrgency.SelectedIndex = 2;
                newCondition.Text = "";
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(currentEvents).Length;
        summary.Text = $"{currentEvents.Entries.Count} active Planned Events; {serializedBytes:N0} serialized bytes";
        body.Children.Add(summary);
        body.Children.Add(Ui.SecondaryButton("Add Planned Event", (_, _) => addForm.IsVisible = !addForm.IsVisible));
        body.Children.Add(addForm);
        body.Children.Add(search);
        var filters = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) },
            Children = { importance, urgency }
        };
        Grid.SetColumn(urgency, 1);
        body.Children.Add(filters);
        body.Children.Add(entries);
        if (alwaysExpanded) return body;
        var toggle = Ui.SecondaryButton("Show / hide Planned Events", (_, _) => body.IsVisible = !body.IsVisible);
        return new VerticalStackLayout { Children = { toggle, body } };
    }

    private static string Summarize(string description) =>
        description.Length <= 80 ? description : description[..80] + "…";
}
