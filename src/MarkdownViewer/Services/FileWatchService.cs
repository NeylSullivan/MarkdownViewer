using System.IO;

namespace MarkdownViewer.Services;

public sealed class FileWatchService : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextRecovery;
    private bool _disposed;

    public FileWatchService() => _timer = new Timer(PublishChanges, null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Raised on a background thread after the last filesystem event for a file.</summary>
    public event EventHandler<string>? FileChanged;

    public void Watch(IEnumerable<string> paths)
    {
        var fullPaths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = fullPaths.Select(Path.GetDirectoryName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _paths = fullPaths;
            _directories = directories;
            foreach (var path in _pending.Keys.Where(path => !fullPaths.Contains(path)).ToArray())
                _pending.Remove(path);
            foreach (var directory in _watchers.Keys.Where(directory => !directories.Contains(directory)).ToArray())
            {
                _watchers[directory].Dispose();
                _watchers.Remove(directory);
            }

            foreach (var directory in directories.Where(directory => !_watchers.ContainsKey(directory)))
                TryWatchDirectory(directory);
            UpdateTimer();
        }
    }

    private bool TryWatchDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 16_384
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += (sender, _) => OnWatcherError(directory, (FileSystemWatcher)sender!);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(directory, watcher);
            return true;
        }
        catch (IOException) { watcher?.Dispose(); }
        catch (UnauthorizedAccessException) { watcher?.Dispose(); }
        catch (ArgumentException) when (!Directory.Exists(directory)) { watcher?.Dispose(); }
        return false;
    }

    private void OnWatcherError(string directory, FileSystemWatcher watcher)
    {
        lock (_gate)
        {
            if (_disposed || !_watchers.TryGetValue(directory, out var current) || current != watcher)
                return;
            _watchers.Remove(directory);
            watcher.Dispose();
            QueueDirectory(directory);
            UpdateTimer();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Queue(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        Queue(args.OldFullPath);
        Queue(args.FullPath);
    }

    private void Queue(string path)
    {
        lock (_gate)
        {
            if (_disposed || !_paths.Contains(path))
                return;
            _pending[path] = DateTimeOffset.UtcNow;
            UpdateTimer();
        }
    }

    private void QueueDirectory(string directory)
    {
        foreach (var path in _paths.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)))
            _pending[path] = DateTimeOffset.UtcNow;
    }

    private void UpdateTimer()
    {
        var interval = _pending.Count > 0 || _watchers.Count < _directories.Count ? 100 : Timeout.Infinite;
        _timer.Change(interval, interval);
    }

    private void PublishChanges(object? state)
    {
        string[] ready;
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = DateTimeOffset.UtcNow;
            if (now >= _nextRecovery)
            {
                foreach (var directory in _directories.Where(directory => !_watchers.ContainsKey(directory)))
                    if (TryWatchDirectory(directory)) QueueDirectory(directory);
                _nextRecovery = now + RecoveryDelay;
            }
            var threshold = now - DebounceDelay;
            ready = _pending.Where(pair => pair.Value <= threshold).Select(pair => pair.Key).ToArray();
            foreach (var path in ready)
                _pending.Remove(path);
            UpdateTimer();
        }

        foreach (var path in ready)
        {
            lock (_gate)
            {
                if (_disposed || !_paths.Contains(path))
                    continue;
            }
            FileChanged?.Invoke(this, path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Dispose();
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
            _pending.Clear();
            _paths.Clear();
            _directories.Clear();
        }
    }
}
