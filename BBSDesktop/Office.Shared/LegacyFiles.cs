// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared;

/// <summary>Files of earlier versions that nothing maintains any more (docs/ops-first-workflow.md 16.6).</summary>
public static class LegacyFiles
{
    /// <summary>
    /// Delete <c>active-project.json</c> in the local state folder. MynaCAD used to write it (which drawing is open) and MynaOps used to read it; the
    /// leases replace it and it carries no data of its own. Best effort: never throws. True when a file was removed.
    /// </summary>
    public static bool RemoveActiveProjectPointer(string? localDirectory = null)
    {
        try
        {
            var path = Path.Combine(localDirectory ?? SuiteWire.LocalDirectory, "active-project.json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
