using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace SLH.Services;

/// <summary>
/// Fetches and caches EVE type icons (memory + disk). One download per type ID shared across callers.
/// </summary>
public sealed class ShipIconCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _iconsDir;
    private readonly ConcurrentDictionary<int, Task<byte[]?>> _memory = new();

    public ShipIconCache(IConfiguration configuration, string? iconsDirectory = null)
    {
        _iconsDir = iconsDirectory ?? Path.Combine(AppPaths.AppDataDirectory, "ship-icons");
        var ua = configuration["UserAgent"] ?? "SLH/0.1";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
    }

    /// <summary>Returns raw image bytes for the type icon, or null if unavailable.</summary>
    public Task<byte[]?> GetOrLoadBytesAsync(int typeId, CancellationToken cancellationToken = default)
    {
        if (typeId <= 0)
            return Task.FromResult<byte[]?>(null);

        var task = _memory.GetOrAdd(typeId, id => LoadIconCoreAsync(id));
        if (!cancellationToken.CanBeCanceled)
            return task;
        return AwaitWithCancellationAsync(task, cancellationToken);
    }

    private static async Task<byte[]?> AwaitWithCancellationAsync(Task<byte[]?> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<byte[]?> LoadIconCoreAsync(int typeId)
    {
        try
        {
            Directory.CreateDirectory(_iconsDir);
            var path = Path.Combine(_iconsDir, $"{typeId}.png");
            if (File.Exists(path))
            {
                var disk = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                if (disk.Length > 0)
                    return disk;
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // ignore
                }
            }

            byte[]? body = null;
            foreach (var url in IconUrls(typeId))
            {
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (body.Length > 0)
                    break;
                body = null;
            }

            if (body == null || body.Length == 0)
                return null;

            try
            {
                await File.WriteAllBytesAsync(path, body).ConfigureAwait(false);
            }
            catch
            {
                // still return bytes
            }

            return body;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Current CDN first (EVE docs), then legacy host.</summary>
    private static IEnumerable<string> IconUrls(int typeId)
    {
        yield return $"https://images.evetech.net/types/{typeId}/icon?size=64";
        yield return $"https://images.eveonline.com/types/{typeId}/icon";
    }
}
