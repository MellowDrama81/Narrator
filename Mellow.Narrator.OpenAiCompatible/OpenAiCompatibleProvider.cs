using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mellow.Narrator.OpenAiCompatible;

public sealed class OpenAiCompatibleProvider(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<OpenAiCompatibleProvider>? logger = null) : ILanguageModelProvider
{
    private readonly ILogger<OpenAiCompatibleProvider> _logger =
        logger ?? NullLogger<OpenAiCompatibleProvider>.Instance;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(
        ApiConnectionSettings settings,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        RequireBaseUrl(settings);
        var root = await SendAsync(
            settings,
            credential,
            () => CreateRequest(HttpMethod.Get, settings, "models", credential),
            cancellationToken);
        return root["data"]?.AsArray()
            .Select(x => x?["id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(ApiConnectionSettings settings, string? credential, CancellationToken cancellationToken = default)
    {
        try
        {
            RequireConnection(settings);
            var tier = StructuredOutputTier.Unsupported;
            ProviderRequestContract? supportedContract = null;
            foreach (var contract in RequestContractCandidates())
            {
                foreach (var candidate in new[] { StructuredOutputTier.StrictJsonSchema, StructuredOutputTier.JsonMode, StructuredOutputTier.PromptedJson })
                {
                    try
                    {
                        var response = await CompleteAsync(settings, credential,
                            [Message("system", "Return a JSON object with exactly one boolean property named ok."), Message("user", "Return ok as true.")],
                            SimpleProbeSchema(), candidate, cancellationToken, contract, false);
                        if (response["ok"]?.GetValue<bool>() == true)
                        {
                            tier = candidate;
                            supportedContract = contract;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is ProviderException or JsonException) { }
                }
                if (supportedContract is not null) break;
            }
            var capabilities = new ConnectionCapabilities(
                settings.Capabilities.SupportsModelDiscovery,
                tier,
                settings.ModelId,
                timeProvider.GetUtcNow())
            {
                OutputTokenParameter = supportedContract?.OutputTokenParameter ?? OutputTokenParameter.MaxCompletionTokens,
                InstructionMessageRole = supportedContract?.InstructionMessageRole ?? InstructionMessageRole.Developer
            };
            return tier == StructuredOutputTier.Unsupported
                ? new(false, [], capabilities, "The model could not produce a valid structured response.")
                : new(true, [], capabilities, null);
        }
        catch (Exception ex) when (ex is ProviderException or JsonException or HttpRequestException or TaskCanceledException)
        {
            return new(false, [], new(false, StructuredOutputTier.Unsupported, settings.ModelId, timeProvider.GetUtcNow()), SafeMessage(ex));
        }
    }

    public async Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(
        ApiConnectionSettings settings, string? credential, string storyPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new[]
        {
            Message("system", settings.PromptTemplates.StoryDefinitionInstruction),
            Message("user", storyPrompt)
        };
        return await CompleteWithCorrectionAsync(
            settings,
            credential,
            messages,
            DefinitionSchema(settings),
            node => ParseDefinitionResponse(node, settings),
            cancellationToken);
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
        var messages = BuildStoryMessages(settings.PromptTemplates, settings.ContentLimits, settings.StoryGeneration.RecentTurnCount, context, opening);
        return await CompleteWithCorrectionAsync(
            settings,
            credential,
            messages,
            TurnSchema(settings),
            node => ParseStoryResponse(node, settings, context, opening),
            cancellationToken);
    }

    private async Task<T> CompleteWithCorrectionAsync<T>(
        ApiConnectionSettings settings,
        string? credential,
        IReadOnlyList<JsonObject> messages,
        JsonObject schema,
        Func<JsonObject, T> parseAndValidate,
        CancellationToken cancellationToken)
    {
        var tier = settings.Capabilities.StructuredOutputTier;
        if (tier is StructuredOutputTier.Untested or StructuredOutputTier.Unsupported) tier = StructuredOutputTier.PromptedJson;
        try
        {
            return parseAndValidate(await CompleteAsync(settings, credential, messages, schema, tier, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or NarratorException)
        {
            var corrected = messages.Concat([
                Message("system", settings.PromptTemplates.CorrectiveRetryInstruction.Replace(
                    PromptTemplateDefaults.ValidationErrorPlaceholder,
                    ex.Message,
                    StringComparison.Ordinal))
            ]).ToArray();
            return parseAndValidate(await CompleteAsync(settings, credential, corrected, schema, tier, cancellationToken));
        }
    }

    private async Task<JsonObject> CompleteAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> messages, JsonObject schema,
        StructuredOutputTier tier,
        CancellationToken cancellationToken,
        ProviderRequestContract? requestContract = null,
        bool useConfiguredPromptTemplates = true)
    {
        RequireConnection(settings);
        var promptedJsonInstruction = useConfiguredPromptTemplates
            ? settings.PromptTemplates.PromptedJsonInstruction
            : $"Return an object matching this JSON Schema exactly: {PromptTemplateDefaults.SchemaPlaceholder}";
        var requestMessages = tier == StructuredOutputTier.PromptedJson
            ? messages.Concat([Message("system", promptedJsonInstruction.Replace(
                PromptTemplateDefaults.SchemaPlaceholder,
                schema.ToJsonString(Json),
                StringComparison.Ordinal))]).ToArray()
            : messages;
        requestContract ??= new(
            settings.Capabilities.OutputTokenParameter,
            settings.Capabilities.InstructionMessageRole);
        var instructionRole = requestContract.InstructionMessageRole == InstructionMessageRole.Developer
            ? "developer"
            : "system";
        var serializedMessages = requestMessages.Select(message =>
        {
            var clone = message.DeepClone().AsObject();
            if (string.Equals(clone["role"]?.GetValue<string>(), "system", StringComparison.Ordinal))
                clone["role"] = instructionRole;
            return clone;
        }).ToArray();
        var body = new JsonObject
        {
            ["model"] = settings.ModelId,
            ["messages"] = new JsonArray(serializedMessages.Select(x => (JsonNode)x).ToArray()),
            ["stream"] = false
        };
        body[requestContract.OutputTokenParameter == OutputTokenParameter.MaxCompletionTokens
            ? "max_completion_tokens"
            : "max_tokens"] = settings.MaxOutputTokens;
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
        var requestId = Guid.NewGuid();
        for (var attempt = 0; attempt <= settings.Retry.MaxAutomaticRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(settings.RequestTimeout);
                var endpoint = SafeEndpoint(request.RequestUri);
                _logger.LogInformation(
                    "LLM HTTP request {RequestId}: {Method} {Endpoint}; attempt {Attempt} of {AttemptCount}.",
                    requestId,
                    request.Method,
                    endpoint,
                    attempt + 1,
                    settings.Retry.MaxAutomaticRetries + 1);
                if (TraceBodiesEnabled(settings) && request.Content is not null)
                {
                    var requestBody = await request.Content.ReadAsStringAsync(attemptCts.Token);
                    _logger.LogTrace(
                        "LLM request body for {RequestId}, {Method} {Endpoint}: {RequestBody}",
                        requestId,
                        request.Method,
                        endpoint,
                        RedactCredential(requestBody, credential));
                }
                var started = timeProvider.GetTimestamp();
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                var bytes = await ReadLimitedAsync(response.Content, settings.ContentLimits.MaxResponseBodyBytes, attemptCts.Token);
                _logger.LogInformation(
                    "LLM HTTP response for {RequestId}: {StatusCode} from {Method} {Endpoint} after {ElapsedMilliseconds:F0} ms.",
                    requestId,
                    (int)response.StatusCode,
                    request.Method,
                    endpoint,
                    timeProvider.GetElapsedTime(started).TotalMilliseconds);
                if (TraceBodiesEnabled(settings))
                {
                    _logger.LogTrace(
                        "LLM response body for {RequestId}, {Method} {Endpoint}: {ResponseBody}",
                        requestId,
                        request.Method,
                        endpoint,
                        RedactCredential(Encoding.UTF8.GetString(bytes), credential));
                }
                if (!response.IsSuccessStatusCode)
                {
                    var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    var error = Error(response, bytes, credential);
                    if (retryable && attempt < settings.Retry.MaxAutomaticRetries)
                    {
                        var delay = RetryDelay(settings, response, attempt);
                        if (delay is null) throw error;
                        _logger.LogWarning(
                            "Retrying LLM HTTP request {RequestId} after status {StatusCode}; delay {DelayMilliseconds:F0} ms.",
                            requestId,
                            (int)response.StatusCode,
                            delay.Value.TotalMilliseconds);
                        await Task.Delay(delay.Value, timeProvider, cancellationToken);
                        continue;
                    }
                    throw error;
                }
                return JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Provider response is not a JSON object.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                _logger.LogWarning(
                    "LLM HTTP request {RequestId}, attempt {Attempt} failed with {ErrorType}.",
                    requestId,
                    attempt + 1,
                    ex.GetType().Name);
                if (attempt >= settings.Retry.MaxAutomaticRetries) break;
                await Task.Delay(Backoff(settings, attempt), timeProvider, cancellationToken);
            }
        }
        _logger.LogError(
            "LLM HTTP request {RequestId} failed after all attempts; final error type: {ErrorType}.",
            requestId,
            last?.GetType().Name ?? "Unknown");
        throw new ProviderException(last is TaskCanceledException ? "The provider request timed out." : "The provider request failed.", null, last);
    }

    private TimeSpan? RetryDelay(ApiConnectionSettings settings, HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            response.Headers.RetryAfter?.Date - timeProvider.GetUtcNow();
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
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

    private static ProviderException Error(HttpResponseMessage response, byte[] bytes, string? credential)
    {
        string detail;
        try
        {
            var json = JsonNode.Parse(bytes);
            detail = json?["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Provider error";
        }
        catch { detail = response.ReasonPhrase ?? "Provider error"; }
        detail = RedactCredential(detail, credential);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"Authentication failed: {detail}",
            HttpStatusCode.NotFound =>
                $"API endpoint was not found: {detail}",
            HttpStatusCode.BadRequest when ContainsAny(detail, "temperature", "top_p", "reasoning_effort") =>
                $"The selected model rejected a configured parameter: {detail}",
            HttpStatusCode.BadRequest when ContainsAny(detail, "context length", "context_length", "maximum context") =>
                $"The provider rejected the request for context length. Reduce the recent-turn count: {detail}",
            HttpStatusCode.BadRequest when ContainsAny(detail, "model", "not found") =>
                $"The selected model is unavailable: {detail}",
            _ => $"{(int)response.StatusCode} {detail}"
        };
        return new ProviderException(message, response.StatusCode);
    }

    private static bool TraceBodiesEnabled(ApiConnectionSettings settings) =>
        settings.Logging.MinimumLevel == NarratorLogLevel.Trace;

    private static string RedactCredential(string value, string? credential) =>
        string.IsNullOrEmpty(credential)
            ? value
            : value.Replace(credential, "[REDACTED CREDENTIAL]", StringComparison.Ordinal);

    private static string SafeEndpoint(Uri? uri)
    {
        if (uri is null) return "(unknown endpoint)";
        var safe = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return safe.Uri.ToString();
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static HttpRequestMessage CreateRequest(HttpMethod method, ApiConnectionSettings settings, string relative, string? credential)
    {
        var baseUrl = settings.BaseUrl!.ToString().TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relative));
        if (!string.IsNullOrWhiteSpace(credential)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static IReadOnlyList<JsonObject> BuildStoryMessages(
        PromptTemplateSettings templates,
        ContentLimitSettings limits,
        int recentTurnCount,
        GenerationContext context,
        bool opening)
    {
        var narrationInstruction = templates.StoryNarrationInstruction
            .Replace(PromptTemplateDefaults.MinSuggestedActionsPlaceholder, limits.MinSuggestedActions.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MaxSuggestedActionsPlaceholder, limits.MaxSuggestedActions.ToString(), StringComparison.Ordinal);
        var messages = new List<JsonObject> { Message("system", narrationInstruction) };
        messages.Add(Message("user", JsonSerializer.Serialize(new
        {
            contextType = "storyContext",
            storyPrompt = context.Definition.StoryPrompt,
            storyBible = context.StoryBible.Entries,
            storyBibleUpdateRules = new
            {
                add = "Set entryId to null. The application assigns the new ID; never invent one.",
                replace = "Use only the ID of an existing Story Bible entry supplied above.",
                remove = "Use only the ID of an existing Story Bible entry supplied above."
            },
            relevantStoryBibleEntryRules =
                "Include only IDs copied exactly from the current Story Bible supplied above. Never invent or return any other ID."
        }, Json)));
        if (context.RecentTurns.Count < recentTurnCount && !string.IsNullOrWhiteSpace(context.Definition.InitialEventsPrompt))
            messages.Add(Message("user", JsonSerializer.Serialize(new
            {
                contextType = "initialEvents",
                content = context.Definition.InitialEventsPrompt,
                instruction = "Use this only to help narrate the earliest scenes. It stops being supplied once " +
                    "enough real history has accumulated, so never rely on it being available later; anything " +
                    "that must be remembered belongs in the Story Bible instead."
            }, Json)));
        foreach (var turn in context.RecentTurns)
        {
            if (turn.PlayerAction is not null) messages.Add(Message("user", turn.PlayerAction));
            messages.Add(Message("assistant", turn.Narration));
        }
        messages.Add(Message("user", JsonSerializer.Serialize(new
        {
            requestType = opening ? "openingScene" : "storyTurn",
            turnNumber = context.NextTurnNumber,
            currentPlayerAction = opening ? null : context.PlayerAction,
            instruction = opening
                ? $"{templates.OpeningSceneInstruction} Copy turnNumber exactly into the response and set acknowledgedPlayerAction to null."
                : $"{templates.ContinueStoryInstruction} Resolve currentPlayerAction now. " +
                  "Do not answer an action from the preceding history and do not repeat an earlier scene. " +
                  "Advance beyond the last assistant narration. Copy turnNumber and currentPlayerAction exactly into the response fields."
        }, Json)));
        return messages;
    }


    private static StoryDefinitionGenerationResponse ParseDefinitionResponse(
        JsonObject node,
        ApiConnectionSettings settings)
    {
        node.Remove("_transport");
        RequireProperties(node, "refinedStoryPrompt", "suggestedTitle", "initialEventsPrompt", "initialStoryBibleEntries");
        var refinedStoryPrompt = RequiredString(node, "refinedStoryPrompt");
        if (string.IsNullOrWhiteSpace(refinedStoryPrompt) || refinedStoryPrompt.Length > settings.ContentLimits.MaxStoryPromptCharacters)
            throw new JsonException("The refined Story Prompt is empty or exceeds the configured limit.");
        var suggestedTitle = RequiredString(node, "suggestedTitle");
        if (string.IsNullOrWhiteSpace(suggestedTitle) || suggestedTitle.Length > settings.ContentLimits.MaxStoryTitleCharacters)
            throw new JsonException("The suggested title is empty or exceeds the configured limit.");
        var initialEventsPrompt = RequiredString(node, "initialEventsPrompt");
        if (initialEventsPrompt.Length > settings.ContentLimits.MaxStoryPromptCharacters)
            throw new JsonException("The Initial Events prompt exceeds the configured limit.");
        var entries = RequiredArray(node, "initialStoryBibleEntries");
        if (entries.Count > 2000) throw new JsonException("The initial Story Bible contains too many entries.");
        foreach (var item in entries)
            ValidateProposedEntry(item as JsonObject ?? throw new JsonException("A Story Bible entry must be an object."), settings);
        var result = node.Deserialize<StoryDefinitionGenerationResponse>(Json)
            ?? throw new JsonException("Empty Story Definition response.");
        return result;
    }

    private static StoryGenerationResponse ParseStoryResponse(
        JsonObject node,
        ApiConnectionSettings settings,
        GenerationContext context,
        bool opening)
    {
        var meta = node["_transport"] as JsonObject;
        node.Remove("_transport");
        RequireProperties(
            node,
            "turnNumber",
            "acknowledgedPlayerAction",
            "narration",
            "suggestedActions",
            "relevantStoryBibleEntryIds",
            "storyBibleUpdates");
        var turnNumber = RequiredInteger(node, "turnNumber");
        if (turnNumber != context.NextTurnNumber)
            throw new JsonException(
                $"The response acknowledged turn {turnNumber}, but the current turn is {context.NextTurnNumber}.");
        var acknowledgedAction = node["acknowledgedPlayerAction"] is null
            ? null
            : RequiredString(node, "acknowledgedPlayerAction");
        if (opening)
        {
            if (acknowledgedAction is not null)
                throw new JsonException("An opening-scene response must acknowledge a null player action.");
        }
        else if (acknowledgedAction is null || !NormalizedWords(acknowledgedAction).SequenceEqual(NormalizedWords(context.PlayerAction!)))
        {
            throw new JsonException(
                "The response acknowledged a different player action. Respond to currentPlayerAction and copy it exactly.");
        }
        var narration = RequiredString(node, "narration");
        if (string.IsNullOrWhiteSpace(narration) || narration.Length > settings.ContentLimits.MaxNarrationCharacters)
            throw new JsonException("Narration is empty or exceeds the configured limit.");
        if (!opening && context.RecentTurns.Any(turn => IsSubstantiallyDuplicate(narration, turn.Narration)))
            throw new JsonException(
                "The narration duplicates a recent scene. Advance the story by resolving currentPlayerAction instead.");
        var suggestions = RequiredArray(node, "suggestedActions");
        foreach (var suggestion in suggestions)
        {
            var text = StringValue(suggestion, "A suggested action");
            if (string.IsNullOrWhiteSpace(text) || text.Length > settings.ContentLimits.MaxSuggestedActionCharacters)
                throw new JsonException("A suggested action is empty or exceeds the configured limit.");
        }
        var relevantNodes = RequiredArray(node, "relevantStoryBibleEntryIds");
        var currentEntryIds = context.StoryBible.Entries.Select(entry => entry.Id).ToHashSet();
        var seenRelevantIds = new HashSet<Guid>();
        var normalizedRelevantIds = new List<Guid>();
        foreach (var relevantNode in relevantNodes)
        {
            if (relevantNode is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                Guid.TryParse(text, out var id) &&
                currentEntryIds.Contains(id) &&
                seenRelevantIds.Add(id))
                normalizedRelevantIds.Add(id);
        }
        node["relevantStoryBibleEntryIds"] = new JsonArray(
            normalizedRelevantIds.Select(id => (JsonNode)id.ToString("D")).ToArray());

        var updates = RequiredArray(node, "storyBibleUpdates");
        if (updates.Count > settings.ContentLimits.MaxStoryBibleUpdatesPerResponse)
            throw new JsonException("Too many Story Bible updates.");
        foreach (var item in updates)
        {
            var update = item as JsonObject ?? throw new JsonException("A Story Bible update must be an object.");
            RequireProperties(update, "operation", "entryId", "entry");
            var operation = RequiredString(update, "operation");
            if (operation is not ("add" or "replace" or "remove"))
                throw new JsonException("A Story Bible update has an invalid operation.");
            if (operation == "add")
                update["entryId"] = null;
            if (operation != "add" && (update["entryId"] is null || !Guid.TryParse(StringValue(update["entryId"], "An entry ID"), out _)))
                throw new JsonException("A replace or remove update requires an entry ID.");
            if (operation == "remove")
            {
                if (update["entry"] is not null) throw new JsonException("A remove update cannot contain an entry.");
            }
            else
            {
                ValidateProposedEntry(update["entry"] as JsonObject
                    ?? throw new JsonException("An add or replace update requires an entry."), settings);
            }
        }

        var dto = node.Deserialize<StoryResponseDto>(Json) ?? throw new JsonException("Empty story response.");
        var projectedRelevant = opening
            ? dto.RelevantStoryBibleEntryIds.Concat(context.StoryBible.Entries.Select(x => x.Id)).Distinct().ToArray()
            : dto.RelevantStoryBibleEntryIds;
        _ = StoryBibleProcessor.Apply(
            context.StoryBible,
            projectedRelevant,
            dto.StoryBibleUpdates,
            context.NextTurnNumber,
            settings.StoryGeneration);
        return new(
            dto.Narration,
            dto.SuggestedActions,
            dto.RelevantStoryBibleEntryIds,
            dto.StoryBibleUpdates,
            meta?["responseId"]?.GetValue<string>(),
            meta?["inputTokens"]?.GetValue<int?>(),
            meta?["outputTokens"]?.GetValue<int?>());
    }

    private static void ValidateProposedEntry(JsonObject entry, ApiConnectionSettings settings)
    {
        RequireProperties(entry, "category", "name", "content", "importance");
        var category = RequiredString(entry, "category");
        var name = RequiredString(entry, "name");
        var content = RequiredString(entry, "content");
        if (string.IsNullOrWhiteSpace(category) || category.Length > settings.ContentLimits.MaxStoryBibleCategoryCharacters)
            throw new JsonException("A Story Bible category is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > settings.ContentLimits.MaxStoryBibleNameCharacters)
            throw new JsonException("A Story Bible name is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(content))
            throw new JsonException("A Story Bible entry has empty content.");
        var importance = RequiredInteger(entry, "importance");
        if (importance is < 1 or > 5) throw new JsonException("Story Bible importance must be from 1 to 5.");
    }

    private static void RequireProperties(JsonObject value, params string[] expected)
    {
        var actual = value.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new JsonException($"Expected properties: {string.Join(", ", expected)}.");
    }

    private static JsonArray RequiredArray(JsonObject value, string name) =>
        value[name] as JsonArray ?? throw new JsonException($"'{name}' must be an array.");

    private static string RequiredString(JsonObject value, string name) =>
        StringValue(value[name], $"'{name}'");

    private static string StringValue(JsonNode? value, string description)
    {
        try { return value?.GetValue<string>() ?? throw new JsonException($"{description} must be a string."); }
        catch (InvalidOperationException ex) { throw new JsonException($"{description} must be a string.", ex); }
    }

    private static int RequiredInteger(JsonObject value, string name)
    {
        try { return value[name]?.GetValue<int>() ?? throw new JsonException($"'{name}' must be an integer."); }
        catch (InvalidOperationException ex) { throw new JsonException($"'{name}' must be an integer.", ex); }
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static JsonObject SimpleProbeSchema() => ObjectSchema(new Dictionary<string, JsonNode?> { ["ok"] = new JsonObject { ["type"] = "boolean" } }, ["ok"]);

    private static JsonObject DefinitionSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["refinedStoryPrompt"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryPromptCharacters },
        ["suggestedTitle"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryTitleCharacters },
        ["initialEventsPrompt"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryPromptCharacters },
        ["initialStoryBibleEntries"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = 2000,
            ["items"] = ProposedEntrySchema(settings)
        }
    }, ["refinedStoryPrompt", "suggestedTitle", "initialEventsPrompt", "initialStoryBibleEntries"]);

    private static JsonObject TurnSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["turnNumber"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        ["acknowledgedPlayerAction"] = new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["maxLength"] = settings.ContentLimits.MaxPlayerActionCharacters
        },
        ["narration"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxNarrationCharacters },
        ["suggestedActions"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = settings.ContentLimits.MinSuggestedActions,
            ["maxItems"] = settings.ContentLimits.MaxSuggestedActions,
            ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxSuggestedActionCharacters }
        },
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
    }, ["turnNumber", "acknowledgedPlayerAction", "narration", "suggestedActions", "relevantStoryBibleEntryIds", "storyBibleUpdates"]);

    private static bool IsSubstantiallyDuplicate(string candidate, string previous)
    {
        var candidateWords = NormalizedWords(candidate);
        var previousWords = NormalizedWords(previous);
        if (candidateWords.SequenceEqual(previousWords)) return true;
        if (candidateWords.Length < 20 || previousWords.Length < 20) return false;

        var candidateShingles = Shingles(candidateWords);
        var previousShingles = Shingles(previousWords);
        var intersection = candidateShingles.Count(previousShingles.Contains);
        var smaller = Math.Min(candidateShingles.Count, previousShingles.Count);
        var union = candidateShingles.Count + previousShingles.Count - intersection;
        return smaller > 0 &&
               intersection / (double)smaller >= 0.90 &&
               intersection / (double)union >= 0.80;
    }

    private static string[] NormalizedWords(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(4096)
            .Select(word => new string(word
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray()))
            .Where(word => word.Length > 0)
            .ToArray();

    private static HashSet<int> Shingles(IReadOnlyList<string> words)
    {
        const int shingleSize = 5;
        var result = new HashSet<int>();
        for (var start = 0; start <= words.Count - shingleSize; start++)
        {
            var hash = new HashCode();
            for (var offset = 0; offset < shingleSize; offset++)
                hash.Add(words[start + offset], StringComparer.Ordinal);
            result.Add(hash.ToHashCode());
        }
        return result;
    }

    private static JsonObject ProposedEntrySchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["category"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryBibleCategoryCharacters },
        ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStoryBibleNameCharacters },
        ["content"] = new JsonObject { ["type"] = "string" },
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
        RequireBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(settings.ModelId))
            throw new ProviderException("A model ID is required.", null);
    }

    private static void RequireBaseUrl(ApiConnectionSettings settings)
    {
        if (settings.BaseUrl is null)
            throw new ProviderException("A base URL is required.", null);
    }

    private static IReadOnlyList<ProviderRequestContract> RequestContractCandidates() =>
    [
        new(OutputTokenParameter.MaxCompletionTokens, InstructionMessageRole.Developer),
        new(OutputTokenParameter.MaxCompletionTokens, InstructionMessageRole.System),
        new(OutputTokenParameter.MaxTokens, InstructionMessageRole.Developer),
        new(OutputTokenParameter.MaxTokens, InstructionMessageRole.System)
    ];

    private static string SafeMessage(Exception ex) => ex is ProviderException ? ex.Message : "The provider response could not be processed.";

    private sealed record ProviderRequestContract(
        OutputTokenParameter OutputTokenParameter,
        InstructionMessageRole InstructionMessageRole);

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
