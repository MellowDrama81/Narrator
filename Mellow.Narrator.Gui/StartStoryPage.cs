using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class StartStoryPage : ContentPage, IWorkspacePayloadPage, ICloseGuardPage, IInFlightRequestPage
{
    private readonly Guid _definitionId;
    private readonly IStoryDefinitionRepository _definitions;
    private readonly INarratorApplication _app;
    private readonly MainTabbedPage _tabs;
    private readonly Label _question = new();
    private readonly Entry _answer = new();
    private readonly Label _warning = new() { TextColor = Colors.DarkOrange };
    private readonly Button _validate;
    private readonly Button _continue;
    private readonly ActivityIndicator _busy = new();
    private StoryDefinition? _definition;
    private int _index;
    private readonly List<PlayerResponse> _answers = [];
    private readonly StartStoryDraft? _restoredDraft;
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;

    public StartStoryPage(
        Guid definitionId,
        IStoryDefinitionRepository definitions,
        INarratorApplication app,
        MainTabbedPage tabs,
        StartStoryDraft? restoredDraft = null,
        PendingOperationState? restoredOperation = null)
    {
        _definitionId = definitionId;
        _definitions = definitions;
        _app = app;
        _tabs = tabs;
        _restoredDraft = restoredDraft;
        _pendingOperation = restoredOperation;
        Title = "Start Story";
        _validate = Ui.Button("Validate Answer", Validate);
        _continue = Ui.Button("Continue With Current Answer", Continue);
        _continue.IsVisible = false;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 8,
                Children = { Ui.Heading("Start Story"), _question, _answer, _warning, Ui.Buttons(_validate, _continue), _busy }
            }
        };
        _answer.TextChanged += (_, _) => _tabs.ScheduleWorkspaceSave();
    }

    StartStoryDraft? IWorkspacePayloadPage.StartStoryDraft
    {
        get
        {
            if (_definition is null) return _restoredDraft;
            var questions = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
            var values = questions.Select((q, i) =>
            {
                var accepted = _answers.FirstOrDefault(x => x.QuestionId == q.Id);
                if (accepted is not null) return new PlayerAnswerDraft(q.Id, accepted.Answer, PlayerAnswerValidationStatus.Valid, null);
                return new PlayerAnswerDraft(q.Id, i == _index ? _answer.Text ?? "" : "", PlayerAnswerValidationStatus.NotValidated, null);
            }).ToArray();
            return new(_definitionId, new(_definition.Title, _definition.StoryPrompt, _definition.PlayerQuestions, _definition.InitialStoryBible), _index, values);
        }
    }
    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    void IInFlightRequestPage.CancelInFlightRequest() => _request?.Cancel();

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel request?", "An LLM request is still in progress.", "Cancel and Close", "Keep Open")) return false;
            _request.Cancel();
        }
        if (_answers.Count == 0 && string.IsNullOrWhiteSpace(_answer.Text)) return true;
        return await DisplayAlertAsync("Discard setup progress?", "Your temporary player answers will be discarded.", "Discard", "Keep Open");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (_pendingOperation is not null)
            {
                _pendingOperation = null;
                await _tabs.SaveWorkspaceNowAsync();
                await DisplayAlertAsync("Request interrupted", "The incomplete operation was rolled back. Your answers are preserved; use the current action to retry, or close the tab to cancel.", "OK");
            }
            _definition = await _definitions.GetAsync(_definitionId) ?? throw new NarratorException("Story Definition not found.");
            if (_restoredDraft is not null && _answers.Count == 0)
            {
                _index = Math.Clamp(_restoredDraft.CurrentQuestionIndex, 0, _definition.PlayerQuestions.Count);
                foreach (var item in _restoredDraft.PlayerAnswers.Take(_index))
                {
                    var question = _definition.PlayerQuestions.First(x => x.Id == item.QuestionId);
                    _answers.Add(new(item.QuestionId, question.Question, question.ValidationInstruction, item.Answer));
                }
                if (_index < _restoredDraft.PlayerAnswers.Count) _answer.Text = _restoredDraft.PlayerAnswers[_index].Answer;
            }
            Title = $"Start {_definition.Title}";
            if (!await EnsureLimits()) return;
            await ShowCurrentOrStart();
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task<bool> EnsureLimits()
    {
        if (_definition is null) return false;
        var settings = await _app.GetSettingsAsync();
        if (StoryBibleProcessor.IsWithinLimits(_definition.InitialStoryBible, settings.StoryGeneration)) return true;
        var choice = await DisplayActionSheetAsync("Story Bible exceeds current limits", "Cancel", null, "Increase Limits", "Automatically Cull");
        if (choice == "Increase Limits") { _tabs.OpenSettings(); return false; }
        if (choice != "Automatically Cull") return false;
        var preview = StoryBibleProcessor.CullToLimits(_definition.InitialStoryBible, settings.StoryGeneration);
        var names = string.Join(Environment.NewLine, preview.Changes.Select(x => $"• {x.Before?.Name}"));
        if (!await DisplayAlertAsync("Cull Story Bible?", $"These entries will be removed:\n{names}", "Cull", "Cancel")) return false;
        _definition = await _app.CullDefinitionAsync(_definitionId);
        return true;
    }

    private async void Validate(object? sender, EventArgs e)
    {
        if (_definition is null || _index >= _definition.PlayerQuestions.Count || _request is not null) return;
        try
        {
            _request = new();
            _busy.IsRunning = true;
            var current = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ElementAt(_index);
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.ValidatePlayerAnswer, null, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var response = await _app.ValidateAnswerAsync(_definitionId, current, _answer.Text ?? "", _answers, _request.Token);
            if (response.HasWarning)
            {
                _warning.Text = response.Warning;
                _continue.IsVisible = true;
            }
            else
            {
                Accept(current);
                await ShowCurrentOrStart();
            }
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

    private async void Continue(object? sender, EventArgs e)
    {
        if (_definition is null) return;
        Accept(_definition.PlayerQuestions.OrderBy(x => x.SortOrder).ElementAt(_index));
        await ShowCurrentOrStart();
    }

    private void Accept(PlayerQuestion current)
    {
        _answers.Add(new(current.Id, current.Question, current.ValidationInstruction, _answer.Text ?? ""));
        _index++;
        _answer.Text = "";
        _warning.Text = "";
        _continue.IsVisible = false;
        _tabs.ScheduleWorkspaceSave();
    }

    private async Task ShowCurrentOrStart()
    {
        if (_definition is null) return;
        var questions = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
        if (_index < questions.Length)
        {
            _question.Text = $"Question {_index + 1} of {questions.Length}\n{questions[_index].Question}";
            return;
        }
        try
        {
            if (_request is not null) return;
            _request = new();
            _busy.IsRunning = true;
            _question.Text = "Generating opening scene…";
            _validate.IsVisible = false;
            _answer.IsVisible = false;
            var targetStateId = Guid.NewGuid();
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.GenerateOpeningScene, targetStateId, 0, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.StartStoryAsync(_definitionId, _answers, targetStateId, _request.Token);
            await _tabs.ReplaceCurrentWithPlayAsync(result.State.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Ui.Error(this, ex); _validate.IsVisible = true; }
        finally
        {
            _pendingOperation = null;
            _busy.IsRunning = false;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
    }
}
