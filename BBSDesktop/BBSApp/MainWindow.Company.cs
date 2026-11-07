// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using BBSApp.Services;
using BBSApp.Services.Company;
using BBSApp.Services.Odp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Office.Shared;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp;

/// <summary>
/// The company file belongs to Kivo. Falcon signs in as a Kivo user and pushes project items
/// (<c>aqc.project</c>) when the open <c>.osp</c> is linked to a Kivo project. Tren does not open the company file.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Opens the company file read-only when Kivo launched this app with a ticket. Falcon never asks for a company password.</summary>
    async Task StartFromSuiteAsync()
    {
        var path = Environment.GetEnvironmentVariable(SuiteWire.EnvCompany);
        var ticket = Environment.GetEnvironmentVariable(SuiteWire.EnvTicket);
        Environment.SetEnvironmentVariable(SuiteWire.EnvTicket, null);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !CompanyService.EngineAvailable)
            return;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            AppNotify.Info("Company", "Sign in to the company in Kivo, then open Falcon from its card to read the company's details.");
            return;
        }
        await Task.Delay(800);
        try
        {
            await CompanyService.OpenWithTicketAsync(path, ticket);
            AppNotify.Success("Company", $"{CompanyService.Current!.Name}: details are read from Kivo's company file.");
            await LinkOpenProjectToKivoAsync();
        }
        catch (OfficeException ex)
        {
            AppNotify.Warning("Company", ex.Message + " Open Falcon again from Kivo while signed in.");
        }
    }

    O.Ui.Controls.SuiteWelcomeCard? _welcome;

    /// <summary>The first screen when Falcon runs without a locked project (development and tests): the suite's welcome card, over the work area until a project opens.</summary>
    internal void ShowWelcome()
    {
        var lockup = Path.Combine(AppContext.BaseDirectory, "Assets", "lockup.png");
        if (_welcome is not null || !File.Exists(lockup))
            return;
        _welcome = new O.Ui.Controls.SuiteWelcomeCard("Falcon", lockup,
            "Falcon and Tren share one project file (.osp). Open that file here. Sign in as a Kivo user to push items to Kivo.");
        var open = new Button { Content = "Open project…" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent)
            open.Style = accent;
        open.Click += async (_, _) => await OpenSharedProjectAsync();
        var signIn = new Button { Content = "Sign in to Kivo…" };
        signIn.Click += async (_, _) => await SignInToKivoAsync();
        var push = new Button { Content = "Push to Kivo" };
        push.Click += async (_, _) => await PushItemsToKivoAsync();
        _welcome.Actions.Children.Add(open);
        _welcome.Actions.Children.Add(signIn);
        _welcome.Actions.Children.Add(push);
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

    /// <summary>Open the project the launcher locked. It must lie in a project folder that exists; the file itself is made on the first save.</summary>
    internal void OpenLockedProject(Office.Shared.LockedProject locked)
    {
        var folder = Path.GetDirectoryName(locked.OspPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            AppNotify.Error("Project", $"The folder of the project was not found: {folder}.");
            return;
        }

        OpenProjectFile(locked.OspPath, locked.DisplayName);
    }

    void StartCompanyPolling()
    {
        CompanyService.Changed += () => DispatcherQueue.TryEnqueue(RefreshCompanyBadge);
        RefreshCompanyBadge();
    }

    void RefreshCompanyBadge()
    {
        if (CompanyBadgeText is null) return;
        CompanyBadgeText.Text = CompanyService.StatusText;
        AutomationProperties.SetName(CompanyBadge, CompanyService.StatusText);
        ToolTipService.SetToolTip(CompanyBadge, CompanyService.IsOpen
            ? $"{CompanyService.Current!.Path}. Signed in. Push to Kivo sends this project's items."
            : "Sign in as a Kivo user to push items. The project file is shared with Tren.");
    }

    /// <summary>
    /// Take the open project's details from Kivo. The project is the one the take-off header names (company and project id); a header
    /// of another company, or none, leaves the details as they are.
    /// </summary>
    internal async Task LinkOpenProjectToKivoAsync(string? companyUuid = null, long? projectId = null)
    {
        if (!CompanyService.IsOpen) return;
        if (companyUuid is null || projectId is null)
        {
            var path = ProjectStore.Current.FilePath;
            if (!ProjectFiles.IsOdp(path) || !File.Exists(path)) return;
            TakeoffSource source = new TakeoffSource.Project(path!);
            (string? Company, long? ProjectId) head;
            try { head = await OdpTakeoffService.FirstProjectRefAsync(source); }
            catch (Exception) { return; }
            (companyUuid, projectId) = (head.Company, head.ProjectId);
        }
        if (projectId is null) return;
        try
        {
            if (await CompanyService.LinkProjectAsync(companyUuid, projectId.Value))
            {
                RefreshWindowTitle();
                UpdatePersonaBadge();
            }
        }
        catch (OfficeException ex)
        {
            AppNotify.Warning("Company", ex.Message);
        }
    }

    /// <summary>The company badge only tells where the details come from.</summary>
    async void CompanyBadge_Click(object sender, RoutedEventArgs e)
    {
        var text = CompanyService.IsOpen
            ? $"{CompanyService.Current!.Name}\nSigned in as {CompanyService.Current.User}\n{CompanyService.Current.Path}\n\n"
              + (ProjectStore.Current.Shared is { } link ? $"This project is linked to Kivo project {link.ProjectId}. Push to Kivo sends its items." : "This project is not linked yet. Push to Kivo and choose the Kivo project.")
            : "Sign in as a Kivo user in Falcon, then push items. Tren does not talk to Kivo.";
        await new ContentDialog { Title = "Company", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    async void OpenProject_Click(object sender, RoutedEventArgs e) => await OpenSharedProjectAsync();

    async void SignInKivo_Click(object sender, RoutedEventArgs e) => await SignInToKivoAsync();

    async void PushKivo_Click(object sender, RoutedEventArgs e) => await PushItemsToKivoAsync();

    /// <summary>Open the project file shared with Tren.</summary>
    async Task OpenSharedProjectAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".osp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        OpenProjectFile(file.Path);
    }

    /// <summary>Sign in to the company file as a Kivo user. The password stays in this sign-in; it is not sent to Tren.</summary>
    internal async Task SignInToKivoAsync()
    {
        if (!CompanyService.EngineAvailable)
        {
            AppNotify.Warning("Kivo", CompanyService.MissingEngine);
            return;
        }
        var pathBox = new TextBox { Header = "Company file (.osas)", Text = CompanyService.LastPath ?? CompanyService.SuitePath() ?? "" };
        var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".osas");
            picker.FileTypeFilter.Add(".adms");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                pathBox.Text = file.Path;
        };
        var user = new TextBox { Header = "Kivo user" };
        var password = new PasswordBox { Header = "Password" };
        var panel = new StackPanel { Spacing = 8, Children = { pathBox, browse, user, password } };
        var dlg = new ContentDialog
        {
            Title = "Sign in to Kivo",
            Content = panel,
            PrimaryButtonText = "Sign in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (string.IsNullOrWhiteSpace(pathBox.Text) || !File.Exists(pathBox.Text.Trim()))
        {
            AppNotify.Warning("Kivo", "Choose the company file (.osas).");
            return;
        }
        if (string.IsNullOrWhiteSpace(user.Text) || password.Password.Length == 0)
        {
            AppNotify.Warning("Kivo", "Enter the Kivo user and password.");
            return;
        }
        try
        {
            await CompanyService.OpenAsync(pathBox.Text.Trim(), user.Text.Trim(), password.Password);
            RefreshCompanyBadge();
            AppNotify.Success("Kivo", $"Signed in as {CompanyService.Current!.User}.");
        }
        catch (Exception ex)
        {
            AppNotify.Warning("Kivo", ex.Message);
        }
    }

    /// <summary>Push the open project's items into the company file after a Kivo sign-in.</summary>
    internal async Task PushItemsToKivoAsync()
    {
        if (!CompanyService.IsOpen)
        {
            await SignInToKivoAsync();
            if (!CompanyService.IsOpen)
                return;
        }
        var path = ProjectStore.Current.FilePath;
        if (!ProjectFiles.IsOdp(path) || !File.Exists(path))
        {
            AppNotify.Warning("Push to Kivo", "Open the shared project file (.osp) first.");
            return;
        }
        if (ProjectStore.Current.Shared is null)
        {
            IReadOnlyList<SharedProject> projects;
            try
            {
                projects = await Task.Run(() => CompanyService.Current!.Projects());
            }
            catch (OfficeException ex)
            {
                AppNotify.Warning("Push to Kivo", ex.Message);
                return;
            }
            if (projects.Count == 0)
            {
                AppNotify.Warning("Push to Kivo", "This company has no projects in Kivo yet. Create one there, then push again.");
                return;
            }
            var box = new ComboBox { Header = "Kivo project", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var project in projects)
                box.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(project.Code) ? project.Name : $"{project.Code} — {project.Name}", Tag = project.Id });
            box.SelectedIndex = 0;
            var pick = new ContentDialog
            {
                Title = "Push to Kivo",
                Content = box,
                PrimaryButtonText = "Push",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await pick.ShowAsync() != ContentDialogResult.Primary || box.SelectedItem is not ComboBoxItem item || item.Tag is not long id)
                return;
            if (!await CompanyService.LinkProjectAsync(CompanyService.Current!.Uuid, id))
            {
                AppNotify.Warning("Push to Kivo", "This project could not be linked to the company file.");
                return;
            }
        }
        var err = await Task.Run(() => CompanyService.TryPushProjectSummary(ProjectStore.Current));
        if (err is not null)
        {
            AppNotify.Warning("Push to Kivo", err);
            return;
        }
        var draft = await Task.Run(() => CompanyService.TryShareEstimateDraft(ProjectStore.Current));
        if (draft is not null && draft != "Calculate an estimate first.")
        {
            AppNotify.Warning("Push to Kivo", "The project items were pushed. The estimate was not: " + draft);
            return;
        }
        AppNotify.Success("Push to Kivo", "Items were pushed to the signed-in company.");
    }
}
