using Microsoft.Extensions.DependencyInjection;

namespace Mellow.Narrator.Core;

public sealed class NarratorApplication(
    IStoryDefinitionRepository definitions,
    IStoryStateRepository states,
    IApiConnectionSettingsStore settingsStore,
    ISecureStorageService secureStorage,
    ILanguageModelProvider provider,
    TimeProvider timeProvider) : INarratorApplication
{
    public Task<ApiConnectionSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        settingsStore.LoadAsync(cancellationToken);

    public async Task SaveSettingsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new NarratorException(string.Join(Environment.NewLine, errors.Select(x => $"{x.Key}: {x.Value}")));

        var previous = await secureStorage.GetAsync(SecureStorageKeys.ApiCredential, cancellationToken);
        try
        {
            if (credential is null)
            {
                await settingsStore.SaveAsync(settings, cancellationToken);
                return;
            }
            if (credential.Length == 0)
                await secureStorage.RemoveAsync(SecureStorageKeys.ApiCredential, cancellationToken);
            else
                await secureStorage.SetAsync(SecureStorageKeys.ApiCredential, credential, cancellationToken);
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        catch
        {
            if (previous is null) await secureStorage.RemoveAsync(SecureStorageKeys.ApiCredential, CancellationToken.None);
            else await secureStorage.SetAsync(SecureStorageKeys.ApiCredential, previous, CancellationToken.None);
            throw;
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        var result = await provider.TestConnectionAsync(settings, credential, cancellationToken);
        if (result.Success)
            await settingsStore.SaveAsync(settings with { Capabilities = result.Capabilities }, cancellationToken);
        return result;
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
        var now = timeProvider.GetUtcNow();
        var source = overwrite && draft.SourceStoryDefinitionId is not null
            ? await definitions.GetAsync(draft.SourceStoryDefinitionId.Value, cancellationToken)
            : null;
        var raw = new StoryBible(generated.InitialStoryBibleEntries.Select(x =>
            new StoryBibleEntry(Guid.NewGuid(), x.Category.Trim(), x.Name.Trim(), x.Content.Trim(), x.Importance, 0)).ToArray());
        var (bible, culls) = StoryBibleProcessor.CullToLimits(raw, settings.StoryGeneration);
        var maintenance = source?.StoryBibleMaintenanceHistory.ToList() ?? [];
        if (culls.Count > 0)
            maintenance.Add(new(Guid.NewGuid(), StoryBibleMaintenanceReason.GeneratedBibleLimitCull,
                Limits(settings), culls, now));
        var definition = new StoryDefinition(
            source?.Id ?? targetId,
            draft.Title.Trim(),
            draft.StoryPrompt.Trim(),
            draft.PlayerQuestions.OrderBy(x => x.SortOrder).Select(x =>
                new PlayerQuestion(x.Id == Guid.Empty ? Guid.NewGuid() : x.Id, x.Question.Trim(), x.ValidationInstruction.Trim(), x.SortOrder)).ToArray(),
            bible,
            maintenance,
            source?.SortOrder ?? (await definitions.ListAsync(cancellationToken)).Count,
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
            Guid.NewGuid(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, timeProvider.GetUtcNow())).ToArray();
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
            Guid.NewGuid(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, timeProvider.GetUtcNow())).ToArray();
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
        Guid definitionId,
        IReadOnlyList<PlayerResponse> answers,
        Guid targetStateId,
        CancellationToken cancellationToken = default)
    {
        if (targetStateId == Guid.Empty) throw new ArgumentException("Target ID cannot be empty.", nameof(targetStateId));
        var definition = await definitions.GetAsync(definitionId, cancellationToken)
            ?? throw new NarratorException("Story Definition not found.");
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (!StoryBibleProcessor.IsWithinLimits(definition.InitialStoryBible, settings.StoryGeneration))
            throw new NarratorException("The initial Story Bible exceeds current limits. Increase the limits or cull it first.");

        var idMap = definition.InitialStoryBible.Entries.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var initial = new StoryBible(definition.InitialStoryBible.Entries.Select(x => x with { Id = idMap[x.Id], LastRelevantTurnNumber = 0 }).ToArray());
        var snapshot = new StoryDefinitionSnapshot(definition.Title, definition.StoryPrompt, definition.PlayerQuestions, initial);
        var context = new GenerationContext(snapshot, answers, initial, [], null);
        var response = await provider.GenerateOpeningAsync(settings, credential, context, cancellationToken);
        var mappedRelevant = response.RelevantStoryBibleEntryIds.Select(x => idMap.GetValueOrDefault(x, x)).ToArray();
        var mappedUpdates = response.StoryBibleUpdates.Select(x =>
            x.EntryId is { } oldId && idMap.TryGetValue(oldId, out var newId) ? x with { EntryId = newId } : x).ToArray();
        var applied = StoryBibleProcessor.Apply(initial, mappedRelevant, mappedUpdates, 0, settings.StoryGeneration);
        var now = timeProvider.GetUtcNow();
        var stateId = targetStateId;
        var state = new StoryState(stateId, definition.Title, definition.Id,
            new(snapshot, answers), applied.Bible, [], (await states.ListAsync(cancellationToken)).Count, now, null, 0);
        var turn = CreateTurn(stateId, 0, null, response, applied, settings.ModelId!, now);
        await states.CreateAsync(state, turn, cancellationToken);
        return (state, turn);
    }

    public async Task<(StoryState State, StoryTurn Turn)> PlayTurnAsync(Guid stateId, string action, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (action.Length > settings.ContentLimits.MaxPlayerActionCharacters) throw new NarratorException("The action exceeds the configured limit.");
        if (!StoryBibleProcessor.IsWithinLimits(state.CurrentStoryBible, settings.StoryGeneration))
            throw new NarratorException("The Story Bible exceeds current limits. Increase the limits or cull it first.");
        var recent = await states.GetTurnsAsync(stateId, settings.StoryGeneration.RecentTurnCount, cancellationToken);
        var context = new GenerationContext(state.Setup.Definition, state.Setup.PlayerResponses, state.CurrentStoryBible, recent, action);
        var response = await provider.GenerateTurnAsync(settings, credential, context, cancellationToken);
        var sequence = state.LastCommittedTurnSequence + 1;
        var applied = StoryBibleProcessor.Apply(state.CurrentStoryBible, response.RelevantStoryBibleEntryIds, response.StoryBibleUpdates, sequence, settings.StoryGeneration);
        var now = timeProvider.GetUtcNow();
        var next = state with { CurrentStoryBible = applied.Bible, LastActionAtUtc = now, LastCommittedTurnSequence = sequence };
        var turn = CreateTurn(stateId, sequence, action, response, applied, settings.ModelId!, now);
        await states.CommitTurnAsync(next, turn, cancellationToken);
        return (next, turn);
    }

    private async Task<(ApiConnectionSettings Settings, string? Credential)> ConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (settings.BaseUrl is null || string.IsNullOrWhiteSpace(settings.ModelId))
            throw new NarratorException("Configure an API base URL and model first.");
        return (settings, await secureStorage.GetAsync(SecureStorageKeys.ApiCredential, cancellationToken));
    }

    private static StoryTurn CreateTurn(Guid stateId, int sequence, string? action, StoryGenerationResponse response,
        StoryBibleApplyResult applied, string model, DateTimeOffset now) =>
        new(Guid.NewGuid(), stateId, sequence, action, response.Narration, response.SuggestedActions,
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
    }
}

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMellowNarratorCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<INarratorApplication, NarratorApplication>();
        return services;
    }
}
