using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLH.Services;

/// <summary>
/// Persists user-assigned pilot tags under %AppData%\SLH. Separate from enrichment cache (no TTL).
/// </summary>
public sealed class CharacterTagStore : IDisposable
{
    private const int FileVersion = 1;

    private static readonly IReadOnlySet<string> EmptyTags = new HashSet<string>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<long, HashSet<string>> _byCharacter = new();
    private Timer? _saveTimer;
    private readonly object _flushSync = new();
    private bool _loaded;

    public CharacterTagStore(string? path = null)
    {
        _path = path ?? AppPaths.CharacterTagsPath;
    }

    public IReadOnlySet<string> GetTags(long characterId)
    {
        if (characterId <= 0)
            return EmptyTags;
        EnsureLoaded();
        lock (_gate)
        {
            if (!_byCharacter.TryGetValue(characterId, out var set) || set.Count == 0)
                return EmptyTags;
            return new HashSet<string>(set, StringComparer.Ordinal);
        }
    }

    public bool HasTag(long characterId, string tag)
    {
        if (characterId <= 0 || !CharacterTagIds.IsKnown(tag))
            return false;
        EnsureLoaded();
        lock (_gate)
            return _byCharacter.TryGetValue(characterId, out var set) && set.Contains(tag);
    }

    public void SetTag(long characterId, string tag, bool enabled)
    {
        if (characterId <= 0 || !CharacterTagIds.IsKnown(tag))
            return;
        EnsureLoaded();
        lock (_gate)
        {
            if (!_byCharacter.TryGetValue(characterId, out var set))
            {
                if (!enabled)
                    return;
                set = new HashSet<string>(StringComparer.Ordinal);
                _byCharacter[characterId] = set;
            }

            if (enabled)
                set.Add(tag);
            else
            {
                set.Remove(tag);
                if (set.Count == 0)
                    _byCharacter.Remove(characterId);
            }
        }

        ScheduleSave();
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _saveTimer;
            _saveTimer = null;
        }

        timer?.Dispose();
        Flush();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (_gate)
        {
            if (_loaded)
                return;
            LoadUnlocked();
            _loaded = true;
        }
    }

    private void LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<CharacterTagsFileDto>(json, JsonOptions);
            if (doc?.ByCharacter == null || doc.Version != FileVersion)
                return;
            foreach (var kv in doc.ByCharacter)
            {
                if (kv.Key <= 0 || kv.Value == null)
                    continue;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in kv.Value)
                {
                    if (CharacterTagIds.IsKnown(t))
                        set.Add(t);
                }

                if (set.Count > 0)
                    _byCharacter[kv.Key] = set;
            }
        }
        catch
        {
            // ignore corrupt file
        }
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer(
                    _ => Flush(),
                    null,
                    TimeSpan.FromMilliseconds(750),
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _saveTimer.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Flush()
    {
        CharacterTagsFileDto snapshot;
        lock (_gate)
        {
            var dict = new Dictionary<long, List<string>>();
            foreach (var kv in _byCharacter)
            {
                if (kv.Value.Count == 0)
                    continue;
                dict[kv.Key] = kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToList();
            }

            snapshot = new CharacterTagsFileDto
            {
                Version = FileVersion,
                ByCharacter = dict
            };
        }

        lock (_flushSync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                var tmp = _path + ".tmp";
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(_path + ".tmp");
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private sealed class CharacterTagsFileDto
    {
        public int Version { get; set; }
        public Dictionary<long, List<string>>? ByCharacter { get; set; }
    }
}
