using System.Text.Json;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

internal static class PlannedEventsView
{
    public static View Create(Page page, PlannedEvents events, ContentLimitSettings limits, int newEntryRelevantTurn, Func<PlannedEvents, Task> onSaveAsync, bool alwaysExpanded = false)
    {
        var body = new VerticalStackLayout { IsVisible = alwaysExpanded, Spacing = 8 };
        var entries = new VerticalStackLayout { Spacing = 8 };
        var search = new SearchBar { Placeholder = "Search description" };
        var importance = new Picker { Title = "All importance levels" };
        importance.ItemsSource = new[] { "All importance levels" }
            .Concat(events.Entries.Select(x => x.Importance).Distinct().OrderDescending().Select(x => x.ToString()))
            .ToArray();
        importance.SelectedIndex = 0;
        var urgency = new Picker { Title = "All urgency levels" };
        urgency.ItemsSource = new[] { "All urgency levels" }
            .Concat(events.Entries.Select(x => x.Urgency).Distinct().OrderDescending().Select(x => x.ToString()))
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
            var filtered = events.Entries.Where(x =>
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
                var selectedPrerequisites = entry.PrerequisiteEventIds.ToHashSet();
                var (prerequisitesSection, _) = BuildPrerequisitesPicker(
                    events.Entries.Where(x => x.Id != entry.Id), selectedPrerequisites);
                var details = new VerticalStackLayout
                {
                    IsVisible = false,
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = "Description" }, descriptionInput,
                        new Label { Text = "Importance (5 is mandatory: the narrator must force it to happen)" }, importanceInput,
                        new Label { Text = "Urgency (5 = steer toward it now; 1 = let it emerge naturally)" }, urgencyInput,
                        new Label { Text = "Prerequisites (must occur before this is pursued)" }, prerequisitesSection,
                        Ui.Buttons(
                            Ui.Button("Save", async (_, _) =>
                            {
                                var description = (descriptionInput.Text ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(description))
                                {
                                    await page.DisplayAlertAsync("Description required", "Enter a description for this Planned Event.", "OK");
                                    return;
                                }
                                var updated = entry with
                                {
                                    Description = description,
                                    Importance = int.TryParse(importanceInput.SelectedItem?.ToString(), out var parsedImportance) ? parsedImportance : entry.Importance,
                                    Urgency = int.TryParse(urgencyInput.SelectedItem?.ToString(), out var parsedUrgency) ? parsedUrgency : entry.Urgency,
                                    PrerequisiteEventIds = selectedPrerequisites.ToArray()
                                };
                                await onSaveAsync(new PlannedEvents(events.Entries.Select(x => x.Id == entry.Id ? updated : x).ToArray()));
                            }),
                            Ui.DestructiveButton("Remove", async (_, _) =>
                            {
                                var dependents = events.Entries
                                    .Where(x => x.Id != entry.Id && x.PrerequisiteEventIds.Contains(entry.Id))
                                    .Select(x => Summarize(x.Description))
                                    .ToArray();
                                var prompt = mandatory
                                    ? $"\"{entry.Description}\" is a mandatory Planned Event. Remove it anyway?"
                                    : $"Remove \"{entry.Description}\" from Planned Events?";
                                if (dependents.Length > 0)
                                    prompt += $" This is a prerequisite for: {string.Join(", ", dependents)}.";
                                if (!await page.DisplayAlertAsync("Remove Planned Event?", prompt, "Remove", "Cancel")) return;
                                await onSaveAsync(new PlannedEvents(events.Entries.Where(x => x.Id != entry.Id).ToArray()));
                            })),
                        new Label { Text = $"Stable ID: {entry.Id:D}", FontSize = 11 }
                    }
                };
                var tag = mandatory ? "mandatory" : $"importance {entry.Importance}";
                var pendingPrerequisites = entry.PrerequisiteEventIds.Count(id => events.Entries.Any(x => x.Id == id));
                var prerequisiteTag = pendingPrerequisites == 0 ? "" : $", {pendingPrerequisites} prerequisite(s) pending";
                entries.Children.Add(new VerticalStackLayout
                {
                    Children =
                    {
                        Ui.Button(
                            $"{Summarize(entry.Description)} [{tag}, urgency {entry.Urgency}, relevant turn {entry.LastRelevantTurnNumber}{prerequisiteTag}]",
                            (_, _) => details.IsVisible = !details.IsVisible),
                        details
                    }
                });
            }
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
        var newSelectedPrerequisites = new HashSet<Guid>();
        var (newPrerequisitesSection, newPrerequisiteBoxes) = BuildPrerequisitesPicker(events.Entries, newSelectedPrerequisites);
        var addForm = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 4,
            Children =
            {
                new Label { Text = "Description" }, newDescription,
                new Label { Text = "Importance (5 is mandatory: the narrator must force it to happen)" }, newImportance,
                new Label { Text = "Urgency (5 = steer toward it now; 1 = let it emerge naturally)" }, newUrgency,
                new Label { Text = "Prerequisites (must occur before this is pursued)" }, newPrerequisitesSection
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
                var added = new PlannedEvent(
                    Guid.Empty,
                    description,
                    int.TryParse(newImportance.SelectedItem?.ToString(), out var parsedNewImportance) ? parsedNewImportance : 3,
                    int.TryParse(newUrgency.SelectedItem?.ToString(), out var parsedNewUrgency) ? parsedNewUrgency : 3,
                    newSelectedPrerequisites.ToArray(),
                    newEntryRelevantTurn);
                await onSaveAsync(new PlannedEvents(events.Entries.Append(added).ToArray()));
            }),
            Ui.SecondaryButton("Cancel", (_, _) =>
            {
                newDescription.Text = "";
                newImportance.SelectedIndex = 2;
                newUrgency.SelectedIndex = 2;
                foreach (var box in newPrerequisiteBoxes) box.IsChecked = false;
                addForm.IsVisible = false;
            })));

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(events).Length;
        body.Children.Add(new Label { Text = $"{events.Entries.Count} active Planned Events; {serializedBytes:N0} serialized bytes" });
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

    // Builds a checklist of candidate Planned Events to depend on, toggling membership in `selected`
    // (owned by the caller) as boxes are checked/unchecked. Returns the checkboxes too so a Cancel
    // handler can reset them - unchecking a box fires CheckedChanged, which keeps `selected` in sync.
    private static (View Section, List<CheckBox> Boxes) BuildPrerequisitesPicker(
        IEnumerable<PlannedEvent> candidates, HashSet<Guid> selected)
    {
        var list = new VerticalStackLayout { Spacing = 2 };
        var boxes = new List<CheckBox>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Importance).ThenBy(x => x.LastRelevantTurnNumber))
        {
            var checkBox = new CheckBox { IsChecked = selected.Contains(candidate.Id) };
            checkBox.CheckedChanged += (_, e) =>
            {
                if (e.Value) selected.Add(candidate.Id);
                else selected.Remove(candidate.Id);
            };
            boxes.Add(checkBox);
            list.Children.Add(new HorizontalStackLayout
            {
                Spacing = 6,
                Children = { checkBox, new Label { Text = Summarize(candidate.Description), VerticalOptions = LayoutOptions.Center } }
            });
        }
        if (list.Children.Count == 0)
            list.Children.Add(new Label { Text = "No other Planned Events to depend on yet.", FontSize = 11, TextColor = Colors.Gray });
        return (list, boxes);
    }

    private static string Summarize(string description) =>
        description.Length <= 80 ? description : description[..80] + "…";
}
