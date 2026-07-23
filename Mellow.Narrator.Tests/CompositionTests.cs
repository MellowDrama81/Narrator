using Mellow.Narrator.Core;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Mellow.Narrator.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void CoreProviderAndPersistenceComposition_ResolvesSharedImplementations()
    {
        var root = Path.Combine(Path.GetTempPath(), "mellow-narrator-composition", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddMellowNarratorCore()
                .AddMellowNarratorOpenAiCompatible()
                .AddMellowNarratorPersistence(new(root));
            services.AddSingleton<ISecureStorageService, EmptySecureStorage>();
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<INarratorApplication>());
            Assert.IsType<OpenAiCompatibleProvider>(provider.GetRequiredService<ILanguageModelProvider>());
            var definitions = provider.GetRequiredService<IStoryDefinitionRepository>();
            var states = provider.GetRequiredService<IStoryStateRepository>();
            Assert.Same(
                provider.GetRequiredService<JsonNarratorStore>(),
                Assert.IsType<JsonNarratorStore>(definitions));
            Assert.Same(provider.GetRequiredService<JsonNarratorStore>(), Assert.IsType<JsonNarratorStore>(states));
            Assert.NotNull(provider.GetRequiredService<IRecoveryNoticeStore>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class EmptySecureStorage : ISecureStorageService
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
