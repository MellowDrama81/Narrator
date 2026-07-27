using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Maui.Storage;
using Mellow.Narrator.Core;

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
        await SaveJsonAsync($"{Safe(definition.Title)}-definition.json", document);
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
        await SaveJsonAsync($"{Safe(state.Label)}-story.json", document);
    }

    public static async Task ExportNarrationHistoryAsync(StoryState state, IReadOnlyList<StoryTurn> turns)
    {
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            turns.OrderBy(x => x.SequenceNumber).Select(FormatTurn));
        await SaveTextAsync($"{Safe(state.Label)}-history.txt", text);
    }

    public static async Task ExportBibleHistoryAsync(StoryState state, IReadOnlyList<StoryTurn> turns)
    {
        var groups = new List<(DateTimeOffset At, string Header, IReadOnlyList<AppliedStoryBibleChange> Changes)>();
        groups.AddRange(state.StoryBibleMaintenanceHistory.Select(x =>
            (x.CompletedAtUtc, x.Reason.ToString(), (IReadOnlyList<AppliedStoryBibleChange>)x.Changes)));
        groups.AddRange(turns.Where(x => x.StoryBibleChanges.Count > 0).Select(x =>
            (x.CompletedAtUtc, $"Turn {x.SequenceNumber}", x.StoryBibleChanges)));
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            groups.OrderByDescending(x => x.At).Select(FormatBibleHistoryGroup));
        await SaveTextAsync($"{Safe(state.Label)}-bible-history.txt", text);
    }

    private static string FormatBibleHistoryGroup(
        (DateTimeOffset At, string Header, IReadOnlyList<AppliedStoryBibleChange> Changes) group)
    {
        var lines = new List<string> { $"{group.Header} — {group.At.ToLocalTime():g}" };
        lines.AddRange(group.Changes.Select(FormatBibleChange));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBibleChange(AppliedStoryBibleChange change) =>
        $"{change.Operation}: {change.Before?.Name ?? change.After?.Name} ({change.Source})" + Environment.NewLine +
        $"Before: {change.Before?.Content ?? "—"}" + Environment.NewLine +
        $"After: {change.After?.Content ?? "—"}";

    private static string FormatTurn(StoryTurn turn) =>
        turn.PlayerAction is null
            ? turn.Narration
            : $"> {turn.PlayerAction}{Environment.NewLine}{Environment.NewLine}{turn.Narration}";

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

    private static async Task SaveJsonAsync<T>(string fileName, T value)
    {
        await EnsureStoragePermissionAsync();
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value, Json);
        stream.Position = 0;
        FileSaverResult result;
        try { result = await FileSaver.Default.SaveAsync(fileName, stream); }
        catch (OperationCanceledException) { return; }
        if (result.IsSuccessful) return;
        if (result.Exception is null or OperationCanceledException) return;
        throw new IOException("The export could not be saved.", result.Exception);
    }

    private static async Task SaveTextAsync(string fileName, string text)
    {
        await EnsureStoragePermissionAsync();
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        FileSaverResult result;
        try { result = await FileSaver.Default.SaveAsync(fileName, stream); }
        catch (OperationCanceledException) { return; }
        if (result.IsSuccessful) return;
        if (result.Exception is null or OperationCanceledException) return;
        throw new IOException("The export could not be saved.", result.Exception);
    }

    private static async Task EnsureStoragePermissionAsync()
    {
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var read = await Permissions.RequestAsync<Permissions.StorageRead>();
            var write = await Permissions.RequestAsync<Permissions.StorageWrite>();
            if (read != PermissionStatus.Granted || write != PermissionStatus.Granted)
                throw new NarratorException("Storage permission is required to save an export.");
        }
#endif
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
