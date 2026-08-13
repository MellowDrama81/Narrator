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
    public Task<(StoryState State, StoryTurn Opening)> StartAsync(StartStoryDraft draft, Guid id) => application.StartStoryAsync(draft, id);
    public Task<(StoryState State, StoryTurn Turn)> PlayAsync(Guid id, string action) => application.PlayTurnAsync(id, action);
}
