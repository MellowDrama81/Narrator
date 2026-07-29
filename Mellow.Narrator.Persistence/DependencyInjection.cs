using Mellow.Narrator.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mellow.Narrator.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddMellowNarratorPersistence(this IServiceCollection services, PersistenceOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<NarratorFileLoggerProvider>();
        services.AddSingleton<INarratorLogLevelSwitch>(x => x.GetRequiredService<NarratorFileLoggerProvider>());
        services.AddSingleton<ILoggerProvider>(x => x.GetRequiredService<NarratorFileLoggerProvider>());
        // Scoped to NarratorFileLoggerProvider specifically, not a global SetMinimumLevel(Trace): that
        // would let every registered ILoggerProvider receive unfiltered Trace-level logs, including raw
        // HTTP traffic that can carry the provider's bearer credential. NarratorFileLoggerProvider does
        // its own per-category/per-configured-level filtering in IsEnabled; this filter only needs to
        // stop the framework from pre-filtering Trace/Debug calls before they reach it.
        services.AddLogging(logging => logging.AddFilter<NarratorFileLoggerProvider>(null, LogLevel.Trace));
        services.AddSingleton<JsonNarratorStore>();
        services.AddSingleton<IStoryDefinitionRepository>(x => x.GetRequiredService<JsonNarratorStore>());
        services.AddSingleton<IStoryStateRepository>(x => x.GetRequiredService<JsonNarratorStore>());
        services.AddSingleton<IWorkspaceStateStore>(x => x.GetRequiredService<JsonNarratorStore>());
        services.AddSingleton<IApiConnectionSettingsStore>(x => x.GetRequiredService<JsonNarratorStore>());
        services.AddSingleton<ITrashStore>(x => x.GetRequiredService<JsonNarratorStore>());
        services.AddSingleton<IRecoveryNoticeStore>(x => x.GetRequiredService<JsonNarratorStore>());
        return services;
    }
}
