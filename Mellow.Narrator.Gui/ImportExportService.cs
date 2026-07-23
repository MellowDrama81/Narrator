using System.Text.Json;
using System.Text.Json.Serialization;
using Mellow.Narrator.Core;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.Gui;

internal static class ImportExportService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task ExportDefinitionAsync(StoryDefinition definition)
    {
        var document = new StoryDefinitionExport(FormatVersion, DateTimeOffset.UtcNow, definition);
        await ShareJsonAsync($"{Safe(definition.Title)}-definition.json", document);
    }

    public static async Task<StoryDefinition?> ImportDefinitionAsync(IStoryDefinitionRepository repository)
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Import Story Definition JSON" });
        if (picked is null) return null;
        await using var stream = await picked.OpenReadAsync();
        var document = await JsonSerializer.DeserializeAsync<StoryDefinitionExport>(stream, Json)
            ?? throw new InvalidDataException("The Story Definition export is empty.");
        CheckVersion(document.FormatVersion);
        var source = document.Definition;
        var ids = new Dictionary<Guid, Guid>();
        Guid Map(Guid id) => ids.TryGetValue(id, out var value) ? value : ids[id] = Guid.NewGuid();
        StoryBibleEntry MapEntry(StoryBibleEntry x) => x with { Id = Map(x.Id) };
        AppliedStoryBibleChange MapChange(AppliedStoryBibleChange x) => x with
        {
            EntryId = Map(x.EntryId),
            Before = x.Before is null ? null : MapEntry(x.Before),
            After = x.After is null ? null : MapEntry(x.After)
        };
        var imported = source with
        {
            Id = Guid.NewGuid(),
            PlayerQuestions = source.PlayerQuestions.Select(x => x with { Id = Guid.NewGuid() }).ToArray(),
            InitialStoryBible = new(source.InitialStoryBible.Entries.Select(MapEntry).ToArray()),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = (await repository.ListAsync()).Count
        };
        await repository.SaveAsync(imported);
        return imported;
    }

    public static async Task ExportStateAsync(StoryState state, IReadOnlyList<StoryTurn> turns)
    {
        var document = new StoryStateExport(FormatVersion, DateTimeOffset.UtcNow, state, turns);
        await ShareJsonAsync($"{Safe(state.Label)}-story.json", document);
    }

    public static async Task<StoryState?> ImportStateAsync(IStoryStateRepository repository)
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Import Story State JSON" });
        if (picked is null) return null;
        await using var stream = await picked.OpenReadAsync();
        var document = await JsonSerializer.DeserializeAsync<StoryStateExport>(stream, Json)
            ?? throw new InvalidDataException("The Story State export is empty.");
        CheckVersion(document.FormatVersion);
        if (document.Turns.Count == 0) throw new InvalidDataException("The Story State export has no turns.");

        var source = document.State;
        var stateId = Guid.NewGuid();
        var ids = new Dictionary<Guid, Guid>();
        Guid Map(Guid id) => ids.TryGetValue(id, out var value) ? value : ids[id] = Guid.NewGuid();
        StoryBible MapBible(StoryBible bible) => new(bible.Entries.Select(x => x with { Id = Map(x.Id) }).ToArray());
        AppliedStoryBibleChange MapChange(AppliedStoryBibleChange x) => x with
        {
            EntryId = Map(x.EntryId),
            Before = x.Before is null ? null : x.Before with { Id = Map(x.Before.Id) },
            After = x.After is null ? null : x.After with { Id = Map(x.After.Id) }
        };
        var imported = source with
        {
            Id = stateId,
            Setup = source.Setup with
            {
                Definition = source.Setup.Definition with
                {
                    PlayerQuestions = source.Setup.Definition.PlayerQuestions.Select(x => x with { Id = Guid.NewGuid() }).ToArray(),
                    InitialStoryBible = MapBible(source.Setup.Definition.InitialStoryBible)
                }
            },
            CurrentStoryBible = MapBible(source.CurrentStoryBible),
            StoryBibleMaintenanceHistory = source.StoryBibleMaintenanceHistory.Select(x => x with
            {
                Id = Guid.NewGuid(),
                Changes = x.Changes.Select(MapChange).ToArray()
            }).ToArray(),
            SortOrder = (await repository.ListAsync()).Count
        };
        var turns = document.Turns.OrderBy(x => x.SequenceNumber).Select(x => x with
        {
            Id = Guid.NewGuid(),
            StoryStateId = stateId,
            RelevantStoryBibleEntryIds = x.RelevantStoryBibleEntryIds.Select(Map).ToArray(),
            StoryBibleChanges = x.StoryBibleChanges.Select(MapChange).ToArray()
        }).ToArray();
        await repository.ImportAsync(imported, turns);
        return imported;
    }

    private static async Task ShareJsonAsync<T>(string fileName, T value)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json));
        await Share.Default.RequestAsync(new ShareFileRequest("Export from Mellow Narrator", new ShareFile(path)));
    }

    private static void CheckVersion(int version)
    {
        if (version != FormatVersion) throw new NotSupportedException($"Export format {version} is not supported.");
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "mellow-narrator" : safe;
    }
}
