using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    ILogger<OpenAiCompatibleProvider>? logger = null,
    ISecureStorageService? secureStorage = null) : ILanguageModelProvider
{
    private readonly ILogger<OpenAiCompatibleProvider> _logger =
        logger ?? NullLogger<OpenAiCompatibleProvider>.Instance;
    private readonly ISecureStorageService? _secureStorage = secureStorage;

    private static readonly PromptTemplateSettings Templates = PromptTemplateDefaults.Create();

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
        if (root["data"] is not { } data) return [];
        var models = data as JsonArray ?? throw new JsonException("'data' must be an array.");
        return models
            .Select(x => (x as JsonObject)?["id"] is { } id ? StringValue(id, "A model 'id'") : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Where(IsTextGenerationModel)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // Most OpenAI-compatible endpoints return only an ID, not modality metadata. Exclude only model
    // families that are unambiguously non-chat; unfamiliar IDs remain available for compatible providers.
    private static bool IsTextGenerationModel(string modelId) => !new[]
    {
        "embed", "embedding", "moderation", "whisper", "transcri", "tts", "text-to-speech",
        "dall-e", "stable-diffusion", "image-generation", "imagegen", "rerank", "re-rank"
    }.Any(marker => modelId.Contains(marker, StringComparison.OrdinalIgnoreCase));

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
                            SimpleProbeSchema(), candidate, cancellationToken, contract);
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
        ApiConnectionSettings settings, string? credential, string storyDefinitionPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new[]
        {
            Message("system", Templates.StoryDefinitionInstruction),
            Message("user", storyDefinitionPrompt)
        };
        return await CompleteWithCorrectionAsync(
            settings,
            credential,
            messages,
            DefinitionSchema(settings),
            node => ParseDefinitionResponse(node, settings),
            cancellationToken,
            GenerationCall.StoryDefinition);
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
        if (settings.TurnPipeline == TurnPipelineMode.TwoCalls)
            return await GenerateStoryWithTwoCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        if (settings.TurnPipeline == TurnPipelineMode.ThreeCalls)
            return await GenerateStoryWithThreeCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        if (settings.TurnPipeline == TurnPipelineMode.FourCalls)
            return await GenerateStoryWithFourCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        if (settings.TurnPipeline == TurnPipelineMode.FiveCalls)
            return await GenerateStoryWithFiveCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        if (settings.TurnPipeline == TurnPipelineMode.SevenCalls)
            return await GenerateStoryWithSevenCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        if (settings.TurnPipeline == TurnPipelineMode.SevenCallsParallel)
            return await GenerateStoryWithSevenCallPipelineAsync(settings, credential, context, opening, cancellationToken, parallelAnalyses: true);
        if (settings.TurnPipeline == TurnPipelineMode.EightCalls)
            return await GenerateStoryWithEightCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        var messages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        return await CompleteWithCorrectionAsync(
            settings,
            credential,
            messages,
            TurnSchema(settings),
            node => ParseStoryResponse(node, settings, context, opening),
            cancellationToken);
    }

    // The player-facing answer is deliberately not asked to decide hidden state. Each intermediate
    // artifact is supplied to the next call, making eligibility and pacing decisions inspectable and
    // keeping the final extraction call focused on persistence rather than prose invention.
    private async Task<StoryGenerationResponse> GenerateStoryWithFourCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        var baseMessages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var adjudication = await GenerateStageAsync(
            settings, credential, baseMessages,
            Templates.TurnAdjudicationInstruction,
            cancellationToken);
        var scenePlan = await GenerateStageAsync(
            settings, credential, baseMessages,
            Templates.ScenePlanInstruction.Replace("{adjudication}", adjudication),
            cancellationToken);
        var draft = await GenerateNarrationDraftAsync(
            settings, credential, baseMessages,
            Templates.NarrationFromPlanInstruction.Replace("{scenePlan}", scenePlan),
            cancellationToken);
        var extractionMessages = baseMessages.Concat([
            Message("system", Templates.StateExtractionInstruction.Replace("{artifacts}", "")),
            Message("user", JsonSerializer.Serialize(new { adjudication, scenePlan, narration = draft.Narration, suggestedActions = draft.SuggestedActions }, Json))
        ]).ToArray();
        var extracted = await CompleteWithCorrectionAsync(
            settings, credential, extractionMessages, TurnSchema(settings),
            node => ParseStoryResponse(node, settings, context, opening), cancellationToken, GenerationCall.StateExtraction);
        return extracted with { Narration = draft.Narration, SuggestedActions = draft.SuggestedActions };
    }

    private async Task<StoryGenerationResponse> GenerateStoryWithTwoCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        var messages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var draft = await GenerateNarrationDraftAsync(settings, credential, messages,
            Templates.NarrationOnlyInstruction, cancellationToken);
        return await ExtractStoryAsync(settings, credential, messages, new { narration = draft.Narration, suggestedActions = draft.SuggestedActions }, context, opening, cancellationToken, draft);
    }

    private async Task<StoryGenerationResponse> GenerateStoryWithThreeCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        var messages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var adjudication = await GenerateStageAsync(settings, credential, messages,
            Templates.TurnAdjudicationInstruction, cancellationToken);
        var draft = await GenerateNarrationDraftAsync(settings, credential, messages,
            Templates.NarrationFromAdjudicationInstruction.Replace("{adjudication}", adjudication), cancellationToken);
        return await ExtractStoryAsync(settings, credential, messages, new { adjudication, narration = draft.Narration, suggestedActions = draft.SuggestedActions }, context, opening, cancellationToken, draft);
    }

    private async Task<StoryGenerationResponse> GenerateStoryWithFiveCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        var messages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var adjudication = await GenerateStageAsync(settings, credential, messages,
            Templates.TurnAdjudicationInstruction, cancellationToken);
        var scenePlan = await GenerateStageAsync(settings, credential, messages,
            Templates.ScenePlanInstruction.Replace("{adjudication}", adjudication), cancellationToken);
        var critique = await GenerateStageAsync(settings, credential, messages,
            Templates.PlanCriticInstruction.Replace("{adjudication}", adjudication).Replace("{scenePlan}", scenePlan), cancellationToken);
        var draft = await GenerateNarrationDraftAsync(settings, credential, messages,
            Templates.NarrationFromCritiqueInstruction.Replace("{scenePlan}", scenePlan).Replace("{critique}", critique), cancellationToken);
        return await ExtractStoryAsync(settings, credential, messages, new { adjudication, scenePlan, critique, narration = draft.Narration, suggestedActions = draft.SuggestedActions }, context, opening, cancellationToken, draft);
    }

    private async Task<StoryGenerationResponse> GenerateStoryWithSevenCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken,
        bool parallelAnalyses = false)
    {
        var baseMessages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var adjudication = await GenerateStageAsync(settings, credential, baseMessages,
            Templates.TurnAdjudicationInstruction, cancellationToken);
        var scenePlan = await GenerateStageAsync(settings, credential, baseMessages,
            Templates.ScenePlanInstruction.Replace("{adjudication}", adjudication), cancellationToken);
        var draft = await GenerateNarrationDraftAsync(settings, credential, baseMessages,
            Templates.NarrationFromPlanInstruction.Replace("{scenePlan}", scenePlan), cancellationToken);
        var artifacts = new { adjudication, scenePlan, narration = draft.Narration, suggestedActions = draft.SuggestedActions };
        var artifactJson = JsonSerializer.Serialize(artifacts, Json);
        string bibleAnalysis, eventAnalysis, outcomeAnalysis;
        if (parallelAnalyses)
        {
            var bibleTask = GenerateStageAsync(settings, credential, baseMessages,
                Templates.StoryBibleAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
            var eventTask = GenerateStageAsync(settings, credential, baseMessages,
                Templates.PlannedEventAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
            var outcomeTask = GenerateStageAsync(settings, credential, baseMessages,
                Templates.ConditionSummaryAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
            await Task.WhenAll(bibleTask, eventTask, outcomeTask);
            bibleAnalysis = await bibleTask;
            eventAnalysis = await eventTask;
            outcomeAnalysis = await outcomeTask;
        }
        else
        {
            bibleAnalysis = await GenerateStageAsync(settings, credential, baseMessages,
                Templates.StoryBibleAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
            eventAnalysis = await GenerateStageAsync(settings, credential, baseMessages,
                Templates.PlannedEventAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
            outcomeAnalysis = await GenerateStageAsync(settings, credential, baseMessages,
                Templates.ConditionSummaryAnalysisInstruction.Replace("{artifacts}", artifactJson), cancellationToken);
        }
        var extracted = await CompleteWithCorrectionAsync(settings, credential, baseMessages.Concat([
            Message("system", Templates.StateExtractionFromAnalysesInstruction.Replace("{analyses}", "")),
            Message("user", JsonSerializer.Serialize(new { artifacts, bibleAnalysis, eventAnalysis, outcomeAnalysis }, Json))
        ]).ToArray(), TurnSchema(settings), node => ParseStoryResponse(node, settings, context, opening), cancellationToken, GenerationCall.StateExtraction);
        return extracted with { Narration = draft.Narration, SuggestedActions = draft.SuggestedActions };
    }

    private async Task<StoryGenerationResponse> GenerateStoryWithEightCallPipelineAsync(
        ApiConnectionSettings settings, string? credential, GenerationContext context, bool opening, CancellationToken cancellationToken)
    {
        // The seven-call result is deliberately generated first. The eighth call may improve only the
        // prose and suggestions; it never gets to change the already-extracted persistent state.
        var extracted = await GenerateStoryWithSevenCallPipelineAsync(settings, credential, context, opening, cancellationToken);
        var messages = BuildStoryMessages(settings.ContentLimits, settings.StoryGeneration, context, opening);
        var revised = await GenerateNarrationDraftAsync(settings, credential, messages,
            Templates.ProseRevisionInstruction.Replace("{turn}", "") +
            JsonSerializer.Serialize(new { extracted.Narration, extracted.SuggestedActions, extracted.StorySummary }, Json), cancellationToken);
        return extracted with { Narration = revised.Narration, SuggestedActions = revised.SuggestedActions };
    }

    private async Task<StoryGenerationResponse> ExtractStoryAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> baseMessages, object artifacts,
        GenerationContext context, bool opening, CancellationToken cancellationToken, NarrationDraft draft)
    {
        var extracted = await CompleteWithCorrectionAsync(settings, credential, baseMessages.Concat([
            Message("system", Templates.StateExtractionInstruction.Replace("{artifacts}", "")),
            Message("user", JsonSerializer.Serialize(artifacts, Json))
        ]).ToArray(), TurnSchema(settings), node => ParseStoryResponse(node, settings, context, opening), cancellationToken, GenerationCall.StateExtraction);
        return extracted with { Narration = draft.Narration, SuggestedActions = draft.SuggestedActions };
    }

    private async Task<string> GenerateStageAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> baseMessages, string instruction,
        CancellationToken cancellationToken, GenerationCall generationCall = GenerationCall.Turn)
    {
        generationCall = generationCall == GenerationCall.Turn ? StageCall(instruction) : generationCall;
        var messages = baseMessages.Concat([Message("system", instruction)]).ToArray();
        return await CompleteWithCorrectionAsync(
            settings, credential, messages, StageSchema(),
            node => RequiredString(node, "result"), cancellationToken, generationCall);
    }

    private async Task<NarrationDraft> GenerateNarrationDraftAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> baseMessages, string instruction,
        CancellationToken cancellationToken, GenerationCall generationCall = GenerationCall.Narration)
    {
        var messages = baseMessages.Concat([Message("system", instruction)]).ToArray();
        return await CompleteWithCorrectionAsync(
            settings, credential, messages, NarrationDraftSchema(settings),
            node => ParseNarrationDraft(node, settings), cancellationToken, generationCall);
    }

    private static GenerationCall StageCall(string instruction)
    {
        if (instruction.Contains("turn adjudicator", StringComparison.Ordinal)) return GenerationCall.Adjudication;
        if (instruction.Contains("scene planner", StringComparison.Ordinal)) return GenerationCall.ScenePlan;
        if (instruction.Contains("continuity and rule critic", StringComparison.Ordinal)) return GenerationCall.PlanCritic;
        if (instruction.Contains("Story Bible analyst", StringComparison.Ordinal)) return GenerationCall.StoryBibleAnalysis;
        if (instruction.Contains("planned-event analyst", StringComparison.Ordinal)) return GenerationCall.PlannedEventAnalysis;
        if (instruction.Contains("condition and summary analyst", StringComparison.Ordinal)) return GenerationCall.ConditionSummaryAnalysis;
        return GenerationCall.Turn;
    }

    private async Task<T> CompleteWithCorrectionAsync<T>(
        ApiConnectionSettings settings,
        string? credential,
        IReadOnlyList<JsonObject> messages,
        JsonObject schema,
        Func<JsonObject, T> parseAndValidate,
        CancellationToken cancellationToken,
        GenerationCall generationCall = GenerationCall.Turn)
    {
        var resolved = await ResolveConnectionAsync(settings, credential, generationCall, cancellationToken);
        settings = resolved.Settings;
        credential = resolved.Credential;
        var tier = settings.Capabilities.StructuredOutputTier;
        if (tier is StructuredOutputTier.Untested or StructuredOutputTier.Unsupported) tier = StructuredOutputTier.PromptedJson;
        try
        {
            return parseAndValidate(await CompleteAsync(settings, credential, messages, schema, tier, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or NarratorException)
        {
            var corrected = messages.Concat([
                Message("system", Templates.CorrectiveRetryInstruction.Replace(
                    PromptTemplateDefaults.ValidationErrorPlaceholder,
                    ex.Message,
                    StringComparison.Ordinal))
            ]).ToArray();
            return parseAndValidate(await CompleteAsync(settings, credential, corrected, schema, tier, cancellationToken));
        }
    }

    private async Task<(ApiConnectionSettings Settings, string? Credential)> ResolveConnectionAsync(
        ApiConnectionSettings settings, string? fallbackCredential, GenerationCall generationCall, CancellationToken cancellationToken)
    {
        if (!settings.GenerationCallRoutes.TryGetValue(generationCall, out var route) || route.ConnectionId is not { } connectionId)
            return (settings, fallbackCredential);
        var connection = settings.Connections.FirstOrDefault(candidate => candidate.Id == connectionId);
        if (connection is null)
            throw new NarratorException($"The connection selected for {generationCall} no longer exists.");
        var credential = _secureStorage is null
            ? fallbackCredential
            : await _secureStorage.GetAsync(SecureStorageKeys.ApiCredentialForConnection(connection.Id), cancellationToken);
        credential ??= fallbackCredential;
        return (settings with
        {
            BaseUrl = connection.BaseUrl,
            ModelId = string.IsNullOrWhiteSpace(route.ModelId) ? settings.ModelId : route.ModelId,
            RequestTimeout = route.RequestTimeout ?? settings.RequestTimeout,
            MaxOutputTokens = route.MaxOutputTokens ?? settings.MaxOutputTokens,
            Parameters = route.Parameters ?? settings.Parameters,
            Retry = route.Retry ?? settings.Retry,
            Capabilities = connection.Capabilities
        }, credential);
    }

    private async Task<JsonObject> CompleteAsync(
        ApiConnectionSettings settings, string? credential, IReadOnlyList<JsonObject> messages, JsonObject schema,
        StructuredOutputTier tier,
        CancellationToken cancellationToken,
        ProviderRequestContract? requestContract = null)
    {
        RequireConnection(settings);
        var requestMessages = tier == StructuredOutputTier.PromptedJson
            ? messages.Concat([Message("system", Templates.PromptedJsonInstruction
                .Replace(PromptTemplateDefaults.SchemaPlaceholder, schema.ToJsonString(Json), StringComparison.Ordinal)
                .Replace(PromptTemplateDefaults.ExamplePlaceholder, ExampleFor(schema)?.ToJsonString(Json) ?? "{}", StringComparison.Ordinal))]).ToArray()
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
        var choice = envelope["choices"] is JsonArray { Count: > 0 } choices
            ? choices[0] as JsonObject ?? throw new JsonException("The provider returned no choices.")
            : throw new JsonException("The provider returned no choices.");
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
                    LogRequestMessages(requestId, endpoint, requestBody, credential);
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
                    LogResponseBody(requestId, request.Method, endpoint, bytes, credential);
                if (!response.IsSuccessStatusCode)
                {
                    var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                        || (int)response.StatusCode >= 500;
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

    private void LogRequestMessages(Guid requestId, string endpoint, string requestBody, string? credential)
    {
        var messages = (JsonNode.Parse(requestBody) as JsonObject)?["messages"] as JsonArray;
        if (messages is null)
        {
            _logger.LogTrace("LLM request body for {RequestId}, {Endpoint}: {RequestBody}", requestId, endpoint, RedactCredential(requestBody, credential));
            return;
        }
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index] as JsonObject;
            var role = message?["role"]?.GetValue<string>() ?? "unknown";
            var content = message?["content"]?.GetValue<string>() ?? "";
            _logger.LogTrace(
                "LLM request message {MessageIndex} for {RequestId}, {Endpoint} [{Role}]: {Content}",
                index,
                requestId,
                endpoint,
                role,
                RedactCredential(Decoded(content), credential));
        }
    }

    private void LogResponseBody(Guid requestId, HttpMethod method, string endpoint, byte[] bytes, string? credential)
    {
        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            var choices = (JsonNode.Parse(text) as JsonObject)?["choices"] as JsonArray;
            var content = choices is { Count: > 0 } ? choices[0]?["message"]?["content"]?.GetValue<string>() : null;
            if (content is not null && LogStoryResponse(requestId, method, endpoint, content, credential)) return;
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        _logger.LogTrace(
            "LLM response body for {RequestId}, {Method} {Endpoint}: {ResponseBody}",
            requestId,
            method,
            endpoint,
            RedactCredential(text, credential));
    }

    private bool LogStoryResponse(Guid requestId, HttpMethod method, string endpoint, string content, string? credential)
    {
        var parsed = JsonNode.Parse(content) as JsonObject;
        var narration = parsed?["narration"]?.GetValue<string>();
        if (narration is null) return false;

        _logger.LogTrace(
            "LLM response message for {RequestId}, {Method} {Endpoint}: {Narration}",
            requestId,
            method,
            endpoint,
            RedactCredential(narration, credential));

        var suggestions = parsed!["suggestedActions"] as JsonArray ?? [];
        for (var index = 0; index < suggestions.Count; index++)
        {
            var suggestion = suggestions[index]?.GetValue<string>();
            if (suggestion is null) continue;
            _logger.LogTrace(
                "LLM response suggested action {ActionIndex} for {RequestId}: {Action}",
                index,
                requestId,
                RedactCredential(suggestion, credential));
        }

        var updates = parsed["storyBibleUpdates"] as JsonArray ?? [];
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index] as JsonObject;
            var operation = update?["operation"]?.GetValue<string>() ?? "unknown";
            var name = (update?["entry"] as JsonObject)?["name"]?.GetValue<string>() ?? update?["entryId"]?.GetValue<string>() ?? "(unknown)";
            _logger.LogTrace(
                "LLM response Story Bible update {UpdateIndex} for {RequestId}: {Operation} {Name}",
                index,
                requestId,
                operation,
                RedactCredential(name, credential));
        }

        var plannedEventUpdates = parsed["plannedEventUpdates"] as JsonArray ?? [];
        for (var index = 0; index < plannedEventUpdates.Count; index++)
        {
            var update = plannedEventUpdates[index] as JsonObject;
            var operation = update?["operation"]?.GetValue<string>() ?? "unknown";
            var description = (update?["entry"] as JsonObject)?["description"]?.GetValue<string>() ?? update?["entryId"]?.GetValue<string>() ?? "(unknown)";
            _logger.LogTrace(
                "LLM response Planned Event update {UpdateIndex} for {RequestId}: {Operation} {Description}",
                index,
                requestId,
                operation,
                RedactCredential(description, credential));
        }
        return true;
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

    private static string Decoded(string content)
    {
        if (content.Length == 0 || (content[0] != '{' && content[0] != '[')) return content;
        try
        {
            var node = JsonNode.Parse(content);
            return node is null ? content : Readable(node);
        }
        catch (JsonException) { return content; }
    }

    private static string Readable(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        JsonArray array => string.Join(", ", array.Select(Readable)),
        JsonObject obj => string.Join("; ", obj.Select(x => $"{x.Key}: {Readable(x.Value)}")),
        _ => node.ToJsonString()
    };

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
        // Append to the path via UriBuilder rather than string-concatenating settings.BaseUrl.ToString(),
        // so a base URL with a query string (e.g. Azure-OpenAI-style endpoints) keeps that query string
        // instead of it being silently swallowed by the path merge.
        var baseUri = settings.BaseUrl!;
        var uri = new UriBuilder(baseUri) { Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + relative }.Uri;
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(credential)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static IReadOnlyList<JsonObject> BuildStoryMessages(
        ContentLimitSettings limits,
        StoryGenerationSettings storyGeneration,
        GenerationContext context,
        bool opening)
    {
        var narrationInstruction = Templates.StoryNarrationInstruction
            .Replace(PromptTemplateDefaults.MinSuggestedActionsPlaceholder, limits.MinSuggestedActions.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MaxSuggestedActionsPlaceholder, limits.MaxSuggestedActions.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MinParagraphsPlaceholder, limits.MinParagraphsPerResponse.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MaxParagraphsPlaceholder, limits.MaxParagraphsPerResponse.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MinSentencesPlaceholder, limits.MinSentencesPerParagraph.ToString(), StringComparison.Ordinal)
            .Replace(PromptTemplateDefaults.MaxSentencesPlaceholder, limits.MaxSentencesPerParagraph.ToString(), StringComparison.Ordinal);
        var messages = new List<JsonObject> { Message("system", narrationInstruction) };
        // The add/replace/remove ID rules and the relevant-entry ID rule live only in the system prompt
        // (StoryNarrationInstruction) - they're static across every turn, so repeating them here would
        // just burn tokens on every single request without adding any information.
        var plannedEventCount = context.PlannedEvents.Entries.Count;
        messages.Add(Message("user", JsonSerializer.Serialize(new
        {
            contextType = "storyContext",
            storyPrompt = context.Definition.StoryPrompt,
            storyBible = context.StoryBible.Entries,
            plannedEvents = PlannedEventPayload(context.PlannedEvents),
            // Reported so the model can scale its own eagerness to propose new Planned Events against
            // remaining room, using the same warning threshold the app itself uses to flag the list as
            // approaching capacity (see PlannedEventProcessor.IsApproachingLimits) - if that threshold is
            // reconfigured, the model's behavior tracks it automatically without a prompt change.
            plannedEventCapacity = new
            {
                count = plannedEventCount,
                max = storyGeneration.MaxPlannedEvents,
                remaining = Math.Max(0, storyGeneration.MaxPlannedEvents - plannedEventCount),
                usedPercent = storyGeneration.MaxPlannedEvents <= 0
                    ? 100
                    : (int)Math.Round(100.0 * plannedEventCount / storyGeneration.MaxPlannedEvents),
                warningPercent = storyGeneration.PlannedEventsWarningPercent
            },
            victoryConditions = ConditionPayload(context.VictoryConditions),
            lossConditions = ConditionPayload(context.LossConditions),
            storySummary = context.StorySummary
        }, Json)));
        if (context.RecentTurns.Count < storyGeneration.RecentTurnCount && !string.IsNullOrWhiteSpace(context.Definition.InitialEventsPrompt))
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
            resolutionRoll = opening ? (int?)null : RandomNumberGenerator.GetInt32(1, 101),
            instruction = opening ? Templates.OpeningSceneInstruction : Templates.ContinueStoryInstruction,
            conditionGate = "For every planned event marked requires condition verification, first decide whether its condition was already established before this turn. Unless it was, treat the event as unavailable: do not narrate, imply, foreshadow as already underway, advance, fulfill, or mark it relevant."
        }, Json)));
        return messages;
    }

    private static IReadOnlyList<object> PlannedEventPayload(PlannedEvents events) =>
        events.Entries.Select(entry => new
        {
            id = entry.Id,
            description = entry.Description,
            importance = entry.Importance,
            urgency = entry.Urgency,
            condition = entry.Condition,
            availability = string.IsNullOrWhiteSpace(entry.Condition) ? "eligible" : "requires condition verification before eligible"
        }).Cast<object>().ToArray();

    // Already-met conditions are dropped entirely - nothing left to evaluate for them - while the rest
    // are sent with a revealed flag so the model never re-reveals a non-secret condition already
    // established in the narration.
    private static IReadOnlyList<object> ConditionPayload(ConditionsContext context) =>
        context.Conditions.Entries
            .Where(x => !context.MetIds.Contains(x.Id))
            .Select(x => new { id = x.Id, description = x.Description, secret = x.Secret, revealed = context.RevealedIds.Contains(x.Id) })
            .ToArray();

    private static StoryDefinitionGenerationResponse ParseDefinitionResponse(
        JsonObject node,
        ApiConnectionSettings settings)
    {
        node.Remove("_transport");
        RequireProperties(node, settings, "refinedStoryPrompt", "suggestedTitle", "initialEventsPrompt", "initialStoryBibleEntries",
            "initialPlannedEvents", "initialVictoryConditions", "initialLossConditions");
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
        var plannedEvents = RequiredArray(node, "initialPlannedEvents");
        if (plannedEvents.Count > SettingsValidator.MaxPlannedEventsUpperBound)
            throw new JsonException("The initial Planned Events contain too many entries.");
        foreach (var item in plannedEvents)
            ValidateProposedPlannedEvent(item as JsonObject ?? throw new JsonException("A Planned Event must be an object."), settings);
        var victoryConditions = RequiredArray(node, "initialVictoryConditions");
        if (victoryConditions.Count > SettingsValidator.MaxConditionsUpperBound)
            throw new JsonException("The initial Victory Conditions contain too many entries.");
        foreach (var item in victoryConditions)
            ValidateProposedCondition(item as JsonObject ?? throw new JsonException("A Victory Condition must be an object."), settings);
        var lossConditions = RequiredArray(node, "initialLossConditions");
        if (lossConditions.Count > SettingsValidator.MaxConditionsUpperBound)
            throw new JsonException("The initial Loss Conditions contain too many entries.");
        foreach (var item in lossConditions)
            ValidateProposedCondition(item as JsonObject ?? throw new JsonException("A Loss Condition must be an object."), settings);
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
        NormalizeAcknowledgedPlayerActionField(node);
        RequireProperties(
            node,
            settings,
            "turnNumber",
            "acknowledgedPlayerAction",
            "narration",
            "suggestedActions",
            "relevantStoryBibleEntryIds",
            "storyBibleUpdates",
            "relevantPlannedEventIds",
            "plannedEventUpdates",
            "revealedVictoryConditionIds",
            "metVictoryConditionIds",
            "revealedLossConditionIds",
            "metLossConditionIds",
            "storySummary");
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
                "The response must set acknowledgedPlayerAction - not currentPlayerAction, which is only ever an input field, " +
                "never part of the response - to an exact copy of currentPlayerAction's text.");
        }
        var narration = RequiredString(node, "narration");
        if (string.IsNullOrWhiteSpace(narration) || narration.Length > settings.ContentLimits.MaxNarrationCharacters)
            throw new JsonException("Narration is empty or exceeds the configured limit.");
        if (!opening && context.RecentTurns.Any(turn => IsSubstantiallyDuplicate(narration, turn.Narration)))
            throw new JsonException(
                "The narration duplicates a recent scene. Advance the story by resolving currentPlayerAction instead.");
        var storySummary = RequiredString(node, "storySummary");
        if (storySummary.Length > settings.ContentLimits.MaxStorySummaryCharacters)
            throw new JsonException("The story summary exceeds the configured limit.");
        var suggestions = RequiredArray(node, "suggestedActions");
        foreach (var suggestion in suggestions)
        {
            var text = StringValue(suggestion, "A suggested action");
            if (string.IsNullOrWhiteSpace(text) || text.Length > settings.ContentLimits.MaxSuggestedActionCharacters)
                throw new JsonException("A suggested action is empty or exceeds the configured limit.");
        }
        // Unlike every other violation in this method, an unknown, malformed, or duplicate relevant-entry
        // ID is silently dropped rather than thrown as a JsonException. This is deliberate: the model
        // getting a stray ID wrong here doesn't corrupt story data - it just under-marks relevance - so
        // it isn't worth spending the one corrective retry on, unlike a bad narration or update shape.
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

        // Same lenient-drop treatment as relevantStoryBibleEntryIds above, for the same reason.
        var relevantPlannedEventNodes = RequiredArray(node, "relevantPlannedEventIds");
        var currentPlannedEventIds = context.PlannedEvents.Entries.Select(entry => entry.Id).ToHashSet();
        var seenRelevantPlannedEventIds = new HashSet<Guid>();
        var normalizedRelevantPlannedEventIds = new List<Guid>();
        foreach (var relevantNode in relevantPlannedEventNodes)
        {
            if (relevantNode is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                Guid.TryParse(text, out var id) &&
                currentPlannedEventIds.Contains(id) &&
                seenRelevantPlannedEventIds.Add(id))
                normalizedRelevantPlannedEventIds.Add(id);
        }
        node["relevantPlannedEventIds"] = new JsonArray(
            normalizedRelevantPlannedEventIds.Select(id => (JsonNode)id.ToString("D")).ToArray());

        // Same lenient-drop treatment as the relevant-id lists above: an unknown id, a duplicate mention,
        // an attempt to reveal an already-revealed/already-met/secret condition, or to re-report an
        // already-met one is a low-stakes narrative-pacing slip, not data corruption, so it's silently
        // dropped rather than spent as a corrective retry. NormalizeConditionIds rewrites node in place,
        // so the dto deserialized below and the response returned to NarratorApplication both see only
        // the cleaned ids - NarratorApplication's own StoryConditionProcessor.ApplyTurn call is then
        // guaranteed to accept them.
        var (revealedVictoryIds, metVictoryIds) = NormalizeConditionIds(
            node, "revealedVictoryConditionIds", "metVictoryConditionIds", context.VictoryConditions);
        var (revealedLossIds, metLossIds) = NormalizeConditionIds(
            node, "revealedLossConditionIds", "metLossConditionIds", context.LossConditions);

        var updates = RequiredArray(node, "storyBibleUpdates");
        if (updates.Count > settings.ContentLimits.MaxStoryBibleUpdatesPerResponse)
            throw new JsonException("Too many Story Bible updates.");
        foreach (var item in updates)
        {
            var update = item as JsonObject ?? throw new JsonException("A Story Bible update must be an object.");
            RequireProperties(update, settings, "operation", "entryId", "entry");
            var operation = RequiredString(update, "operation");
            if (operation is not ("add" or "replace" or "remove"))
                throw new JsonException("A Story Bible update has an invalid operation.");
            // A stray entryId on an "add" is silently cleared rather than rejected - same reasoning as
            // the relevant-IDs leniency above: the model ignoring "always set entryId to null for add" is
            // a harmless formatting slip, not a data-corrupting mistake worth a corrective retry over.
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

        var plannedEventUpdates = RequiredArray(node, "plannedEventUpdates");
        if (plannedEventUpdates.Count > settings.ContentLimits.MaxPlannedEventUpdatesPerResponse)
            throw new JsonException("Too many Planned Event updates.");
        foreach (var item in plannedEventUpdates)
        {
            var update = item as JsonObject ?? throw new JsonException("A Planned Event update must be an object.");
            RequireProperties(update, settings, "operation", "entryId", "entry", "outcome");
            var operation = RequiredString(update, "operation");
            if (operation is not ("add" or "replace" or "remove"))
                throw new JsonException("A Planned Event update has an invalid operation.");
            if (operation == "add")
                update["entryId"] = null;
            if (operation != "add" && (update["entryId"] is null || !Guid.TryParse(StringValue(update["entryId"], "An entry ID"), out _)))
                throw new JsonException("A replace or remove Planned Event update requires an entry ID.");
            if (operation == "remove")
            {
                if (update["entry"] is not null) throw new JsonException("A remove update cannot contain a Planned Event.");
                var outcome = update["outcome"] is null ? null : RequiredString(update, "outcome");
                if (outcome is not ("fulfilled" or "abandoned"))
                    throw new JsonException("A Planned Event removal must state outcome as fulfilled or abandoned.");
            }
            else
            {
                // outcome only carries meaning for a remove update; clear a stray value on add/replace
                // the same way entryId is cleared on add above, rather than rejecting a harmless slip.
                update["outcome"] = null;
                ValidateProposedPlannedEvent(update["entry"] as JsonObject
                    ?? throw new JsonException("An add or replace Planned Event update requires an entry."), settings);
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
        var projectedRelevantPlannedEvents = opening
            ? dto.RelevantPlannedEventIds.Concat(context.PlannedEvents.Entries.Select(x => x.Id)).Distinct().ToArray()
            : dto.RelevantPlannedEventIds;
        _ = PlannedEventProcessor.Apply(
            context.PlannedEvents,
            projectedRelevantPlannedEvents,
            dto.PlannedEventUpdates,
            context.NextTurnNumber,
            settings.StoryGeneration);
        return new(
            dto.Narration,
            dto.SuggestedActions,
            dto.RelevantStoryBibleEntryIds,
            dto.StoryBibleUpdates,
            dto.RelevantPlannedEventIds,
            dto.PlannedEventUpdates,
            revealedVictoryIds,
            metVictoryIds,
            revealedLossIds,
            metLossIds,
            dto.StorySummary,
            meta?["responseId"]?.GetValue<string>(),
            meta?["inputTokens"]?.GetValue<int?>(),
            meta?["outputTokens"]?.GetValue<int?>());
    }

    private static NarrationDraft ParseNarrationDraft(JsonObject node, ApiConnectionSettings settings)
    {
        RequireProperties(node, settings, "narration", "suggestedActions");
        var narration = RequiredString(node, "narration");
        if (string.IsNullOrWhiteSpace(narration) || narration.Length > settings.ContentLimits.MaxNarrationCharacters)
            throw new JsonException("Narration is empty or exceeds the configured limit.");
        var actions = RequiredArray(node, "suggestedActions")
            .Select(item => StringValue(item, "A suggested action"))
            .ToArray();
        if (actions.Length < settings.ContentLimits.MinSuggestedActions || actions.Length > settings.ContentLimits.MaxSuggestedActions ||
            actions.Any(action => string.IsNullOrWhiteSpace(action) || action.Length > settings.ContentLimits.MaxSuggestedActionCharacters))
            throw new JsonException("Suggested actions do not meet the configured limits.");
        return new(narration, actions);
    }

    private sealed record NarrationDraft(string Narration, IReadOnlyList<string> SuggestedActions);

    private static void ValidateProposedEntry(JsonObject entry, ApiConnectionSettings settings)
    {
        RequireProperties(entry, settings, "category", "name", "knownFacts", "secretFacts", "importance");
        var category = RequiredString(entry, "category");
        var name = RequiredString(entry, "name");
        if (string.IsNullOrWhiteSpace(category) || category.Length > settings.ContentLimits.MaxStoryBibleCategoryCharacters)
            throw new JsonException("A Story Bible category is empty or exceeds the configured limit.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > settings.ContentLimits.MaxStoryBibleNameCharacters)
            throw new JsonException("A Story Bible name is empty or exceeds the configured limit.");
        var knownFacts = RequiredStringArray(entry, "knownFacts");
        var secretFacts = RequiredStringArray(entry, "secretFacts");
        if (knownFacts.Count == 0 && secretFacts.Count == 0)
            throw new JsonException("A Story Bible entry has no known or secret facts.");
        if (knownFacts.Any(string.IsNullOrWhiteSpace) || secretFacts.Any(string.IsNullOrWhiteSpace))
            throw new JsonException("A Story Bible entry has an empty fact.");
        var importance = RequiredInteger(entry, "importance");
        if (importance is < 1 or > 5) throw new JsonException("Story Bible importance must be from 1 to 5.");
    }

    private static void ValidateProposedPlannedEvent(JsonObject entry, ApiConnectionSettings settings)
    {
        RequireProperties(entry, settings, "description", "importance", "urgency", "condition");
        var description = RequiredString(entry, "description");
        if (string.IsNullOrWhiteSpace(description) || description.Length > settings.ContentLimits.MaxPlannedEventDescriptionCharacters)
            throw new JsonException("A Planned Event description is empty or exceeds the configured limit.");
        var importance = RequiredInteger(entry, "importance");
        if (importance is < 1 or > 5) throw new JsonException("Planned Event importance must be from 1 to 5.");
        var urgency = RequiredInteger(entry, "urgency");
        if (urgency is < 1 or > 5) throw new JsonException("Planned Event urgency must be from 1 to 5.");
        var condition = entry["condition"] is null ? null : StringValue(entry["condition"], "'condition'");
        if (condition is { Length: > 0 } && condition.Length > settings.ContentLimits.MaxPlannedEventConditionCharacters)
            throw new JsonException("A Planned Event condition exceeds the configured limit.");
    }

    private static void ValidateProposedCondition(JsonObject entry, ApiConnectionSettings settings)
    {
        RequireProperties(entry, settings, "description", "secret");
        var description = RequiredString(entry, "description");
        if (string.IsNullOrWhiteSpace(description) || description.Length > settings.ContentLimits.MaxConditionDescriptionCharacters)
            throw new JsonException("A condition description is empty or exceeds the configured limit.");
        RequiredBoolean(entry, "secret");
    }

    // Rewrites node's two named id-array properties in place with only the ids that are valid candidates
    // for revealed/met respectively - see the call sites' comments for what "valid" excludes and why.
    private static (IReadOnlyList<Guid> Revealed, IReadOnlyList<Guid> Met) NormalizeConditionIds(
        JsonObject node, string revealedProperty, string metProperty, ConditionsContext context)
    {
        var byId = context.Conditions.Entries.ToDictionary(x => x.Id);
        var alreadyRevealed = context.RevealedIds.ToHashSet();
        var alreadyMet = context.MetIds.ToHashSet();
        var revealCandidates = byId.Values
            .Where(x => !x.Secret && !alreadyRevealed.Contains(x.Id) && !alreadyMet.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSet();
        var metCandidates = byId.Keys.Where(id => !alreadyMet.Contains(id)).ToHashSet();

        var revealed = FilterKnownIds(RequiredArray(node, revealedProperty), revealCandidates);
        node[revealedProperty] = new JsonArray(revealed.Select(id => (JsonNode)id.ToString("D")).ToArray());
        var met = FilterKnownIds(RequiredArray(node, metProperty), metCandidates);
        node[metProperty] = new JsonArray(met.Select(id => (JsonNode)id.ToString("D")).ToArray());
        return (revealed, met);
    }

    private static IReadOnlyList<Guid> FilterKnownIds(JsonArray nodes, IReadOnlySet<Guid> candidateIds)
    {
        var seen = new HashSet<Guid>();
        var result = new List<Guid>();
        foreach (var idNode in nodes)
        {
            if (idNode is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                Guid.TryParse(text, out var id) &&
                candidateIds.Contains(id) &&
                seen.Add(id))
                result.Add(id);
        }
        return result;
    }

    private static void RequireProperties(JsonObject value, ApiConnectionSettings settings, params string[] expected)
    {
        var actual = value.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        // PromptedJson has no schema enforcing "no additional properties" like the strict tiers do, so a
        // model that adds one harmless extra property shouldn't fail the whole response and burn the one
        // corrective retry over it - only missing required properties matter for that tier.
        var ok = settings.Capabilities.StructuredOutputTier == StructuredOutputTier.PromptedJson
            ? expected.All(actual.Contains)
            : actual.SetEquals(expected);
        if (!ok)
            throw new JsonException($"Expected properties: {string.Join(", ", expected)}.");
    }

    private static JsonArray RequiredArray(JsonObject value, string name) =>
        value[name] as JsonArray ?? throw new JsonException($"'{name}' must be an array.");

    private static IReadOnlyList<string> RequiredStringArray(JsonObject value, string name) =>
        RequiredArray(value, name).Select(x => StringValue(x, $"An item in '{name}'")).ToArray();

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

    private static bool RequiredBoolean(JsonObject value, string name)
    {
        try { return value[name]?.GetValue<bool>() ?? throw new JsonException($"'{name}' must be a boolean."); }
        catch (InvalidOperationException ex) { throw new JsonException($"'{name}' must be a boolean.", ex); }
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
        },
        ["initialPlannedEvents"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = SettingsValidator.MaxPlannedEventsUpperBound,
            ["items"] = ProposedPlannedEventSchema(settings)
        },
        ["initialVictoryConditions"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = SettingsValidator.MaxConditionsUpperBound,
            ["items"] = ProposedConditionSchema(settings)
        },
        ["initialLossConditions"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = SettingsValidator.MaxConditionsUpperBound,
            ["items"] = ProposedConditionSchema(settings)
        }
    }, ["refinedStoryPrompt", "suggestedTitle", "initialEventsPrompt", "initialStoryBibleEntries", "initialPlannedEvents",
        "initialVictoryConditions", "initialLossConditions"]);

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
        },
        ["relevantPlannedEventIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["plannedEventUpdates"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = settings.ContentLimits.MaxPlannedEventUpdatesPerResponse,
            ["items"] = ObjectSchema(new Dictionary<string, JsonNode?>
            {
                ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("add", "replace", "remove") },
                ["entryId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "uuid" },
                ["entry"] = new JsonObject { ["anyOf"] = new JsonArray(ProposedPlannedEventSchema(settings), new JsonObject { ["type"] = "null" }) },
                ["outcome"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray("fulfilled", "abandoned", null) }
            }, ["operation", "entryId", "entry", "outcome"])
        },
        ["revealedVictoryConditionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["metVictoryConditionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["revealedLossConditionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["metLossConditionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
        ["storySummary"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxStorySummaryCharacters }
    }, ["turnNumber", "acknowledgedPlayerAction", "narration", "suggestedActions", "relevantStoryBibleEntryIds", "storyBibleUpdates",
        "relevantPlannedEventIds", "plannedEventUpdates",
        "revealedVictoryConditionIds", "metVictoryConditionIds", "revealedLossConditionIds", "metLossConditionIds", "storySummary"]);

    private static JsonObject StageSchema() => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["result"] = new JsonObject { ["type"] = "string", ["maxLength"] = 12000 }
    }, ["result"]);

    private static JsonObject NarrationDraftSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["narration"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxNarrationCharacters },
        ["suggestedActions"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = settings.ContentLimits.MinSuggestedActions,
            ["maxItems"] = settings.ContentLimits.MaxSuggestedActions,
            ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxSuggestedActionCharacters }
        }
    }, ["narration", "suggestedActions"]);

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

    // Some models, when not constrained by a strict JSON schema (json_object mode or the PromptedJson
    // fallback tier), mistakenly echo the request's field name (currentPlayerAction) as if it were the
    // response's field, instead of using the response's actual field name (acknowledgedPlayerAction) -
    // observed in practice even when the copied text itself is exactly correct. Folding that fallback in
    // here, before RequireProperties runs (which would otherwise reject the response outright for
    // missing acknowledgedPlayerAction and/or carrying an unexpected extra property), is far cheaper
    // than spending the one corrective retry on a mistake the model is likely to just repeat verbatim.
    private static void NormalizeAcknowledgedPlayerActionField(JsonObject node)
    {
        if (!node.ContainsKey("acknowledgedPlayerAction") &&
            node["currentPlayerAction"] is JsonValue value &&
            value.TryGetValue<string>(out var text))
            node["acknowledgedPlayerAction"] = text;
        node.Remove("currentPlayerAction");
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
        ["knownFacts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        ["secretFacts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        ["importance"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5 }
    }, ["category", "name", "knownFacts", "secretFacts", "importance"]);

    private static JsonObject ProposedPlannedEventSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["description"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxPlannedEventDescriptionCharacters },
        ["importance"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5 },
        ["urgency"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5 },
        // Freeform prose describing what must happen, or what state the story must be in, before this
        // event can be pursued - not a structured reference to another entry. Nullable: most events have
        // no prerequisite.
        ["condition"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = settings.ContentLimits.MaxPlannedEventConditionCharacters }
    }, ["description", "importance", "urgency", "condition"]);

    private static JsonObject ProposedConditionSchema(ApiConnectionSettings settings) => ObjectSchema(new Dictionary<string, JsonNode?>
    {
        ["description"] = new JsonObject { ["type"] = "string", ["maxLength"] = settings.ContentLimits.MaxConditionDescriptionCharacters },
        ["secret"] = new JsonObject { ["type"] = "boolean" }
    }, ["description", "secret"]);

    // Weak models handling the PromptedJson fallback tier tend to follow a concrete example far more
    // reliably than raw JSON Schema syntax (nullable-as-type-array, anyOf, format:uuid). This walks any
    // of our schemas generically to synthesize a structurally-correct (not semantically meaningful)
    // example instance to show alongside the schema.
    private static JsonNode? ExampleFor(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return null;
        if (obj["enum"] is JsonArray { Count: > 0 } enumValues) return enumValues[0]?.DeepClone();
        if (obj["anyOf"] is JsonArray anyOf)
            return ExampleFor(anyOf.FirstOrDefault(x => (x as JsonObject)?["type"]?.GetValue<string>() != "null") ?? anyOf.FirstOrDefault());
        var type = obj["type"] switch
        {
            JsonArray typeArray => typeArray.Select(x => x?.GetValue<string>()).FirstOrDefault(x => x != "null"),
            JsonValue value => value.GetValue<string>(),
            _ => null
        };
        return type switch
        {
            "object" => ExampleObject(obj),
            "array" => new JsonArray(ExampleFor(obj["items"])),
            "string" => JsonValue.Create(obj["format"]?.GetValue<string>() == "uuid" ? Guid.Empty.ToString() : "string"),
            "integer" => JsonValue.Create(obj["minimum"]?.GetValue<int?>() ?? 0),
            "number" => JsonValue.Create(obj["minimum"]?.GetValue<double?>() ?? 0),
            "boolean" => JsonValue.Create(true),
            _ => null
        };
    }

    private static JsonObject ExampleObject(JsonObject schema)
    {
        var result = new JsonObject();
        if (schema["properties"] is JsonObject properties)
            foreach (var (key, value) in properties)
                result[key] = ExampleFor(value);
        return result;
    }

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
        IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates,
        IReadOnlyList<Guid> RelevantPlannedEventIds,
        IReadOnlyList<ProposedPlannedEventUpdate> PlannedEventUpdates,
        IReadOnlyList<Guid> RevealedVictoryConditionIds,
        IReadOnlyList<Guid> MetVictoryConditionIds,
        IReadOnlyList<Guid> RevealedLossConditionIds,
        IReadOnlyList<Guid> MetLossConditionIds,
        string StorySummary);
}

public sealed class ProviderException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
