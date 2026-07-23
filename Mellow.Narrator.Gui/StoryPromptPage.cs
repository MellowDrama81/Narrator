using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StoryPromptPage : ContentPage, IWorkspacePayloadPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid? _sourceId;
    private readonly IStoryDefinitionRepository _repository;
    private readonly INarratorApplication _app;
    private readonly MainTabbedPage _tabs;
    private readonly Entry _title = new() { Placeholder = "Title" };
    private readonly Editor _prompt = new() { Placeholder = "Story Prompt", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 160 };
    private readonly Editor _questions = new() { Placeholder = "One per line: Question | Validation instruction", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 120 };
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
                    new Label { Text = "Player questions" }, _questions, Ui.Button("Generate Story Definition", Generate), _busy }
            }
        };
        if (restoredDraft is not null)
        {
            _title.Text = restoredDraft.Title;
            _prompt.Text = restoredDraft.StoryPrompt;
            _questions.Text = string.Join(Environment.NewLine, restoredDraft.PlayerQuestions.OrderBy(x => x.SortOrder).Select(x => $"{x.Question} | {x.ValidationInstruction}"));
        }
        _title.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
        _prompt.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
        _questions.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
    }

    StoryPromptDraft? IWorkspacePayloadPage.StoryPromptDraft =>
        new(_sourceId, _title.Text ?? "", _prompt.Text ?? "", ParseQuestions(_questions.Text));
    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    void IInFlightRequestPage.CancelInFlightRequest() => _request?.Cancel();

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel generation?", "Generation is still in progress.", "Cancel and Close", "Keep Open")) return false;
            _request.Cancel();
        }
        if (string.IsNullOrWhiteSpace(_title.Text) && string.IsNullOrWhiteSpace(_prompt.Text) && string.IsNullOrWhiteSpace(_questions.Text)) return true;
        return await DisplayAlertAsync("Discard draft?", "This temporary Story Prompt draft will be discarded.", "Discard", "Keep Open");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_pendingOperation is not null)
        {
            _pendingOperation = null;
            await _tabs.SaveWorkspaceNowAsync();
            await DisplayAlertAsync("Generation interrupted", "The incomplete operation was rolled back. Your draft is preserved; choose Generate Story Definition to retry, or close the tab to cancel.", "OK");
        }
        if (_sourceId is null || !string.IsNullOrEmpty(_title.Text)) return;
        try
        {
            var source = await _repository.GetAsync(_sourceId.Value) ?? throw new NarratorException("Story Definition not found.");
            _title.Text = source.Title;
            _prompt.Text = source.StoryPrompt;
            _questions.Text = string.Join(Environment.NewLine, source.PlayerQuestions.OrderBy(x => x.SortOrder)
                .Select(x => $"{x.Question} | {x.ValidationInstruction}"));
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Generate(object? sender, EventArgs e)
    {
        if (_request is not null) return;
        try
        {
            _request = new();
            _busy.IsRunning = true;
            var draft = new StoryPromptDraft(_sourceId, _title.Text ?? "", _prompt.Text ?? "", ParseQuestions(_questions.Text));
            var overwrite = _sourceId is not null && await DisplayAlertAsync("Save Definition", "Overwrite the existing definition?", "Overwrite", "Create New");
            var targetId = overwrite && _sourceId is not null ? _sourceId.Value : Guid.NewGuid();
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.GenerateStoryDefinition, targetId, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.GenerateDefinitionAsync(draft, overwrite, targetId, _request.Token);
            await _tabs.ReplaceCurrentWithDefinitionAsync(result.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Ui.Error(this, ex); }
        finally
        {
            _pendingOperation = null;
            _busy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
    }

    private static IReadOnlyList<PlayerQuestionDraft> ParseQuestions(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select((line, index) =>
        {
            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            return new PlayerQuestionDraft(Guid.NewGuid(), parts[0], parts.Length > 1 ? parts[1] : "The answer should be appropriate for the story.", index);
        }).ToArray();
}
