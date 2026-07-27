using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryPromptPage : ContentPage, IWorkspacePayloadPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid? _sourceId;
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _app;
    private readonly MainTabbedPage _tabs;
    private readonly Entry _title = new() { Placeholder = "Title (leave blank to generate one)" };
    private readonly Editor _prompt = new() { Placeholder = "Story Prompt", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 160 };
    private readonly ActivityIndicator _busy = new();
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;

    public StoryPromptPage(
        Guid? sourceId,
        IStoryDefinitionRepository repository,
        INarratorApplication app,
        MainTabbedPage tabs,
        StoryPromptDraft? restoredDraft = null,
        PendingOperationState? restoredOperation = null)
    {
        _sourceId = sourceId;
        _repository = repository;
        _app = app;
        _tabs = tabs;
        _pendingOperation = restoredOperation;
        Title = sourceId is null ? "New Definition" : "Edit Definition";
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 8,
                Children = { Ui.Heading(Title), new Label { Text = "Title" }, _title, new Label { Text = "Story Prompt" }, _prompt,
                    Ui.Button("Generate Story Definition", Generate), _busy }
            }
        };
        if (restoredDraft is not null)
        {
            _title.Text = restoredDraft.Title;
            _prompt.Text = restoredDraft.StoryPrompt;
        }
        _title.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
        _prompt.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
    }

    StoryPromptDraft? IWorkspacePayloadPage.StoryPromptDraft =>
        new(_sourceId, _title.Text ?? "", _prompt.Text ?? "");
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
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel generation?", "Generation is still in progress.", "Cancel and Close", "Keep Open")) return false;
            await ((IInFlightRequestPage)this).CancelInFlightRequestAsync();
        }
        if (string.IsNullOrWhiteSpace(_title.Text) && string.IsNullOrWhiteSpace(_prompt.Text)) return true;
        return await DisplayAlertAsync("Discard draft?", "This temporary Story Prompt draft will be discarded.", "Discard", "Keep Open");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_pendingOperation is not null)
        {
            _pendingOperation = null;
            await _tabs.SaveWorkspaceNowAsync();
            if (await DisplayActionSheetAsync(
                    "Generation was interrupted. Your draft is preserved.",
                    "Cancel",
                    null,
                    "Retry") == "Retry")
                Generate(null, EventArgs.Empty);
        }
        if (_sourceId is null || !string.IsNullOrEmpty(_title.Text)) return;
        try
        {
            var source = await _repository.GetAsync(_sourceId.Value) ?? throw new NarratorException("Story Definition not found.");
            _title.Text = source.Title;
            _prompt.Text = source.StoryPrompt;
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Generate(object? sender, EventArgs e)
    {
        if (_request is not null) return;
        var retry = false;
        try
        {
            _request = new();
            _busy.IsRunning = true;
            var draft = new StoryPromptDraft(_sourceId, _title.Text ?? "", _prompt.Text ?? "");
            var overwrite = _sourceId is not null && await DisplayAlertAsync("Save Definition", "Overwrite the existing definition?", "Overwrite", "Create New");
            var targetId = overwrite && _sourceId is not null ? _sourceId.Value : Guid.NewGuid();
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.GenerateStoryDefinition, targetId, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.GenerateDefinitionAsync(draft, overwrite, targetId, _request.Token);
            await _tabs.ReplaceCurrentWithDefinitionAsync(result.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            retry = await DisplayActionSheetAsync(
                $"Story Definition generation failed: {ex.Message}",
                "Cancel",
                null,
                "Retry") == "Retry";
        }
        finally
        {
            _pendingOperation = null;
            _busy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
        if (retry) Generate(null, EventArgs.Empty);
    }
}
