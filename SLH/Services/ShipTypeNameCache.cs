using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SLH.Services;

/// <summary>
/// Resolves inventory type names from ESI (public) with JSON disk cache under %AppData%\SLH.
/// </summary>
public sealed class ShipTypeNameCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, Task<string?>> _inflight = new();
    private Dictionary<int, string> _byId = new();
    private bool _loaded;

    public ShipTypeNameCache(IConfiguration configuration, string? jsonPath = null)
    {
        _path = jsonPath ?? AppPaths.ShipTypeNamesPath;
        var ua = configuration["UserAgent"] ?? "SLH/0.1";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
    }

    /// <summary>Returns the type name, or null if ESI does not resolve it.</summary>
    public Task<string?> GetOrLoadAsync(int typeId, CancellationToken cancellationToken = default)
    {
        if (typeId <= 0)
            return Task.FromResult<string?>(null);

        EnsureLoaded();
        lock (_gate)
        {
            if (_byId.TryGetValue(typeId, out var diskName))
                return Task.FromResult<string?>(diskName);
        }

        var task = _inflight.GetOrAdd(typeId, id => LoadNetworkAsync(id));
        if (!cancellationToken.CanBeCanceled)
            return task;
        return AwaitWithCancellationAsync(task, cancellationToken);
    }

    private static async Task<string?> AwaitWithCancellationAsync(Task<string?> task, CancellationToken cancellationToken)
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

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (_gate)
        {
            if (_loaded)
                return;
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (raw != null)
                    {
                        foreach (var kv in raw)
                        {
                            if (!int.TryParse(kv.Key, out var id) || id <= 0)
                                continue;
                            var n = kv.Value?.Trim() ?? "";
                            if (n.Length > 0)
                                _byId[id] = n;
                        }
                    }
                }
            }
            catch
            {
                _byId = new Dictionary<int, string>();
            }

            _loaded = true;
        }
    }

    private void PersistLocked()
    {
        var serializable = _byId.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        var json = JsonSerializer.Serialize(serializable);
        File.WriteAllText(_path, json);
    }

    private async Task<string?> LoadNetworkAsync(int typeId)
    {
        try
        {
            EnsureLoaded();
            lock (_gate)
            {
                if (_byId.TryGetValue(typeId, out var existing))
                    return existing;
            }

            var url = $"https://esi.evetech.net/latest/universe/types/{typeId}/?datasource=tranquility";
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _inflight.TryRemove(typeId, out _);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                _inflight.TryRemove(typeId, out _);
                return null;
            }

            var name = nameEl.GetString()?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                _inflight.TryRemove(typeId, out _);
                return null;
            }

            lock (_gate)
            {
                _byId[typeId] = name;
                PersistLocked();
            }

            return name;
        }
        catch
        {
            _inflight.TryRemove(typeId, out _);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
