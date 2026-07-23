namespace Mellow.Narrator.Gui.Tests;

public sealed class GuiArchitectureTests
{
    [Fact]
    public void GuiUsesTabbedPageWithoutShellCustomHandlersOrThirdPartyControls()
    {
        var guiRoot = Path.Combine(FindRepositoryRoot(), "Mellow.Narrator.Gui");
        var project = File.ReadAllText(Path.Combine(guiRoot, "Mellow.Narrator.Gui.csproj"));
        var source = Directory.EnumerateFiles(guiRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join(Environment.NewLine, source);

        Assert.Contains("class MainTabbedPage", combined, StringComparison.Ordinal);
        Assert.Contains(": TabbedPage", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("<Shell", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(": Shell", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConfigureMauiHandlers", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Renderer", combined, StringComparison.OrdinalIgnoreCase);

        var packageLines = project.Split(Environment.NewLine)
            .Where(line => line.Contains("<PackageReference", StringComparison.Ordinal))
            .ToArray();
        Assert.All(packageLines, line =>
            Assert.True(
                line.Contains("Microsoft.Maui.Controls", StringComparison.Ordinal) ||
                line.Contains("Microsoft.Extensions.Logging.Debug", StringComparison.Ordinal),
                $"Unexpected GUI package reference: {line.Trim()}"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mellow.Narrator.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Mellow Narrator repository root.");
    }
}
