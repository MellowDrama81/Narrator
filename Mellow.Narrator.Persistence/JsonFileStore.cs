using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mellow.Narrator.Persistence;

internal sealed record PersistenceDocument<T>(int FormatVersion, T Data);

internal static class JsonFileStore
{
    public const int CurrentFormatVersion = 1;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        var primary = await TryReadAsync<T>(path, cancellationToken);
        if (primary.Success) return primary.Value;

        var backupPath = path + ".bak";
        var backup = await TryReadAsync<T>(backupPath, cancellationToken);
        if (!backup.Success)
        {
            if (!File.Exists(path) && !File.Exists(backupPath)) return default;
            throw new InvalidDataException($"Neither '{path}' nor its last-known-good backup is valid.");
        }

        await WriteRawAsync(path, backup.Bytes!, cancellationToken);
        return backup.Value;
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PersistenceDocument<T>(CurrentFormatVersion, value), Options);
        _ = Deserialize<T>(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteRawAsync(temp, bytes, cancellationToken);
            if (File.Exists(path))
                File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
            _ = await TryReadRequiredAsync<T>(path, cancellationToken);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static async Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PersistenceDocument<T>(CurrentFormatVersion, value), Options);
        _ = Deserialize<T>(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static async Task<T> TryReadRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        var result = await TryReadAsync<T>(path, cancellationToken);
        return result.Success ? result.Value! : throw new InvalidDataException($"'{path}' is invalid.");
    }

    private static async Task<(bool Success, T? Value, byte[]? Bytes)> TryReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (false, default, null);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return (true, Deserialize<T>(bytes), bytes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, default, null);
        }
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<PersistenceDocument<T>>(bytes, Options)
            ?? throw new JsonException("The persistence document is empty.");
        if (document.FormatVersion != CurrentFormatVersion)
            throw new NotSupportedException($"Persistence format {document.FormatVersion} is not supported.");
        return document.Data ?? throw new JsonException("The persistence document has no data.");
    }

    private static async Task WriteRawAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }
}
