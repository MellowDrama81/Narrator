using System.Net;
using System.Text;
using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;
using Microsoft.Extensions.Logging;

namespace Mellow.Narrator.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task TraceLogging_IncludesBodiesButNeverCredential()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response(
            """{"initialStoryBibleEntries":[{"category":"private","name":"Fact","content":"PRIVATE RESPONSE TOP-SECRET-KEY","importance":4}]}""")));
        var informationLogger = new CaptureLogger<OpenAiCompatibleProvider>();
        var informationProvider = new OpenAiCompatibleProvider(
            new HttpClient(handler),
            TimeProvider.System,
            informationLogger);

        await informationProvider.GenerateStoryDefinitionAsync(
            Settings(),
            "TOP-SECRET-KEY",
            "PRIVATE REQUEST");

        var informationLog = string.Join(Environment.NewLine, informationLogger.Messages);
        Assert.DoesNotContain("PRIVATE REQUEST", informationLog);
        Assert.DoesNotContain("PRIVATE RESPONSE", informationLog);
        Assert.DoesNotContain("TOP-SECRET-KEY", informationLog);

        var traceLogger = new CaptureLogger<OpenAiCompatibleProvider>();
        var traceProvider = new OpenAiCompatibleProvider(
            new HttpClient(handler),
            TimeProvider.System,
            traceLogger);
        await traceProvider.GenerateStoryDefinitionAsync(
            Settings() with { Logging = new(NarratorLogLevel.Trace) },
            "TOP-SECRET-KEY",
            "PRIVATE REQUEST");

        var traceLog = string.Join(Environment.NewLine, traceLogger.Messages);
        Assert.Contains("PRIVATE REQUEST", traceLog);
        Assert.Contains("PRIVATE RESPONSE", traceLog);
        Assert.Contains("[REDACTED CREDENTIAL]", traceLog);
        Assert.DoesNotContain("TOP-SECRET-KEY", traceLog);
    }

    [Fact]
    public async Task DiscoverModels_DoesNotRequireModelAndReturnsSortedUniqueIds()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"model-b"},{"id":"model-a"},{"id":"model-b"}]}""",
                    Encoding.UTF8,
                    "application/json")
            });
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings() with { ModelId = null };

        var models = await provider.DiscoverModelsAsync(settings, "secret");

        Assert.Equal(["model-a", "model-b"], models);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("https://example.test/v1/models", captured.RequestUri!.ToString());
        Assert.Equal("secret", captured.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GenerateDefinition_SendsBearerModelAndPrompt()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            var content = """{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"The moon is red.","importance":4}]}""";
            var envelope = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "response-1",
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = 10, completion_tokens = 20 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = NarratorDefaults.Create() with
        {
            BaseUrl = new("https://example.test/v1/"),
            ModelId = "story-model",
            Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow),
            PromptTemplates = PromptTemplateDefaults.Create() with
            {
                StoryDefinitionInstruction = "CUSTOM DEFINITION INSTRUCTION"
            }
        };

        var result = await provider.GenerateStoryDefinitionAsync(settings, "secret", "A red moon story");

        Assert.Single(result.InitialStoryBibleEntries);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
        Assert.Contains("story-model", body);
        Assert.Contains("A red moon story", body);
        Assert.Contains("CUSTOM DEFINITION INSTRUCTION", body);
        Assert.Contains("\"max_completion_tokens\"", body);
        Assert.DoesNotContain("\"max_tokens\"", body);
        Assert.Contains("\"role\":\"developer\"", body);
    }

    [Fact]
    public async Task GenerateDefinition_RetriesOnceAfterLocalValidationFailure()
    {
        var requests = 0;
        var bodies = new List<string>();
        var handler = new StubHandler(async request =>
        {
            requests++;
            bodies.Add(await request.Content!.ReadAsStringAsync());
            var content = requests == 1
                ? """{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"Red","importance":9}]}"""
                : """{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"Red","importance":4}]}""";
            return Response(content);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings() with
        {
            PromptTemplates = PromptTemplateDefaults.Create() with
            {
                CorrectiveRetryInstruction = $"CUSTOM CORRECTION {PromptTemplateDefaults.ValidationErrorPlaceholder}"
            }
        };

        var result = await provider.GenerateStoryDefinitionAsync(settings, null, "Story");

        Assert.Equal(2, requests);
        Assert.Equal(4, Assert.Single(result.InitialStoryBibleEntries).Importance);
        Assert.Contains("CUSTOM CORRECTION", bodies[1]);
        Assert.Contains("importance", bodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromptedJsonRequest_IncludesTheSchema()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""{"hasWarning":false,"warning":null}""");
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var settings = Settings() with
        {
            PromptTemplates = PromptTemplateDefaults.Create() with
            {
                PlayerAnswerValidationInstruction = "CUSTOM VALIDATION INSTRUCTION",
                PromptedJsonInstruction = $"CUSTOM SCHEMA {PromptTemplateDefaults.SchemaPlaceholder}"
            }
        };

        await provider.ValidatePlayerAnswerAsync(
            settings,
            null,
            new(Guid.NewGuid(), "Name?", "Required", 0),
            "Alex",
            []);

        Assert.Contains("CUSTOM VALIDATION INSTRUCTION", body);
        Assert.Contains("CUSTOM SCHEMA", body);
        Assert.Contains("hasWarning", body);
    }

    [Fact]
    public async Task GenerateTurn_RetriesUnknownBibleReferenceThenSucceeds()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", "Content", 3, 0);
        var requests = 0;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            requests++;
            body = await request.Content!.ReadAsStringAsync();
            var relevant = requests == 1 ? Guid.NewGuid() : entry.Id;
            return Response($$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue"],"relevantStoryBibleEntryIds":["{{relevant}}"],"storyBibleUpdates":[]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", [], new([entry])),
            [],
            new([entry]),
            [],
            "Continue",
            1);

        var settings = Settings() with
        {
            PromptTemplates = PromptTemplateDefaults.Create() with
            {
                StoryNarrationInstruction = "CUSTOM NARRATION INSTRUCTION"
            }
        };
        var result = await provider.GenerateTurnAsync(settings, null, context);

        Assert.Equal(2, requests);
        Assert.Equal(entry.Id, Assert.Single(result.RelevantStoryBibleEntryIds));
        Assert.Contains("CUSTOM NARRATION INSTRUCTION", body);
        Assert.Contains("currentPlayerAction", body);
        Assert.Contains("Continue", body);
        Assert.Contains("turnNumber", body);
    }

    [Fact]
    public async Task GenerateTurn_RetriesResponseForPreviousAction()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var acknowledged = requests == 1 ? "Previous action" : "Current action";
            return Task.FromResult(Response($$"""
                {"turnNumber":2,"acknowledgedPlayerAction":"{{acknowledged}}","narration":"A new scene unfolds.","suggestedActions":[],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", [], new([])),
            [],
            new([]),
            [],
            "Current action",
            2);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(2, requests);
        Assert.Equal("A new scene unfolds.", result.Narration);
    }

    [Fact]
    public async Task GenerateTurn_RetriesDuplicatedRecentNarration()
    {
        const string previousNarration =
            "The silent corridor stretches ahead beneath cold emergency lights while dust drifts through the air. " +
            "A sealed door waits at the far end beside a damaged console and a motionless security camera. " +
            "Nothing else moves as you listen for danger, study the scattered footprints, and consider how to cross " +
            "the exposed floor without alerting whoever controls the facility.";
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var narration = requests == 1
                ? previousNarration.Replace("silent corridor", "quiet corridor", StringComparison.Ordinal)
                : "Your new decision changes the situation and opens an entirely different path.";
            return Task.FromResult(Response($$"""
                {"turnNumber":2,"acknowledgedPlayerAction":"Choose another path","narration":"{{narration}}","suggestedActions":[],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var stateId = Guid.NewGuid();
        var recent = new StoryTurn(
            Guid.NewGuid(),
            stateId,
            1,
            "Wait",
            previousNarration,
            [],
            [],
            [],
            DateTimeOffset.UtcNow,
            new("model", null, null, null));
        var context = new GenerationContext(
            new("Story", "Prompt", [], new([])),
            [],
            new([]),
            [recent],
            "Choose another path",
            2);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(2, requests);
        Assert.NotEqual(previousNarration, result.Narration);
    }

    [Fact]
    public async Task GenerateOpening_RequiresTurnZeroAndNullAcknowledgedAction()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response("""
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":[],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", [], new([])),
            [],
            new([]),
            [],
            null,
            0);

        var result = await provider.GenerateOpeningAsync(Settings(), null, context);

        Assert.Equal("The story begins.", result.Narration);
    }

    [Fact]
    public async Task ConnectionTest_NegotiatesLegacyTokenFieldAndSystemRole()
    {
        var acceptedProbe = false;
        string? generatedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.NotFound);
            var body = await request.Content!.ReadAsStringAsync();
            var legacy = body.Contains("\"max_tokens\"", StringComparison.Ordinal) &&
                         body.Contains("\"role\":\"system\"", StringComparison.Ordinal);
            if (!legacy)
            {
                return new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"Unsupported request contract"}}""")
                };
            }
            if (!acceptedProbe)
            {
                acceptedProbe = true;
                return Response("""{"ok":true}""");
            }
            generatedBody = body;
            return Response("""{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"Red","importance":4}]}""");
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings();

        var connection = await provider.TestConnectionAsync(settings, null);
        await provider.GenerateStoryDefinitionAsync(
            settings with { Capabilities = connection.Capabilities },
            null,
            "Story");

        Assert.True(connection.Success);
        Assert.Equal(OutputTokenParameter.MaxTokens, connection.Capabilities.OutputTokenParameter);
        Assert.Equal(InstructionMessageRole.System, connection.Capabilities.InstructionMessageRole);
        Assert.Contains("\"max_tokens\"", generatedBody);
        Assert.DoesNotContain("\"max_completion_tokens\"", generatedBody);
        Assert.Contains("\"role\":\"system\"", generatedBody);
    }

    [Fact]
    public async Task CancellationWhileReadingErrorBody_RemainsCancellation()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(new CancellationOnlyStream())
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ValidatePlayerAnswerAsync(
                Settings(),
                null,
                new(Guid.NewGuid(), "Name?", "Required", 0),
                "Alex",
                [],
                cancellation.Token));
    }

    private static ApiConnectionSettings Settings() => NarratorDefaults.Create() with
    {
        BaseUrl = new("https://example.test/v1/"),
        ModelId = "story-model",
        Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow)
    };

    private static HttpResponseMessage Response(string content)
    {
        var envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "response",
            choices = new[] { new { message = new { content } } }
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CancellationOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
