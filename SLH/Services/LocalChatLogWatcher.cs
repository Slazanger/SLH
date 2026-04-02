namespace SLH.Services;

public sealed class LocalChatLogWatcher : IDisposable
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _watchedFile;
    private long _lastPosition;
    private CancellationTokenSource? _debounceCts;

    public event EventHandler<IReadOnlyList<string>>? NewLines;

    public void WatchFolder(string folderPath, string? characterNameFilter)
    {
        DisposeWatcher();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        _watcher = new FileSystemWatcher(folderPath, "Local_*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, e) => OnActivity(e.FullPath, characterNameFilter);
        _watcher.Created += (_, e) => OnActivity(e.FullPath, characterNameFilter);

        var initial = PickLatestLocalFile(folderPath, characterNameFilter);
        if (initial != null)
            ReadNewContent(initial, reset: true);
    }

    public void Dispose()
    {
        DisposeWatcher();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }

    private void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _watchedFile = null;
        _lastPosition = 0;
    }

    private void OnActivity(string fullPath, string? characterFilter)
    {
        if (!fullPath.Contains("Local_", StringComparison.OrdinalIgnoreCase) || !fullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return;
        if (characterFilter != null && !Path.GetFileName(fullPath).Contains(characterFilter, StringComparison.OrdinalIgnoreCase))
            return;

        Debounce(() =>
        {
            var folder = Path.GetDirectoryName(fullPath);
            if (folder == null)
                return;
            var latest = PickLatestLocalFile(folder, characterFilter) ?? fullPath;
            ReadNewContent(latest, reset: false);
        });
    }

    private void Debounce(Action action)
    {
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                        return;
                    action();
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
            });
        }
    }

    private void ReadNewContent(string path, bool reset)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                if (reset || _watchedFile != path)
                {
                    _watchedFile = path;
                    _lastPosition = 0;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (_lastPosition > stream.Length)
                    _lastPosition = 0;
                var remain = stream.Length - _lastPosition;
                if (remain <= 0)
                    return;
                stream.Seek(_lastPosition, SeekOrigin.Begin);
                var buf = new byte[remain];
                stream.ReadExactly(buf);
                _lastPosition = stream.Length;
                var chunk = System.Text.Encoding.UTF8.GetString(buf);

                if (chunk.Length == 0)
                    return;

                var lines = chunk.Replace("\r\n", "\n").Split('\n');
                NewLines?.Invoke(this, lines);
            }
            catch
            {
                // log file may be locked briefly
            }
        }
    }

    private static string? PickLatestLocalFile(string folder, string? characterFilter)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "Local_*.txt");
            if (!string.IsNullOrWhiteSpace(characterFilter))
            {
                var filtered = files.Where(f => Path.GetFileName(f).Contains(characterFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count > 0)
                    files = filtered;
            }

            return files.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
