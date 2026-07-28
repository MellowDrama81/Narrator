using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    IIdGenerator idGenerator,
    ILogger<NarratorApplication>? logger = null) : INarratorApplication
{
    private readonly ILogger<NarratorApplication> _logger =
        logger ?? NullLogger<NarratorApplication>.Instance;

    public Task<ApiConnectionSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        settingsStore.LoadAsync(cancellationToken);

    public Task<bool> HasApiCredentialAsync(CancellationToken cancellationToken = default) =>
        connectionCoordinator.RunExclusiveAsync(async () =>
        {
            if (connectionCoordinator.RequiresCredentialReentry) return false;
            return !string.IsNullOrEmpty(
                await secureStorage.GetAsync(SecureStorageKeys.ApiCredential, cancellationToken));
        }, cancellationToken);

    public async Task SaveSettingsAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new NarratorException(string.Join(Environment.NewLine, errors.Select(x => $"{x.Key}: {x.Value}")));
        await connectionCoordinator.RunExclusiveAsync(async () =>
        {
            var previousSettings = await settingsStore.LoadAsync(cancellationToken);
            if (previousSettings.BaseUrl != settings.BaseUrl)
                settings = settings with { Capabilities = new(false, StructuredOutputTier.Untested, null, null) };
            else if (previousSettings.ModelId != settings.ModelId)
                settings = settings with
                {
                    Capabilities = new(
                        previousSettings.Capabilities.SupportsModelDiscovery,
                        StructuredOutputTier.Untested,
                        null,
                        null)
                };
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
        _logger.LogInformation(
            "API settings saved for model {ModelId}; credential action: {CredentialAction}; log level: {LogLevel}.",
            settings.ModelId,
            credential is null ? "unchanged" : credential.Length == 0 ? "removed" : "replaced",
            settings.Logging.MinimumLevel);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        _logger.LogInformation("Testing API connection for model {ModelId}.", settings.ModelId);
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
        _logger.LogInformation(
            "API connection test completed for model {ModelId}; success: {Success}; structured output: {StructuredOutputTier}.",
            settings.ModelId,
            result.Success,
            result.Capabilities.StructuredOutputTier);
        return result;
    }

    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        var (settings, credential) = await DiscoveryConnectionAsync(cancellationToken);
        _logger.LogInformation("Discovering models from the configured API endpoint.");
        var models = await provider.DiscoverModelsAsync(settings, credential, cancellationToken);
        await connectionCoordinator.RunExclusiveAsync(async () =>
        {
            var current = await settingsStore.LoadAsync(cancellationToken);
            if (current.BaseUrl == settings.BaseUrl)
                await settingsStore.SaveAsync(current with
                {
                    Capabilities = current.Capabilities with { SupportsModelDiscovery = true }
                }, cancellationToken);
        }, cancellationToken);
        _logger.LogInformation("Model discovery completed; {ModelCount} models returned.", models.Count);
        return models;
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
        _logger.LogInformation(
            "Generating Story Definition {StoryDefinitionId}; overwrite: {Overwrite}; model: {ModelId}.",
            targetId,
            overwrite,
            settings.ModelId);
        var generated = await provider.GenerateStoryDefinitionAsync(settings, credential, draft.StoryPrompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(generated.RefinedStoryPrompt) || generated.RefinedStoryPrompt.Length > settings.ContentLimits.MaxStoryPromptCharacters)
            throw new NarratorException("The refined Story Prompt is empty or exceeds the configured limit.");
        var title = draft.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            if (string.IsNullOrWhiteSpace(generated.SuggestedTitle) || generated.SuggestedTitle.Length > settings.ContentLimits.MaxStoryTitleCharacters)
                throw new NarratorException("The suggested title is empty or exceeds the configured limit.");
            title = generated.SuggestedTitle.Trim();
        }
        if (generated.InitialEventsPrompt.Length > settings.ContentLimits.MaxStoryPromptCharacters)
            throw new NarratorException("The Initial Events prompt exceeds the configured limit.");
        if (generated.InitialStoryBibleEntries.Count > 2000)
            throw new NarratorException("The generated initial Story Bible contains too many entries.");
        foreach (var entry in generated.InitialStoryBibleEntries)
            ValidateGeneratedEntry(entry, settings.ContentLimits);
        var now = timeProvider.GetUtcNow();
        var source = overwrite && draft.SourceStoryDefinitionId is not null
            ? await definitions.GetAsync(draft.SourceStoryDefinitionId.Value, cancellationToken)
            : null;
        var raw = new StoryBible(generated.InitialStoryBibleEntries.Select(x =>
            new StoryBibleEntry(
                idGenerator.NewId(),
                x.Category.Trim(),
                x.Name.Trim(),
                x.KnownFacts.Select(f => f.Trim()).ToArray(),
                x.SecretFacts.Select(f => f.Trim()).ToArray(),
                x.Importance,
                0)).ToArray());
        var (bible, culls) = StoryBibleProcessor.CullToLimits(raw, settings.StoryGeneration);
        var maintenance = source?.StoryBibleMaintenanceHistory.ToList() ?? [];
        if (culls.Count > 0)
            maintenance.Add(new(idGenerator.NewId(), StoryBibleMaintenanceReason.GeneratedBibleLimitCull,
                Limits(settings), culls, now));
        var definitionSummaries = source is null ? await definitions.ListAsync(cancellationToken) : [];
        var definition = new StoryDefinition(
            source?.Id ?? targetId,
            title,
            generated.RefinedStoryPrompt.Trim(),
            bible,
            maintenance,
            source?.SortOrder ?? (definitionSummaries.Count == 0 ? 0 : definitionSummaries.Max(x => x.SortOrder) + 1),
            source?.CreatedAtUtc ?? now,
            now)
        {
            InitialEventsPrompt = generated.InitialEventsPrompt.Trim()
        };
        await definitions.SaveAsync(definition, cancellationToken);
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} saved with {BibleEntryCount} initial Story Bible entries.",
            definition.Id,
            definition.InitialStoryBible.Entries.Count);
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

    public async Task<StoryDefinition> UpdateInitialStoryBibleAsync(Guid definitionId, StoryBible bible, CancellationToken cancellationToken = default)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken) ?? throw new NarratorException("Story Definition not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var normalized = NormalizeManualBible(bible, settings.ContentLimits);
        if (!StoryBibleProcessor.IsWithinLimits(normalized, settings.StoryGeneration))
            throw new NarratorException("The Story Bible exceeds current limits. Increase the limits or cull it first.");
        var now = timeProvider.GetUtcNow();
        var changes = DiffManualEdit(definition.InitialStoryBible, normalized);
        var history = changes.Count == 0
            ? definition.StoryBibleMaintenanceHistory
            : definition.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
                idGenerator.NewId(), StoryBibleMaintenanceReason.ManualEdit, Limits(settings), changes, now)).ToArray();
        var updated = definition with { InitialStoryBible = normalized, StoryBibleMaintenanceHistory = history, UpdatedAtUtc = now };
        await definitions.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} Story Bible manually updated with {ChangeCount} changes.",
            definitionId,
            changes.Count);
        return updated;
    }

    public async Task<StoryState> UpdateCurrentStoryBibleAsync(Guid stateId, StoryBible bible, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var normalized = NormalizeManualBible(bible, settings.ContentLimits);
        if (!StoryBibleProcessor.IsWithinLimits(normalized, settings.StoryGeneration))
            throw new NarratorException("The Story Bible exceeds current limits. Increase the limits or cull it first.");
        var changes = DiffManualEdit(state.CurrentStoryBible, normalized);
        var history = changes.Count == 0
            ? state.StoryBibleMaintenanceHistory
            : state.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
                idGenerator.NewId(), StoryBibleMaintenanceReason.ManualEdit, Limits(settings), changes, timeProvider.GetUtcNow())).ToArray();
        var updated = state with { CurrentStoryBible = normalized, StoryBibleMaintenanceHistory = history };
        await states.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story State {StoryStateId} Story Bible manually updated with {ChangeCount} changes.",
            stateId,
            changes.Count);
        return updated;
    }

    public async Task<(StoryState State, StoryTurn Opening)> StartStoryAsync(
        StartStoryDraft draft,
        Guid targetStateId,
        CancellationToken cancellationToken = default)
    {
        if (targetStateId == Guid.Empty) throw new ArgumentException("Target ID cannot be empty.", nameof(targetStateId));
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        _logger.LogInformation(
            "Generating opening scene for Story State {StoryStateId} with model {ModelId}.",
            targetStateId,
            settings.ModelId);
        if (!StoryBibleProcessor.IsWithinLimits(draft.Definition.InitialStoryBible, settings.StoryGeneration))
            throw new NarratorException("The initial Story Bible exceeds current limits. Increase the limits or cull it first.");

        var idMap = draft.Definition.InitialStoryBible.Entries.ToDictionary(x => x.Id, _ => idGenerator.NewId());
        var initial = new StoryBible(draft.Definition.InitialStoryBible.Entries
            .Select(x => x with { Id = idMap[x.Id], LastRelevantTurnNumber = 0 }).ToArray());
        var snapshot = draft.Definition with { InitialStoryBible = initial };
        var context = new GenerationContext(snapshot, initial, [], null, 0);
        var response = await provider.GenerateOpeningAsync(settings, credential, context, cancellationToken);
        response = ValidateGenerationResponse(response, settings.ContentLimits);
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
            new(snapshot), applied.Bible, draft.StoryBibleMaintenanceHistory,
            stateSummaries.Count == 0 ? 0 : stateSummaries.Max(x => x.SortOrder) + 1, now, null, 0);
        var turn = CreateTurn(stateId, 0, null, response, applied, settings.ModelId!, now);
        await states.CreateAsync(state, turn, cancellationToken);
        _logger.LogInformation("Story State {StoryStateId} created.", state.Id);
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
            state.CurrentStoryBible,
            recent,
            action,
            state.LastCommittedTurnSequence + 1);
        _logger.LogInformation(
            "Generating turn {TurnSequence} for Story State {StoryStateId} with model {ModelId}.",
            context.NextTurnNumber,
            stateId,
            settings.ModelId);
        var response = await provider.GenerateTurnAsync(settings, credential, context, cancellationToken);
        response = ValidateGenerationResponse(response, settings.ContentLimits);
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
        _logger.LogInformation(
            "Turn {TurnSequence} committed for Story State {StoryStateId}.",
            sequence,
            stateId);
        return (next, turn);
    }

    private async Task<(ApiConnectionSettings Settings, string? Credential)> ConnectionAsync(CancellationToken cancellationToken)
    {
        var (settings, credential) = await DiscoveryConnectionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ModelId))
            throw new NarratorException("Load models and select one, or enter a model ID first.");
        return (settings, credential);
    }

    private async Task<(ApiConnectionSettings Settings, string? Credential)> DiscoveryConnectionAsync(CancellationToken cancellationToken)
    {
        return await connectionCoordinator.RunExclusiveAsync(async () =>
        {
            if (connectionCoordinator.RequiresCredentialReentry)
                throw new NarratorException("Re-enter and save the API credential before making another request.");
            var settings = await settingsStore.LoadAsync(cancellationToken);
            if (settings.BaseUrl is null)
                throw new NarratorException("Configure an API base URL first.");
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
        if (draft.Title.Length > limits.MaxStoryTitleCharacters)
            throw new NarratorException("The title exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(draft.StoryPrompt) || draft.StoryPrompt.Length > limits.MaxStoryPromptCharacters)
            throw new NarratorException("Enter a valid Story Prompt.");
    }

    private static StoryGenerationResponse ValidateGenerationResponse(StoryGenerationResponse response, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(response.Narration) || response.Narration.Length > limits.MaxNarrationCharacters)
            throw new NarratorException("The returned narration is empty or exceeds the configured limit.");
        if (response.SuggestedActions.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > limits.MaxSuggestedActionCharacters))
            throw new NarratorException("A returned suggested action is empty or exceeds the configured limit.");
        if (response.SuggestedActions.Count > limits.MaxSuggestedActions)
            response = response with { SuggestedActions = response.SuggestedActions.Take(limits.MaxSuggestedActions).ToArray() };
        if (response.StoryBibleUpdates.Count > limits.MaxStoryBibleUpdatesPerResponse)
            throw new NarratorException("The response contains too many Story Bible updates.");
        foreach (var update in response.StoryBibleUpdates.Where(x => x.Entry is not null))
            ValidateGeneratedEntry(update.Entry!, limits);
        return response;
    }

    private static void ValidateGeneratedEntry(ProposedStoryBibleEntry entry, ContentLimitSettings limits) =>
        ValidateEntryFields(entry.Category, entry.Name, entry.KnownFacts, entry.SecretFacts, entry.Importance, limits);

    private static void ValidateEntryFields(
        string category, string name, IReadOnlyList<string> knownFacts, IReadOnlyList<string> secretFacts, int importance, ContentLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > limits.MaxStoryBibleCategoryCharacters)
            throw new NarratorException("A Story Bible category is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > limits.MaxStoryBibleNameCharacters)
            throw new NarratorException("A Story Bible entry name is empty or exceeds the configured limit.");
        if (knownFacts.Count == 0 && secretFacts.Count == 0)
            throw new NarratorException("A Story Bible entry must have at least one known or secret fact.");
        if (knownFacts.Any(string.IsNullOrWhiteSpace) || secretFacts.Any(string.IsNullOrWhiteSpace))
            throw new NarratorException("A Story Bible entry has an empty fact.");
        if (importance is < 1 or > 5)
            throw new NarratorException("Story Bible importance must be from 1 to 5.");
    }

    private StoryBible NormalizeManualBible(StoryBible bible, ContentLimitSettings limits)
    {
        var seenIds = new HashSet<Guid>();
        var entries = new List<StoryBibleEntry>(bible.Entries.Count);
        foreach (var entry in bible.Entries)
        {
            var id = entry.Id == Guid.Empty ? idGenerator.NewId() : entry.Id;
            if (!seenIds.Add(id)) throw new NarratorException("Story Bible entry IDs must be unique.");
            var category = entry.Category.Trim();
            var name = entry.Name.Trim();
            var knownFacts = entry.KnownFacts.Select(x => x.Trim()).ToArray();
            var secretFacts = entry.SecretFacts.Select(x => x.Trim()).ToArray();
            ValidateEntryFields(category, name, knownFacts, secretFacts, entry.Importance, limits);
            entries.Add(entry with { Id = id, Category = category, Name = name, KnownFacts = knownFacts, SecretFacts = secretFacts });
        }
        return new StoryBible(entries);
    }

    private static IReadOnlyList<AppliedStoryBibleChange> DiffManualEdit(StoryBible before, StoryBible after)
    {
        var beforeById = before.Entries.ToDictionary(x => x.Id);
        var afterById = after.Entries.ToDictionary(x => x.Id);
        var changes = new List<AppliedStoryBibleChange>();
        foreach (var entry in after.Entries)
        {
            if (!beforeById.TryGetValue(entry.Id, out var previous))
                changes.Add(new(StoryBibleOperation.Add, entry.Id, null, entry, StoryBibleChangeSource.ManualEdit));
            else if (previous != entry)
                changes.Add(new(StoryBibleOperation.Replace, entry.Id, previous, entry, StoryBibleChangeSource.ManualEdit));
        }
        foreach (var entry in before.Entries)
            if (!afterById.ContainsKey(entry.Id))
                changes.Add(new(StoryBibleOperation.Remove, entry.Id, entry, null, StoryBibleChangeSource.ManualEdit));
        return changes;
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
