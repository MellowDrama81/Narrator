using System.Net;
using System.Text;
using System.Text.Json;
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
            """{"refinedStoryPrompt":"PRIVATE RESPONSE TOP-SECRET-KEY","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"private","name":"Fact","knownFacts":["PRIVATE RESPONSE TOP-SECRET-KEY"],"secretFacts":[],"importance":4}]}""")));
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
    public async Task DiscoverModels_BaseUrlWithoutTrailingSlashStillAppendsPathCorrectly()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
            });
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings() with { BaseUrl = new("https://example.test/v1"), ModelId = null };

        await provider.DiscoverModelsAsync(settings, "secret");

        Assert.Equal("https://example.test/v1/models", captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task DiscoverModels_BaseUrlWithQueryStringIsPreserved()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
            });
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings() with { BaseUrl = new("https://example.test/v1?api-version=2024"), ModelId = null };

        await provider.DiscoverModelsAsync(settings, "secret");

        Assert.Equal("https://example.test/v1/models?api-version=2024", captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task DiscoverModels_NonArrayDataThrowsJsonException()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"id":"model-a"}}""", Encoding.UTF8, "application/json")
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        await Assert.ThrowsAsync<JsonException>(() => provider.DiscoverModelsAsync(Settings() with { ModelId = null }, "secret"));
    }

    [Fact]
    public async Task DiscoverModels_NonStringIdThrowsJsonException()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":42}]}""", Encoding.UTF8, "application/json")
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        await Assert.ThrowsAsync<JsonException>(() => provider.DiscoverModelsAsync(Settings() with { ModelId = null }, "secret"));
    }

    [Fact]
    public async Task DiscoverModels_SkipsArrayElementsWithoutAnId()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":["not-an-object",{"id":"model-a"}]}""", Encoding.UTF8, "application/json")
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var models = await provider.DiscoverModelsAsync(Settings() with { ModelId = null }, "secret");

        Assert.Equal(["model-a"], models);
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
            var content = """{"refinedStoryPrompt":"A red moon story.","suggestedTitle":"Red Moon","initialEventsPrompt":"The moon glows ominously overhead.","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["The moon is red."],"secretFacts":[],"importance":4}]}""";
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
            Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow)
        };

        var result = await provider.GenerateStoryDefinitionAsync(settings, "secret", "A red moon story");

        Assert.Single(result.InitialStoryBibleEntries);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
        Assert.Contains("story-model", body);
        Assert.Contains("A red moon story", body);
        Assert.Contains("Refine the Story Prompt", body);
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
                ? """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":9}]}"""
                : """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":4}]}""";
            return Response(content);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings();

        var result = await provider.GenerateStoryDefinitionAsync(settings, null, "Story");

        Assert.Equal(2, requests);
        Assert.Equal(4, Assert.Single(result.InitialStoryBibleEntries).Importance);
        Assert.Contains("failed validation", bodies[1]);
        Assert.Contains("importance", bodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromptedJsonTier_ToleratesExtraPropertyWithoutRetrying()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var content = """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"note":"unexpected extra field"}""";
            return Task.FromResult(Response(content));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var result = await provider.GenerateStoryDefinitionAsync(Settings(), null, "Story");

        Assert.Equal(1, requests);
        Assert.Equal("Story", result.RefinedStoryPrompt);
    }

    [Fact]
    public async Task PromptedJsonRequest_IncludesTheSchema()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[]}""");
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var settings = Settings();

        await provider.GenerateStoryDefinitionAsync(settings, null, "Story prompt");

        Assert.Contains("Refine the Story Prompt", body);
        Assert.Contains("Return an object matching this JSON Schema exactly", body);
        Assert.Contains("refinedStoryPrompt", body);
    }

    [Fact]
    public async Task GenerateTurn_RemovesBadRelevantEntryIdsWithoutRetry()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", ["Content"], [], 3, 0);
        var unknown = Guid.NewGuid();
        var requests = 0;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            requests++;
            body = await request.Content!.ReadAsStringAsync();
            return Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":["{{{entry.Id}}}","{{{entry.Id}}}","{{{unknown}}}","not-a-uuid",42,null],"storyBibleUpdates":[]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([entry])),
            new([entry]),
            [],
            "Continue",
            1);

        var settings = Settings();
        var result = await provider.GenerateTurnAsync(settings, null, context);

        Assert.Equal(1, requests);
        Assert.Equal(entry.Id, Assert.Single(result.RelevantStoryBibleEntryIds));
        Assert.Contains("You narrate an interactive story", body);
        Assert.Contains("use only IDs copied exactly from the current Story", body);
        Assert.Contains("currentPlayerAction", body);
        Assert.Contains("Continue", body);
        Assert.Contains("turnNumber", body);
    }

    [Fact]
    public async Task GenerateTurn_SubstitutesConfiguredSuggestedActionCountIntoPromptAndSchema()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B","C","D"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            "Continue",
            1);
        var settings = Settings() with
        {
            ContentLimits = Settings().ContentLimits with { MinSuggestedActions = 4, MaxSuggestedActions = 7 }
        };

        await provider.GenerateTurnAsync(settings, null, context);

        Assert.Contains("Offer between 4 and 7 concise suggested actions", body);
        Assert.DoesNotContain(PromptTemplateDefaults.MinSuggestedActionsPlaceholder, body);
        Assert.DoesNotContain(PromptTemplateDefaults.MaxSuggestedActionsPlaceholder, body);
        Assert.Contains("minItems\\u0022:4", body);
        Assert.Contains("maxItems\\u0022:7", body);
    }

    [Fact]
    public async Task GenerateTurn_IncludesInitialEventsPromptWhileRecentTurnsWindowIsNotFull()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":2,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", new([])) { InitialEventsPrompt = "The village is under curfew." };
        var recent = new StoryTurn(Guid.NewGuid(), Guid.NewGuid(), 1, "Wait", "Nothing happens.", [], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));
        var context = new GenerationContext(definition, new([]), [recent], "Continue", 2);
        var settings = Settings() with { StoryGeneration = Settings().StoryGeneration with { RecentTurnCount = 3 } };

        await provider.GenerateTurnAsync(settings, null, context);

        Assert.Contains("The village is under curfew.", body);
    }

    [Fact]
    public async Task GenerateTurn_ExcludesInitialEventsPromptOnceRecentTurnsWindowIsFull()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":2,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", new([])) { InitialEventsPrompt = "The village is under curfew." };
        var recent = new StoryTurn(Guid.NewGuid(), Guid.NewGuid(), 1, "Wait", "Nothing happens.", [], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));
        var context = new GenerationContext(definition, new([]), [recent], "Continue", 2);
        var settings = Settings() with { StoryGeneration = Settings().StoryGeneration with { RecentTurnCount = 1 } };

        await provider.GenerateTurnAsync(settings, null, context);

        Assert.DoesNotContain("The village is under curfew.", body);
    }

    [Fact]
    public async Task GenerateTurn_DoesNotRejectOrRetryWhenSuggestedActionCountIsOutOfRange()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response("""
                {"turnNumber":2,"acknowledgedPlayerAction":"Current action","narration":"A new scene unfolds.","suggestedActions":["Only one"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            "Current action",
            2);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(1, requests);
        Assert.Equal(["Only one"], result.SuggestedActions);
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
                {"turnNumber":2,"acknowledgedPlayerAction":"{{acknowledged}}","narration":"A new scene unfolds.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            "Current action",
            2);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(2, requests);
        Assert.Equal("A new scene unfolds.", result.Narration);
    }

    [Fact]
    public async Task GenerateTurn_AcceptsAcknowledgedActionWithBenignFormattingDifferences()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response("""
                {"turnNumber":2,"acknowledgedPlayerAction":"  Current   action.  ","narration":"A new scene unfolds.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            "Current action",
            2);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(1, requests);
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
                {"turnNumber":2,"acknowledgedPlayerAction":"Choose another path","narration":"{{narration}}","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
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
            new("Story", "Prompt", new([])),
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
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[]}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            null,
            0);

        var result = await provider.GenerateOpeningAsync(Settings(), null, context);

        Assert.Equal("The story begins.", result.Narration);
    }

    [Fact]
    public async Task GenerateTurn_IgnoresModelCreatedIdForAddUpdate()
    {
        var modelCreatedId = Guid.NewGuid();
        var requests = 0;
        string? requestBody = null;
        var handler = new StubHandler(async request =>
        {
            requests++;
            requestBody = await request.Content!.ReadAsStringAsync();
            return Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"A new place appears.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[{"operation":"add","entryId":"{{{modelCreatedId}}}","entry":{"category":"location","name":"New Place","knownFacts":["A newly discovered place."],"secretFacts":[],"importance":3}}]}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", new([])),
            new([]),
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(1, requests);
        Assert.Null(Assert.Single(result.StoryBibleUpdates).EntryId);
        Assert.Contains("never invent one", requestBody);
    }

    [Fact]
    public async Task RequestTimeoutStatus_IsRetried()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(requests == 1
                ? new HttpResponseMessage(HttpStatusCode.RequestTimeout)
                : Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[]}"""));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var settings = Settings() with { Retry = Settings().Retry with { InitialDelay = TimeSpan.FromMilliseconds(1) } };

        var result = await provider.GenerateStoryDefinitionAsync(settings, null, "Story");

        Assert.Equal(2, requests);
        Assert.Equal("Story", result.RefinedStoryPrompt);
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
            return Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":4}]}""");
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
        // Deterministic instead of a wall-clock race: the stream signals readStarted the moment its
        // ReadAsync is entered, so cancellation is triggered exactly once the read is actually in
        // flight rather than after some fixed delay that could fire too early or too late under load.
        var readStarted = new TaskCompletionSource();
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(new CancellationOnlyStream(readStarted))
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();

        var generateTask = provider.GenerateStoryDefinitionAsync(Settings(), null, "Story", cancellation.Token);
        await readStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generateTask);
    }

    [Fact]
    public async Task EmptyChoicesArray_ThrowsJsonExceptionInsteadOfCrashing()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json")
        }));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            provider.GenerateStoryDefinitionAsync(Settings(), null, "Story"));
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

    private sealed class CancellationOnlyStream(TaskCompletionSource readStarted) : Stream
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
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
