// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.UI.Xaml;

namespace BBSApp;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; set; }

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
        // The launcher chooses the project and starts this app on it. Started any other way, ask the launcher to start O-CoreOp once a company is
        // signed in and a project is locked, and stop here. OSTUDIO_STANDALONE=1 skips this for development and automated tests.
        var locked = Office.Shared.SuiteProjectLock.FromEnvironment();
        var standalone = Office.Shared.SuiteProjectLock.Standalone || (locked is null && !Office.Shared.SuiteProjectLock.RequestLauncher("O-CoreOp"));
        if (locked is null && !standalone)
        {
            Exit();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
        if (locked is not null)
        {
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.OpenLockedProject(locked));
            return;
        }

        // Standalone only: a project file given on the command line is opened once the window is up.
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => Services.ProjectFiles.IsOdp(a) && System.IO.File.Exists(a));
        if (file is not null)
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.OpenProjectFile(System.IO.Path.GetFullPath(file)));
    }
}
