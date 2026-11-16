// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared;

/// <summary>What a MynaOps start (or a second start that is forwarded) asks for.</summary>
public enum StartupKind
{
    /// <summary>Nothing beyond starting, or just bringing the window to the front.</summary>
    None,
    /// <summary>Open this project file (a double-clicked <c>.osp</c>); a file without a configuration goes through the Adopt window.</summary>
    OpenProject,
    /// <summary>A drawing (<c>.dwg</c>, <c>.dxf</c>) handed over by MynaCAD (<c>--import-pick</c>): ask where it goes.</summary>
    ImportPick,
}

/// <summary>
/// A request on the command line or on the MynaOps pipe (docs/ops-first-workflow.md 16.4): the kind and an absolute, existing file. The path is hostile input
/// (a shell verb, MynaCAD, any local process): bounded, no NUL, checked for extension and existence, never opened here.
/// </summary>
public sealed record StartupRequest(StartupKind Kind, string Path)
{
    public const int MaxPath = 4096;
    public static readonly StartupRequest None = new(StartupKind.None, "");

    /// <summary>Check one path for a kind. Null when it is not acceptable.</summary>
    public static StartupRequest? Make(StartupKind kind, string? path)
    {
        if (kind == StartupKind.None) return None;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        if (path.Length == 0 || path.Length > MaxPath || path.IndexOf('\0') >= 0) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
        var ext = System.IO.Path.GetExtension(full);
        var ok = kind == StartupKind.OpenProject
            ? ext.Equals(OdpFile.Extension, StringComparison.OrdinalIgnoreCase)
            : ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase);
        if (!ok) return null;
        try { if (!System.IO.File.Exists(full)) return null; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
        return new StartupRequest(kind, full);
    }

    /// <summary>
    /// The request in a command line (without the program name): <c>--import-pick &lt;drawing&gt;</c>, or the first argument that is an existing <c>.osp</c>.
    /// Anything else (other options, other files) is ignored; <see cref="None"/> when nothing usable is there.
    /// </summary>
    public static StartupRequest Parse(IEnumerable<string>? args)
    {
        if (args is null) return None;
        var list = args.Take(64).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == "--import-pick")
            {
                if (i + 1 < list.Count && Make(StartupKind.ImportPick, list[i + 1]) is { } pick) return pick;
                i++;
                continue;
            }
            if (list[i].StartsWith("--", StringComparison.Ordinal)) continue;
            if (Make(StartupKind.OpenProject, list[i]) is { } open) return open;
        }
        return None;
    }
}
