using Mellow.Narrator.Core;
using Mellow.Narrator.Maui.Services;
using Mellow.Narrator.OpenAiCompatible;
using Mellow.Narrator.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var appDataDirectory = FileSystem.AppDataDirectory;
#if WINDOWS
        RestoreLegacyWindowsAppData(appDataDirectory);
#endif
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
            .AddMellowNarratorPersistence(new PersistenceOptions(appDataDirectory));
        builder.Services.AddSingleton<ISecureStorage>(SecureStorage.Default);
        builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        builder.Services.AddSingleton<MainTabbedPage>();

        var app = builder.Build();
        Ui.ConfigureLogging(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mellow.Narrator.Maui"),
            app.Services.GetRequiredService<INarratorLogLevelSwitch>());
        return app;
    }

#if WINDOWS
    private static void RestoreLegacyWindowsAppData(string appDataDirectory)
    {
        var currentPackageRoot = Directory.GetParent(appDataDirectory)
            ?? throw new InvalidOperationException("The Windows application-data directory has no package root.");
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyPackageRoot = Path.Combine(localApplicationData, "User Name", currentPackageRoot.Name);
        ApplicationDataMigration.CopyMissingLegacyWindowsIdentityData(
            legacyPackageRoot,
            currentPackageRoot.FullName);
    }
#endif
}
