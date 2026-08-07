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
    // Serializes "list existing sort orders, compute the next one, save" so two concurrent creates
    // within this process can't compute and persist the same SortOrder.
    private readonly SemaphoreSlim _definitionCreateGate = new(1, 1);
    private readonly SemaphoreSlim _stateCreateGate = new(1, 1);

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
        var definitionSummaries = await definitions.ListAsync(cancellationToken);
        var loadedDefinitions = await Task.WhenAll(
            definitionSummaries.Select(x => definitions.GetAsync(x.Id, cancellationToken)));
        var definitionCount = loadedDefinitions.Count(x =>
            x is not null && !StoryBibleProcessor.IsWithinLimits(x.InitialStoryBible, proposed));
        var plannedEventDefinitionCount = loadedDefinitions.Count(x =>
            x is not null && !PlannedEventProcessor.IsWithinLimits(x.InitialPlannedEvents, proposed));

        var stateSummaries = await states.ListAsync(cancellationToken);
        var loadedStates = await Task.WhenAll(
            stateSummaries.Select(x => states.GetAsync(x.Id, cancellationToken)));
        var stateCount = loadedStates.Count(x =>
            x is not null && !StoryBibleProcessor.IsWithinLimits(x.CurrentStoryBible, proposed));
        var plannedEventStateCount = loadedStates.Count(x =>
            x is not null && !PlannedEventProcessor.IsWithinLimits(x.CurrentPlannedEvents, proposed));

        return new(definitionCount, stateCount, plannedEventDefinitionCount, plannedEventStateCount);
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
        if (generated.InitialStoryBibleEntries.Count > SettingsValidator.MaxStoryBibleEntriesUpperBound)
            throw new NarratorException("The generated initial Story Bible contains too many entries.");
        foreach (var entry in generated.InitialStoryBibleEntries)
            ValidateGeneratedEntry(entry, settings.ContentLimits);
        if (generated.InitialPlannedEvents.Count > SettingsValidator.MaxPlannedEventsUpperBound)
            throw new NarratorException("The generated initial Planned Events contain too many entries.");
        foreach (var plannedEvent in generated.InitialPlannedEvents)
            ValidateGeneratedPlannedEvent(plannedEvent, settings.ContentLimits);
        if (generated.InitialVictoryConditions.Count > SettingsValidator.MaxConditionsUpperBound)
            throw new NarratorException("The generated initial Victory Conditions contain too many entries.");
        if (generated.InitialLossConditions.Count > SettingsValidator.MaxConditionsUpperBound)
            throw new NarratorException("The generated initial Loss Conditions contain too many entries.");
        var now = timeProvider.GetUtcNow();
        StoryDefinition? source = null;
        if (overwrite && draft.SourceStoryDefinitionId is { } sourceId)
            source = await definitions.GetAsync(sourceId, cancellationToken)
                ?? throw new NarratorException("The Story Definition to overwrite was not found.");
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
        var rawPlannedEvents = PlannedEventProcessor.ResolveInitialPlannedEvents(generated.InitialPlannedEvents, idGenerator.NewId);
        var (plannedEvents, plannedEventCulls) = PlannedEventProcessor.CullToLimits(rawPlannedEvents, settings.StoryGeneration);
        var plannedEventMaintenance = source?.PlannedEventMaintenanceHistory.ToList() ?? [];
        if (plannedEventCulls.Count > 0)
            plannedEventMaintenance.Add(new(idGenerator.NewId(), PlannedEventMaintenanceReason.GeneratedLimitCull,
                PlannedEventLimits(settings), plannedEventCulls, now));
        var victoryConditions = StoryConditionProcessor.ResolveInitial(generated.InitialVictoryConditions, idGenerator.NewId, settings.ContentLimits);
        if (!StoryConditionProcessor.IsWithinLimits(victoryConditions, settings.ContentLimits))
            throw new NarratorException("The generated initial Victory Conditions exceed the configured limits.");
        var lossConditions = StoryConditionProcessor.ResolveInitial(generated.InitialLossConditions, idGenerator.NewId, settings.ContentLimits);
        if (!StoryConditionProcessor.IsWithinLimits(lossConditions, settings.ContentLimits))
            throw new NarratorException("The generated initial Loss Conditions exceed the configured limits.");
        StoryDefinition definition;
        await _definitionCreateGate.WaitAsync(cancellationToken);
        try
        {
            var definitionSummaries = source is null ? await definitions.ListAsync(cancellationToken) : [];
            definition = new StoryDefinition(
                source?.Id ?? targetId,
                title,
                generated.RefinedStoryPrompt.Trim(),
                generated.InitialEventsPrompt.Trim(),
                bible,
                maintenance,
                plannedEvents,
                plannedEventMaintenance,
                victoryConditions,
                lossConditions,
                source?.SortOrder ?? (definitionSummaries.Count == 0 ? 0 : definitionSummaries.Max(x => x.SortOrder) + 1),
                source?.CreatedAtUtc ?? now,
                now);
            await definitions.SaveAsync(definition, cancellationToken);
        }
        finally { _definitionCreateGate.Release(); }
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} saved with {BibleEntryCount} initial Story Bible entries, {PlannedEventCount} initial Planned Events, " +
            "{VictoryConditionCount} Victory Conditions, and {LossConditionCount} Loss Conditions.",
            definition.Id,
            definition.InitialStoryBible.Entries.Count,
            definition.InitialPlannedEvents.Entries.Count,
            definition.InitialVictoryConditions.Entries.Count,
            definition.InitialLossConditions.Entries.Count);
        return definition;
    }

    public async Task<StoryDefinition> CullDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken) ?? throw new NarratorException("Story Definition not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var (bible, changes) = StoryBibleProcessor.CullToLimits(definition.InitialStoryBible, settings.StoryGeneration);
        var (plannedEvents, plannedEventChanges) = PlannedEventProcessor.CullToLimits(definition.InitialPlannedEvents, settings.StoryGeneration);
        if (changes.Count == 0 && plannedEventChanges.Count == 0) return definition;
        var now = timeProvider.GetUtcNow();
        var history = changes.Count == 0
            ? definition.StoryBibleMaintenanceHistory
            : definition.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
                idGenerator.NewId(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, now)).ToArray();
        var plannedEventHistory = plannedEventChanges.Count == 0
            ? definition.PlannedEventMaintenanceHistory
            : definition.PlannedEventMaintenanceHistory.Append(new PlannedEventMaintenanceRecord(
                idGenerator.NewId(), PlannedEventMaintenanceReason.UserApprovedLimitCull, PlannedEventLimits(settings), plannedEventChanges, now)).ToArray();
        var updated = definition with
        {
            InitialStoryBible = bible,
            StoryBibleMaintenanceHistory = history,
            InitialPlannedEvents = plannedEvents,
            PlannedEventMaintenanceHistory = plannedEventHistory,
            UpdatedAtUtc = now
        };
        await definitions.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} Story Bible culled with {ChangeCount} changes; Planned Events culled with {PlannedEventChangeCount} changes.",
            definitionId,
            changes.Count,
            plannedEventChanges.Count);
        return updated;
    }

    public async Task<StoryState> CullStoryStateAsync(Guid stateId, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var (bible, changes) = StoryBibleProcessor.CullToLimits(state.CurrentStoryBible, settings.StoryGeneration);
        var (plannedEvents, plannedEventChanges) = PlannedEventProcessor.CullToLimits(state.CurrentPlannedEvents, settings.StoryGeneration);
        if (changes.Count == 0 && plannedEventChanges.Count == 0) return state;
        var now = timeProvider.GetUtcNow();
        var history = changes.Count == 0
            ? state.StoryBibleMaintenanceHistory
            : state.StoryBibleMaintenanceHistory.Append(new StoryBibleMaintenanceRecord(
                idGenerator.NewId(), StoryBibleMaintenanceReason.UserApprovedLimitCull, Limits(settings), changes, now)).ToArray();
        var plannedEventHistory = plannedEventChanges.Count == 0
            ? state.PlannedEventMaintenanceHistory
            : state.PlannedEventMaintenanceHistory.Append(new PlannedEventMaintenanceRecord(
                idGenerator.NewId(), PlannedEventMaintenanceReason.UserApprovedLimitCull, PlannedEventLimits(settings), plannedEventChanges, now)).ToArray();
        var updated = state with
        {
            CurrentStoryBible = bible,
            StoryBibleMaintenanceHistory = history,
            CurrentPlannedEvents = plannedEvents,
            PlannedEventMaintenanceHistory = plannedEventHistory
        };
        await states.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story State {StoryStateId} Story Bible culled with {ChangeCount} changes; Planned Events culled with {PlannedEventChangeCount} changes.",
            stateId,
            changes.Count,
            plannedEventChanges.Count);
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

    public async Task<StoryState> UpdateStorySummaryAsync(Guid stateId, string summary, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var trimmed = summary.Trim();
        if (trimmed.Length > settings.ContentLimits.MaxStorySummaryCharacters)
            throw new NarratorException("The story summary exceeds the configured limit.");
        var updated = state with { StorySummary = trimmed };
        await states.SaveAsync(updated, cancellationToken);
        _logger.LogInformation("Story State {StoryStateId} story summary manually updated.", stateId);
        return updated;
    }

    public async Task<StoryDefinition> UpdateInitialPlannedEventsAsync(Guid definitionId, PlannedEvents events, CancellationToken cancellationToken = default)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken) ?? throw new NarratorException("Story Definition not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var normalized = NormalizeManualPlannedEvents(events, settings.ContentLimits);
        if (!PlannedEventProcessor.IsWithinLimits(normalized, settings.StoryGeneration))
            throw new NarratorException("The Planned Events exceed current limits. Increase the limits or cull them first.");
        var now = timeProvider.GetUtcNow();
        var changes = DiffManualPlannedEventEdit(definition.InitialPlannedEvents, normalized);
        var history = changes.Count == 0
            ? definition.PlannedEventMaintenanceHistory
            : definition.PlannedEventMaintenanceHistory.Append(new PlannedEventMaintenanceRecord(
                idGenerator.NewId(), PlannedEventMaintenanceReason.ManualEdit, PlannedEventLimits(settings), changes, now)).ToArray();
        var updated = definition with { InitialPlannedEvents = normalized, PlannedEventMaintenanceHistory = history, UpdatedAtUtc = now };
        await definitions.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} Planned Events manually updated with {ChangeCount} changes.",
            definitionId,
            changes.Count);
        return updated;
    }

    public async Task<StoryState> UpdateCurrentPlannedEventsAsync(Guid stateId, PlannedEvents events, CancellationToken cancellationToken = default)
    {
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var normalized = NormalizeManualPlannedEvents(events, settings.ContentLimits);
        if (!PlannedEventProcessor.IsWithinLimits(normalized, settings.StoryGeneration))
            throw new NarratorException("The Planned Events exceed current limits. Increase the limits or cull them first.");
        var changes = DiffManualPlannedEventEdit(state.CurrentPlannedEvents, normalized);
        var history = changes.Count == 0
            ? state.PlannedEventMaintenanceHistory
            : state.PlannedEventMaintenanceHistory.Append(new PlannedEventMaintenanceRecord(
                idGenerator.NewId(), PlannedEventMaintenanceReason.ManualEdit, PlannedEventLimits(settings), changes, timeProvider.GetUtcNow())).ToArray();
        var updated = state with { CurrentPlannedEvents = normalized, PlannedEventMaintenanceHistory = history };
        await states.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story State {StoryStateId} Planned Events manually updated with {ChangeCount} changes.",
            stateId,
            changes.Count);
        return updated;
    }

    public Task<StoryDefinition> UpdateInitialVictoryConditionsAsync(Guid definitionId, StoryConditions conditions, CancellationToken cancellationToken = default) =>
        UpdateInitialConditionsAsync(definitionId, conditions, isVictory: true, cancellationToken);

    public Task<StoryDefinition> UpdateInitialLossConditionsAsync(Guid definitionId, StoryConditions conditions, CancellationToken cancellationToken = default) =>
        UpdateInitialConditionsAsync(definitionId, conditions, isVictory: false, cancellationToken);

    private async Task<StoryDefinition> UpdateInitialConditionsAsync(
        Guid definitionId, StoryConditions conditions, bool isVictory, CancellationToken cancellationToken)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken) ?? throw new NarratorException("Story Definition not found.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var normalized = NormalizeManualConditions(conditions, settings.ContentLimits);
        if (!StoryConditionProcessor.IsWithinLimits(normalized, settings.ContentLimits))
            throw new NarratorException("The conditions exceed current limits.");
        var now = timeProvider.GetUtcNow();
        var updated = isVictory
            ? definition with { InitialVictoryConditions = normalized, UpdatedAtUtc = now }
            : definition with { InitialLossConditions = normalized, UpdatedAtUtc = now };
        await definitions.SaveAsync(updated, cancellationToken);
        _logger.LogInformation(
            "Story Definition {StoryDefinitionId} {ConditionKind} Conditions manually updated with {ConditionCount} entries.",
            definitionId,
            isVictory ? "Victory" : "Loss",
            normalized.Entries.Count);
        return updated;
    }

    public async Task<(StoryState State, StoryTurn Opening)> StartStoryAsync(
        StartStoryDraft draft,
        Guid targetStateId,
        CancellationToken cancellationToken = default)
    {
        if (targetStateId == Guid.Empty) throw new ArgumentException("Target ID cannot be empty.", nameof(targetStateId));
        using var requestLease = storyRequests.Enter(targetStateId);
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        _logger.LogInformation(
            "Generating opening scene for Story State {StoryStateId} with model {ModelId}.",
            targetStateId,
            settings.ModelId);
        if (!StoryBibleProcessor.IsWithinLimits(draft.Definition.InitialStoryBible, settings.StoryGeneration))
            throw new NarratorException("The initial Story Bible exceeds current limits. Increase the limits or cull it first.");
        if (!PlannedEventProcessor.IsWithinLimits(draft.Definition.InitialPlannedEvents, settings.StoryGeneration))
            throw new NarratorException("The initial Planned Events exceed current limits. Increase the limits or cull them first.");
        if (!StoryConditionProcessor.IsWithinLimits(draft.Definition.InitialVictoryConditions, settings.ContentLimits))
            throw new NarratorException("The initial Victory Conditions exceed current limits.");
        if (!StoryConditionProcessor.IsWithinLimits(draft.Definition.InitialLossConditions, settings.ContentLimits))
            throw new NarratorException("The initial Loss Conditions exceed current limits.");

        // Entries removed by an earlier cull can still be referenced by StoryBibleMaintenanceHistory/
        // PlannedEventMaintenanceHistory below, so ids are mapped lazily (not just for the entries
        // currently in the bible/planned events) to keep every reference to the same old id pointing at
        // the same new id. Bible entry ids, planned event ids, and condition ids are all freshly
        // generated GUIDs and never collide, so one shared map safely covers all three.
        var idMap = new Dictionary<Guid, Guid>();
        Guid MapId(Guid oldId) => idMap.TryGetValue(oldId, out var mapped) ? mapped : idMap[oldId] = idGenerator.NewId();

        var initial = new StoryBible(draft.Definition.InitialStoryBible.Entries
            .Select(x => x with { Id = MapId(x.Id), LastRelevantTurnNumber = 0 }).ToArray());
        var initialPlannedEvents = new PlannedEvents(draft.Definition.InitialPlannedEvents.Entries
            .Select(x => x with { Id = MapId(x.Id), LastRelevantTurnNumber = 0 }).ToArray());
        var initialVictoryConditions = new StoryConditions(draft.Definition.InitialVictoryConditions.Entries
            .Select(x => x with { Id = MapId(x.Id) }).ToArray());
        var initialLossConditions = new StoryConditions(draft.Definition.InitialLossConditions.Entries
            .Select(x => x with { Id = MapId(x.Id) }).ToArray());
        var snapshot = draft.Definition with
        {
            InitialStoryBible = initial,
            InitialPlannedEvents = initialPlannedEvents,
            InitialVictoryConditions = initialVictoryConditions,
            InitialLossConditions = initialLossConditions
        };
        var context = new GenerationContext(
            snapshot, initial, initialPlannedEvents,
            new(initialVictoryConditions, [], []),
            new(initialLossConditions, [], []),
            "",
            [], null, 0);
        var response = await provider.GenerateOpeningAsync(settings, credential, context, cancellationToken);
        response = ValidateGenerationResponse(response, settings.ContentLimits);
        var relevant = response.RelevantStoryBibleEntryIds
            .Concat(initial.Entries.Select(x => x.Id))
            .Distinct()
            .ToArray();
        var applied = StoryBibleProcessor.Apply(initial, relevant, response.StoryBibleUpdates, 0, settings.StoryGeneration, idGenerator.NewId);
        var relevantPlannedEvents = response.RelevantPlannedEventIds
            .Concat(initialPlannedEvents.Entries.Select(x => x.Id))
            .Distinct()
            .ToArray();
        var appliedPlannedEvents = PlannedEventProcessor.Apply(
            initialPlannedEvents, relevantPlannedEvents, response.PlannedEventUpdates, 0, settings.StoryGeneration, idGenerator.NewId);
        var (revealedVictory, metVictory) = StoryConditionProcessor.ApplyTurn(
            initialVictoryConditions, [], [], response.RevealedVictoryConditionIds, response.MetVictoryConditionIds);
        var (revealedLoss, metLoss) = StoryConditionProcessor.ApplyTurn(
            initialLossConditions, [], [], response.RevealedLossConditionIds, response.MetLossConditionIds);
        var maintenanceHistory = draft.StoryBibleMaintenanceHistory.Select(x => x with
        {
            Changes = x.Changes.Select(change => change with
            {
                EntryId = MapId(change.EntryId),
                Before = change.Before is null ? null : change.Before with { Id = MapId(change.Before.Id) },
                After = change.After is null ? null : change.After with { Id = MapId(change.After.Id) }
            }).ToArray()
        }).ToArray();
        var plannedEventMaintenanceHistory = draft.PlannedEventMaintenanceHistory.Select(x => x with
        {
            Changes = x.Changes.Select(change => change with
            {
                EntryId = MapId(change.EntryId),
                Before = change.Before is null ? null : change.Before with { Id = MapId(change.Before.Id) },
                After = change.After is null ? null : change.After with { Id = MapId(change.After.Id) }
            }).ToArray()
        }).ToArray();
        var now = timeProvider.GetUtcNow();
        var stateId = targetStateId;
        StoryState state;
        StoryTurn turn;
        await _stateCreateGate.WaitAsync(cancellationToken);
        try
        {
            var stateSummaries = await states.ListAsync(cancellationToken);
            state = new StoryState(stateId, snapshot.Title, draft.SourceStoryDefinitionId,
                new(snapshot), applied.Bible, maintenanceHistory,
                appliedPlannedEvents.Events, plannedEventMaintenanceHistory,
                initialVictoryConditions, initialLossConditions,
                revealedVictory, metVictory, revealedLoss, metLoss,
                response.StorySummary,
                stateSummaries.Count == 0 ? 0 : stateSummaries.Max(x => x.SortOrder) + 1, now, null, 0);
            turn = CreateTurn(stateId, 0, null, response, applied, appliedPlannedEvents,
                revealedVictory, metVictory, revealedLoss, metLoss, settings.ModelId!, now);
            await states.CreateAsync(state, turn, cancellationToken);
        }
        finally { _stateCreateGate.Release(); }
        _logger.LogInformation("Story State {StoryStateId} created.", state.Id);
        return (state, turn);
    }

    public async Task<(StoryState State, StoryTurn Turn)> PlayTurnAsync(Guid stateId, string action, CancellationToken cancellationToken = default)
    {
        using var requestLease = storyRequests.Enter(stateId);
        var state = await states.GetAsync(stateId, cancellationToken) ?? throw new NarratorException("Story State not found.");
        var (settings, credential) = await ConnectionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(action)) throw new NarratorException("Enter an action.");
        if (action.Length > settings.ContentLimits.MaxPlayerActionCharacters) throw new NarratorException("The action exceeds the configured limit.");
        if (!StoryBibleProcessor.IsWithinLimits(state.CurrentStoryBible, settings.StoryGeneration))
            throw new NarratorException("The Story Bible exceeds current limits. Increase the limits or cull it first.");
        if (!PlannedEventProcessor.IsWithinLimits(state.CurrentPlannedEvents, settings.StoryGeneration))
            throw new NarratorException("The Planned Events exceed current limits. Increase the limits or cull them first.");
        var recent = await states.GetTurnsAsync(stateId, settings.StoryGeneration.RecentTurnCount, cancellationToken);
        var context = new GenerationContext(
            state.Setup.Definition,
            state.CurrentStoryBible,
            state.CurrentPlannedEvents,
            new(state.CurrentVictoryConditions, state.RevealedVictoryConditionIds, state.MetVictoryConditionIds),
            new(state.CurrentLossConditions, state.RevealedLossConditionIds, state.MetLossConditionIds),
            state.StorySummary,
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
        var appliedPlannedEvents = PlannedEventProcessor.Apply(
            state.CurrentPlannedEvents,
            response.RelevantPlannedEventIds,
            response.PlannedEventUpdates,
            sequence,
            settings.StoryGeneration,
            idGenerator.NewId);
        var (revealedVictory, metVictory) = StoryConditionProcessor.ApplyTurn(
            state.CurrentVictoryConditions, state.RevealedVictoryConditionIds, state.MetVictoryConditionIds,
            response.RevealedVictoryConditionIds, response.MetVictoryConditionIds);
        var (revealedLoss, metLoss) = StoryConditionProcessor.ApplyTurn(
            state.CurrentLossConditions, state.RevealedLossConditionIds, state.MetLossConditionIds,
            response.RevealedLossConditionIds, response.MetLossConditionIds);
        var now = timeProvider.GetUtcNow();
        var next = state with
        {
            CurrentStoryBible = applied.Bible,
            CurrentPlannedEvents = appliedPlannedEvents.Events,
            RevealedVictoryConditionIds = state.RevealedVictoryConditionIds.Concat(revealedVictory).ToArray(),
            MetVictoryConditionIds = state.MetVictoryConditionIds.Concat(metVictory).ToArray(),
            RevealedLossConditionIds = state.RevealedLossConditionIds.Concat(revealedLoss).ToArray(),
            MetLossConditionIds = state.MetLossConditionIds.Concat(metLoss).ToArray(),
            StorySummary = response.StorySummary,
            LastActionAtUtc = now,
            LastCommittedTurnSequence = sequence
        };
        var turn = CreateTurn(stateId, sequence, action, response, applied, appliedPlannedEvents,
            revealedVictory, metVictory, revealedLoss, metLoss, settings.ModelId!, now);
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
        StoryBibleApplyResult applied, PlannedEventApplyResult appliedPlannedEvents,
        IReadOnlyList<Guid> revealedVictory, IReadOnlyList<Guid> metVictory,
        IReadOnlyList<Guid> revealedLoss, IReadOnlyList<Guid> metLoss,
        string model, DateTimeOffset now) =>
        new(idGenerator.NewId(), stateId, sequence, action, response.Narration, response.SuggestedActions,
            applied.RelevantEntryIds, applied.Changes,
            appliedPlannedEvents.RelevantEntryIds, appliedPlannedEvents.Changes,
            revealedVictory, metVictory, revealedLoss, metLoss,
            now, new(model, response.ProviderResponseId, response.InputTokens, response.OutputTokens));

    private static StoryBibleLimitSnapshot Limits(ApiConnectionSettings settings) =>
        new(settings.StoryGeneration.MaxStoryBibleEntries, settings.StoryGeneration.MaxStoryBibleEntryCharacters, settings.StoryGeneration.MaxStoryBibleCharacters);

    private static PlannedEventLimitSnapshot PlannedEventLimits(ApiConnectionSettings settings) =>
        new(settings.StoryGeneration.MaxPlannedEvents, settings.StoryGeneration.MaxPlannedEventCharacters, settings.StoryGeneration.MaxPlannedEventsCharacters);

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
        if (response.PlannedEventUpdates.Count > limits.MaxPlannedEventUpdatesPerResponse)
            throw new NarratorException("The response contains too many Planned Event updates.");
        foreach (var update in response.PlannedEventUpdates.Where(x => x.Entry is not null))
            ValidateGeneratedPlannedEvent(update.Entry!, limits);
        if (response.StorySummary.Length > limits.MaxStorySummaryCharacters)
            throw new NarratorException("The returned story summary exceeds the configured limit.");
        return response;
    }

    private static void ValidateGeneratedEntry(ProposedStoryBibleEntry entry, ContentLimitSettings limits) =>
        ValidateEntryFields(entry.Category, entry.Name, entry.KnownFacts, entry.SecretFacts, entry.Importance, null, limits);

    private static void ValidateEntryFields(
        string category, string name, IReadOnlyList<string> knownFacts, IReadOnlyList<string> secretFacts,
        int importance, int? lastRelevantTurnNumber, ContentLimitSettings limits)
    {
        if (StoryBibleProcessor.ValidateEntry(category, name, knownFacts, secretFacts, importance, lastRelevantTurnNumber, limits) is { } error)
            throw new NarratorException(error);
    }

    private static void ValidateGeneratedPlannedEvent(ProposedPlannedEvent plannedEvent, ContentLimitSettings limits) =>
        ValidatePlannedEventFields(plannedEvent.Description, plannedEvent.Importance, plannedEvent.Urgency, plannedEvent.Condition, null, limits);

    private static void ValidatePlannedEventFields(string description, int importance, int urgency, string? condition, int? lastRelevantTurnNumber, ContentLimitSettings limits)
    {
        if (PlannedEventProcessor.ValidateEntry(description, importance, urgency, condition, lastRelevantTurnNumber, limits) is { } error)
            throw new NarratorException(error);
    }

    // Manual edits have no notion of already-revealed/already-met ids (those only exist on a live Story
    // State, not the Story Definition), so a manually re-authored condition's id is preserved when
    // present and assigned fresh only when missing - matching NormalizeManualBible/
    // NormalizeManualPlannedEvents for the same reason.
    private StoryConditions NormalizeManualConditions(StoryConditions conditions, ContentLimitSettings limits)
    {
        var seenIds = new HashSet<Guid>();
        var entries = new List<StoryCondition>(conditions.Entries.Count);
        foreach (var entry in conditions.Entries)
        {
            var id = entry.Id == Guid.Empty ? idGenerator.NewId() : entry.Id;
            if (!seenIds.Add(id)) throw new NarratorException("Condition IDs must be unique.");
            var description = entry.Description.Trim();
            if (StoryConditionProcessor.ValidateEntry(description, limits) is { } error)
                throw new NarratorException(error);
            entries.Add(entry with { Id = id, Description = description });
        }
        return new StoryConditions(entries);
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
            ValidateEntryFields(category, name, knownFacts, secretFacts, entry.Importance, entry.LastRelevantTurnNumber, limits);
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

    // Manual edits (via NarratorApplication) have no LLM-supplied outcome and are not subject to the
    // mandatory-removal rule PlannedEventProcessor.Apply enforces on the LLM path - the author who set an
    // event's importance in the first place is free to remove or demote it directly, same as Story Bible
    // manual edits are unrestricted.
    private PlannedEvents NormalizeManualPlannedEvents(PlannedEvents events, ContentLimitSettings limits)
    {
        var seenIds = new HashSet<Guid>();
        var entries = new List<PlannedEvent>(events.Entries.Count);
        foreach (var entry in events.Entries)
        {
            var id = entry.Id == Guid.Empty ? idGenerator.NewId() : entry.Id;
            if (!seenIds.Add(id)) throw new NarratorException("Planned Event IDs must be unique.");
            var description = entry.Description.Trim();
            var condition = string.IsNullOrWhiteSpace(entry.Condition) ? null : entry.Condition.Trim();
            ValidatePlannedEventFields(description, entry.Importance, entry.Urgency, condition, entry.LastRelevantTurnNumber, limits);
            entries.Add(entry with { Id = id, Description = description, Condition = condition });
        }
        return new PlannedEvents(entries);
    }

    private static IReadOnlyList<AppliedPlannedEventChange> DiffManualPlannedEventEdit(PlannedEvents before, PlannedEvents after)
    {
        var beforeById = before.Entries.ToDictionary(x => x.Id);
        var afterById = after.Entries.ToDictionary(x => x.Id);
        var changes = new List<AppliedPlannedEventChange>();
        foreach (var entry in after.Entries)
        {
            if (!beforeById.TryGetValue(entry.Id, out var previous))
                changes.Add(new(PlannedEventOperation.Add, entry.Id, null, entry, PlannedEventChangeSource.ManualEdit, null));
            else if (previous != entry)
                changes.Add(new(PlannedEventOperation.Replace, entry.Id, previous, entry, PlannedEventChangeSource.ManualEdit, null));
        }
        foreach (var entry in before.Entries)
            if (!afterById.ContainsKey(entry.Id))
                changes.Add(new(PlannedEventOperation.Remove, entry.Id, entry, null, PlannedEventChangeSource.ManualEdit, null));
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
        // Singleton, not Transient: NarratorApplication holds no per-call mutable state beyond its
        // injected singletons, and its sort-order-assignment gates (see _definitionCreateGate/
        // _stateCreateGate) only serialize concurrent creates correctly if every caller shares the
        // same instance.
        services.AddSingleton<INarratorApplication, NarratorApplication>();
        return services;
    }
}
