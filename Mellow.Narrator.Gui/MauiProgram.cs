using Mellow.Narrator.Core;
using Mellow.Narrator.Gui.Services;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.Gui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
            .ConfigureFonts(fonts =>
        {
            fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
        });

        builder.Services
            .AddMellowNarratorCore()
            .AddMellowNarratorOpenAiCompatible()
            .AddMellowNarratorPersistence(new PersistenceOptions(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<ISecureStorage>(SecureStorage.Default);
        builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        builder.Services.AddSingleton<MainTabbedPage>();

        var app = builder.Build();
        Ui.ConfigureLogging(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mellow.Narrator.Gui"),
            app.Services.GetRequiredService<INarratorLogLevelSwitch>());
        return app;
    }
}
