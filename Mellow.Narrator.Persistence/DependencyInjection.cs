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
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
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
