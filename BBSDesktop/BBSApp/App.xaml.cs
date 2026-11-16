// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.UI.Xaml;

namespace BBSApp;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; set; }

    // kept for the life of the process: the pipe that receives forwarded starts
    Office.Shared.OpsInstance? _instance;

    /// <summary>App-wide navigation command surface (implemented by the main window).</summary>
    public static Services.Ai.IAppCommandBus? CommandBus => MainWindow;

    public App()
    {
        InitializeComponent();
        RequestedTheme = O.Ui.SuiteAppearance.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.RateBookStore.Current.EnsureLoaded();
        // MynaOps opens the shared .osp itself; it needs no launcher, no sign-in and no MynaHub.
        MainWindow = new MainWindow();
        MainWindow.Activate();
        // The pointer file MynaCAD used to write is gone (docs/ops-first-workflow.md 16.6); a stale one is removed.
        _ = Task.Run(() => Office.Shared.LegacyFiles.RemoveActiveProjectPointer());
        // A second start (a double-clicked project, a drawing handed over by MynaCAD) is forwarded to this window instead of opening another one.
        var window = MainWindow;
        _instance = new Office.Shared.OpsInstance(request =>
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                window.Activate();
                if (request.Kind != Office.Shared.StartupKind.None) window.HandleStartupRequest(request);
            });
            return true;
        });
        _instance.Start();
        // A project file or a drawing given on the command line is handled once the window is up.
        var start = Office.Shared.StartupRequest.Parse(Environment.GetCommandLineArgs().Skip(1));
        if (start.Kind == Office.Shared.StartupKind.OpenProject)
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.HandleStartupRequest(start));
        else
        {
            MainWindow.DispatcherQueue.TryEnqueue(MainWindow.ShowWelcome);
            if (start.Kind == Office.Shared.StartupKind.ImportPick)
                MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.HandleStartupRequest(start));
        }
    }
}
