using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;

const string Usage = """
    Usage: Mellow.Narrator.Cli --data <isolated-test-directory> [list|test]
      list          List Story Definitions and Story States (default).
      test          Test the configured API connection.
      --help, -h    Show this usage message.
    """;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Usage);
    return 0;
}

if (args.Length < 2 || args[0] != "--data")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args.ElementAtOrDefault(2) ?? "list";
if (command is not ("list" or "test"))
{
    Console.Error.WriteLine($"Unrecognized command '{command}'.");
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let the app shut down through its own cancellation path instead of the process dying
    // mid-request if a call to a slow/misconfigured endpoint is in flight.
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var dataRoot = Path.GetFullPath(args[1]);
    var services = new ServiceCollection();
    services.AddMellowNarratorCore()
        .AddMellowNarratorOpenAiCompatible()
        .AddMellowNarratorPersistence(new PersistenceOptions(dataRoot));
    services.AddSingleton<ISecureStorageService>(new ProcessSecureStorage(
        Environment.GetEnvironmentVariable("MELLOW_NARRATOR_API_KEY")));
    await using var provider = services.BuildServiceProvider();

    if (command == "test")
    {
        var result = await provider.GetRequiredService<INarratorApplication>().TestConnectionAsync(cancellation.Token);
        Console.WriteLine(result.Success
            ? $"Connected; structured output: {result.Capabilities.StructuredOutputTier}; models: {result.Models.Count}"
            : $"Connection failed: {result.Error}");
        return result.Success ? 0 : 1;
    }

    var definitions = await provider.GetRequiredService<IStoryDefinitionRepository>().ListAsync(cancellation.Token);
    var stories = await provider.GetRequiredService<IStoryStateRepository>().ListAsync(cancellation.Token);
    Console.WriteLine($"Data root: {dataRoot}");
    Console.WriteLine($"Story Definitions: {definitions.Count}");
    foreach (var item in definitions) Console.WriteLine($"  {item.Id}  {item.Title}");
    Console.WriteLine($"Story States: {stories.Count}");
    foreach (var item in stories) Console.WriteLine($"  {item.Id}  {item.Label}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal sealed class ProcessSecureStorage(string? credential) : ISecureStorageService
{
    private string? _credential = credential;
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(key == SecureStorageKeys.ApiCredential ? _credential : null);
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (key == SecureStorageKeys.ApiCredential) _credential = value;
        return Task.CompletedTask;
    }
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var existed = key == SecureStorageKeys.ApiCredential && _credential is not null;
        if (key == SecureStorageKeys.ApiCredential) _credential = null;
        return Task.FromResult(existed);
    }
}
