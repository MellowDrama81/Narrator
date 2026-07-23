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

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
