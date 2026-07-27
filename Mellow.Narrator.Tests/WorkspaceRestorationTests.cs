using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class WorkspaceRestorationTests
{
    [Fact]
    public void WorkspaceSupportsSixTabTypes()
    {
        Assert.Equal(6, Enum.GetValues<TabType>().Length);
    }

    [Fact]
    public void FixedTabsOccupyFirstThreeLogicalTypes()
    {
        Assert.Equal(TabType.Settings, (TabType)0);
        Assert.Equal(TabType.StoryDefinitionList, (TabType)1);
        Assert.Equal(TabType.PlayStoryList, (TabType)2);
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

    private static OpenTabState Tab(TabType type, int position) =>
        new(Guid.NewGuid(), type, position, null, null, null, null);
}
