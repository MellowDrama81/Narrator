using Microsoft.Extensions.DependencyInjection;

namespace Mellow.Narrator.Core;

public sealed class NarratorApplication(
    IStoryDefinitionRepository definitions,
    IStoryStateRepository states,
    IApiConnectionSettingsStore settingsStore,
    ISecureStorageService secureStorage,
    ILanguageModelProvider provider,
    TimeProvider timeProvider,
    ApiConnectionCoordinator connectionCoordinator,
    StoryRequestCoordinator storyRequests,
    IIdGenerator idGenerator) : INarratorApplication
{
    public Task<ApiConnectionSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        settingsStore.LoadAsync(cancellationToken);

    public async Task SaveSettingsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new NarratorException(string.Join(Environment.NewLine, errors.Select(x => $"{x.Key}: {x.Value}")));
        await connectionCoordinator.RunExclusiveAsync(async () =>
        {
            var previousSettings = await settingsStore.LoadAsync(cancellationToken);
            if (previousSettings.BaseUrl != settings.BaseUrl || previousSettings.ModelId != settings.ModelId)
                settings = settings with { Capabilities = new(false, StructuredOutputTier.Untested, null, null) };
            var previous = await secureStorage.GetAsync(SecureStorageKeys.ApiCredential, cancellationToken);
            try
            {
                if (credential is not null)
                {
                    if (credential.Length == 0)
                        await secureStorage.RemoveAsync(SecureStorageKeys.ApiCredential, cancellationToken);
                    else
                        await secureStorage.SetAsync(SecureStorageKeys.ApiCredential, credential, cancellationToken);
                }
                await settingsStore.SaveAsync(settings, cancellationToken);
                if (credential is not null) connectionCoordinator.MarkCredentialHealthy();
            }
            catch (Exception original)
            {
                try
                {
                    if (previous is null) await secureStorage.RemoveAsync(SecureStorageKeys.ApiCredential, CancellationToken.None);
                    else await secureStorage.SetAsync(SecureStorageKeys.ApiCredential, previous, CancellationToken.None);
                }
                catch (Exception rollback)
                {
                    connectionCoordinator.MarkCredentialReentryRequired();
                    throw new NarratorException(
                        "Settings could not be saved and the previous credential could not be restored. Re-enter the API credential before making another request.",
                        new AggregateException(original, rollback));
                }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        var result = await provider.TestConnectionAsync(settings, credential, cancellationToken);
        if (result.Success)
        {
            await connectionCoordinator.RunExclusiveAsync(async () =>
            {
                var current = await settingsStore.LoadAsync(cancellationToken);
                if (current.BaseUrl == settings.BaseUrl && current.ModelId == settings.ModelId)
                    await settingsStore.SaveAsync(current with { Capabilities = result.Capabilities }, cancellationToken);
            }, cancellationToken);
        }
        return result;
    }

    public async Task<BibleLimitImpact> GetBibleLimitImpactAsync(
        StoryGenerationSettings proposed,
        CancellationToken cancellationToken = default)
    {
        var definitionCount = 0;
        foreach (var summary in await definitions.ListAsync(cancellationToken))
        {
            var definition = await definitions.GetAsync(summary.Id, cancellationToken);
            if (definition is not null && !StoryBibleProcessor.IsWithinLimits(definition.InitialStoryBible, proposed))
                definitionCount++;
        }
        var stateCount = 0;
        foreach (var summary in await states.ListAsync(cancellationToken))
        {
            var state = await states.GetAsync(summary.Id, cancellationToken);
            if (state is not null && !StoryBibleProcessor.IsWithinLimits(state.CurrentStoryBible, proposed))
                stateCount++;
        }
        return new(definitionCount, stateCount);
    }

    public async Task<StoryDefinition> GenerateDefinitionAsync(
        StoryPromptDraft draft,
        bool overwrite,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty) throw new ArgumentException("Target ID cannot be empty.", nameof(targetId));
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        ValidateDraft(draft, settings.ContentLimits);
        var generated = await provider.GenerateStoryDefinitionAsync(settings, credential, draft.StoryPrompt, cancellationToken);
        if (generated.InitialStoryBibleEntries.Count > 2000)
            throw new NarratorException("The generated initial Story Bible contains too many entries.");
        foreach (var entry in generated.InitialStoryBibleEntries)
            ValidateGeneratedEntry(entry, settings.ContentLimits);
        var now = timeProvider.GetUtcNow();
        var source = overwrite && draft.SourceStoryDefinitionId is not null
            ? await definitions.GetAsync(draft.SourceStoryDefinitionId.Value, cancellationToken)
            : null;
        var raw = new StoryBible(generated.InitialStoryBibleEntries.Select(x =>
            new StoryBibleEntry(idGenerator.NewId(), x.Category.Trim(), x.Name.Trim(), x.Content.Trim(), x.Importance, 0)).ToArray());
        var (bible, culls) = StoryBibleProcessor.CullToLimits(raw, settings.StoryGeneration);
        var maintenance = source?.StoryBibleMaintenanceHistory.ToList() ?? [];
        if (culls.Count > 0)
            maintenance.Add(new(idGenerator.NewId(), StoryBibleMaintenanceReason.GeneratedBibleLimitCull,
                Limits(settings), culls, now));
        var definitionSummaries = source is null ? await definitions.ListAsync(cancellationToken) : [];
        var definition = new StoryDefinition(
            source?.Id ?? targetId,
            draft.Title,
            draft.StoryPrompt,
            draft.PlayerQuestions.OrderBy(x => x.SortOrder).Select(x =>
                new PlayerQuestion(x.Id == Guid.Empty ? idGenerator.NewId() : x.Id, x.Question, x.ValidationInstruction, x.SortOrder)).ToArray(),
            bible,
            maintenance,
            source?.SortOrder ?? (definitionSummaries.Count == 0 ? 0 : definitionSummaries.Max(x => x.SortOrder) + 1),
            source?.CreatedAtUtc ?? now,
            now);
        await definitions.SaveAsync(definition, cancellationToken);
        return definition;
    }

    public async Task<StoryDefinition> CullDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken) ?? throw new NarratorException("Story Definition not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var (bible, changes) = StoryBibleProcessor.CullToLimits(definition.InitialStoryBible, settings.StoryGeneration);
        if (changes.Count == 0) return definition;
        var history = definition.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
            idGenerator.NewId(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, timeProvider.GetUtcNow())).ToArray();
        var updated = definition with { InitialStoryBible = bible, StoryBibleMaintenanceHistory = history, UpdatedAtUtc = timeProvider.GetUtcNow() };
        await definitions.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<StoryState> CullStoryStateAsync(Guid stateId, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var (bible, changes) = StoryBibleProcessor.CullToLimits(state.CurrentStoryBible, settings.StoryGeneration);
        if (changes.Count == 0) return state;
        var history = state.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
            idGenerator.NewId(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, timeProvider.GetUtcNow())).ToArray();
        var updated = state with { CurrentStoryBible = bible, StoryBibleMaintenanceHistory = history };
        await states.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<PlayerAnswerValidationResponse> ValidateAnswerAsync(
        Guid definitionId, PlayerQuestion question, string answer, IReadOnlyList<PlayerResponse> previousAnswers,
        CancellationToken cancellationToken = default)
    {
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (answer.Length > settings.ContentLimits.MaxPlayerAnswerCharacters)
            throw new NarratorException("The answer exceeds the configured limit.");
        return await provider.ValidatePlayerAnswerAsync(settings, credential, question, answer, previousAnswers, cancellationToken);
    }

    public async Task<(StoryState State, StoryTurn Opening)> StartStoryAsync(
        StartStoryDraft draft,
        Guid targetStateId,
        CancellationToken cancellationToken = default)
    {
        if (targetStateId == Guid.Empty) throw new ArgumentException("Target ID cannot be empty.", nameof(targetStateId));
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (!StoryBibleProcessor.IsWithinLimits(draft.Definition.InitialStoryBible, settings.StoryGeneration))
            throw new NarratorException("The initial Story Bible exceeds current limits. Increase the limits or cull it first.");
        var questions = draft.Definition.PlayerQuestions.OrderBy(x => x.SortOrder).ToArray();
        if (draft.PlayerAnswers.Count != questions.Length)
            throw new NarratorException("Every player question must be answered.");
        var answers = new List<PlayerResponse>(questions.Length);
        foreach (var question in questions)
        {
            var answer = draft.PlayerAnswers.SingleOrDefault(x => x.QuestionId == question.Id)
                ?? throw new NarratorException("A player answer does not match the Story Definition snapshot.");
            if (answer.ValidationStatus is not (PlayerAnswerValidationStatus.Valid or PlayerAnswerValidationStatus.AcceptedWithWarning))
                throw new NarratorException("Every player answer must be validated or explicitly accepted with a warning.");
            if (string.IsNullOrWhiteSpace(answer.Answer) || answer.Answer.Length > settings.ContentLimits.MaxPlayerAnswerCharacters)
                throw new NarratorException("A player answer is empty or exceeds the configured limit.");
            answers.Add(new(question.Id, question.Question, question.ValidationInstruction, answer.Answer));
        }

        var idMap = draft.Definition.InitialStoryBible.Entries.ToDictionary(x => x.Id, _ => idGenerator.NewId());
        var initial = new StoryBible(draft.Definition.InitialStoryBible.Entries
            .Select(x => x with { Id = idMap[x.Id], LastRelevantTurnNumber = 0 }).ToArray());
        var snapshot = draft.Definition with { InitialStoryBible = initial };
        var context = new GenerationContext(snapshot, answers, initial, [], null, 0);
        var response = await provider.GenerateOpeningAsync(settings, credential, context, cancellationToken);
        ValidateGenerationResponse(response, settings.ContentLimits);
        var mappedRelevant = response.RelevantStoryBibleEntryIds
            .Select(x => idMap.GetValueOrDefault(x, x))
            .Concat(initial.Entries.Select(x => x.Id))
            .Distinct()
            .ToArray();
        var mappedUpdates = response.StoryBibleUpdates.Select(x =>
            x.EntryId is { } oldId && idMap.TryGetValue(oldId, out var newId) ? x with { EntryId = newId } : x).ToArray();
        var applied = StoryBibleProcessor.Apply(initial, mappedRelevant, mappedUpdates, 0, settings.StoryGeneration, idGenerator.NewId);
        var now = timeProvider.GetUtcNow();
        var stateId = targetStateId;
        var stateSummaries = await states.ListAsync(cancellationToken);
        var state = new StoryState(stateId, snapshot.Title, draft.SourceStoryDefinitionId,
            new(snapshot, answers), applied.Bible, draft.StoryBibleMaintenanceHistory,
            stateSummaries.Count == 0 ? 0 : stateSummaries.Max(x => x.SortOrder) + 1, now, null, 0);
        var turn = CreateTurn(stateId, 0, null, response, applied, settings.ModelId!, now);
        await states.CreateAsync(state, turn, cancellationToken);
        return (state, turn);
    }

    public async Task<(StoryState State, StoryTurn Turn)> PlayTurnAsync(Guid stateId, string action, CancellationToken cancellationToken = default)
    {
        using var requestLease = storyRequests.Enter(stateId);
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (action.Length > settings.ContentLimits.MaxPlayerActionCharacters) throw new NarratorException("The action exceeds the configured limit.");
        if (!StoryBibleProcessor.IsWithinLimits(state.CurrentStoryBible, settings.StoryGeneration))
            throw new NarratorException("The Story Bible exceeds current limits. Increase the limits or cull it first.");
        var recent = await states.GetTurnsAsync(stateId, settings.StoryGeneration.RecentTurnCount, cancellationToken);
        var context = new GenerationContext(
            state.Setup.Definition,
            state.Setup.PlayerResponses,
            state.CurrentStoryBible,
            recent,
            action,
            state.LastCommittedTurnSequence + 1);
        var response = await provider.GenerateTurnAsync(settings, credential, context, cancellationToken);
        ValidateGenerationResponse(response, settings.ContentLimits);
        var sequence = state.LastCommittedTurnSequence + 1;
        var applied = StoryBibleProcessor.Apply(
            state.CurrentStoryBible,
            response.RelevantStoryBibleEntryIds,
            response.StoryBibleUpdates,
            sequence,
            settings.StoryGeneration,
            idGenerator.NewId);
        var now = timeProvider.GetUtcNow();
        var next = state with { CurrentStoryBible = applied.Bible, LastActionAtUtc = now, LastCommittedTurnSequence = sequence };
        var turn = CreateTurn(stateId, sequence, action, response, applied, settings.ModelId!, now);
        await states.CommitTurnAsync(next, turn, cancellationToken);
        return (next, turn);
    }

    private async Task<(ApiConnectionSettings Settings, string? Credential)> ConnectionAsync(CancellationToken cancellationToken)
    {
        return await connectionCoordinator.RunExclusiveAsync(async () =>
        {
            if (connectionCoordinator.RequiresCredentialReentry)
                throw new NarratorException("Re-enter and save the API credential before making another request.");
            var settings = await settingsStore.LoadAsync(cancellationToken);
            if (settings.BaseUrl is null)
                throw new NarratorException("Configure an API base URL first.");
            if (string.IsNullOrWhiteSpace(settings.ModelId))
                throw new NarratorException("Select or enter a model ID first.");
            return (settings, await secureStorage.GetAsync(SecureStorageKeys.ApiCredential, cancellationToken));
        }, cancellationToken);
    }

    private StoryTurn CreateTurn(Guid stateId, int sequence, string? action, StoryGenerationResponse response,
        StoryBibleApplyResult applied, string model, DateTimeOffset now) =>
        new(idGenerator.NewId(), stateId, sequence, action, response.Narration, response.SuggestedActions,
            applied.RelevantEntryIds, applied.Changes, now,
            new(model, response.ProviderResponseId, response.InputTokens, response.OutputTokens));

    private static StoryBibleLimitSnapshot Limits(ApiConnectionSettings settings) =>
        new(settings.StoryGeneration.MaxStoryBibleEntries, settings.StoryGeneration.MaxStoryBibleEntryCharacters, settings.StoryGeneration.MaxStoryBibleCharacters);

    private static void ValidateDraft(StoryPromptDraft draft, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > limits.MaxStoryTitleCharacters)
            throw new NarratorException("Enter a valid title.");
        if (string.IsNullOrWhiteSpace(draft.StoryPrompt) || draft.StoryPrompt.Length > limits.MaxStoryPromptCharacters)
            throw new NarratorException("Enter a valid Story Prompt.");
        if (draft.PlayerQuestions.Any(x => string.IsNullOrWhiteSpace(x.Question) || string.IsNullOrWhiteSpace(x.ValidationInstruction)))
            throw new NarratorException("Every player question requires a question and validation instruction.");
        if (draft.PlayerQuestions.Any(x => x.Question.Length > limits.MaxPlayerQuestionCharacters))
            throw new NarratorException("A player question exceeds the configured limit.");
        if (draft.PlayerQuestions.Any(x => x.ValidationInstruction.Length > limits.MaxValidationInstructionCharacters))
            throw new NarratorException("A validation instruction exceeds the configured limit.");
        if (draft.PlayerQuestions.Select(x => x.Id).Where(x => x != Guid.Empty).Distinct().Count() !=
            draft.PlayerQuestions.Count(x => x.Id != Guid.Empty))
            throw new NarratorException("Player question IDs must be unique.");
    }

    private static void ValidateGenerationResponse(StoryGenerationResponse response, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(response.Narration) || response.Narration.Length > limits.MaxNarrationCharacters)
            throw new NarratorException("The returned narration is empty or exceeds the configured limit.");
        if (response.SuggestedActions.Count > limits.MaxSuggestedActions ||
            response.SuggestedActions.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > limits.MaxSuggestedActionCharacters))
            throw new NarratorException("The returned suggested actions exceed configured limits.");
        if (response.StoryBibleUpdates.Count > limits.MaxStoryBibleUpdatesPerResponse)
            throw new NarratorException("The response contains too many Story Bible updates.");
        foreach (var update in response.StoryBibleUpdates.Where(x => x.Entry is not null))
            ValidateGeneratedEntry(update.Entry!, limits);
    }

    private static void ValidateGeneratedEntry(ProposedStoryBibleEntry entry, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(entry.Category) || entry.Category.Length > limits.MaxStoryBibleCategoryCharacters)
            throw new NarratorException("A Story Bible category is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > limits.MaxStoryBibleNameCharacters)
            throw new NarratorException("A Story Bible entry name is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(entry.Content))
            throw new NarratorException("A Story Bible entry has empty content.");
        if (entry.Importance is < 1 or > 5)
            throw new NarratorException("Story Bible importance must be from 1 to 5.");
    }
}

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMellowNarratorCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ApiConnectionCoordinator>();
        services.AddSingleton<StoryRequestCoordinator>();
        services.AddSingleton<IIdGenerator, SystemIdGenerator>();
        services.AddTransient<INarratorApplication, NarratorApplication>();
        return services;
    }
}
