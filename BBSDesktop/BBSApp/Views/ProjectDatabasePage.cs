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
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp.Views;

/// <summary>
/// Project &gt; Project database (also File &gt; Project database): the shared project file (<c>.osp</c>) as a database. It shows the format,
/// version, revision, size, the rows in each table and the folder; checks integrity; compacts; makes a backup now; lists the backups and restores
/// one; and shows whether MynaCAD (or another program) has the file open, in which case compacting and restoring are refused. The work is done by
/// <see cref="ProjectDatabase"/>, off the UI thread. MynaCAD reads and writes the file through its own engine and never needs MynaOps for it.
/// </summary>
public sealed class ProjectDatabasePage : Page
{
    string? _path;
    readonly TextBlock _fileLine = Text("", "BodyStrongStyle");
    readonly TextBlock _openLine = Text("", "BodyTextStyle");
    readonly Grid _facts = new() { ColumnSpacing = 16, RowSpacing = 4 };
    readonly StackPanel _tables = new() { Spacing = 2 };
    readonly ListView _backups = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
    readonly TextBlock _result = Text("", "BodyTextStyle");
    readonly List<Button> _actions = new();
    Button _compact = null!, _restore = null!;
    bool _openElsewhere;

    public ProjectDatabasePage(string? path = null)
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _path = path ?? (ProjectFiles.IsOdp(ProjectStore.Current.FilePath) ? ProjectStore.Current.FilePath : null);

        var page = new StackPanel { Spacing = 12, Padding = new Thickness(4, 4, 4, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Text("The project file is one database that MynaCAD and MynaOps share. Nothing here changes what is in it, except Restore.",
            "CaptionSecondaryStyle"));

        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(_fileLine);
        fileRow.Children.Add(Action("Choose file…", ChooseAsync, primary: false, always: true));
        fileRow.Children.Add(Action("Open folder", OpenFolderAsync, primary: false));
        fileRow.Children.Add(Action("Refresh", RefreshAsync, primary: false));
        page.Children.Add(fileRow);
        _openLine.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_openLine, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        page.Children.Add(_openLine);

        page.Children.Add(Card("File", _facts));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Action("Check integrity", CheckAsync, primary: false));
        _compact = Action("Compact", CompactAsync, primary: false);
        buttons.Children.Add(_compact);
        buttons.Children.Add(Action("Back up now", BackupAsync, primary: true));
        page.Children.Add(buttons);
        _result.TextWrapping = TextWrapping.Wrap;
        _result.IsTextSelectionEnabled = true;
        AutomationProperties.SetLiveSetting(_result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        page.Children.Add(_result);

        var backupBox = new StackPanel { Spacing = 8 };
        backupBox.Children.Add(Text("Each backup is a whole project file in the folder <name>.backups beside it. MynaCAD makes one before every save that changes "
            + "the drawing and keeps the newest few; the ones made here are named manual and count with them.", "CaptionSecondaryStyle"));
        AutomationProperties.SetName(_backups, "Backups, newest first");
        backupBox.Children.Add(_backups);
        var backupButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _restore = Action("Restore selected…", RestoreAsync, primary: false);
        backupButtons.Children.Add(_restore);
        backupButtons.Children.Add(Action("Open backups folder", OpenBackupsAsync, primary: false));
        backupBox.Children.Add(backupButtons);
        page.Children.Add(Card("Backups", backupBox));

        page.Children.Add(Card("Tables (rows)", _tables));

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled };
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ---- building blocks (existing styles only) ----

