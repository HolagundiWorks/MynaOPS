// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared;

/// <summary>
/// A hint, not a second poll: a <see cref="FileSystemWatcher"/> on the open project file and its write-ahead log brings the next
/// ServedProject.PollAsync forward from "within two seconds" to about half a second. Events are debounced (300 ms). A watcher can miss events
/// (a replaced file, a full buffer), so the two-second poll stays the fallback and decides; the watcher only calls it earlier.
/// </summary>
public sealed class ProjectFileWatcher : IDisposable
{
    public const int DebounceMs = 300;

    readonly Func<string, Task> _poll;
    FileSystemWatcher? _watcher;
    Timer? _timer;
    string? _path;

    /// <param name="poll">What to run when the file changed (the main window passes ServedProject.PollAsync).</param>
    public ProjectFileWatcher(Func<string, Task> poll) => _poll = poll;

    /// <summary>Watch <paramref name="projectPath"/> (call it every tick: nothing happens while the path is the same).</summary>
    public void Watch(string? projectPath)
    {
        if (string.Equals(projectPath, _path, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose(); _watcher = null;
        _path = projectPath;
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (dir is null || !Directory.Exists(dir)) return;
            // The name filter also matches "<name>.osp-wal", where a write shows up first.
            var w = new FileSystemWatcher(dir, Path.GetFileName(projectPath) + "*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = false };
            w.Changed += (_, _) => Kick();
            w.Created += (_, _) => Kick();
            w.Renamed += (_, _) => Kick();
            w.Error += (_, _) => { /* the poll still runs */ };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) { _watcher = null; }
    }

    /// <summary>An event arrived: poll once, 300 ms after the last event of a burst.</summary>
    internal void Kick()
    {
        var path = _path;
        if (path is null) return;
        lock (this)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => { try { _ = _poll(path); } catch (Exception) { /* the next tick tries again */ } }, null, DebounceMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (this) { _timer?.Dispose(); _timer = null; }
    }
}
