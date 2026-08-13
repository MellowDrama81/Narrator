using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            """{"refinedStoryPrompt":"PRIVATE RESPONSE TOP-SECRET-KEY","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"private","name":"Fact","knownFacts":["PRIVATE RESPONSE TOP-SECRET-KEY"],"secretFacts":[],"importance":4}],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}""")));
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
                    """{"data":[{"id":"model-b"},{"id":"text-embedding-3-small"},{"id":"model-a"},{"id":"model-b"}]}""",
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
            var content = """{"refinedStoryPrompt":"A red moon story.","suggestedTitle":"Red Moon","initialEventsPrompt":"The moon glows ominously overhead.","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["The moon is red."],"secretFacts":[],"importance":4}],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}""";
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
        Assert.Contains("Story Definition Prompt", body);
        Assert.Contains("source material for generating the entire Story", body);
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
                ? """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":9}],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}"""
                : """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":4}],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}""";
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
            var content = """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[],"note":"unexpected extra field"}""";
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
            return Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}""");
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var settings = Settings();

        await provider.GenerateStoryDefinitionAsync(settings, null, "Story prompt");

        Assert.Contains("Story Definition Prompt", body);
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
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":["{{{entry.Id}}}","{{{entry.Id}}}","{{{unknown}}}","not-a-uuid",42,null],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([entry]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([entry]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
    public async Task GenerateTurn_ParsesConditionIdArrays()
    {
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var loss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":["{{{victory.Id}}}"],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":["{{{loss.Id}}}"],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(new([victory]), [], []),
            new(new([loss]), [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(victory.Id, Assert.Single(result.RevealedVictoryConditionIds));
        Assert.Empty(result.MetVictoryConditionIds);
        Assert.Empty(result.RevealedLossConditionIds);
        Assert.Equal(loss.Id, Assert.Single(result.MetLossConditionIds));
    }

    [Fact]
    public async Task GenerateOpening_ParsesConditionIdArrays()
    {
        var victory = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":["{{{victory.Id}}}"],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(new([victory]), [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            null,
            0);

        var result = await provider.GenerateOpeningAsync(Settings(), null, context);

        Assert.Equal(victory.Id, Assert.Single(result.RevealedVictoryConditionIds));
        Assert.Empty(result.MetVictoryConditionIds);
        Assert.Empty(result.RevealedLossConditionIds);
        Assert.Empty(result.MetLossConditionIds);
    }

    [Fact]
    public async Task GenerateOpening_RoundTripsAConditionOnANewPlannedEvent()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[{"operation":"add","entryId":null,"entry":{"description":"The tower falls.","importance":3,"urgency":3,"condition":"The hero must reach the tower first."},"outcome":null}],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            null,
            0);

        var result = await provider.GenerateOpeningAsync(Settings(), null, context);

        var update = Assert.Single(result.PlannedEventUpdates);
        Assert.Equal("The hero must reach the tower first.", update.Entry!.Condition);
    }

    [Fact]
    public async Task GenerateOpening_RoundTripsReturnedStorySummary()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":"A hero arrives in a quiet village."}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            null,
            0);

        var result = await provider.GenerateOpeningAsync(Settings(), null, context);

        Assert.Equal("A hero arrives in a quiet village.", result.StorySummary);
    }

    [Fact]
    public async Task GenerateTurn_RoundTripsReturnedStorySummary()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":"The hero left the village and reached the forest."}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "A hero arrives in a quiet village.",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal("The hero left the village and reached the forest.", result.StorySummary);
    }

    [Fact]
    public async Task GenerateTurn_SilentlyDropsDuplicateAndUnknownConditionIdsWithoutRetry()
    {
        var known = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var unknown = Guid.NewGuid();
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":["{{{known.Id}}}","{{{known.Id}}}","{{{unknown}}}","not-a-uuid",42,null],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(new([known]), [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(1, requests);
        Assert.Equal(known.Id, Assert.Single(result.RevealedVictoryConditionIds));
    }

    [Fact]
    public async Task GenerateTurn_SilentlyDropsAttemptToRevealASecretConditionWithoutRetry()
    {
        var secret = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":["{{{secret.Id}}}"],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(new([secret]), [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        // A secret condition can only ever be reported met, never revealed - the attempt is dropped
        // silently, like an unknown/duplicate id, rather than triggering the corrective-retry path.
        Assert.Equal(1, requests);
        Assert.Empty(result.RevealedLossConditionIds);
    }

    [Fact]
    public async Task GenerateTurn_SilentlyDropsAlreadyRevealedAndAlreadyMetConditionReportsWithoutRetry()
    {
        var revealedAgain = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var metAgain = new StoryCondition(Guid.NewGuid(), "Rescue the princess.", false);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":["{{{revealedAgain.Id}}}"],"metVictoryConditionIds":["{{{metAgain.Id}}}"],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(new([revealedAgain, metAgain]), [revealedAgain.Id], [metAgain.Id]),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(1, requests);
        Assert.Empty(result.RevealedVictoryConditionIds);
        Assert.Empty(result.MetVictoryConditionIds);
    }

    [Fact]
    public async Task GenerateTurn_IncludesVictoryAndLossConditionsInTheStoryContextMessageExcludingAlreadyMetOnes()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var unrevealed = new StoryCondition(Guid.NewGuid(), "Defeat the dragon.", false);
        var revealed = new StoryCondition(Guid.NewGuid(), "Rescue the princess.", false);
        var alreadyMet = new StoryCondition(Guid.NewGuid(), "Find the sword.", false);
        var secretLoss = new StoryCondition(Guid.NewGuid(), "The kingdom falls.", true);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(new([unrevealed, revealed, alreadyMet]), [revealed.Id], [alreadyMet.Id]),
            new(new([secretLoss]), [], []),
            "",
            [],
            "Continue",
            1);

        await provider.GenerateTurnAsync(Settings(), null, context);

        var message = StoryContextMessage(body!);
        var victoryConditions = message["victoryConditions"]!.AsArray();
        // The already-met condition is dropped entirely - nothing left to evaluate for it - while the
        // other two remain, each annotated with whether it has already been revealed.
        Assert.Equal(2, victoryConditions.Count);
        Assert.DoesNotContain(victoryConditions, x => x!["id"]!.GetValue<string>() == alreadyMet.Id.ToString());
        var unrevealedPayload = Assert.Single(victoryConditions, x => x!["id"]!.GetValue<string>() == unrevealed.Id.ToString())!;
        Assert.Equal("Defeat the dragon.", unrevealedPayload["description"]!.GetValue<string>());
        Assert.False(unrevealedPayload["secret"]!.GetValue<bool>());
        Assert.False(unrevealedPayload["revealed"]!.GetValue<bool>());
        var revealedPayload = Assert.Single(victoryConditions, x => x!["id"]!.GetValue<string>() == revealed.Id.ToString())!;
        Assert.True(revealedPayload["revealed"]!.GetValue<bool>());

        var lossConditions = message["lossConditions"]!.AsArray();
        var lossPayload = Assert.Single(lossConditions)!;
        Assert.Equal(secretLoss.Id.ToString(), lossPayload["id"]!.GetValue<string>());
        Assert.True(lossPayload["secret"]!.GetValue<bool>());
        Assert.False(lossPayload["revealed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GenerateTurn_IncludesPlannedEventsInTheStoryContextMessage()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var plannedEvent = new PlannedEvent(Guid.NewGuid(), "The bridge must collapse.", 5, 3, "The scout has arrived.", 0);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            new([plannedEvent]),
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Contains("The bridge must collapse.", body);
        Assert.Contains("plannedEvents", body);
        var eventPayload = Assert.Single(StoryContextMessage(body!)["plannedEvents"]!.AsArray())!;
        Assert.Equal("requires condition verification before eligible", eventPayload["availability"]!.GetValue<string>());
        var capacity = StoryContextMessage(body!)["plannedEventCapacity"]!;
        Assert.Equal(1, capacity["count"]!.GetValue<int>());
        Assert.Equal(50, capacity["max"]!.GetValue<int>());
        Assert.Equal(49, capacity["remaining"]!.GetValue<int>());
        Assert.Equal(2, capacity["usedPercent"]!.GetValue<int>());
        Assert.Equal(80, capacity["warningPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task GenerateTurn_IncludesTheCurrentStorySummaryInTheStoryContextMessage()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "The hero arrives in a quiet village.",
            [],
            "Continue",
            1);

        await provider.GenerateTurnAsync(Settings(), null, context);

        var message = StoryContextMessage(body!);
        Assert.Equal("The hero arrives in a quiet village.", message["storySummary"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateTurn_RejectsAResponseThatAcknowledgesADifferentPlayerAction()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response("""
            {"turnNumber":1,"acknowledgedPlayerAction":"Something else entirely","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Search for a light",
            1);

        var exception = await Assert.ThrowsAsync<JsonException>(() => provider.GenerateTurnAsync(Settings(), null, context));
        Assert.Contains("must set acknowledgedPlayerAction", exception.Message);
    }

    // Some models, when not constrained by a strict JSON schema (json_object mode or the PromptedJson
    // fallback tier), mistakenly echo the request's field name (currentPlayerAction) instead of the
    // response's actual field name (acknowledgedPlayerAction) - observed in practice against a real
    // provider even when the copied text itself was exactly correct.
    [Fact]
    public async Task GenerateTurn_AcceptsCurrentPlayerActionAsFallbackWhenAcknowledgedPlayerActionIsMissing()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response("""
            {"turnNumber":1,"currentPlayerAction":"Search for a light","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Search for a light",
            1);

        var response = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal("Scene", response.Narration);
    }

    [Fact]
    public async Task GenerateOpening_SendsAnEmptyStorySummaryInTheStoryContextMessage()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            null,
            0);

        await provider.GenerateOpeningAsync(Settings(), null, context);

        var message = StoryContextMessage(body!);
        Assert.Equal("", message["storySummary"]!.GetValue<string>());
    }

    // The storyContext message is a JSON object serialized into a chat message's string `content`
    // field, so it's escaped one level deep inside the outer request body - parse both levels rather
    // than pattern-matching the escaped text, which is fragile against harmless formatting changes.
    private static JsonObject StoryContextMessage(string requestBody)
    {
        var messages = JsonNode.Parse(requestBody)!.AsObject()["messages"]!.AsArray();
        foreach (var message in messages)
        {
            var content = message!["content"]!.GetValue<string>();
            // Most messages (the system instruction, narration history) are plain text, not JSON - only
            // the structured context/request messages parse at all, so a failed parse just means "skip".
            JsonObject? parsed;
            try { parsed = JsonNode.Parse(content) as JsonObject; }
            catch (JsonException) { continue; }
            if (parsed?["contextType"]?.GetValue<string>() == "storyContext") return parsed;
        }
        throw new InvalidOperationException("No storyContext message was found in the request body.");
    }

    [Fact]
    public async Task GenerateTurn_ParsesPlannedEventUpdatesAndRelevantIds()
    {
        var existing = new PlannedEvent(Guid.NewGuid(), "Existing plot point.", 3, 3, null, 0);
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":["{{{existing.Id}}}"],"plannedEventUpdates":[{"operation":"add","entryId":null,"entry":{"description":"A new complication.","importance":2,"urgency":4,"condition":"The bridge must have already fallen."},"outcome":null}],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            new([existing]),
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        Assert.Equal(existing.Id, Assert.Single(result.RelevantPlannedEventIds));
        var update = Assert.Single(result.PlannedEventUpdates);
        Assert.Equal(PlannedEventOperation.Add, update.Operation);
        Assert.Equal("A new complication.", update.Entry!.Description);
        Assert.Equal(2, update.Entry.Importance);
        Assert.Equal(4, update.Entry.Urgency);
        Assert.Equal("The bridge must have already fallen.", update.Entry.Condition);
    }

    [Fact]
    public async Task GenerateTurn_AcceptsANullConditionOnAPlannedEventUpdate()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[{"operation":"add","entryId":null,"entry":{"description":"A new complication.","importance":2,"urgency":4,"condition":null},"outcome":null}],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        var result = await provider.GenerateTurnAsync(Settings(), null, context);

        var update = Assert.Single(result.PlannedEventUpdates);
        Assert.Null(update.Entry!.Condition);
    }

    [Fact]
    public async Task GenerateTurn_RejectsPlannedEventRemovalMissingOutcome()
    {
        var existing = new PlannedEvent(Guid.NewGuid(), "Existing plot point.", 3, 3, null, 0);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response($$$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[{"operation":"remove","entryId":"{{{existing.Id}}}","entry":null,"outcome":null}],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            new([existing]),
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        // The stub always returns the same invalid payload, so the one automatic corrective retry also
        // fails validation and the exception from that second attempt propagates instead of being retried again.
        await Assert.ThrowsAsync<JsonException>(() => provider.GenerateTurnAsync(Settings(), null, context));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GenerateTurn_RejectsPlannedEventRemovalWithInvalidOutcome()
    {
        var existing = new PlannedEvent(Guid.NewGuid(), "Existing plot point.", 3, 3, null, 0);
        var handler = new StubHandler(_ => Task.FromResult(Response($$$"""
            {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[{"operation":"remove","entryId":"{{{existing.Id}}}","entry":null,"outcome":"maybe"}],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            new([existing]),
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        await Assert.ThrowsAsync<JsonException>(() => provider.GenerateTurnAsync(Settings(), null, context));
    }

    [Fact]
    public async Task GenerateTurn_RejectsAResponseMissingStorySummary()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[]}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        // The stub always omits storySummary, so the one automatic corrective retry also fails
        // validation and the exception from that second attempt propagates instead of being retried again.
        await Assert.ThrowsAsync<JsonException>(() => provider.GenerateTurnAsync(Settings(), null, context));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GenerateTurn_RejectsAStorySummaryExceedingTheConfiguredLimit()
    {
        var settings = Settings();
        var oversized = new string('x', settings.ContentLimits.MaxStorySummaryCharacters + 1);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(Response($$"""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":"{{oversized}}"}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Continue",
            1);

        await Assert.ThrowsAsync<JsonException>(() => provider.GenerateTurnAsync(settings, null, context));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GenerateDefinition_ParsesInitialPlannedEvents()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response(
            """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[{"description":"The tower must fall.","importance":5,"urgency":2,"condition":"The hero must reach the tower first."}],"initialVictoryConditions":[],"initialLossConditions":[]}""")));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var result = await provider.GenerateStoryDefinitionAsync(Settings(), null, "Story");

        var plannedEvent = Assert.Single(result.InitialPlannedEvents);
        Assert.Equal("The tower must fall.", plannedEvent.Description);
        Assert.Equal(5, plannedEvent.Importance);
        Assert.Equal(2, plannedEvent.Urgency);
        Assert.Equal("The hero must reach the tower first.", plannedEvent.Condition);
    }

    [Fact]
    public async Task GenerateDefinition_AcceptsANullConditionOnAnInitialPlannedEvent()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response(
            """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[{"description":"The tower must fall.","importance":5,"urgency":2,"condition":null}],"initialVictoryConditions":[],"initialLossConditions":[]}""")));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var result = await provider.GenerateStoryDefinitionAsync(Settings(), null, "Story");

        Assert.Null(Assert.Single(result.InitialPlannedEvents).Condition);
    }

    [Fact]
    public async Task GenerateDefinition_ParsesInitialVictoryAndLossConditions()
    {
        var handler = new StubHandler(_ => Task.FromResult(Response(
            """{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[],"initialVictoryConditions":[{"description":"Defeat the dragon.","secret":false}],"initialLossConditions":[{"description":"The kingdom falls.","secret":true}]}""")));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);

        var result = await provider.GenerateStoryDefinitionAsync(Settings(), null, "Story");

        var victory = Assert.Single(result.InitialVictoryConditions);
        Assert.Equal("Defeat the dragon.", victory.Description);
        Assert.False(victory.Secret);
        var loss = Assert.Single(result.InitialLossConditions);
        Assert.Equal("The kingdom falls.", loss.Description);
        Assert.True(loss.Secret);
    }

    [Fact]
    public async Task GenerateTurn_SubstitutesConfiguredSuggestedActionCountIntoPromptAndSchema()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B","C","D"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
    public async Task GenerateTurn_IncludesRandomResolutionRollAndDifficultyRules()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":1,"acknowledgedPlayerAction":"Leap over the hole","narration":"You catch the far edge.","suggestedActions":["Pull yourself up","Call for help"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Leap over the hole",
            1);

        await provider.GenerateTurnAsync(Settings(), null, context);

        using var request = JsonDocument.Parse(body!);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Contains("Choose the difficulty before considering resolutionRoll",
            messages[0].GetProperty("content").GetString());
        Assert.Contains("ordinary human attempting to levitate",
            messages[0].GetProperty("content").GetString());
        Assert.Contains("the player controls only their own character",
            messages[0].GetProperty("content").GetString());
        Assert.Contains("the guard gives me the key",
            messages[0].GetProperty("content").GetString());

        string? action = null;
        int? roll = null;
        foreach (var message in messages.EnumerateArray())
        {
            var content = message.GetProperty("content").GetString();
            if (content is null || !content.StartsWith('{')) continue;
            using var candidate = JsonDocument.Parse(content);
            if (!candidate.RootElement.TryGetProperty("currentPlayerAction", out var currentAction)) continue;
            action = currentAction.GetString();
            roll = candidate.RootElement.GetProperty("resolutionRoll").GetInt32();
        }

        Assert.Equal("Leap over the hole", action);
        Assert.InRange(roll!.Value, 1, 100);
    }

    [Fact]
    public async Task GenerateTurn_IncludesInitialEventsPromptWhileRecentTurnsWindowIsNotFull()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response("""
                {"turnNumber":2,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", "The village is under curfew.", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var recent = new StoryTurn(Guid.NewGuid(), Guid.NewGuid(), 1, "Wait", "Nothing happens.", [], [], [], [], [], [], [], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));
        var context = new GenerationContext(definition, new([]), PlannedEvents.Empty, new(StoryConditions.Empty, [], []), new(StoryConditions.Empty, [], []), "", [recent], "Continue", 2);
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
                {"turnNumber":2,"acknowledgedPlayerAction":"Continue","narration":"Scene","suggestedActions":["A","B"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var definition = new StoryDefinitionSnapshot("Story", "Prompt", "The village is under curfew.", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty);
        var recent = new StoryTurn(Guid.NewGuid(), Guid.NewGuid(), 1, "Wait", "Nothing happens.", [], [], [], [], [], [], [], [], [], DateTimeOffset.UtcNow, new("model", null, null, null));
        var context = new GenerationContext(definition, new([]), PlannedEvents.Empty, new(StoryConditions.Empty, [], []), new(StoryConditions.Empty, [], []), "", [recent], "Continue", 2);
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
                {"turnNumber":2,"acknowledgedPlayerAction":"Current action","narration":"A new scene unfolds.","suggestedActions":["Only one"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
                {"turnNumber":2,"acknowledgedPlayerAction":"{{acknowledged}}","narration":"A new scene unfolds.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
                {"turnNumber":2,"acknowledgedPlayerAction":"  Current   action.  ","narration":"A new scene unfolds.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
                {"turnNumber":2,"acknowledgedPlayerAction":"Choose another path","narration":"{{narration}}","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
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
            [],
            [],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow,
            new("model", null, null, null));
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
            {"turnNumber":0,"acknowledgedPlayerAction":null,"narration":"The story begins.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
            """)));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
                {"turnNumber":1,"acknowledgedPlayerAction":"Continue","narration":"A new place appears.","suggestedActions":["Continue","Wait"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[{"operation":"add","entryId":"{{{modelCreatedId}}}","entry":{"category":"location","name":"New Place","knownFacts":["A newly discovered place."],"secretFacts":[],"importance":3}}],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":""}
                """);
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", new([]), PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            new([]),
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
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
                : Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}"""));
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
            return Response("""{"refinedStoryPrompt":"Story","suggestedTitle":"Title","initialEventsPrompt":"","initialStoryBibleEntries":[{"category":"world","name":"Moon","knownFacts":["Red"],"secretFacts":[],"importance":4}],"initialPlannedEvents":[],"initialVictoryConditions":[],"initialLossConditions":[]}""");
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

    [Fact]
    public async Task GenerateTurn_UsesFourCallPipelineAndReturnsNarrationDraft()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var content = requests switch
            {
                1 => """{"result":"The action succeeds; conditional events remain blocked."}""",
                2 => """{"result":"The door opens, revealing a choice at the stairs."}""",
                3 => """{"narration":"The door gives beneath your hand. Cold air rises from the stairwell beyond.","suggestedActions":["Descend the stairs","Listen at the threshold"]}""",
                4 => """{"turnNumber":1,"acknowledgedPlayerAction":"Open the door","narration":"This must be replaced by the narration draft.","suggestedActions":["This must be replaced","This must also be replaced"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":"The door has opened."}""",
                _ => throw new InvalidOperationException("The pipeline made an unexpected extra request.")
            };
            return Task.FromResult(Response(content));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            StoryBible.Empty,
            PlannedEvents.Empty,
            new(StoryConditions.Empty, [], []),
            new(StoryConditions.Empty, [], []),
            "",
            [],
            "Open the door",
            1);

        var result = await provider.GenerateTurnAsync(Settings() with { TurnPipeline = TurnPipelineMode.FourCalls }, null, context);

        Assert.Equal(4, requests);
        Assert.Equal("The door gives beneath your hand. Cold air rises from the stairwell beyond.", result.Narration);
        Assert.Equal(["Descend the stairs", "Listen at the threshold"], result.SuggestedActions);
        Assert.Equal("The door has opened.", result.StorySummary);
    }

    [Fact]
    public async Task GenerateTurn_UsesEightCallPipelineAndReturnsRevisedNarration()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var content = requests switch
            {
                3 => """{"narration":"The door gives beneath your hand.","suggestedActions":["Descend the stairs","Listen at the threshold"]}""",
                7 => """{"turnNumber":1,"acknowledgedPlayerAction":"Open the door","narration":"Placeholder","suggestedActions":["Placeholder"],"relevantStoryBibleEntryIds":[],"storyBibleUpdates":[],"relevantPlannedEventIds":[],"plannedEventUpdates":[],"revealedVictoryConditionIds":[],"metVictoryConditionIds":[],"revealedLossConditionIds":[],"metLossConditionIds":[],"storySummary":"The door has opened."}""",
                8 => """{"narration":"The door yields, and cold air rises from the stairwell.","suggestedActions":["Descend the stairs","Listen at the threshold"]}""",
                _ => """{"result":"Internal analysis."}"""
            };
            return Task.FromResult(Response(content));
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), TimeProvider.System);
        var context = new GenerationContext(
            new("Story", "Prompt", "", StoryBible.Empty, PlannedEvents.Empty, StoryConditions.Empty, StoryConditions.Empty),
            StoryBible.Empty, PlannedEvents.Empty, new(StoryConditions.Empty, [], []), new(StoryConditions.Empty, [], []),
            "", [], "Open the door", 1);

        var result = await provider.GenerateTurnAsync(Settings() with { TurnPipeline = TurnPipelineMode.EightCalls }, null, context);

        Assert.Equal(8, requests);
        Assert.Equal("The door yields, and cold air rises from the stairwell.", result.Narration);
    }

    private static ApiConnectionSettings Settings() => NarratorDefaults.Create() with
    {
        BaseUrl = new("https://example.test/v1/"),
        ModelId = "story-model",
        Capabilities = new(false, StructuredOutputTier.PromptedJson, "story-model", DateTimeOffset.UtcNow),
        TurnPipeline = TurnPipelineMode.OneCall
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
