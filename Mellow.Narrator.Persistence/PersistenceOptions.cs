namespace Mellow.Narrator.Persistence;

public sealed record PersistenceOptions(string ApplicationDataRoot)
{
    public string GetValidatedRoot()
    {
        if (string.IsNullOrWhiteSpace(ApplicationDataRoot))
            throw new ArgumentException("An application-data root is required.", nameof(ApplicationDataRoot));
        return Path.GetFullPath(ApplicationDataRoot);
    }
}
