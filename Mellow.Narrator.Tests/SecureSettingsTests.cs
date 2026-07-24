using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class SecureSettingsTests
{
    [Fact]
    public async Task CredentialPresence_ReportsSavedKeyWithoutReturningIt()
    {
        var secure = new RecordingSecureStorage("stored-secret");
        var app = CreateApplication(
            new FailingSettingsStore(ConfiguredSettings("model")),
            secure,
            new ApiConnectionCoordinator());

        var result = await app.HasApiCredentialAsync();

        Assert.True(result);
        Assert.Equal([$"get:{SecureStorageKeys.ApiCredential}"], secure.Operations);
    }

    [Fact]
    public async Task FailedSettingsSave_RestoresPreviousCredential()
    {
        var original = ConfiguredSettings("old-model");
        var store = new FailingSettingsStore(original);
        var secure = new RecordingSecureStorage("old-secret");
        var coordinator = new ApiConnectionCoordinator();
        var app = CreateApplication(store, secure, coordinator);

        await Assert.ThrowsAsync<IOException>(() =>
            app.SaveSettingsAsync(ConfiguredSettings("new-model"), "new-secret"));

        Assert.Equal("old-secret", secure.Value);
        Assert.Equal(
            [
                $"get:{SecureStorageKeys.ApiCredential}",
                $"set:{SecureStorageKeys.ApiCredential}:new-secret",
                $"set:{SecureStorageKeys.ApiCredential}:old-secret"
            ],
            secure.Operations);
        Assert.False(coordinator.RequiresCredentialReentry);
        Assert.Equal(original, await store.LoadAsync());
    }

    [Fact]
    public async Task FailedCredentialRollback_BlocksProviderRequestsUntilCredentialIsSavedAgain()
    {
        var store = new FailingSettingsStore(ConfiguredSettings("old-model"));
        var secure = new RecordingSecureStorage("old-secret") { FailSecondSet = true };
        var coordinator = new ApiConnectionCoordinator();
        var provider = new RecordingProvider();
        var app = CreateApplication(store, secure, coordinator, provider);

        var error = await Assert.ThrowsAsync<NarratorException>(() =>
            app.SaveSettingsAsync(ConfiguredSettings("new-model"), "new-secret"));

        Assert.Contains("re-enter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.RequiresCredentialReentry);
        await Assert.ThrowsAsync<NarratorException>(() => app.TestConnectionAsync());
        Assert.Equal(0, provider.ConnectionTests);
    }

    private static NarratorApplication CreateApplication(
        IApiConnectionSettingsStore settings,
        ISecureStorageService secureStorage,
        ApiConnectionCoordinator coordinator,
        ILanguageModelProvider? provider = null) =>
        new(
            new EmptyDefinitions(),
            new EmptyStates(),
            settings,
            secureStorage,
            provider ?? new RecordingProvider(),
            TimeProvider.System,
            coordinator,
            new StoryRequestCoordinator(),
            new SystemIdGenerator());

    private static ApiConnectionSettings ConfiguredSettings(string model) => NarratorDefaults.Create() with
    {
        BaseUrl = new("https://example.test/v1"),
        ModelId = model,
        Capabilities = new(false, StructuredOutputTier.PromptedJson, model, DateTimeOffset.UtcNow)
    };

    private sealed class FailingSettingsStore(ApiConnectionSettings value) : IApiConnectionSettingsStore
    {
        public Task<ApiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task SaveAsync(ApiConnectionSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated settings failure."));
    }

    private sealed class RecordingSecureStorage(string? value) : ISecureStorageService
    {
        private int _setCount;
        public string? Value { get; private set; } = value;
        public bool FailSecondSet { get; init; }
        public List<string> Operations { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            Operations.Add($"get:{key}");
            return Task.FromResult(Value);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Operations.Add($"set:{key}:{value}");
            if (FailSecondSet && ++_setCount == 2)
                return Task.FromException(new IOException("Simulated secure-storage rollback failure."));
            Value = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Operations.Add($"remove:{key}");
            var existed = Value is not null;
            Value = null;
            return Task.FromResult(existed);
        }
    }

    private sealed class RecordingProvider : ILanguageModelProvider
    {
        public int ConnectionTests { get; private set; }

        public Task<IReadOnlyList<string>> DiscoverModelsAsync(
            ApiConnectionSettings settings,
            string? credential,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ConnectionTestResult> TestConnectionAsync(
            ApiConnectionSettings settings,
            string? credential,
            CancellationToken cancellationToken = default)
        {
            ConnectionTests++;
            return Task.FromResult(new ConnectionTestResult(true, [], settings.Capabilities, null));
        }

        public Task<PlayerAnswerValidationResponse> ValidatePlayerAnswerAsync(
            ApiConnectionSettings settings, string? credential, PlayerQuestion question, string answer,
            IReadOnlyList<PlayerResponse> previousAnswers, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoryDefinitionGenerationResponse> GenerateStoryDefinitionAsync(
            ApiConnectionSettings settings, string? credential, string storyPrompt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoryGenerationResponse> GenerateOpeningAsync(
            ApiConnectionSettings settings, string? credential, GenerationContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoryGenerationResponse> GenerateTurnAsync(
            ApiConnectionSettings settings, string? credential, GenerationContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyDefinitions : IStoryDefinitionRepository
    {
        public Task<IReadOnlyList<StoryDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoryDefinitionSummary>>([]);
        public Task<StoryDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoryDefinition?>(null);
        public Task SaveAsync(StoryDefinition definition, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyStates : IStoryStateRepository
    {
        public Task<IReadOnlyList<StoryStateSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoryStateSummary>>([]);
        public Task<StoryState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoryState?>(null);
        public Task<IReadOnlyList<StoryTurn>> GetTurnsAsync(Guid id, int? takeLast = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoryTurn>>([]);
        public Task<StoryStateAggregateSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoryStateAggregateSnapshot?>(null);
        public Task CreateAsync(StoryState state, StoryTurn openingTurn, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ImportAsync(StoryState state, IReadOnlyList<StoryTurn> turns, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CommitTurnAsync(StoryState state, StoryTurn turn, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SaveAsync(StoryState state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UpdateLabelAsync(Guid id, string label, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SwapSortOrderAsync(Guid firstId, Guid secondId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoryState> CopyAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MoveToTrashAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
