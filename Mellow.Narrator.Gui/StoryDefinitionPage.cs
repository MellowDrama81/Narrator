using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryDefinitionPage : ContentPage, IPendingOperationPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid _id;
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _application;
    private readonly MainTabbedPage _tabs;
    private readonly VerticalStackLayout _content = new() { Padding = 16, Spacing = 8 };
    private readonly ActivityIndicator _startBusy = new();
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;
    private bool _loaded;

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

    PendingOperationState? IPendingOperationPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        await Ui.WaitWhileAsync(() => _request is not null, TimeSpan.FromSeconds(5));
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
                _loaded = true;
                await StartStoryAsync();
                return;
            }
        }
        // TabbedPage raises OnAppearing on every tab switch, not just the first time this page is
        // shown, so the form is only (re)built once here - otherwise switching tabs away and back
        // silently discards unsaved Title/Story Prompt/Initial Events edits.
        if (_loaded) return;
        await RefreshAsync();
        _loaded = true;
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
            _content.Children.Add(Ui.Heading($"Initial Planned Events ({value.InitialPlannedEvents.Entries.Count})"));
            _content.Children.Add(new Label
            {
                Text = "Future plot points kept secret from the player. Importance 5 is mandatory: the narrator must " +
                    "find a way to make it happen no matter how the player's choices diverge. Urgency controls how " +
                    "directly and soon the narrator should steer toward it, independent of importance. Prerequisites " +
                    "let an event require one or more other events to occur first; the narrator will not pursue it " +
                    "until every prerequisite has happened.",
                FontSize = 12
            });
            if (PlannedEventProcessor.IsApproachingLimits(value.InitialPlannedEvents, settings.StoryGeneration))
                _content.Children.Add(new Label { Text = "Planned Events are approaching one or more configured limits.", TextColor = Colors.DarkOrange });
            _content.Children.Add(PlannedEventsView.Create(this, value.InitialPlannedEvents, settings.ContentLimits, 0, SavePlannedEventsAsync, alwaysExpanded: true));
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveDefinitionAsync(StoryDefinition value, string? title, string? prompt, string? initialEventsPrompt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title)) throw new NarratorException("Enter a title.");
            if (string.IsNullOrWhiteSpace(prompt)) throw new NarratorException("Enter a Story Prompt.");
            var saved = value with
            {
                Title = title,
                StoryPrompt = prompt,
                InitialEventsPrompt = initialEventsPrompt ?? "",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.SaveAsync(saved);
            // Deliberately not a full RefreshAsync(): that would tear down and rebuild the whole form,
            // losing focus/cursor position and collapsing any expanded Story Bible entries immediately
            // after every save. Only Title/StoryPrompt/InitialEventsPrompt/UpdatedAtUtc changed, and the
            // live editors already hold the saved text, so just reflect the new title.
            Title = saved.Title;
            if (Parent is NavigationPage navigation) navigation.Title = Title;
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveBibleAsync(StoryBible next)
    {
        try { await _application.UpdateInitialStoryBibleAsync(_id, next); await RefreshAsync(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SavePlannedEventsAsync(PlannedEvents next)
    {
        try { await _application.UpdateInitialPlannedEventsAsync(_id, next); await RefreshAsync(); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task ExportAsync(StoryDefinition definition)
    {
        try { await ImportExportService.ExportDefinitionAsync(definition); }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }
}
