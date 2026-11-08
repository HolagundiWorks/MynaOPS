// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Diagnostics;
using System.Globalization;

namespace Office.Shared;

/// <summary>The project the launcher locked: its id in the company file and its project file.</summary>
public sealed record LockedProject(long ProjectId, string OspPath, string? DisplayName = null);

/// <summary>
/// The project is chosen in the launcher (or Kivo), never in this app. The launcher starts the app with
/// <c>OSTUDIO_PROJECT_ID</c> and <c>OSTUDIO_PROJECT_OSP</c>. An app started any other way asks the launcher to start it once a company is
/// signed in and a project is locked.
/// </summary>
public static class SuiteProjectLock
{
    public const string EnvStandalone = "OSTUDIO_STANDALONE";
    public const string EnvLauncher = "OSTUDIO_LAUNCHER";

    public static LockedProject? FromEnvironment()
    {
        var osp = Environment.GetEnvironmentVariable(SuiteWire.EnvProjectOsp);
        var id = Environment.GetEnvironmentVariable(SuiteWire.EnvProjectId);
        var name = Environment.GetEnvironmentVariable(SuiteWire.EnvProjectName);
        if (string.IsNullOrWhiteSpace(osp) || !long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var projectId))
            return null;
        return new LockedProject(projectId, osp, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    /// <summary>Development and automated tests set <c>OSTUDIO_STANDALONE=1</c> to run without the launcher.</summary>
    public static bool Standalone => Environment.GetEnvironmentVariable(EnvStandalone) == "1";

    public static string? LauncherPath()
    {
        var given = Environment.GetEnvironmentVariable(EnvLauncher);
        if (!string.IsNullOrWhiteSpace(given) && File.Exists(given))
            return given;
        var folder = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\O-Studio", "InstallLocation", null) as string;
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "O-Studio");
        foreach (var root in new[] { folder, programs })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var path = Path.Combine(root, "launcher", "O-Studio.Launcher.exe");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>Start (or wake) the launcher with a request to start <paramref name="app"/> once the company and project are ready. False when there is no launcher.</summary>
    public static bool RequestLauncher(string app)
    {
        var exe = LauncherPath();
        if (exe is null)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--launch " + app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
