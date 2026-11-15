// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet


namespace Office.Shared;

/// <summary>
/// Live sync status of the open project file (MynaCAD <c>docs/live-sync.md</c>). It does not poll: ServedProject.PollAsync (every two seconds,
/// brought forward by <see cref="ProjectFileWatcher"/>) calls <see cref="RefreshAsync"/> after it reads the revision. The file is only read, never reloaded
/// under the user: the status says "Updated by MynaCAD at 14:32" until the Take-off page has read the file again (<see cref="Accept"/>).
/// </summary>
public static class SyncStatusService
{
    /// <summary>Raised on a thread-pool thread when the status text or state changed.</summary>
    public static event Action<SyncStatus>? Changed;

    static readonly Dictionary<string, SyncAccepted> _accepted = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static SyncStatus _current = SyncStatus.None;

    public static SyncStatus Current { get { lock (_lock) return _current; } }

    static string? Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); } catch (ArgumentException) { return null; }
    }

    /// <summary>Our own write took the file from revision <paramref name="revision"/> - 1 to <paramref name="revision"/>. It is accepted unless another program wrote in between.</summary>
    public static void NoteOwnWrite(string path, long revision)
    {
        if (Key(path) is not { } k) return;
        lock (_lock)
        {
            if (_accepted.TryGetValue(k, out var a) && a.Revision != revision - 1) return;
            var id = a?.ProjectId ?? "";
            _accepted[k] = new SyncAccepted(revision, id);
        }
    }

    /// <summary>The program has read the file as it is now (the Take-off page reloaded): what is in it is what the user sees.</summary>
    public static async Task AcceptAsync(string? path)
    {
        if (Key(path) is not { } k) return;
        var peek = await Task.Run(() => OdpSync.Probe(path)).ConfigureAwait(false);
        if (peek.Probe is { } p) lock (_lock) _accepted[k] = new SyncAccepted(p.Revision, p.ProjectId);
        await RefreshAsync(path).ConfigureAwait(false);
    }

    /// <summary>Look at the file (read only) and raise <see cref="Changed"/> when the status differs from the last one.</summary>
    public static async Task RefreshAsync(string? path)
    {
        var k = Key(path);
        SyncStatus next;
        if (k is null || !OdpFile.LooksLikeOdpPath(path)) next = SyncStatus.None;
        else
        {
            var peek = await Task.Run(() => OdpSync.Probe(path)).ConfigureAwait(false);
            lock (_lock)
            {
                // A file seen for the first time has nothing to be compared with: what it holds now is the baseline. An id left empty by an own write is filled in.
                if (peek.Probe is { } p && (!_accepted.TryGetValue(k, out var a) || a.ProjectId.Length == 0))
                    _accepted[k] = a is null ? new SyncAccepted(p.Revision, p.ProjectId) : a with { ProjectId = p.ProjectId };
                next = OdpSync.Evaluate(_accepted.GetValueOrDefault(k), peek);
            }
        }
        bool changed;
        lock (_lock) { changed = next.State != _current.State || next.Text != _current.Text; _current = next; }
        if (changed) Changed?.Invoke(next);
    }
}
