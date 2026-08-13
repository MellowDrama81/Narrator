using Mellow.Narrator.Core;

namespace Mellow.Narrator.MauiBlazor.Services;

// Thin UI-facing facade. It deliberately delegates all story mutations to the shared application
// service, keeping the Hybrid UI behavior identical to the native MAUI application.
public sealed class NarratorWorkspace(
    INarratorApplication application,
    IStoryDefinitionRepository definitions,
    IStoryStateRepository stories,
    ITrashStore trash)
{
    public Task<IReadOnlyList<StoryDefinitionSummary>> DefinitionsAsync() => definitions.ListAsync();
    public Task<IReadOnlyList<StoryStateSummary>> StoriesAsync() => stories.ListAsync();
    public Task<StoryDefinition?> DefinitionAsync(Guid id) => definitions.GetAsync(id);
    public Task<StoryState?> StoryAsync(Guid id) => stories.GetAsync(id);
    public Task<IReadOnlyList<StoryTurn>> TurnsAsync(Guid id) => stories.GetTurnsAsync(id);
    public Task<IReadOnlyList<TrashItem>> TrashAsync() => trash.ListAsync();
    public Task<ApiConnectionSettings> SettingsAsync() => application.GetSettingsAsync();
    public Task<StoryDefinition> CreateBlankDefinitionAsync(string? title) => application.CreateBlankDefinitionAsync(title);
    public Task<StoryDefinition> GenerateDefinitionAsync(StoryPromptDraft draft, bool overwrite, Guid targetId) => application.GenerateDefinitionAsync(draft, overwrite, targetId);
    public Task SaveDefinitionAsync(StoryDefinition definition) => definitions.SaveAsync(definition);
    public Task DeleteDefinitionAsync(Guid id) => definitions.MoveToTrashAsync(id);
    public Task DeleteStoryAsync(Guid id) => stories.MoveToTrashAsync(id);
    public Task<StoryState> CopyStoryAsync(Guid id) => stories.CopyAsync(id);
    public Task SaveStoryAsync(StoryState state) => stories.SaveAsync(state);
    public Task<StoryDefinition> SaveDefinitionBibleAsync(Guid id, StoryBible bible) => application.UpdateInitialStoryBibleAsync(id, bible);
    public Task<StoryDefinition> SaveDefinitionEventsAsync(Guid id, PlannedEvents events) => application.UpdateInitialPlannedEventsAsync(id, events);
    public Task<StoryDefinition> SaveDefinitionVictoryConditionsAsync(Guid id, StoryConditions conditions) => application.UpdateInitialVictoryConditionsAsync(id, conditions);
    public Task<StoryDefinition> SaveDefinitionLossConditionsAsync(Guid id, StoryConditions conditions) => application.UpdateInitialLossConditionsAsync(id, conditions);
    public Task<StoryState> SaveStoryBibleAsync(Guid id, StoryBible bible) => application.UpdateCurrentStoryBibleAsync(id, bible);
    public Task<StoryState> SaveStoryEventsAsync(Guid id, PlannedEvents events) => application.UpdateCurrentPlannedEventsAsync(id, events);
    public Task<StoryState> SaveStorySummaryAsync(Guid id, string summary) => application.UpdateStorySummaryAsync(id, summary);
    public Task<IReadOnlyList<string>> DiscoverModelsAsync(Guid connectionId) => application.DiscoverModelsAsync(connectionId);
    public Task<ConnectionTestResult> TestConnectionAsync(Guid connectionId) => application.TestConnectionAsync(connectionId);
    public Task SaveSettingsAsync(ApiConnectionSettings settings) => application.SaveSettingsAsync(settings, null);
    public Task SaveConnectionCredentialAsync(Guid connectionId, string? credential) => application.SaveConnectionCredentialAsync(connectionId, credential);
    public Task<string?> ConnectionCredentialAsync(Guid connectionId) => application.GetConnectionCredentialAsync(connectionId);
    public Task RestoreTrashAsync(string trashId) => trash.RestoreAsync(trashId);
    public Task DeleteTrashAsync(string trashId) => trash.DeletePermanentlyAsync(trashId);
    public Task EmptyTrashAsync() => trash.EmptyAsync();
    public Task<(StoryState State, StoryTurn Opening)> StartAsync(StartStoryDraft draft, Guid id) => application.StartStoryAsync(draft, id);
    public Task<(StoryState State, StoryTurn Turn)> PlayAsync(Guid id, string action) => application.PlayTurnAsync(id, action);
}
