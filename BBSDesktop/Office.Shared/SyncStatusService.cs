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

    // ---- realtime sync on/off (docs/ops-first-workflow.md section 15). Off pauses the detector (the 2 s poll, the watcher-driven poll and this refresh);
    // writing is never paused. The state is per run, on at the start, and not stored in the file.
    static bool _realtime = true;
    static long _detectorReads;
    static long _waiting;

    /// <summary>Raised when realtime sync is switched on or off.</summary>
    public static event Action<bool>? RealtimeChanged;

    public static bool RealtimeOn { get { lock (_lock) return _realtime; } }

    /// <summary>Reads of the project file made by the detector since the program started (the counter the tests look at).</summary>
    public static long DetectorReads => Interlocked.Read(ref _detectorReads);

    /// <summary>A detector read happened (the revision poll in ServedProject, the probe here).</summary>
    public static void NoteDetectorRead() => Interlocked.Increment(ref _detectorReads);

    /// <summary>Whether the detector may look at the file now: always when on; when off only for Sync now (<paramref name="force"/>).</summary>
    public static bool AllowDetect(bool force = false) => force || RealtimeOn;

    /// <summary>The changes counted by the last on-demand probe while off.</summary>
    public static long Waiting { get { lock (_lock) return _waiting; } }

    /// <summary>"Sync off" or "Sync off - 2 changes waiting" (the same words as MynaCAD).</summary>
    public static string OffText(long waiting) => waiting switch { <= 0 => "Sync off", 1 => "Sync off - 1 change waiting", _ => $"Sync off - {waiting} changes waiting" };

    /// <summary>
    /// How many revisions the file is ahead of the one this program accepted (changes by another program that have not been read): one read-only probe
    /// taken when the user looks, never by polling. 0 when nothing was accepted yet, the file was replaced or cannot be read.
    /// </summary>
    public static async Task<long> WaitingAsync(string? path)
    {
        if (Key(path) is not { } k || !OdpFile.LooksLikeOdpPath(path)) return 0;
        NoteDetectorRead();
        var peek = await Task.Run(() => OdpSync.Probe(path)).ConfigureAwait(false);
        lock (_lock)
        {
            long n = peek.Probe is { } p && _accepted.TryGetValue(k, out var a) && (a.ProjectId.Length == 0 || a.ProjectId == p.ProjectId) ? Math.Max(0, p.Revision - a.Revision) : 0;
            _waiting = n;
            return n;
        }
    }

    /// <summary>Switch realtime sync on or off. Off: one probe counts what is waiting and the status becomes "Sync off ...". On: one forced refresh restores the normal status.</summary>
    public static async Task SetRealtimeAsync(string? path, bool on)
    {
        lock (_lock) _realtime = on;
        if (on)
        {
            lock (_lock) _waiting = 0;
            await RefreshAsync(path, force: true).ConfigureAwait(false);
        }
        else await ShowOffAsync(path).ConfigureAwait(false);
        RealtimeChanged?.Invoke(on);
    }

    /// <summary>While off: the user looks (the button, a tooltip, the Home card): one probe, then the status text says how many changes wait.</summary>
    public static async Task ShowOffAsync(string? path)
    {
        if (RealtimeOn) return;
        var n = await WaitingAsync(path).ConfigureAwait(false);
        Publish(new SyncStatus(SyncState.Paused, OffText(n), null, null));
    }

    static void Publish(SyncStatus next)
    {
        bool changed;
        lock (_lock) { changed = next.State != _current.State || next.Text != _current.Text; _current = next; }
        if (changed) Changed?.Invoke(next);
    }

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
        NoteDetectorRead();
        var peek = await Task.Run(() => OdpSync.Probe(path)).ConfigureAwait(false);
        if (peek.Probe is { } p) lock (_lock) _accepted[k] = new SyncAccepted(p.Revision, p.ProjectId);
        await RefreshAsync(path, force: true).ConfigureAwait(false);
    }

    /// <summary>Look at the file (read only) and raise <see cref="Changed"/> when the status differs from the last one.</summary>
    public static async Task RefreshAsync(string? path, bool force = false)
    {
        // Realtime sync off: the file is not read and nothing is published (Sync now forces one look).
        if (!AllowDetect(force)) return;
        var k = Key(path);
        SyncStatus next;
        if (k is null || !OdpFile.LooksLikeOdpPath(path)) next = SyncStatus.None;
        else
        {
            NoteDetectorRead();
            var peek = await Task.Run(() => OdpSync.Probe(path)).ConfigureAwait(false);
            lock (_lock)
            {
                // A file seen for the first time has nothing to be compared with: what it holds now is the baseline. An id left empty by an own write is filled in.
                if (peek.Probe is { } p && (!_accepted.TryGetValue(k, out var a) || a.ProjectId.Length == 0))
                    _accepted[k] = a is null ? new SyncAccepted(p.Revision, p.ProjectId) : a with { ProjectId = p.ProjectId };
                next = OdpSync.Evaluate(_accepted.GetValueOrDefault(k), peek);
            }
        }
        // Off and forced (Sync now): the file was read, and the status stays "Sync off" with what still waits.
        if (!RealtimeOn) { await ShowOffAsync(path).ConfigureAwait(false); return; }
        Publish(next);
    }
}
