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
    private readonly StartStoryDraft? _restoredDraft;
    private readonly List<PlayerAnswerDraft> _answers = [];
    private readonly List<StoryBibleMaintenanceRecord> _maintenance = [];
    private StoryDefinitionSnapshot? _definition;
    private int _index;
    private bool _loaded;
    private bool _updatingAnswer;
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
        _validate = Ui.Button("Validate Answer", async (_, _) => await PrimaryActionAsync());
        _continue = Ui.Button("Continue With Current Answer", async (_, _) => await AcceptWarningAsync());
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
        _answer.TextChanged += (_, _) => AnswerChanged();
    }

    StartStoryDraft? IWorkspacePayloadPage.StartStoryDraft => CreateDraft();
    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker) =>
        CancelRequestAsync(preserveInterruptedMarker);

    async Task<bool> ICloseGuardPage.CanCloseAsync()
    {
        if (_request is not null)
        {
            if (!await DisplayAlertAsync("Cancel request?", "An LLM request is still in progress.", "Cancel and Close", "Keep Open"))
                return false;
            await CancelRequestAsync();
        }
        if (_answers.All(x => string.IsNullOrWhiteSpace(x.Answer))) return true;
        return await DisplayAlertAsync(
            "Discard setup progress?",
            "Your temporary player answers will be discarded.",
            "Discard",
            "Keep Open");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (!_loaded && !await LoadSnapshotAsync()) return;
            if (!await EnsureSnapshotLimitsAsync()) return;
            Title = $"Start {_definition!.Title}";
            RenderCurrent();

            if (_pendingOperation is not null)
            {
                var interrupted = _pendingOperation;
                _pendingOperation = null;
                await _tabs.SaveWorkspaceNowAsync();
                var choice = await DisplayActionSheetAsync(
                    "The previous request was interrupted. Your progress was preserved.",
                    "Cancel",
                    null,
                    "Retry");
                if (choice == "Retry")
                {
                    if (interrupted.Type == PendingOperationType.GenerateOpeningScene)
                        await GenerateOpeningAsync();
                    else
                        await ValidateCurrentAsync();
                }
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task<bool> LoadSnapshotAsync()
    {
        if (_restoredDraft is not null)
        {
            _definition = _restoredDraft.Definition;
            _maintenance.AddRange(_restoredDraft.StoryBibleMaintenanceHistory);
            var questions = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
            foreach (var question in questions)
            {
                _answers.Add(_restoredDraft.PlayerAnswers.FirstOrDefault(x => x.QuestionId == question.Id)
                    ?? new(question.Id, "", PlayerAnswerValidationStatus.NotValidated, null));
            }
            _index = Math.Clamp(_restoredDraft.CurrentQuestionIndex, 0, questions.Length);
            _loaded = true;
            return true;
        }

        var source = await _definitions.GetAsync(_definitionId)
            ?? throw new NarratorException("Story Definition not found.");
        if (!StoryBibleProcessor.IsWithinLimits(source.InitialStoryBible, (await _app.GetSettingsAsync()).StoryGeneration))
        {
            var choice = await DisplayActionSheetAsync(
                "The Story Bible exceeds current limits.",
                "Cancel",
                null,
                "Increase Limits",
                "Automatically Cull");
            if (choice == "Increase Limits") { _tabs.OpenSettings(); return false; }
            if (choice != "Automatically Cull") return false;
            var settings = await _app.GetSettingsAsync();
            var preview = StoryBibleProcessor.CullToLimits(source.InitialStoryBible, settings.StoryGeneration);
            if (!await ConfirmCullAsync(preview.Changes)) return false;
            source = await _app.CullDefinitionAsync(_definitionId);
        }

        _definition = new(source.Title, source.StoryPrompt, source.PlayerQuestions, source.InitialStoryBible);
        _answers.AddRange(source.PlayerQuestions.OrderBy(x => x.SortOrder)
            .Select(x => new PlayerAnswerDraft(x.Id, "", PlayerAnswerValidationStatus.NotValidated, null)));
        _loaded = true;
        return true;
    }

    private async Task<bool> EnsureSnapshotLimitsAsync()
    {
        if (_definition is null) return false;
        var settings = await _app.GetSettingsAsync();
        if (StoryBibleProcessor.IsWithinLimits(_definition.InitialStoryBible, settings.StoryGeneration)) return true;
        var choice = await DisplayActionSheetAsync(
            "This Start Story snapshot exceeds current limits.",
            "Cancel",
            null,
            "Increase Limits",
            "Automatically Cull");
        if (choice == "Increase Limits") { _tabs.OpenSettings(); return false; }
        if (choice != "Automatically Cull") return false;
        var preview = StoryBibleProcessor.CullToLimits(_definition.InitialStoryBible, settings.StoryGeneration);
        if (!await ConfirmCullAsync(preview.Changes)) return false;
        _definition = _definition with { InitialStoryBible = preview.Bible };
        _maintenance.Add(new(
            Guid.NewGuid(),
            StoryBibleMaintenanceReason.UserApprovedLimitCull,
            new(
                settings.StoryGeneration.MaxStoryBibleEntries,
                settings.StoryGeneration.MaxStoryBibleEntryCharacters,
                settings.StoryGeneration.MaxStoryBibleCharacters),
            preview.Changes,
            DateTimeOffset.UtcNow));
        await _tabs.SaveWorkspaceNowAsync();
        return true;
    }

    private async Task<bool> ConfirmCullAsync(IReadOnlyList<AppliedStoryBibleChange> changes)
    {
        var names = string.Join(Environment.NewLine, changes.Select(x => $"• {x.Before?.Name}"));
        return await DisplayAlertAsync("Cull Story Bible?", $"These entries will be removed:\n{names}", "Cull", "Cancel");
    }

    private StartStoryDraft? CreateDraft()
    {
        if (_definition is null) return _restoredDraft;
        return new(_definitionId, _definition, _index, _answers.ToArray())
        {
            StoryBibleMaintenanceHistory = _maintenance.ToArray()
        };
    }

    private void AnswerChanged()
    {
        if (_updatingAnswer || _definition is null || _index >= _answers.Count) return;
        _answers[_index] = _answers[_index] with
        {
            Answer = _answer.Text ?? "",
            ValidationStatus = PlayerAnswerValidationStatus.NotValidated,
            ValidationWarning = null
        };
        _warning.Text = "";
        _continue.IsVisible = false;
        _tabs.ScheduleWorkspaceSave();
    }

    private async Task PrimaryActionAsync()
    {
        if (_index >= _answers.Count) await GenerateOpeningAsync();
        else await ValidateCurrentAsync();
    }

    private async Task ValidateCurrentAsync()
    {
        if (_definition is null || _index >= _answers.Count || _request is not null) return;
        var retry = false;
        try
        {
            _request = new();
            _busy.IsRunning = true;
            var question = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ElementAt(_index);
            var current = _answers[_index] with { Answer = _answer.Text ?? "" };
            _answers[_index] = current;
            var previous = PreviousResponses();
            _pendingOperation = new(
                Guid.NewGuid(),
                PendingOperationType.ValidatePlayerAnswer,
                null,
                null,
                DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var response = await _app.ValidateAnswerAsync(
                _definitionId,
                question,
                current.Answer,
                previous,
                _request.Token);
            if (response.HasWarning)
            {
                _answers[_index] = current with
                {
                    ValidationStatus = PlayerAnswerValidationStatus.Warning,
                    ValidationWarning = response.Warning
                };
                _warning.Text = response.Warning;
                _continue.IsVisible = true;
            }
            else
            {
                _answers[_index] = current with
                {
                    ValidationStatus = PlayerAnswerValidationStatus.Valid,
                    ValidationWarning = null
                };
                Advance();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _answers[_index] = _answers[_index] with
            {
                ValidationStatus = PlayerAnswerValidationStatus.NotValidated,
                ValidationWarning = null
            };
            retry = await DisplayActionSheetAsync(
                $"Validation failed: {ex.Message}",
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
        if (retry) await ValidateCurrentAsync();
    }

    private Task AcceptWarningAsync()
    {
        if (_index >= _answers.Count || _answers[_index].ValidationStatus != PlayerAnswerValidationStatus.Warning)
            return Task.CompletedTask;
        _answers[_index] = _answers[_index] with { ValidationStatus = PlayerAnswerValidationStatus.AcceptedWithWarning };
        Advance();
        return Task.CompletedTask;
    }

    private void Advance()
    {
        _index++;
        RenderCurrent();
        _tabs.ScheduleWorkspaceSave();
    }

    private void RenderCurrent()
    {
        if (_definition is null) return;
        var questions = _definition.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
        if (_index >= questions.Length)
        {
            _question.Text = "All answers are ready. Generate the opening scene when you are ready.";
            _answer.IsVisible = false;
            _warning.Text = "";
            _continue.IsVisible = false;
            _validate.Text = "Generate Opening Scene";
            _validate.IsVisible = true;
            return;
        }

        _question.Text = $"Question {_index + 1} of {questions.Length}\n{questions[_index].Question}";
        _answer.IsVisible = true;
        _validate.Text = "Validate Answer";
        _validate.IsVisible = true;
        _updatingAnswer = true;
        _answer.Text = _answers[_index].Answer;
        _updatingAnswer = false;
        _warning.Text = _answers[_index].ValidationWarning ?? "";
        _continue.IsVisible = _answers[_index].ValidationStatus == PlayerAnswerValidationStatus.Warning;
    }

    private IReadOnlyList<PlayerResponse> PreviousResponses()
    {
        var questions = _definition!.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
        return questions.Take(_index).Select((question, index) =>
            new PlayerResponse(
                question.Id,
                question.Question,
                question.ValidationInstruction,
                _answers[index].Answer)).ToArray();
    }

    private async Task GenerateOpeningAsync()
    {
        if (_definition is null || _request is not null) return;
        var draft = CreateDraft() ?? throw new NarratorException("The Start Story draft is unavailable.");
        if (draft.PlayerAnswers.Any(x =>
                x.ValidationStatus is not (PlayerAnswerValidationStatus.Valid or PlayerAnswerValidationStatus.AcceptedWithWarning)))
            return;

        var retry = false;
        try
        {
            if (!await EnsureSnapshotLimitsAsync()) return;
            draft = CreateDraft()!;
            _request = new();
            _busy.IsRunning = true;
            _validate.IsVisible = false;
            _question.Text = "Generating opening scene…";
            var targetStateId = Guid.NewGuid();
            _pendingOperation = new(
                Guid.NewGuid(),
                PendingOperationType.GenerateOpeningScene,
                targetStateId,
                0,
                DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.StartStoryAsync(draft, targetStateId, _request.Token);
            await _tabs.ReplaceCurrentWithPlayAsync(result.State.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            retry = await DisplayActionSheetAsync(
                $"Opening-scene generation failed: {ex.Message}",
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
            _validate.IsVisible = true;
            if (Parent is not null) RenderCurrent();
            await _tabs.SaveWorkspaceNowAsync();
        }
        if (retry) await GenerateOpeningAsync();
    }

    private async Task CancelRequestAsync(bool preserveInterruptedMarker = false)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        while (_request is not null) await Task.Delay(20);
        if (marker is not null) _pendingOperation = marker;
    }
}
