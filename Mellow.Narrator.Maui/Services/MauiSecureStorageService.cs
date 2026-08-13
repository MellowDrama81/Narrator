using Mellow.Narrator.Core;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.Maui.Services;

public sealed class MauiSecureStorageService(ISecureStorage storage) : ISecureStorageService
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await storage.GetAsync(key);
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        await storage.SetAsync(key, value);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(storage.Remove(key));
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A secure-storage key is required.", nameof(key));
    }
}
