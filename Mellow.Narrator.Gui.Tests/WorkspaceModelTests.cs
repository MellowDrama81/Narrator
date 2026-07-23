using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui.Tests;

public sealed class WorkspaceModelTests
{
    [Fact]
    public void WorkspaceSupportsSevenTabTypes()
    {
        Assert.Equal(7, Enum.GetValues<TabType>().Length);
    }

    [Fact]
    public void FixedTabsOccupyFirstThreeLogicalTypes()
    {
        Assert.Equal(TabType.Settings, (TabType)0);
        Assert.Equal(TabType.StoryDefinitionList, (TabType)1);
        Assert.Equal(TabType.PlayStoryList, (TabType)2);
    }
}
