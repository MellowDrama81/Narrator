namespace Mellow.Narrator.Persistence;

public static class ApplicationDataMigration
{
    private const string MarkerFileName = ".legacy-windows-identity-migrated";

    public static bool CopyMissingLegacyWindowsIdentityData(
        string legacyPackageRoot,
        string currentPackageRoot)
    {
        var source = ValidateRoot(legacyPackageRoot, nameof(legacyPackageRoot));
        var destination = ValidateRoot(currentPackageRoot, nameof(currentPackageRoot));
        if (!Directory.Exists(source)) return false;

        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, MarkerFileName);
        if (File.Exists(marker)) return false;

        foreach (var folderName in new[] { "Data", "Settings" })
        {
            var sourceFolder = Path.Combine(source, folderName);
            if (Directory.Exists(sourceFolder))
                CopyMissing(sourceFolder, Path.Combine(destination, folderName));
        }

        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        return true;
    }

    private static void CopyMissing(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyMissing(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string ValidateRoot(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An application-data root is required.", parameterName);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("The application-data root must be absolute.", parameterName);
        return Path.GetFullPath(value);
    }
}
