using Mellow.Narrator.Core;
using Microsoft.Maui.Storage;

namespace Mellow.Narrator.MauiBlazor.Services;

public sealed class MauiSecureStorageService(ISecureStorage storage) : ISecureStorageService
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => storage.GetAsync(key);
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => storage.SetAsync(key, value);
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(storage.Remove(key));
}
