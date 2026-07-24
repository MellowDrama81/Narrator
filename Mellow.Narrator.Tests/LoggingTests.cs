using Mellow.Narrator.Core;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mellow.Narrator.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mellow-narrator-logging-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileLogger_UsesPersistedLevelAndWritesJsonLines()
    {
        var services = new ServiceCollection();
        services.AddMellowNarratorPersistence(new(_root));
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<LoggingTests>>();

        logger.LogTrace("HIDDEN TRACE BODY");
        logger.LogInformation("Application started.");

        var logPath = Path.Combine(_root, "Mellow.Narrator", "logs", "narrator.log");
        var initial = await File.ReadAllTextAsync(logPath);
        Assert.Contains("\"level\":\"Information\"", initial);
        Assert.Contains("Application started.", initial);
        Assert.DoesNotContain("HIDDEN TRACE BODY", initial);

        var settings = provider.GetRequiredService<IApiConnectionSettingsStore>();
        await settings.SaveAsync(NarratorDefaults.Create() with
        {
            Logging = new(NarratorLogLevel.Trace)
        });
        logger.LogTrace("VISIBLE TRACE BODY");

        var traced = await File.ReadAllTextAsync(logPath);
        Assert.Contains("\"level\":\"Trace\"", traced);
        Assert.Contains("VISIBLE TRACE BODY", traced);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
