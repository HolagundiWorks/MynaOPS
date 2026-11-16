// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using BBSApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Office.Shared;

namespace BBSApp.Views;

/// <summary>
/// Project &gt; Home (docs/ops-first-workflow.md 1.1, step 4): the front door. With no project open it offers Open project, New project and the recent
/// list. With one open it shows the project and its configuration, <b>MynaCAD</b> (state and <b>Open MynaCAD</b>), <b>Database</b> (online or offline),
/// the <b>AI agent</b> (<b>Start</b> or <b>Stop</b>) and the sync status. It reads the file's configuration with a read-only probe every two seconds
/// while the database is online, and writes nothing.
/// </summary>
public sealed class ProjectHomePage : Page
{
    readonly StackPanel _page = new() { Spacing = 12, Padding = new Thickness(4, 4, 4, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly TextBlock _cadState = OpsUi.Text(""), _cadApplied = OpsUi.Text("", "CaptionSecondaryStyle"), _dbState = OpsUi.Text(""), _dbNote = OpsUi.Text("", "CaptionSecondaryStyle"),
        _aiState = OpsUi.Text(""), _aiDetail = OpsUi.Text("", "CaptionSecondaryStyle"), _syncState = OpsUi.Text(""), _busy = OpsUi.Text("", "CaptionSecondaryStyle");
    readonly StackPanel _facts = new();
    readonly StackPanel _config = new();
    Button? _openCad, _dbToggle, _aiToggle, _cancelWait, _syncToggle, _syncNow, _cadSyncToggle, _cadSyncNow;
    TextBlock _cadSyncState = OpsUi.Text("", "CaptionSecondaryStyle");
    StackPanel _cadSyncRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    bool _cadSyncOn = true;
    CancellationTokenSource? _wait;
    bool _building, _refreshing;
    string? _shownPath;

    public ProjectHomePage()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Content = new ScrollViewer { Content = _page, HorizontalScrollMode = ScrollMode.Disabled };
        Loaded += async (_, _) =>
        {
            OpsProject.Changed += OnChanged;
            SyncStatusService.Changed += OnSync;
            SyncStatusService.RealtimeChanged += OnRealtime;
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
            Build();
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            OpsProject.Changed -= OnChanged;
            SyncStatusService.Changed -= OnSync;
            SyncStatusService.RealtimeChanged -= OnRealtime;
            _wait?.Cancel();
        };
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(() => { if (_shownPath != OpsProject.Path) Build(); _ = RefreshAsync(); });

    void OnSync(SyncStatus s) => DispatcherQueue.TryEnqueue(() => _syncState.Text = s.Text.Length > 0 ? s.Text : "Synced");

    void OnRealtime(bool on) => DispatcherQueue.TryEnqueue(ShowSyncControls);

    void ShowSyncControls()
    {
        if (_syncToggle is not null)
        {
            _syncToggle.Content = SyncStatusService.RealtimeOn ? "Realtime sync: on" : "Realtime sync: off";
            AutomationProperties.SetName(_syncToggle, (string)_syncToggle.Content);
        }
        if (_cadSyncToggle is not null)
        {
            _cadSyncToggle.Content = _cadSyncOn ? "MynaCAD sync: on" : "MynaCAD sync: off";
            AutomationProperties.SetName(_cadSyncToggle, (string)_cadSyncToggle.Content);
        }
    }

    async Task ToggleSyncAsync()
    {
        if (App.MainWindow is { } w) await w.SetRealtimeSyncAsync(!SyncStatusService.RealtimeOn);
    }

    async Task SyncNowAsync()
    {
        if (App.MainWindow is { } w) await w.SyncNowAsync();
    }

    /// <summary>MynaCAD's own sync state over the pipe (shown only while MynaCAD holds the project): one round trip, no file read.</summary>
    async Task RefreshCadSyncAsync(string path, bool cadHeld)
    {
        _cadSyncRow.Visibility = cadHeld ? Visibility.Visible : Visibility.Collapsed;
        if (!cadHeld) { _cadSyncState.Text = ""; return; }
        var st = await OpsProject.Launcher.SyncStatusAsync(path);
        ApplyCadSync(st);
    }

    void ApplyCadSync(CadSyncState? st)
    {
        if (st is null) { _cadSyncState.Text = "MynaCAD did not answer."; return; }
        _cadSyncOn = st.On;
        _cadSyncState.Text = st.On ? "MynaCAD: realtime sync on" : "MynaCAD: " + (st.Text.Length > 0 ? st.Text : "Sync off");
        ShowSyncControls();
    }

    async Task ToggleCadSyncAsync()
    {
        if (OpsProject.Session is not { } s) return;
        ApplyCadSync(await OpsProject.Launcher.SetSyncAsync(s.Path, !_cadSyncOn));
    }

    async Task CadSyncNowAsync()
    {
        if (OpsProject.Session is not { } s) return;
        _cadSyncState.Text = "MynaCAD: syncing...";
        ApplyCadSync(await OpsProject.Launcher.SyncNowAsync(s.Path));
    }

    // ---- layout

    void Build()
    {
        if (_building) return;
        _building = true;
        try
        {
            _shownPath = OpsProject.Path;
            _page.Children.Clear();
            if (OpsProject.Session is null) { BuildStart(); return; }

            var intro = OpsUi.Text("This project is open here. MynaCAD is started from this page; the project's details, levels and layer sets are changed here and MynaCAD follows them.", "CaptionSecondaryStyle");
            _page.Children.Add(intro);

            AutomationProperties.SetAutomationId(_facts, "Home.Project");
            _page.Children.Add(OpsUi.Card("Project", _facts, "Home.ProjectCard"));
            AutomationProperties.SetAutomationId(_config, "Home.Config");
            _page.Children.Add(OpsUi.Card("Configuration", _config, "Home.ConfigCard"));

            // MynaCAD
            var cad = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_cadState, "Home.CadState");
            cad.Children.Add(_cadState);
            cad.Children.Add(_cadApplied);
            _openCad = OpsUi.Button("Open MynaCAD", true, "Home.OpenCad", () => _ = OpenCadAsync());
            cad.Children.Add(_openCad);
            _page.Children.Add(OpsUi.Card("MynaCAD", cad, "Home.CadCard"));

            // Database
            var db = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_dbState, "Home.DbState");
            db.Children.Add(_dbState);
            db.Children.Add(_dbNote);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _dbToggle = OpsUi.Button("Take database offline", false, "Home.DbToggle", () => _ = ToggleDbAsync());
            _cancelWait = OpsUi.Button("Cancel", false, "Home.DbCancel", () => _wait?.Cancel());
            _cancelWait.Visibility = Visibility.Collapsed;
            row.Children.Add(_dbToggle);
            row.Children.Add(_cancelWait);
            db.Children.Add(row);
            AutomationProperties.SetAutomationId(_busy, "Home.DbBusy");
            AutomationProperties.SetLiveSetting(_busy, AutomationLiveSetting.Polite);
            db.Children.Add(_busy);
            _page.Children.Add(OpsUi.Card("Database", db, "Home.DbCard"));

            // AI
            var ai = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_aiState, "Home.AiState");
            ai.Children.Add(_aiState);
            ai.Children.Add(_aiDetail);
            _aiToggle = OpsUi.Button("Stop", false, "Home.AiToggle", () => _ = ToggleAiAsync());
            ai.Children.Add(_aiToggle);
            _page.Children.Add(OpsUi.Card("AI agent", ai, "Home.AiCard"));

            // Sync
            var sync = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_syncState, "Home.SyncState");
            _syncState.Text = SyncStatusService.Current.Text.Length > 0 ? SyncStatusService.Current.Text : "Synced";
            sync.Children.Add(_syncState);
            var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _syncToggle = OpsUi.Button("Realtime sync: on", false, "Home.SyncToggle", () => _ = ToggleSyncAsync());
            _syncNow = OpsUi.Button("Sync now", false, "Home.SyncNow", () => _ = SyncNowAsync());
            syncRow.Children.Add(_syncToggle);
            syncRow.Children.Add(_syncNow);
            sync.Children.Add(syncRow);
            // MynaCAD's own switch (independent of this one), shown while MynaCAD holds the project.
            _cadSyncState = OpsUi.Text("", "CaptionSecondaryStyle");
            _cadSyncRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_cadSyncState, "Home.CadSyncState");
            _cadSyncToggle = OpsUi.Button("MynaCAD sync: on", false, "Home.CadSyncToggle", () => _ = ToggleCadSyncAsync());
            _cadSyncNow = OpsUi.Button("Sync MynaCAD now", false, "Home.CadSyncNow", () => _ = CadSyncNowAsync());
            _cadSyncRow.Children.Add(_cadSyncToggle);
            _cadSyncRow.Children.Add(_cadSyncNow);
            sync.Children.Add(_cadSyncState);
            sync.Children.Add(_cadSyncRow);
            ShowSyncControls();
            _page.Children.Add(OpsUi.Card("Sync with MynaCAD", sync, "Home.SyncCard"));
        }
        finally { _building = false; }
    }

    void BuildStart()
    {
        _page.Children.Add(OpsUi.Text("No project is open. Open a project file, or create a new project: MynaOps holds the project, and MynaCAD is opened from it.", "CaptionSecondaryStyle"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(OpsUi.Button("Open project…", true, "Home.Open", () => _ = App.MainWindow?.OpenProjectDialogAsync()));
        actions.Children.Add(OpsUi.Button("New project…", false, "Home.New", () => _ = App.MainWindow?.NewProjectAsync()));
        _page.Children.Add(actions);
        var recent = new StackPanel { Spacing = 4 };
        AutomationProperties.SetAutomationId(recent, "Home.Recent");
        var list = OpsProject.Recent.Read().Where(File.Exists).ToList();
        if (list.Count == 0) recent.Children.Add(OpsUi.Text("No recent projects yet."));
        foreach (var path in list)
        {
            var p = path;
            var b = OpsUi.Button(p, false, "Home.Recent." + Path.GetFileNameWithoutExtension(p), () => App.MainWindow?.OpenProjectFile(p));
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            recent.Children.Add(b);
        }
        _page.Children.Add(OpsUi.Card("Recent projects", recent, "Home.RecentCard"));
    }

    // ---- refresh

    async Task RefreshAsync()
    {
        if (_refreshing || OpsProject.Session is not { } s || _openCad is null) return;
        _refreshing = true;
        try
        {
            var path = s.Path;
            var online = s.Online;
            // the facts are read off the UI thread, with a read-only probe; while the database is offline the file is left alone
            var (info, loaded, cadHeld, err) = online
                ? await Task.Run(() =>
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        return ((fi.Exists ? fi.Length : 0L, fi.Exists ? fi.LastWriteTime : default), (ConfigLoaded?)ProjectConfig.Probe(path), ProjectLease.IsHeld(path, ProjectLease.AppCad), (string?)null);
                    }
                    catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException) { return ((0L, default(DateTime)), null, ProjectLease.IsHeld(path, ProjectLease.AppCad), ex.Message); }
                })
                : ((0L, default(DateTime)), null, ProjectLease.IsHeld(path, ProjectLease.AppCad), null);

            if (online)
            {
                var cfg = loaded?.Read.Config;
                var rows = new List<(string, string, string?)>
                {
                    ("Name", ProjectStore.Current.Name, "Home.Name"),
                    ("Client", Dash(cfg?.Project?.Client ?? ProjectStore.Current.Info.ClientName), "Home.Client"),
                    ("Location", Dash(cfg?.Project?.Location ?? ProjectStore.Current.Info.Location), "Home.Location"),
                    ("Persona", Dash(cfg?.Project?.Persona ?? ""), "Home.Persona"),
                    ("File", path, "Home.Path"),
                    ("Size", OpsUi.Size(info.Item1), "Home.Size"),
                };
                _facts.Children.Clear();
                _facts.Children.Add(OpsUi.Facts(rows));

                _config.Children.Clear();
                if (cfg is null || !cfg.Configured)
                    _config.Children.Add(OpsUi.Text(err ?? "This file has no project configuration yet."));
                else
                {
                    var lv = cfg.Levels?.Items ?? Array.Empty<ConfigLevelItem>();
                    _config.Children.Add(OpsUi.Facts(new (string, string, string?)[]
                    {
                        ("Revision", $"configuration {cfg.Revision}, file {loaded!.Revision}", "Home.ConfigRev"),
                        ("Drawing unit", cfg.Units?.DrawingUnit + (cfg.Units?.Locked == true ? " (locked)" : ""), "Home.Unit"),
                        ($"Levels ({lv.Count})", lv.Count == 0 ? "none" : string.Join(", ", lv.Select(l => l.Name)), "Home.Levels"),
                        ("Layer sets", cfg.LayerSets is { Sets.Count: > 0 } ? string.Join(", ", cfg.LayerSets.Sets) : "none", "Home.LayerSets"),
                        ("Discipline tabs", cfg.Tabs is { Items.Count: > 0 } ? string.Join(", ", cfg.Tabs.Items.Select(t => t.Name)) : "none", "Home.Tabs"),
                    }));
                }
                var applied = loaded?.Applied;
                _cadApplied.Text = applied is null ? "MynaCAD has not applied a configuration yet."
                    : $"MynaCAD has applied configuration revision {applied.Rev} ({applied.Result}).";
            }

            _cadState.Text = cadHeld ? "MynaCAD is open for this project." : "MynaCAD is not running.";
            _openCad.IsEnabled = online;
            _dbState.Text = online ? "Database: online. MynaOps holds the project." : "Database: offline. MynaCAD is closed and the project is released.";
            _dbNote.Text = online
                ? "Taking it offline asks MynaCAD to close, folds the write-ahead log into the file and lets go of the project, so Compact, Restore and copying the file are safe."
                : "Compact, Restore and copying the project file are possible now. Take it online to work again.";
            if (_dbToggle is not null && _wait is null)
            {
                _dbToggle.Content = online ? "Take database offline" : "Take database online";
                AutomationProperties.SetName(_dbToggle, (string)_dbToggle.Content);
            }
            var (ai, detail, running) = await Task.Run(async () =>
            {
                // Stopped here: nothing is asked of the port (a closed listener can leave a request waiting for its timeout).
                if (Office.Ai.SuiteAiCoordinator.StoppedByUser && !Office.Ai.SuiteAiCoordinator.IsHost)
                {
                    var off = Office.Ai.AiStatus.Describe(new Office.Ai.AiStatus.Snapshot(false, false, false, true, true, null, StoppedByUser: true));
                    return (off.Short, off.Detail, false);
                }
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var (a, d) = await Office.Ai.AiStatus.CheckAsync(cts.Token);
                    var up = Office.Ai.SuiteAiCoordinator.IsHost || await Office.Ai.RemoteLlm.IsAvailableAsync(cts.Token);
                    return (a, d, up);
                }
                catch (OperationCanceledException) { return ("AI: checking…", "The AI agent did not answer within three seconds.", false); }
            }).ConfigureAwait(true);
            _aiState.Text = ai;
            _aiDetail.Text = detail;
            if (_aiToggle is not null)
            {
                _aiToggle.Content = running ? "Stop" : "Start";
                AutomationProperties.SetName(_aiToggle, running ? "Stop the AI agent" : "Start the AI agent");
            }
            await RefreshCadSyncAsync(path, cadHeld);
            if (_syncState.Text.Length == 0) _syncState.Text = SyncStatusService.Current.Text.Length > 0 ? SyncStatusService.Current.Text : "Synced";
        }
        catch (Exception ex)
        {
            // a failed look is shown, never swallowed: the page stays usable and the next tick tries again
            _aiDetail.Text = "Could not read the state: " + ex.Message;
        }
        finally { _refreshing = false; }
    }

    static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    // ---- actions

    async Task OpenCadAsync()
    {
        if (App.MainWindow is not { } w || OpsProject.Session is not { } s) return;
        if (!await w.SaveProjectAsync(quiet: true)) return;       // MynaCAD should see what has been edited here
        var r = await OpsProject.Launcher.OpenAsync(s.Path);
        switch (r.Outcome)
        {
            case CadLaunch.Started: AppNotify.Success("MynaCAD", r.Message); break;
            case CadLaunch.Activated: AppNotify.Info("MynaCAD", r.Message); break;
            default: AppNotify.Error("MynaCAD", r.Message); break;
        }
        await RefreshAsync();
    }

    async Task ToggleDbAsync()
    {
        if (OpsProject.Session is not { } s || _dbToggle is null) return;
        if (!s.Online)
        {
            OpsProject.GoOnline(out var m);
            AppNotify.Info("Database", m);
            await RefreshAsync();
            return;
        }
        if (App.MainWindow is { } w) await w.SaveProjectAsync(quiet: true);
        _wait = new CancellationTokenSource();
        _dbToggle.IsEnabled = false;
        _cancelWait!.Visibility = ProjectLease.IsHeld(s.Path, ProjectLease.AppCad) ? Visibility.Visible : Visibility.Collapsed;
        _busy.Text = ProjectLease.IsHeld(s.Path, ProjectLease.AppCad) ? "Waiting for MynaCAD to close (it may ask about unsaved work)…" : "Folding the log into the file…";
        try
        {
            var r = await OpsProject.GoOfflineAsync(_wait.Token);
            _busy.Text = r.Message;
            AppNotify.Show("Database", r.Message, r.Offline ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { _busy.Text = "Cancelled; the project database is still online."; }
        finally
        {
            _wait.Dispose();
            _wait = null;
            _dbToggle.IsEnabled = true;
            _cancelWait.Visibility = Visibility.Collapsed;
        }
        await RefreshAsync();
    }

    async Task ToggleAiAsync()
    {
        if (_aiToggle is null) return;
        _aiToggle.IsEnabled = false;
        try
        {
            if ((string)_aiToggle.Content == "Stop")
            {
                Office.Ai.SuiteAiCoordinator.StopByUser();
                App.MainWindow?.AiHostChanged(null);
                AppNotify.Info("AI agent", "The AI agent is stopped.");
            }
            else
            {
                var l = Office.Ai.SuiteAiCoordinator.StartByUser();
                App.MainWindow?.AiHostChanged(l);
                if (l is null && !await Office.Ai.RemoteLlm.IsAvailableAsync(CancellationToken.None))
                    AppNotify.Warning("AI agent", "The AI agent could not be started here (the port is not free, or this MynaOps only uses a host started elsewhere).");
                else
                    AppNotify.Success("AI agent", "The AI agent is running.");
            }
        }
        finally { _aiToggle.IsEnabled = true; }
        await RefreshAsync();
    }
}
