using Mellow.Narrator.Core;
using Mellow.Narrator.MauiBlazor.Services;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.MauiBlazor;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG && WINDOWS
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services
            .AddMellowNarratorCore()
            .AddMellowNarratorOpenAiCompatible()
            .AddMellowNarratorPersistence(new PersistenceOptions(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<ISecureStorage>(SecureStorage.Default);
        builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        builder.Services.AddSingleton<NarratorWorkspace>();
        builder.Services.AddSingleton<HybridWorkspace>();
        builder.Services.AddSingleton<ImportExportService>();
        return builder.Build();
    }
}
