using System.Text.Json;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class WorkspaceRestorationTests
{
    [Fact]
    public void TabType_MembersSerializeToStableNames()
    {
        // TabType is persisted in workspace.json as a camelCase string via JsonStringEnumConverter, not
        // as its ordinal, so reordering the enum's declaration is harmless - renaming or removing a
        // member is what would break loading an existing user's saved workspace. Pin the exact name
        // every currently-supported member serializes to, so that regression shows up here.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var serialized = Enum.GetValues<TabType>().ToDictionary(x => x, x => JsonSerializer.Serialize(x, options).Trim('"'));

        Assert.Equal(new Dictionary<TabType, string>
        {
            [TabType.Settings] = "settings",
            [TabType.StoryDefinitionList] = "storyDefinitionList",
            [TabType.PlayStoryList] = "playStoryList",
            [TabType.StoryDefinition] = "storyDefinition",
            [TabType.StoryPrompt] = "storyPrompt",
            [TabType.PlayStory] = "playStory"
        }, serialized);
    }

    [Fact]
    public void SelectActiveTabId_UsesRestoredIdentityWhenPositionsHaveShifted()
    {
        var settings = Tab(TabType.Settings, 0);
        var missing = Tab(TabType.StoryDefinition, 3);
        var active = Tab(TabType.PlayStory, 4);
        var workspace = new WorkspaceState(active.TabId, [settings, missing, active]);

        var selected = WorkspaceRestoration.SelectActiveTabId(
            workspace,
            new HashSet<Guid> { settings.TabId, active.TabId });

        Assert.Equal(active.TabId, selected);
    }

    [Fact]
    public void SelectActiveTabId_FallsBackToRestoredSettingsWhenActiveTabWasSkipped()
    {
        var settings = Tab(TabType.Settings, 0);
        var missingActive = Tab(TabType.PlayStory, 4);
        var remaining = Tab(TabType.StoryDefinition, 5);
        var workspace = new WorkspaceState(missingActive.TabId, [settings, missingActive, remaining]);

        var selected = WorkspaceRestoration.SelectActiveTabId(
            workspace,
            new HashSet<Guid> { settings.TabId, remaining.TabId });

        Assert.Equal(settings.TabId, selected);
    }

    [Fact]
    public void SelectActiveTabId_ReturnsNullWhenNoTabsSurvivedRestoration()
    {
        var missingActive = Tab(TabType.PlayStory, 0);
        var workspace = new WorkspaceState(missingActive.TabId, [missingActive]);

        var selected = WorkspaceRestoration.SelectActiveTabId(workspace, new HashSet<Guid>());

        Assert.Null(selected);
    }

    [Fact]
    public void SelectActiveTabId_BreaksSameTypeTiesByPosition()
    {
        var missingActive = Tab(TabType.PlayStory, 5);
        var earlier = Tab(TabType.StoryDefinition, 1);
        var later = Tab(TabType.StoryDefinition, 2);
        var workspace = new WorkspaceState(missingActive.TabId, [missingActive, earlier, later]);

        var selected = WorkspaceRestoration.SelectActiveTabId(
            workspace,
            new HashSet<Guid> { earlier.TabId, later.TabId });

        Assert.Equal(earlier.TabId, selected);
    }

    private static OpenTabState Tab(TabType type, int position) =>
        new(Guid.NewGuid(), type, position, null, null, null, null);
}
