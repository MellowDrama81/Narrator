using System.Text.Json;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.MauiBlazor.Services;

// Uses native platform pickers from the Hybrid host. Imports are always copied into a new durable record.
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

    public Task ExportDefinitionAsync(StoryDefinition definition) => SaveJsonAsync($"{Safe(definition.Title)}-definition.json", new StoryDefinitionExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, definition));
    public async Task ExportStoryAsync(StoryState state)
    {
        var turns = await stories.GetTurnsAsync(state.Id);
        await SaveJsonAsync($"{Safe(state.Label)}-story.json", new StoryStateExport(ImportExportProcessor.CurrentFormatVersion, DateTimeOffset.UtcNow, state, turns));
    }
    public async Task ExportNarrationHistoryAsync(StoryState state)
    {
        var turns = await stories.GetTurnsAsync(state.Id);
        var text = string.Join(Environment.NewLine + Environment.NewLine, turns.OrderBy(x => x.SequenceNumber).Select(turn => turn.PlayerAction is null ? turn.Narration : $"> {turn.PlayerAction}{Environment.NewLine}{Environment.NewLine}{turn.Narration}"));
        await ShareTextAsync($"{Safe(state.Label)}-history.txt", text);
    }
    public async Task ExportBibleHistoryAsync(StoryState state)
    {
        var turns = await stories.GetTurnsAsync(state.Id);
        var groups = state.StoryBibleMaintenanceHistory.Select(item => (item.CompletedAtUtc, item.Reason.ToString(), (IReadOnlyList<AppliedStoryBibleChange>)item.Changes))
            .Concat(turns.Where(turn => turn.StoryBibleChanges.Count > 0).Select(turn => (turn.CompletedAtUtc, $"Turn {turn.SequenceNumber}", turn.StoryBibleChanges)));
        var text = string.Join(Environment.NewLine + Environment.NewLine, groups.OrderByDescending(x => x.CompletedAtUtc).Select(group => $"{group.Item2} — {group.CompletedAtUtc.ToLocalTime():g}{Environment.NewLine}" + string.Join(Environment.NewLine, group.Item3.Select(change => $"{change.Operation}: {change.Before?.Name ?? change.After?.Name} ({change.Source})"))));
        await ShareTextAsync($"{Safe(state.Label)}-bible-history.txt", text);
    }
    public async Task ExportPlannedEventHistoryAsync(StoryState state)
    {
        var turns = await stories.GetTurnsAsync(state.Id);
        var groups = state.PlannedEventMaintenanceHistory.Select(item => (item.CompletedAtUtc, item.Reason.ToString(), (IReadOnlyList<AppliedPlannedEventChange>)item.Changes))
            .Concat(turns.Where(turn => turn.PlannedEventChanges.Count > 0).Select(turn => (turn.CompletedAtUtc, $"Turn {turn.SequenceNumber}", turn.PlannedEventChanges)));
        var text = string.Join(Environment.NewLine + Environment.NewLine, groups.OrderByDescending(x => x.CompletedAtUtc).Select(group => $"{group.Item2} — {group.CompletedAtUtc.ToLocalTime():g}{Environment.NewLine}" + string.Join(Environment.NewLine, group.Item3.Select(change => $"{change.Operation}: {change.Before?.Description ?? change.After?.Description} ({change.Source}{(change.Outcome is null ? "" : $", {change.Outcome}")})"))));
        await ShareTextAsync($"{Safe(state.Label)}-planned-events-history.txt", text);
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
    private static async Task SaveJsonAsync<T>(string name, T value)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(name)
        };
        picker.FileTypeChoices.Add("JSON file", [".json"]);
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("The application window is not available.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await Windows.Storage.FileIO.WriteTextAsync(file, JsonSerializer.Serialize(value, Json));
#else
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await using (var stream = File.Create(path)) await JsonSerializer.SerializeAsync(stream, value, Json);
        await Share.Default.RequestAsync(new ShareFileRequest("Export Mellow Narrator", new ShareFile(path)));
#endif
    }
    private static async Task ShareTextAsync(string name, string value)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await File.WriteAllTextAsync(path, value);
        await Share.Default.RequestAsync(new ShareFileRequest("Export Mellow Narrator", new ShareFile(path)));
    }
    private static void CheckVersion(int version) { if (version is < 0 or > ImportExportProcessor.CurrentFormatVersion) throw new NotSupportedException($"Export format {version} is not supported."); }
    private static string Safe(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim(); return string.IsNullOrWhiteSpace(safe) ? "mellow-narrator" : safe; }
}
