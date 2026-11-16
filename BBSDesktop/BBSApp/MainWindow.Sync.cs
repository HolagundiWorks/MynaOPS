// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using BBSApp.Services;
using BBSApp.Services.Odp;
using Office.Shared;

namespace BBSApp;

/// <summary>
/// Realtime sync on/off and Sync now (docs/ops-first-workflow.md section 15). Off pauses the 2 s poll, the watcher-driven poll and the status refresh;
/// writing (File > Save) is never paused. Sync now saves the project record when it has unsaved edits, then reads the file once and refreshes.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Switch this program's realtime sync (the ribbon and the Home page); MynaCAD's own switch is separate (Q3).</summary>
    internal async Task SetRealtimeSyncAsync(bool on)
    {
        await SyncStatusService.SetRealtimeAsync(ProjectStore.Current.FilePath, on);
        DispatcherQueue.TryEnqueue(BuildNavigation);
    }

    /// <summary>Flush (the normal save of unsaved edits), then one forced poll and refresh, whatever the switch says. False when the save failed.</summary>
    internal async Task<bool> SyncNowAsync()
    {
        var path = ProjectStore.Current.FilePath;
        if (!string.IsNullOrEmpty(path) && ProjectFiles.IsOdp(path) && ProjectStore.Current.IsDirty && !await SaveProjectAsync(quiet: true))
            return false;
        await ServedProject.PollAsync(path, force: true);
        // The read views read the file as it is now when they are shown: what is in it has been taken.
        await SyncStatusService.AcceptAsync(path);
        return true;
    }
}
