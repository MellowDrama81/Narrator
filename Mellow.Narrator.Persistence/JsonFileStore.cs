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

    public static async Task<T?> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Action<string>? reportRecovery = null)
    {
        var primary = await TryReadAsync<T>(path, cancellationToken);
        if (primary.Success)
        {
            if (primary.RequiresMigration)
            {
                await WriteAsync(path, primary.Value!, cancellationToken);
                reportRecovery?.Invoke($"Migrated '{Path.GetFileName(path)}' to persistence format {CurrentFormatVersion}.");
            }
            return primary.Value;
        }

        var backupPath = path + ".bak";
        var backup = await TryReadAsync<T>(backupPath, cancellationToken);
        if (!backup.Success)
        {
            if (!File.Exists(path) && !File.Exists(backupPath)) return default;
            Quarantine(path);
            Quarantine(backupPath);
            reportRecovery?.Invoke($"Quarantined unreadable primary and backup documents for '{Path.GetFileName(path)}'.");
            throw new InvalidDataException($"Neither '{path}' nor its last-known-good backup is valid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await WriteRawAsync(path, backup.Bytes!, CancellationToken.None);
        reportRecovery?.Invoke($"Restored '{Path.GetFileName(path)}' from its last-known-good backup.");
        if (backup.RequiresMigration)
            await WriteAsync(path, backup.Value!, CancellationToken.None);
        return backup.Value;
    }

    public static async Task<T?> ReadExactAsync<T>(string path, CancellationToken cancellationToken)
    {
        var result = await TryReadAsync<T>(path, cancellationToken);
        return result.Success ? result.Value : default;
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
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
                File.Replace(temp, path, path + ".bak", true);
            else
                File.Move(temp, path);
            _ = await TryReadRequiredAsync<T>(path, CancellationToken.None);
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

    private static async Task<(bool Success, T? Value, byte[]? Bytes, bool RequiresMigration)> TryReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (false, default, null, false);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var (value, version) = DeserializeWithVersion<T>(bytes);
            return (true, value, bytes, version != CurrentFormatVersion);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, default, null, false);
        }
    }

    private static T Deserialize<T>(byte[] bytes) => DeserializeWithVersion<T>(bytes).Value;

    private static (T Value, int Version) DeserializeWithVersion<T>(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<PersistenceDocument<T>>(bytes, Options)
            ?? throw new JsonException("The persistence document is empty.");
        if (document.FormatVersion is < 0 or > CurrentFormatVersion)
            throw new NotSupportedException($"Persistence format {document.FormatVersion} is not supported.");
        return (document.Data ?? throw new JsonException("The persistence document has no data."), document.FormatVersion);
    }

    private static async Task WriteRawAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    public static void Quarantine(string path)
    {
        if (!File.Exists(path)) return;
        var quarantine = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
        File.Move(path, quarantine, true);
    }
}
