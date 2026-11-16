// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Runtime.InteropServices;
using BBSApp.Services;
using BBSApp.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Office.Shared;

namespace BBSApp;

/// <summary>
/// The Ops-first front door (docs/ops-first-workflow.md section 1.1): MynaOps opens or creates the project, holds it (lease and session), adopts an
/// existing file that has no configuration, and starts MynaCAD from it. Closing MynaOps while MynaCAD has the project asks first.
/// </summary>
public sealed partial class MainWindow
{
    bool _closeConfirmed;

    /// <summary>Called once from the constructor: the close question.</summary>
    void WireProjectLifetime()
    {
        AppWindow.Closing += (sender, e) =>
        {
            if (_closeConfirmed) return;
            if (OpsProject.Session is { } s && ProjectLease.IsHeld(s.Path, ProjectLease.AppCad))
            {
                e.Cancel = true;
                _ = AskCloseAsync(s.Path);
            }
        };
        Closed += (_, _) => OpsProject.Detach();
    }

    /// <summary>MynaOps is being closed while MynaCAD has the project: Close both, or Keep MynaCAD open (it keeps working on the file).</summary>
    async Task AskCloseAsync(string path)
    {
        var dlg = new ContentDialog
        {
            Title = "MynaCAD has this project open",
            Content = new TextBlock
            {
                Text = "Close both asks MynaCAD to save and close first (it asks about unsaved work). Keep MynaCAD open leaves it running on the project without MynaOps.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
            },
            PrimaryButtonText = "Close both",
            SecondaryButtonText = "Keep MynaCAD open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult r;
        try { r = await dlg.ShowAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return; }
        if (r == ContentDialogResult.Primary)
        {
            AppNotify.Info("MynaCAD", "Asking MynaCAD to save and close…");
            if (!await OpsProject.CloseCadAsync(path, TimeSpan.FromSeconds(120)))
            {
                AppNotify.Warning("MynaCAD", "MynaCAD did not close (it may be waiting for an answer). MynaOps stays open.");
                return;
            }
        }
        else if (r != ContentDialogResult.Secondary) return;
        _closeConfirmed = true;
        Close();
    }

    // ---- open and new

    async void NewProject_Click(object sender, RoutedEventArgs e) => await NewProjectAsync();

    async void OpenCad_Click(object sender, RoutedEventArgs e) => await OpenCadFromMenuAsync();

    async Task OpenCadFromMenuAsync()
    {
        if (OpsProject.Session is not { } s) { AppNotify.Warning("MynaCAD", "Open or create a project first: MynaCAD is opened from a project."); return; }
        if (!s.Online) { AppNotify.Warning("MynaCAD", "The project database is offline. Take it online on the Home page first."); return; }
        if (!await SaveProjectAsync(quiet: true)) return;
        var r = await OpsProject.Launcher.OpenAsync(s.Path);
        if (r.Outcome is CadLaunch.Started or CadLaunch.Activated) AppNotify.Success("MynaCAD", r.Message);
        else AppNotify.Error("MynaCAD", r.Message);
    }

    /// <summary>File &gt; Open project: pick a project file.</summary>
    internal async Task OpenProjectDialogAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.List, SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".osp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenProjectFileAsync(file.Path);
    }

