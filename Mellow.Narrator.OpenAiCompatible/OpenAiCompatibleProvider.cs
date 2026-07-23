using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.OpenAiCompatible;

public sealed class OpenAiCompatibleProvider(HttpClient httpClient, TimeProvider timeProvider) : ILanguageModelProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
    {
        try
        {
            RequireConnection(settings);
            IReadOnlyList<string> models = [];
            var discovery = false;
            try
            {
                var root = await SendAsync(settings, credential, () => CreateRequest(HttpMethod.Get, settings, "models", credential), cancellationToken);
                models = root["data"]?.AsArray().Select(x => x?["id"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Order().ToArray() ?? [];
                discovery = true;
            }
            catch (ProviderException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) { }

            var tier = StructuredOutputTier.Unsupported;
            foreach (var candidate in new[] { StructuredOutputTier.StrictJsonSchema, StructuredOutputTier.JsonMode, StructuredOutputTier.PromptedJson })
            {
                try
                {
                    var response = await CompleteAsync(settings, credential,
                        [Message("system", "Return a JSON object with exactly one boolean property named ok."), Message("user", "Return ok as true.")],
                        SimpleProbeSchema(), candidate, cancellationToken);
                    if (response["ok"]?.GetValue<bool>() == true) { tier = candidate; break; }
                }
                catch (Exception ex) when (ex is ProviderException or JsonException) { }
            }
            var capabilities = new ConnectionCapabilities(discovery, tier, settings.ModelId, timeProvider.GetUtcNow());
            return tier == StructuredOutputTier.Unsupported
                ? new(false, models, capabilities, "The model could not produce a valid structured response.")
                : new(true, models, capabilities, null);
        }
        catch (Exception ex) when (ex is ProviderException or JsonException or HttpRequestException or TaskCanceledException)
        {
            return new(false, [], new(false, StructuredOutputTier.Unsupported, settings.ModelId, timeProvider.GetUtcNow()), SafeMessage(ex));
        }
    }

    public async Task<PlayerAnswerValidationResponse> ValidatePlayerAnswerAsync(
        ApiConnectionSettings settings, string? credential, PlayerQuestion question, string answer,
        IReadOnlyList<PlayerResponse> previousAnswers, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            question = question.Question,
            validationInstruction = question.ValidationInstruction,
            answer,
            previousAnswers = previousAnswers.Select(x => new { x.Question, x.Answer })
        };
        var messages = new[]
        {
            Message("system", "You validate one interactive-story setup answer. Apply the validation instruction in context. Return JSON only. A failed rule is advisory: set hasWarning true and explain concisely."),
            Message("user", JsonSerializer.Serialize(payload, Json))
        };
        var node = await CompleteWithCorrectionAsync(settings, credential, messages, ValidationSchema(), cancellationToken);
        return node.Deserialize<PlayerAnswerValidationResponse>(Json) ?? throw new JsonException("Empty validation response.");
    }

    public async Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(
        ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new[]
        {
            Message("system", "Create the initial Story Bible for an interactive story. Include every durable fact required to narrate consistently. Keep entries concise, avoid duplicates, and assign importance 1 through 5. Return JSON only."),
            Message("user", storyPrompt)
        };
        var node = await CompleteWithCorrectionAsync(settings, credential, messages, DefinitionSchema(settings), cancellationToken);
        return node.Deserialize<StoryDefinitionGenerationResponse>(Json) ?? throw new JsonException("Empty Story Definition response.");
    }

    public Task<StoryGenerationResponse> GenerateOpeningAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default) =>
        GenerateStoryAsync(settings, credential, context, true, cancellationToken);

    public Task<StoryGenerationResponse> GenerateTurnAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, CancellationToken cancellationToken = default) =>
        GenerateStoryAsync(settings, credential, context, false, cancellationToken);

    private async Task<StoryGenerationResponse> GenerateStoryAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        var messages = BuildStoryMessages(context, opening);
        var node = await CompleteWithCorrectionAsync(settings, credential, messages, TurnSchema(settings), cancellationToken);
        var dto = node.Deserialize<StoryResponseDto>(Json) ?? throw new JsonException("Empty story response.");
        if (dto.Narration.Length > settings.ContentLimits.MaxNarrationCharacters) throw new JsonException("Narration is too long.");
        if (dto.SuggestedActions.Count > settings.ContentLimits.MaxSuggestedActions ||
            dto.SuggestedActions.Any(x => x.Length > settings.ContentLimits.MaxSuggestedActionCharacters))
            throw new JsonException("Suggested actions exceed configured limits.");
        if (dto.StoryBibleUpdates.Count > settings.ContentLimits.MaxStoryBibleUpdatesPerResponse)
            throw new JsonException("Too many Story Bible updates.");
        var meta = node["_transport"] as JsonObject;
        node.Remove("_transport");
        return new(dto.Narration, dto.SuggestedActions, dto.RelevantStoryBibleEntryIds, dto.StoryBibleUpdates,
            meta?["responseId"]?.GetValue<string>(), meta?["inputTokens"]?.GetValue<int?>(), meta?["outputTokens"]?.GetValue<int?>());
    }

    private async Task<JsonObject> CompleteWithCorrectionAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> messages, JsonObject schema,
        CancellationToken cancellationToken)
    {
        var tier = settings.Capabilities.StructuredOutputTier;
        if (tier is StructuredOutputTier.Untested or StructuredOutputTier.Unsupported) tier = StructuredOutputTier.PromptedJson;
        try { return await CompleteAsync(settings, credential, messages, schema, tier, cancellationToken); }
        catch (JsonException ex)
        {
            var corrected = messages.Concat([
                Message("system", $"Your previous response failed validation: {ex.Message}. Return a corrected JSON object only.")
            ]).ToArray();
            return await CompleteAsync(settings, credential, corrected, schema, tier, cancellationToken);
        }
    }

    private async Task<JsonObject> CompleteAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> messages, JsonObject schema,
        StructuredOutputTier tier, CancellationToken cancellationToken)
    {
        RequireConnection(settings);
        var body = new JsonObject
        {
            ["model"] = settings.ModelId,
            ["messages"] = new JsonArray(messages.Select(x => (JsonNode)x).ToArray()),
            ["max_tokens"] = settings.MaxOutputTokens,
            ["stream"] = false
        };
        if (settings.Parameters.Temperature is { } temperature) body["temperature"] = temperature;
        if (settings.Parameters.TopP is { } topP) body["top_p"] = topP;
        if (!string.IsNullOrWhiteSpace(settings.Parameters.ReasoningEffort)) body["reasoning_effort"] = settings.Parameters.ReasoningEffort;
        if (tier == StructuredOutputTier.StrictJsonSchema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "mellow_narrator_response", ["strict"] = true, ["schema"] = schema.DeepClone() }
            };
        else if (tier == StructuredOutputTier.JsonMode)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };

        var envelope = await SendAsync(settings, credential, () =>
        {
            var request = CreateRequest(HttpMethod.Post, settings, "chat/completions", credential);
            request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken);
        var choice = envelope["choices"]?[0] as JsonObject ?? throw new JsonException("The provider returned no choices.");
        var refusal = choice["message"]?["refusal"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(refusal)) throw new ProviderException("The model refused the request.", null);
        var content = choice["message"]?["content"]?.GetValue<string>() ?? throw new JsonException("The provider returned no message content.");
        var result = JsonNode.Parse(content) as JsonObject ?? throw new JsonException("The model response is not a JSON object.");
        result["_transport"] = new JsonObject
        {
            ["responseId"] = envelope["id"]?.GetValue<string>(),
            ["inputTokens"] = envelope["usage"]?["prompt_tokens"]?.GetValue<int?>(),
            ["outputTokens"] = envelope["usage"]?["completion_tokens"]?.GetValue<int?>()
        };
        return result;
    }

    private async Task<JsonObject> SendAsync(
        ApiConnectionSettings settings, string? credential, Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= settings.Retry.MaxAutomaticRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(settings.RequestTimeout);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    if (retryable && attempt < settings.Retry.MaxAutomaticRetries)
                    {
                        var delay = RetryDelay(settings, response, attempt);
                        if (delay is null) throw await ErrorAsync(response, settings.ContentLimits.MaxResponseBodyBytes, attemptCts.Token);
                        await Task.Delay(delay.Value, timeProvider, cancellationToken);
                        continue;
                    }
                    throw await ErrorAsync(response, settings.ContentLimits.MaxResponseBodyBytes, attemptCts.Token);
                }
                var bytes = await ReadLimitedAsync(response.Content, settings.ContentLimits.MaxResponseBodyBytes, attemptCts.Token);
                return JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Provider response is not a JSON object.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                if (attempt >= settings.Retry.MaxAutomaticRetries) break;
                await Task.Delay(Backoff(settings, attempt), timeProvider, cancellationToken);
            }
        }
        throw new ProviderException(last is TaskCanceledException ? "The provider request timed out." : "The provider request failed.", null, last);
    }

    private static TimeSpan? RetryDelay(ApiConnectionSettings settings, HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is not null) return retryAfter <= settings.Retry.MaxRetryAfter ? retryAfter : null;
        return Backoff(settings, attempt);
    }

    private static TimeSpan Backoff(ApiConnectionSettings settings, int attempt)
    {
        var raw = Math.Min(settings.Retry.MaxDelay.TotalMilliseconds,
            settings.Retry.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(raw * (1 + Random.Shared.NextDouble() * .2));
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new ProviderException("The provider response exceeded the configured size limit.", null);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<ProviderException> ErrorAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        string detail;
        try
        {
            var bytes = await ReadLimitedAsync(response.Content, maxBytes, cancellationToken);
            var json = JsonNode.Parse(bytes);
            detail = json?["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Provider error";
        }
        catch { detail = response.ReasonPhrase ?? "Provider error"; }
        return new ProviderException($"{(int)response.StatusCode} {detail}", response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, ApiConnectionSettings settings, string relative, string? credential)
    {
        var baseUrl = settings.BaseUrl!.ToString().TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relative));
        if (!string.IsNullOrWhiteSpace(credential)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static IReadOnlyList<JsonObject> BuildStoryMessages(GenerationContext context, bool opening)
    {
        var system = """
            You narrate an interactive story. Return JSON only. The Story Bible is authoritative and complete.
            Narrate the immediate scene, offer concise suggested actions, flag every existing Bible entry relevant now,
            and return only incremental Story Bible updates. Preserve durable facts, replace rather than duplicate,
            remove obsolete facts, and assign importance 1 through 5.
            """;
        var messages = new List<JsonObject> { Message("system", system) };
        messages.Add(Message("user", JsonSerializer.Serialize(new
        {
            storyPrompt = context.Definition.StoryPrompt,
            playerResponses = context.PlayerResponses.Select(x => new { x.Question, x.Answer }),
            storyBible = context.StoryBible.Entries
        }, Json)));
        foreach (var turn in context.RecentTurns)
        {
            if (turn.PlayerAction is not null) messages.Add(Message("user", turn.PlayerAction));
            messages.Add(Message("assistant", turn.Narration));
        }
        messages.Add(Message("user", opening ? "Create the opening scene." : context.PlayerAction ?? "Continue the story."));
        return messages;
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static JsonObject SimpleProbeSchema() => ObjectSchema(new Dictionary<string, JsonNode?> { ["ok"] = new JsonObject { ["type"] = "boolean" } }, ["ok"]);

    private static JsonObject ValidationSchema() => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["hasWarning"] = new JsonObject { ["type"] = "boolean" },
        ["warning"] = new JsonObject { ["type"] = new JsonArray("string", "null") }
    }, ["hasWarning", "warning"]);

    private static JsonObject DefinitionSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["initialStoryBibleEntries"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = settings.StoryGeneration.MaxStoryBibleEntries,
            ["items"] = ProposedEntrySchema(settings)
        }
    }, ["initialStoryBibleEntries"]);

    private static JsonObject TurnSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["narration"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxNarrationCharacters },
        ["suggestedActions"] = new JsonObject { ["type"] = "array", ["maxItems"] = settings.ContentLimits.MaxSuggestedActions, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxSuggestedActionCharacters } },
        ["relevantStoryBibleEntryIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["storyBibleUpdates"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = settings.ContentLimits.MaxStoryBibleUpdatesPerResponse,
            ["items"] = ObjectSchema(new Dictionary<string, JsonNode?>
            {
                ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("add", "replace", "remove") },
                ["entryId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "uuid" },
                ["entry"] = new JsonObject { ["anyOf"] = new JsonArray(ProposedEntrySchema(settings), new JsonObject { ["type"] = "null" }) }
            }, ["operation", "entryId", "entry"])
        }
    }, ["narration", "suggestedActions", "relevantStoryBibleEntryIds", "storyBibleUpdates"]);

    private static JsonObject ProposedEntrySchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["category"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryBibleCategoryCharacters },
        ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryBibleNameCharacters },
        ["content"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.StoryGeneration.MaxStoryBibleEntryCharacters },
        ["importance"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5 }
    }, ["category", "name", "content", "importance"]);

    private static JsonObject ObjectSchema(IReadOnlyDictionary<string, JsonNode?> properties, IEnumerable<string> required) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject(properties),
        ["required"] = new JsonArray(required.Select(x => (JsonNode)x).ToArray())
    };

    private static void RequireConnection(ApiConnectionSettings settings)
    {
        if (settings.BaseUrl is null || string.IsNullOrWhiteSpace(settings.ModelId))
            throw new ProviderException("Base URL and model ID are required.", null);
    }

    private static string SafeMessage(Exception ex) => ex is ProviderException ? ex.Message : "The provider response could not be processed.";

    private sealed record StoryResponseDto(
        string Narration,
        IReadOnlyList<string> SuggestedActions,
        IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
        IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates);
}

public sealed class ProviderException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
