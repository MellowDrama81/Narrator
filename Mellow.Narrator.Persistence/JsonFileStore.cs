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
            // Verify the staged file before committing it, not after: verifying path only after the
            // swap means the new data is already persisted by the time a verification failure would be
            // reported, misleadingly implying the write itself failed when it actually succeeded.
            _ = await TryReadRequiredAsync<T>(temp, CancellationToken.None);
            // Reading temp right before replacing it can transiently race a lingering OS/AV file handle
            // on Windows; retry briefly rather than surface a spurious failure for an already-valid write.
            await RetryTransientIoAsync(() =>
            {
                if (File.Exists(path))
                    File.Replace(temp, path, path + ".bak", true);
                else
                    File.Move(temp, path);
            });
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task RetryTransientIoAsync(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 3) { await Task.Delay(25 * attempt); }
        }
    }

    public static async Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PersistenceDocument<T>(CurrentFormatVersion, value), Options);
        _ = Deserialize<T>(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Stage to a temp file and rename into place, like WriteAsync, so a write interrupted mid-flight
        // (crash, cancellation) never leaves a partial file at `path` — File.Move without overwrite still
        // throws if `path` already exists, preserving the "never rewritten" guarantee callers rely on.
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteRawAsync(temp, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (false, default, null, false);
        }
    }

    private static T Deserialize<T>(byte[] bytes) => DeserializeWithVersion<T>(bytes).Value;

    private static (T Value, int Version) DeserializeWithVersion<T>(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<PersistenceDocument<T>>(bytes, Options)
            ?? throw new JsonException("The persistence document is empty.");
        // A negative version is malformed data - treat it like any other corruption so it can fall
        // back to the backup or get quarantined. A version newer than this app supports is different:
        // it means a newer app version legitimately wrote this file, and silently reverting to an
        // older backup would downgrade/lose that data, so that case must keep propagating uncaught.
        if (document.FormatVersion < 0)
            throw new JsonException($"Persistence format {document.FormatVersion} is invalid.");
        if (document.FormatVersion > CurrentFormatVersion)
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
