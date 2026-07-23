using System.Text.Json;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.Gui;

internal static class ImportExportService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 128,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task ExportDefinitionAsync(StoryDefinition definition)
    {
        var document = new StoryDefinitionExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, definition);
        await ShareJsonAsync($"{Safe(definition.Title)}-definition.json", document);
    }

    public static async Task<StoryDefinition?> ImportDefinitionAsync(
        IStoryDefinitionRepository repository,
        INarratorApplication application)
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Import Story Definition JSON" });
        if (picked is null) return null;
        await using var stream = await picked.OpenReadAsync();
        var document = JsonSerializer.Deserialize<StoryDefinitionExport>(await ReadLimitedAsync(stream), Json)
            ?? throw new InvalidDataException("The Story Definition export is empty.");
        CheckVersion(document.FormatVersion);
        var settings = await application.GetSettingsAsync();
        var summaries = await repository.ListAsync();
        var imported = ImportExportProcessor.CopyDefinition(
            document.Definition,
            summaries.Count == 0 ? 0 : summaries.Max(x => x.SortOrder) + 1,
            settings.ContentLimits);
        await repository.SaveAsync(imported);
        return imported;
    }

    public static async Task ExportStateAsync(StoryState state, IReadOnlyList<StoryTurn> turns)
    {
        var document = new StoryStateExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, state, turns);
        await ShareJsonAsync($"{Safe(state.Label)}-story.json", document);
    }

    public static async Task<StoryState?> ImportStateAsync(
        IStoryStateRepository repository,
        INarratorApplication application)
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Import Story State JSON" });
        if (picked is null) return null;
        await using var stream = await picked.OpenReadAsync();
        var document = JsonSerializer.Deserialize<StoryStateExport>(await ReadLimitedAsync(stream), Json)
            ?? throw new InvalidDataException("The Story State export is empty.");
        CheckVersion(document.FormatVersion);
        var settings = await application.GetSettingsAsync();
        var summaries = await repository.ListAsync();
        var imported = ImportExportProcessor.CopyState(
            document.State,
            document.Turns,
            summaries.Count == 0 ? 0 : summaries.Max(x => x.SortOrder) + 1,
            settings.ContentLimits);
        await repository.ImportAsync(imported.State, imported.Turns);
        return imported.State;
    }

    private static async Task ShareJsonAsync<T>(string fileName, T value)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json));
        await Share.Default.RequestAsync(new ShareFileRequest("Export from Mellow Narrator", new ShareFile(path)));
    }

    private static void CheckVersion(int version)
    {
        if (version is < 0 or > ImportExportProcessor.CurrentFormatVersion)
            throw new NotSupportedException($"Export format {version} is not supported.");
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream)
    {
        if (stream.CanSeek && stream.Length > ImportExportProcessor.MaximumImportBytes)
            throw new InvalidDataException("The import file exceeds the maximum supported size.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > ImportExportProcessor.MaximumImportBytes)
                throw new InvalidDataException("The import file exceeds the maximum supported size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "mellow-narrator" : safe;
    }
}
