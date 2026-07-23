using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;

if (args.Length < 2 || args[0] != "--data")
{
    Console.Error.WriteLine("Usage: Mellow.Narrator.Cli --data <isolated-test-directory> [list|test]");
    return 2;
}

var dataRoot = Path.GetFullPath(args[1]);
var command = args.ElementAtOrDefault(2) ?? "list";
var services = new ServiceCollection();
services.AddMellowNarratorCore()
    .AddMellowNarratorOpenAiCompatible()
    .AddMellowNarratorPersistence(new PersistenceOptions(dataRoot));
services.AddSingleton<ISecureStorageService>(new ProcessSecureStorage(
    Environment.GetEnvironmentVariable("MELLOW_NARRATOR_API_KEY")));
await using var provider = services.BuildServiceProvider();

try
{
    if (command == "test")
    {
        var result = await provider.GetRequiredService<INarratorApplication>().TestConnectionAsync();
        Console.WriteLine(result.Success
            ? $"Connected; structured output: {result.Capabilities.StructuredOutputTier}; models: {result.Models.Count}"
            : $"Connection failed: {result.Error}");
        return result.Success ? 0 : 1;
    }

    var definitions = await provider.GetRequiredService<IStoryDefinitionRepository>().ListAsync();
    var stories = await provider.GetRequiredService<IStoryStateRepository>().ListAsync();
    Console.WriteLine($"Data root: {dataRoot}");
    Console.WriteLine($"Story Definitions: {definitions.Count}");
    foreach (var item in definitions) Console.WriteLine($"  {item.Id}  {item.Title}");
    Console.WriteLine($"Story States: {stories.Count}");
    foreach (var item in stories) Console.WriteLine($"  {item.Id}  {item.Label}");
    return 0;
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
