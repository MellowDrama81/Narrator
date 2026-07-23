namespace Mellow.Narrator.Core;

public static class WorkspaceRestoration
{
    public static Guid? SelectActiveTabId(
        WorkspaceState workspace,
        IReadOnlySet<Guid> restoredTabIds)
    {
        if (restoredTabIds.Contains(workspace.ActiveTabId))
            return workspace.ActiveTabId;

        return workspace.Tabs
            .Where(tab => restoredTabIds.Contains(tab.TabId))
            .OrderBy(tab => tab.Type == TabType.Settings ? 0 : 1)
            .ThenBy(tab => tab.Position)
            .Select(tab => (Guid?)tab.TabId)
            .FirstOrDefault();
    }
}