    /// <summary>File &gt; New project: the create-project wizard, then the new project is the open one.</summary>
    internal async Task NewProjectAsync(string? suggestedName = null, string? importDrawing = null)
    {
        if (!await LeaveCurrentProjectAsync()) return;
        var wizard = new NewProjectWindow(OpsProject.Path is { } p ? Path.GetDirectoryName(p) : null, suggestedName);
        await wizard.ShowModalAsync(this);
        if (wizard.CreatedPath is not { } path) return;
        await OpenCreatedAsync(path);
        // New project from a drawing: MynaCAD opens the new project and imports the drawing into it (never beside the source)
        if (importDrawing is not null && OpsProject.Session is { } s && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
            await OpenCadWithImportAsync(s, importDrawing);
    }

    /// <summary>
    /// A drawing handed over by MynaCAD (<c>--import-pick</c>, docs/ops-first-workflow.md 16.5): import it into the open project, make a new project
    /// from it, or cancel. MynaOps never reads the drawing; MynaCAD converts it inside the project file.
    /// </summary>
    internal async Task ImportPickAsync(string drawing)
    {
        var canImport = OpsProject.Session is { Online: true };
        var dlg = new ContentDialog
        {
            Title = $"Import {Path.GetFileName(drawing)}",
            Content = new TextBlock
            {
                Text = canImport
                    ? $"Import the drawing into {ProjectStore.Current.Name}, or make a new project from it. The drawing is stored inside the project file; nothing is written beside {Path.GetFileName(drawing)}."
                    : "Make a new project from this drawing. It is stored inside the project file; nothing is written beside the drawing.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 440,
            },
            PrimaryButtonText = canImport ? "Import into project" : "",
            SecondaryButtonText = "New project",
            CloseButtonText = "Cancel",
            DefaultButton = canImport ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult r;
        try { r = await dlg.ShowAsync(); } catch (Exception ex) when (ex is InvalidOperationException or COMException) { return; }
        if (r == ContentDialogResult.Primary && OpsProject.Session is { } s)
        {
            if (!await SaveProjectAsync(quiet: true)) return;
            await OpenCadWithImportAsync(s, drawing);
        }
        else if (r == ContentDialogResult.Secondary)
            await NewProjectAsync(Path.GetFileNameWithoutExtension(drawing), drawing);
    }

    async Task OpenCadWithImportAsync(ProjectSession s, string drawing)
    {
        if (!s.Online) { AppNotify.Warning("MynaCAD", "The project database is offline. Take it online on the Home page first."); return; }
        var r = await OpsProject.Launcher.OpenAsync(s.Path, drawing);
        if (r.Outcome is CadLaunch.Started or CadLaunch.Activated) AppNotify.Success("MynaCAD", r.Message);
        else AppNotify.Error("MynaCAD", r.Message);
    }

    /// <summary>A request from the command line or from a second MynaOps start (<see cref="OpsInstance"/>).</summary>
    internal void HandleStartupRequest(StartupRequest request)
    {
        switch (request.Kind)
        {
            case StartupKind.OpenProject: OpenProjectFile(request.Path); break;
            case StartupKind.ImportPick: _ = ImportPickAsync(request.Path); break;
        }
    }

    /// <summary>The project the wizard just made: take its lease, fill the quantities side from its configuration, save the project document, show Home.</summary>
    async Task OpenCreatedAsync(string path)
    {
        var opened = OpsProject.TryOpen(path);
        if (opened.Session is null) { AppNotify.Error("New project", "The new project is held elsewhere: " + opened.Refusal); return; }
        try
        {
            var cfg = await Task.Run(() => ProjectConfig.Probe(path).Read.Config);
            HideWelcome();
            OpsProject.SeedStoreFromConfig(ProjectStore.Current, cfg);
            ProjectStore.Current.FilePath = path;
            ProjectStore.Current.Info.OspPath = path;
            if (!ProjectFiles.Save(path, ProjectStore.Current.ToJson(), out var err)) throw new IOException(err ?? "The project could not be saved.");
            ProjectStore.Current.IsDirty = false;
        }
        catch (Exception ex) when (ex is IOException or ConfigException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            opened.Session.Dispose();
            AppNotify.Error("New project", ex.Message);
            return;
        }
        OpsProject.Commit(opened.Session);
        AfterProjectOpened(path, "Project created");
    }

    /// <summary>
    /// Make a project file the open project. A <c>.osp</c> is taken under MynaOps' lease first; one without a configuration goes through the Adopt
    /// page; a file that cannot be held or read leaves the project that was open as it was.
    /// </summary>
    internal void OpenProjectFile(string path, string? launcherDisplayName = null) => _ = OpenProjectFileAsync(path, launcherDisplayName);

    internal async Task OpenProjectFileAsync(string path, string? launcherDisplayName = null)
    {
        path = Path.GetFullPath(path);
        if (!ProjectFiles.IsOdp(path))
        {
            // a project file from before (.bbsproj, .json): no lease, no MynaCAD
            if (!await LeaveCurrentProjectAsync()) return;
            HideWelcome();
            OpsProject.Detach();
            if (!ProjectFiles.OpenInto(ProjectStore.Current, path, out var legacyErr)) { AppNotify.Error("Open failed", legacyErr ?? "Could not open project."); return; }
            AfterProjectOpened(path, "Project opened");
            return;
        }
        if (!File.Exists(path)) { AppNotify.Error("Open failed", $"{Path.GetFileName(path)} was not found."); OpsProject.Recent.Remove(path); return; }

        var sameProject = OpsProject.Session is { } cur && string.Equals(cur.Path, path, StringComparison.OrdinalIgnoreCase);
        if (!sameProject && !await LeaveCurrentProjectAsync()) return;

        // 1. the lease
        var opened = OpsProject.TryOpen(path);
        if (opened.Refusal == SessionRefusal.LiveElsewhere && opened.Holder is { } h)
        {
            var age = (DateTime.UtcNow - h.HeartbeatUtc).TotalSeconds;
            var take = await OpsUi.ConfirmAsync(Content.XamlRoot, "This project looks open in MynaOps elsewhere",
                $"MynaOps on {h.Machine} ({h.User}) used this project {age:0} seconds ago. Opening it here as well can make the two overwrite each other. Take it over only when you know the other MynaOps is closed.",
                "Take over", "Cancel");
            if (!take) return;
            opened = OpsProject.TryOpen(path, takeOver: true);
        }
        if (opened.Session is null)
        {
            var who = opened.Lease is { } l ? $" (process {l.Pid})" : "";
            AppNotify.Error("Project is open elsewhere", $"{Path.GetFileName(path)} is open in another MynaOps window{who}. Close it there first.");
            return;
        }
        var session = opened.Session;
        bool created = !sameProject;          // a session this call made: dispose it when the open fails

        try
        {
            // 2. adoption
            ConfigLoaded loaded;
            try { loaded = await Task.Run(() => ProjectConfig.Probe(path)); }
            catch (ConfigException ex) { throw new InvalidDataException("The project configuration cannot be read: " + ex.Message); }
            if (!loaded.Configured)
            {
                if (ProjectLease.IsHeld(path, ProjectLease.AppCad))
                    throw new InvalidOperationException("MynaCAD has this project open. Close it in MynaCAD first; then MynaOps can adopt the project's settings.");
                var proposal = await Task.Run(() => ProjectAdoption.Propose(path));
                var adopt = new AdoptWindow(path, proposal);
                await adopt.ShowModalAsync(this);
                while (adopt.ReadFromCad)
                {
                    // MynaCAD described the drawing (D8): show the window again with its proposal.
                    proposal = await Task.Run(() => ProjectAdoption.Propose(path));
                    adopt = new AdoptWindow(path, proposal);
                    await adopt.ShowModalAsync(this);
                }
                if (!adopt.Adopted) { if (created) session.Dispose(); return; }
            }

            // 3. read the project
            HideWelcome();
            var hadDoc = await Task.Run(() => { using var odp = OdpFile.Open(path); return odp.ProjectDoc() is not null; });
            if (!ProjectFiles.OpenInto(ProjectStore.Current, path, out var err)) throw new IOException(err ?? "Could not open project.");
            if (!hadDoc)
            {
                // a project file MynaCAD made (no MynaOps project in it yet): the project starts from the configuration
                var cfg = ProjectConfig.Probe(path).Read.Config;
                OpsProject.SeedStoreFromConfig(ProjectStore.Current, cfg);
                ProjectStore.Current.FilePath = path;
                ProjectStore.Current.Info.OspPath = path;
                ProjectStore.Current.IsDirty = true;
            }
            if (hadDoc) OpsProject.ReadLevelsBack(ProjectStore.Current, path);           // a configuration written elsewhere since the quantities side was saved
            if (!string.IsNullOrWhiteSpace(launcherDisplayName) && (string.IsNullOrWhiteSpace(ProjectStore.Current.Info.Name) || ProjectStore.Current.Name == "Untitled Project"))
                ProjectStore.Current.Name = launcherDisplayName.Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (created) session.Dispose();
            if (OpsProject.Session is null) ShowWelcome();
            AppNotify.Error("Open failed", ex.Message);
            return;
        }
        OpsProject.Commit(session);
        AfterProjectOpened(path, "Project opened");
    }

    void AfterProjectOpened(string path, string toastTitle)
    {
        RefreshWindowTitle();
        UpdatePersonaBadge();
        SelectNavTag("home");
        AppNotify.Success(toastTitle, $"{ProjectStore.Current.Name}  ·  {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Before another project replaces the open one: unsaved changes are asked about, and a MynaCAD that has the open project is asked to close or left
    /// open (it then works on without MynaOps). False when the user cancels.
    /// </summary>
    async Task<bool> LeaveCurrentProjectAsync()
    {
        if (OpsProject.Session is not { } s) return true;
        if (ProjectStore.Current.IsDirty && ProjectFiles.IsOdp(ProjectStore.Current.FilePath))
        {
            var dlg = new ContentDialog
            {
                Title = $"Save changes to {ProjectStore.Current.Name}?",
                PrimaryButtonText = "Save", SecondaryButtonText = "Don't save", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
            };
            ContentDialogResult r;
            try { r = await dlg.ShowAsync(); } catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
            if (r == ContentDialogResult.None) return false;
            if (r == ContentDialogResult.Primary && !await SaveProjectAsync(quiet: true)) return false;
        }
        if (ProjectLease.IsHeld(s.Path, ProjectLease.AppCad))
        {
            var dlg = new ContentDialog
            {
                Title = "MynaCAD has the open project",
                Content = new TextBlock { Text = $"MynaCAD is working on {Path.GetFileName(s.Path)}. Close it first, or leave it open (it keeps working on that project without MynaOps).", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                PrimaryButtonText = "Close MynaCAD", SecondaryButtonText = "Leave it open", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
            };
            ContentDialogResult r;
            try { r = await dlg.ShowAsync(); } catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
            if (r == ContentDialogResult.None) return false;
            if (r == ContentDialogResult.Primary && !await OpsProject.CloseCadAsync(s.Path, TimeSpan.FromSeconds(120)))
            {
                AppNotify.Warning("MynaCAD", "MynaCAD did not close (it may be waiting for an answer).");
                return false;
            }
        }
        return true;
    }

    // ---- save

    /// <summary>
    /// Save: the project document into the project file, then the Settings and Levels page values into <c>config.*</c> (one revision of their own,
    /// nothing when nothing differs). A project that has no file yet is saved to a new project file, which is then held and adopted. Returns false
    /// when it was not saved.
    /// </summary>
    internal async Task<bool> SaveProjectAsync(bool quiet = false)
    {
        var store = ProjectStore.Current;
        var firstSave = false;
        if (string.IsNullOrEmpty(store.FilePath) || !ProjectFiles.IsOdp(store.FilePath))
        {
            var chosen = await ChooseNewProjectFileAsync();
            if (string.IsNullOrEmpty(chosen)) return false;
            store.FilePath = chosen;
            store.Info.OspPath = chosen;
            firstSave = true;
        }
        var path = store.FilePath!;
        if (!ProjectFiles.Save(path, store.ToJson(), out var err))
        {
            AppNotify.Error("Save failed", err ?? "Could not save.");
            return false;
        }
        store.IsDirty = false;
        try
        {
            if (firstSave)
            {
                // a file MynaOps just made has no configuration: it is configured from this project's own details, with nothing to ask
                var opened = OpsProject.TryOpen(path);
                if (opened.Session is not null)
                {
                    await Task.Run(() => ProjectAdoption.Adopt(path, ProjectAdoption.Propose(path).Config));
                    OpsProject.Commit(opened.Session);
                }
            }
            else if (await Task.Run(() => OpsProject.PublishConfig(store, path)) is { Changed.Count: > 0 } w && !quiet)
                AppNotify.Info("Configuration", $"Project configuration revision {w.Rev} written; MynaCAD applies it.");
        }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            AppNotify.Error("Configuration not written", ex.Message);
        }
        RefreshWindowTitle();
        if (!quiet) AppNotify.Success("Saved", Path.GetFileName(path));
        return true;
    }

    // ---- AI

    /// <summary>The AI agent was started or stopped from the Home page: keep the window's own handle and assistant in step.</summary>
    internal void AiHostChanged(Office.Ai.AiCoreListener? listener)
    {
        _aiCore = listener;
        ForgetAssistant();
    }
}
