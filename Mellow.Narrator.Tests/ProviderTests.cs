using System.Net;
using System.Text;
using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;

namespace Mellow.Narrator.Tests;

public sealed class ProviderTests
{
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
            Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow)
        };

        var result = await provider.GenerateStoryDefinitionAsync(settings, "secret", "A red moon story");

        Assert.Single(result.InitialStoryBibleEntries);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
        Assert.Contains("story-model", body);
        Assert.Contains("A red moon story", body);
        Assert.Contains("\"max_completion_tokens\"", body);
        Assert.DoesNotContain("\"max_tokens\"", body);
        Assert.Contains("\"role\":\"developer\"", body);
    }

    [Fact]
    public async Task GenerateDefinition_RetriesOnceAfterLocalValidationFailure()
    {
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            var content = requests == 1
                ? """{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"Red","importance":9}]}"""
                : """{"initialStoryBibleEntries":[{"category":"world","name":"Moon","content":"Red","importance":4}]}""";
            return Task.FromResult(Response(content));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var result = await provider.GenerateStoryDefinitionAsync(Settings(), null, "Story");

        Assert.Equal(2, requests);
        Assert.Equal(4, Assert.Single(result.InitialStoryBibleEntries).Importance);
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

        await provider.ValidatePlayerAnswerAsync(
            Settings(),
            null,
            new(Guid.NewGuid(), "Name?", "Required", 0),
            "Alex",
            []);

        Assert.Contains("JSON Schema", body);
        Assert.Contains("hasWarning", body);
    }

    [Fact]
    public async Task GenerateTurn_RetriesUnknownBibleReferenceThenSucceeds()
    {
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Fact", "Content", 3, 0);
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            var relevant = requests == 1 ? Guid.NewGuid() : entry.Id;
            return Task.FromResult(Response($$"""
                {"narration":"Scene","suggestedActions":["Continue"],"relevantStoryBibleEntryIds":["{{relevant}}"],"storyBibleUpdates":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", [], new([entry])),
            [],
            new([entry]),
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(2, requests);
        Assert.Equal(entry.Id, Assert.Single(result.RelevantStoryBibleEntryIds));
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
