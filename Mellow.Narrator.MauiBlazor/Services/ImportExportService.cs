using System.Text.Json;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.MauiBlazor.Services;

// Uses the platform picker and share sheet, which work from a Blazor Hybrid page without needing a
// browser download implementation. Imports are always copied into a new durable record.
public sealed class ImportExportService(IStoryDefinitionRepository definitions, IStoryStateRepository stories, INarratorApplication application)
{
    private static readonly FilePickerFileType JsonFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = ["application/json"], [DevicePlatform.WinUI] = [".json"]
    });
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 128,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public Task ExportDefinitionAsync(StoryDefinition definition) => ShareAsync($"{Safe(definition.Title)}-definition.json", new StoryDefinitionExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, definition));
    public async Task ExportStoryAsync(StoryState state)
    {
        var turns = await stories.GetTurnsAsync(state.Id);
        await ShareAsync($"{Safe(state.Label)}-story.json", new StoryStateExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, state, turns));
    }
    public async Task<StoryDefinition?> ImportDefinitionAsync()
    {
        var file = await FilePicker.Default.PickAsync(new() { PickerTitle = "Import Story Definition JSON", FileTypes = JsonFileType });
        if (file is null) return null;
        await using var stream = await file.OpenReadAsync();
        var document = JsonSerializer.Deserialize<StoryDefinitionExport>(await ImportExportProcessor.ReadLimitedAsync(stream), Json) ?? throw new InvalidDataException("The Story Definition export is empty.");
        CheckVersion(document.FormatVersion);
        var settings = await application.GetSettingsAsync(); var list = await definitions.ListAsync();
        var copy = ImportExportProcessor.CopyDefinition(document.Definition, list.Count == 0 ? 0 : list.Max(x => x.SortOrder) + 1, settings.ContentLimits, settings.StoryGeneration);
        await definitions.SaveAsync(copy); return copy;
    }
    public async Task<StoryState?> ImportStoryAsync()
    {
        var file = await FilePicker.Default.PickAsync(new() { PickerTitle = "Import Story JSON", FileTypes = JsonFileType });
        if (file is null) return null;
        await using var stream = await file.OpenReadAsync();
        var document = JsonSerializer.Deserialize<StoryStateExport>(await ImportExportProcessor.ReadLimitedAsync(stream), Json) ?? throw new InvalidDataException("The Story export is empty.");
        CheckVersion(document.FormatVersion);
        var settings = await application.GetSettingsAsync(); var list = await stories.ListAsync();
        var copy = ImportExportProcessor.CopyState(document.State, document.Turns, list.Count == 0 ? 0 : list.Max(x => x.SortOrder) + 1, settings.ContentLimits, settings.StoryGeneration);
        await stories.ImportAsync(copy.State, copy.Turns); return copy.State;
    }
    private static async Task ShareAsync<T>(string name, T value)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await using (var stream = File.Create(path)) await JsonSerializer.SerializeAsync(stream, value, Json);
        await Share.Default.RequestAsync(new ShareFileRequest("Export Mellow Narrator", new ShareFile(path)));
    }
    private static void CheckVersion(int version) { if (version is < 0 or > ImportExportProcessor.CurrentFormatVersion) throw new NotSupportedException($"Export format {version} is not supported."); }
    private static string Safe(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim(); return string.IsNullOrWhiteSpace(safe) ? "mellow-narrator" : safe; }
}
