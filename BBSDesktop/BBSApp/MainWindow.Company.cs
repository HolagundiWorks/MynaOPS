// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp;

/// <summary>
/// MynaOps and MynaCAD share one project file (<c>.osp</c>) on this PC; it is their database. MynaOps needs no sign-in and no MynaHub: it opens that file
/// itself, and what MynaHub needs (estimates, running bills) leaves MynaOps as an exported CSV.
/// </summary>
public sealed partial class MainWindow
{
    O.Ui.Controls.SuiteWelcomeCard? _welcome;

    /// <summary>The first screen when no project is open: the suite's welcome card, over the work area until a project opens.</summary>
    internal void ShowWelcome()
    {
        var lockup = Path.Combine(AppContext.BaseDirectory, "Assets", "lockup.png");
        if (_welcome is not null || !File.Exists(lockup))
            return;
        _welcome = new O.Ui.Controls.SuiteWelcomeCard("MynaOps", lockup,
            "MynaOps opens or creates the project and starts MynaCAD from it. Open a project file here, or create a new project.");
        var open = new Button { Content = "Open project…" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent)
            open.Style = accent;
        open.Click += async (_, _) => await OpenProjectDialogAsync();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(open, "Welcome.Open");
        _welcome.Actions.Children.Add(open);
        var create = new Button { Content = "New project…" };
        create.Click += async (_, _) => await NewProjectAsync();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(create, "Welcome.New");
        _welcome.Actions.Children.Add(create);
        foreach (var recent in Services.OpsProject.Recent.Read().Where(File.Exists).Take(5))
        {
            var p = recent;
            var b = new Button { Content = p, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => OpenProjectFile(p);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "Welcome.Recent." + Path.GetFileNameWithoutExtension(p));
            _welcome.Extra.Children.Add(b);
        }
        Microsoft.UI.Xaml.Controls.Grid.SetRow(_welcome, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_welcome, 3);
        _welcome.Backdrop = WorkArea.Background;
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_welcome, 100);
        WorkArea.Children.Add(_welcome);
    }

    void HideWelcome()
    {
        if (_welcome is null)
            return;
        WorkArea.Children.Remove(_welcome);
        _welcome = null;
    }

    async void OpenProject_Click(object sender, RoutedEventArgs e) => await OpenProjectDialogAsync();

    /// <summary>Choose where a project that has no file yet is saved: a new project file (.osp) that MynaCAD can open too.</summary>
    async Task<string?> ChooseNewProjectFileAsync()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = string.IsNullOrWhiteSpace(Services.ProjectStore.Current.Name) ? "Project" : Services.ProjectStore.Current.Name };
        picker.FileTypeChoices.Add("Project file", new List<string> { ".osp" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
