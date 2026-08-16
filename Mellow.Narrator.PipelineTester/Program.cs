using System.Text;
using System.Text.Json;
using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;

var options = Arguments.Parse(args);
var configuration = await Configuration.LoadAsync(options.ConfigPath);
if (!Enum.TryParse<TurnPipelineMode>(options.Pipeline, true, out var pipeline))
    throw new ArgumentException($"Unknown pipeline '{options.Pipeline}'.");

Directory.CreateDirectory(options.LogDirectory);
using var client = new HttpClient(new WireLogHandler(options.LogDirectory)) { Timeout = Timeout.InfiniteTimeSpan };
var capabilities = new ConnectionCapabilities(false, configuration.StructuredOutputTier, configuration.ModelId, DateTimeOffset.UtcNow)
{
    InstructionMessageRole = configuration.InstructionMessageRole,
    OutputTokenParameter = configuration.OutputTokenParameter
};
var settings = NarratorDefaults.Create() with
{
    BaseUrl = new Uri(configuration.BaseUrl), ModelId = configuration.ModelId,
    RequestTimeout = TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds), MaxOutputTokens = configuration.MaxOutputTokens,
    Capabilities = capabilities, TurnPipeline = pipeline,
    Logging = new LoggingSettings(NarratorLogLevel.Trace)
};
var provider = new OpenAiCompatibleProvider(client, TimeProvider.System);
try
{
    var response = await provider.GenerateTurnAsync(settings, configuration.ApiKey, ProbeContext());
    await File.WriteAllTextAsync(Path.Combine(options.LogDirectory, "result.json"), JsonSerializer.Serialize(new { pipeline, response }, Json.Options));
    Console.WriteLine($"{pipeline}: success. {response.Narration.Length} narration characters; {response.SuggestedActions.Count} suggested actions.");
}
catch (Exception exception)
{
    await File.WriteAllTextAsync(Path.Combine(options.LogDirectory, "error.json"), JsonSerializer.Serialize(new
    {
        pipeline,
        exceptionType = exception.GetType().FullName,
        exception.Message,
        exception.StackTrace,
        innerException = exception.InnerException?.ToString()
    }, Json.Options));
    Console.Error.WriteLine($"{pipeline}: failed: {exception.Message}");
    Environment.ExitCode = 1;
}
Console.WriteLine($"Request and response logs: {Path.GetFullPath(options.LogDirectory)}");

static GenerationContext ProbeContext()
{
    var eventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var definition = new StoryDefinitionSnapshot("Pipeline probe", "Mara investigates a storm-lashed coastal observatory.",
        "Open with the generator failing during a storm.", StoryBible.Empty,
        new PlannedEvents([new(eventId, "Discover why the beacon is transmitting a warning.", 4, 3, null, 0)]), StoryConditions.Empty, StoryConditions.Empty);
    var turn = new StoryTurn(Guid.NewGuid(), Guid.NewGuid(), 1, null,
        "Rain rattles the observatory windows as the generator sputters in the basement.", ["Inspect the generator", "Check the beacon room"],
        [], [], [], [], [], [], [], [], DateTimeOffset.UtcNow, new GenerationMetadata("pipeline-tester", null, null, null));
    return new GenerationContext(definition, StoryBible.Empty, definition.InitialPlannedEvents,
        new ConditionsContext(StoryConditions.Empty, [], []), new ConditionsContext(StoryConditions.Empty, [], []),
        "Mara is inside the observatory during a storm and the generator is failing.", [turn], "Inspect the generator for the source of the sputtering.", 2);
}

sealed record Configuration(string BaseUrl, string ApiKey, string ModelId, StructuredOutputTier StructuredOutputTier,
    InstructionMessageRole InstructionMessageRole, OutputTokenParameter OutputTokenParameter, int RequestTimeoutSeconds, int? MaxOutputTokens)
{
    public static async Task<Configuration> LoadAsync(string path) =>
        JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(path), Json.Options)
        ?? throw new InvalidDataException("The configuration file is empty.");
}

sealed record Arguments(string ConfigPath, string Pipeline, string LogDirectory)
{
    public static Arguments Parse(string[] values)
    {
        string Get(string name, string fallback) { var i = Array.IndexOf(values, name); return i >= 0 && i + 1 < values.Length ? values[i + 1] : fallback; }
        return new(Get("--config", "pipeline-tester.local.json"), Get("--pipeline", "fourCalls"), Get("--log-dir", "logs"));
    }
}

sealed class WireLogHandler(string directory) : DelegatingHandler(new HttpClientHandler())
{
    private int _sequence;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence).ToString("D3");
        var requestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{sequence}-request.json"), requestBody, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        var responseBody = response.Content is null ? "" : await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{sequence}-response.json"), responseBody, cancellationToken);
        response.Content = new StringContent(responseBody, Encoding.UTF8, response.Content?.Headers.ContentType?.MediaType ?? "application/json");
        return response;
    }
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
