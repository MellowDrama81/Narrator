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
}