    static TextBlock Text(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Application.Current.Resources.TryGetValue(style, out var s) && s is Style st) t.Style = st;
        return t;
    }

    static Border Card(string title, UIElement body)
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = Text(title, "FormSectionStyle");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(body);
        var card = new Border { Child = stack };
        if (Application.Current.Resources.TryGetValue("FluentCardStyle", out var s) && s is Style st) card.Style = st;
        return card;
    }

    Button Action(string label, Func<Task> run, bool primary, bool always = false)
    {
        var b = new Button { Content = label };
        if (Application.Current.Resources.TryGetValue(primary ? "HcwPrimaryButton" : "HcwGhostButton", out var s) && s is Style st) b.Style = st;
        b.Click += async (_, _) => await run();
        if (!always) _actions.Add(b);
        return b;
    }

    void SetBusy(bool busy)
    {
        foreach (var b in _actions) b.IsEnabled = !busy && _path is not null;
        if (!busy && _path is not null)
        {
            _compact.IsEnabled = !_openElsewhere;
            _restore.IsEnabled = !_openElsewhere;
        }
    }

    static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.00", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " bytes",
    };

    void Fact(int row, string label, string value)
    {
        if (_facts.RowDefinitions.Count <= row) _facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Text(label, "CaptionSecondaryStyle");
        var v = Text(value, "BodyTextStyle");
        v.IsTextSelectionEnabled = true;
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        _facts.Children.Add(l);
        _facts.Children.Add(v);
    }

    // ---- actions ----

    async Task RefreshAsync()
    {
        _facts.Children.Clear();
        _facts.RowDefinitions.Clear();
        if (_facts.ColumnDefinitions.Count == 0)
        {
            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        _tables.Children.Clear();
        _backups.Items.Clear();
        if (_path is null)
        {
            _fileLine.Text = "No project file is open.";
            _openLine.Text = "Open a project (File > Open project) or choose a project file to look after it here.";
            SetBusy(false);
            return;
        }
        var path = _path;
        _fileLine.Text = Path.GetFileName(path);
        ToolTipService.SetToolTip(_fileLine, path);
        SetBusy(true);
        try
        {
            var (info, open, backups) = await Task.Run(() =>
                (ProjectDatabase.Info(path), ProjectDatabase.OpenElsewhere(path), ProjectDatabase.ListBackups(path)));
            _openElsewhere = open.IsOpen;
            _openLine.Text = open.IsOpen
                ? $"Open in {open.Who}. Compact and Restore are not available until it is closed there; a backup can still be made."
                : "Not open in another program.";
            int r = 0;
            Fact(r++, "Project", string.IsNullOrWhiteSpace(info.Name) ? "(no name)" : info.Name);
            Fact(r++, "Format", $"{info.Format}, version {info.Version}");
            Fact(r++, "Revision", info.Revision.ToString(CultureInfo.CurrentCulture));
            Fact(r++, "Size", info.WalBytes > 0 ? $"{Size(info.FileBytes)} (+ {Size(info.WalBytes)} not yet folded in)" : Size(info.FileBytes));
            Fact(r++, "Free space", info.ReclaimableBytes > 0 ? $"{Size(info.ReclaimableBytes)} can be given back by Compact" : "none");
            Fact(r++, "Folder", info.Folder);
            Fact(r++, "Project id", info.ProjectId);
            Fact(r++, "Journal", info.JournalMode);
            foreach (var (table, rows) in info.Tables)
                _tables.Children.Add(Text($"{table}: {rows.ToString("N0", CultureInfo.CurrentCulture)}", "BodyTextStyle"));
            foreach (var b in backups)
            {
                var when = b.WhenUtc == DateTime.MinValue ? b.Stamp : b.WhenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                var reason = string.IsNullOrEmpty(b.Reason) ? "" : $" · {b.Reason}";
                _backups.Items.Add(new ListViewItem { Content = $"{when}{reason} · {Size(b.Bytes)}", Tag = b.Path });
            }
            if (backups.Count == 0)
                _backups.Items.Add(new ListViewItem { Content = "No backups yet.", IsEnabled = false });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _openLine.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task ChooseAsync()
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(ProjectFiles.Extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _path = file.Path;
        _result.Text = "";
        await RefreshAsync();
    }

    Task OpenFolderAsync() => Reveal(_path is null ? null : Path.GetDirectoryName(_path));

    Task OpenBackupsAsync() => Reveal(_path is null ? null : ProjectDatabase.BackupFolder(_path));

    static Task Reveal(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            AppNotify.Info("Project database", "That folder does not exist yet.");
            return Task.CompletedTask;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppNotify.Error("Project database", ex.Message); }
        return Task.CompletedTask;
    }

    async Task Run(string working, Func<string, string> work, bool refresh = true)
    {
        if (_path is null) return;
        var path = _path;
        SetBusy(true);
        _result.Text = working;
        try
        {
            _result.Text = await Task.Run(() => work(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _result.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
        if (refresh) await RefreshAsync();
    }

    Task CheckAsync() => Run("Checking every page of the file…", path =>
    {
        var problems = ProjectDatabase.IntegrityCheck(path);
        return problems.Count == 0
            ? "Integrity check passed: the file is sound."
            : "Integrity check found problems. Make a backup and keep the file as it is; restore a backup if the project does not open:\n"
              + string.Join("\n", problems);
    }, refresh: false);

    async Task CompactAsync()
    {
        if (!await Confirm("Compact the project file?",
                "The file is rewritten without its free space. Its contents and revision stay the same. It must not be open in MynaCAD meanwhile.",
                "Compact"))
            return;
        await Run("Compacting…", path => $"Compacted: {Size(ProjectDatabase.Compact(path))} given back.");
    }

    Task BackupAsync() => Run("Making a backup…", path => $"Backup made: {Path.GetFileName(ProjectDatabase.Backup(path))}");

    async Task RestoreAsync()
    {
        if (_path is null || _backups.SelectedItem is not ListViewItem { Tag: string backup })
        {
            _result.Text = "Select a backup in the list first.";
            return;
        }
        bool ours = ActiveProject.SamePath(ProjectStore.Current.FilePath, _path);
        var lose = ours && ProjectStore.Current.IsDirty ? " Unsaved changes in MynaOps are discarded." : "";
        if (!await Confirm("Restore this backup?",
                $"The project file is replaced by {Path.GetFileName(backup)}. The file as it is now is kept first as a before-restore backup.{lose}",
                "Restore"))
            return;
        var path = _path;
        bool restored = false;
        await Run("Restoring…", p =>
        {
            var kept = ProjectDatabase.Restore(p, backup);
            restored = true;
            return kept is null ? "Restored." : $"Restored. The file as it was is kept as {Path.GetFileName(kept)}.";
        });
        // MynaOps holds the project in memory: read it again from the restored file, so a later save does not write the old one back.
        if (restored && ours && App.MainWindow is { } window)
        {
            ProjectStore.Current.IsDirty = false;
            window.OpenProjectFile(path);
        }
    }

    async Task<bool> Confirm(string title, string body, string action)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
