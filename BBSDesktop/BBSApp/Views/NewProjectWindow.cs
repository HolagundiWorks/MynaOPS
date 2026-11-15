// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Office.Shared;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp.Views;

/// <summary>
/// The create-project wizard (docs/ops-first-workflow.md 1.2), in a window of its own: Project, Team, Building, Standards, Review. It collects a
/// <see cref="NewProjectRequest"/> and creates the project file through <see cref="NewProjectBuilder"/>, which writes the configuration
/// (<c>config.*</c>) in one revision. Nothing is written before <b>Create project</b>.
/// </summary>
internal sealed class NewProjectWindow : OpsWindow
{
    readonly string[] _pageNames = { "Project", "Team", "Building", "Standards", "Review" };
    readonly List<UIElement> _pages = new();
    readonly TextBlock _step = OpsUi.Text("", "CaptionSecondaryStyle");
    readonly StackPanel _pageHost = new();
    readonly Button _back, _next, _create, _cancel;
    int _page;

    // page 1
    readonly TextBox _file = OpsUi.Box(), _folder = OpsUi.Box(), _name = OpsUi.Box(), _client = OpsUi.Box(), _owner = OpsUi.Box(), _location = OpsUi.Box(), _address = OpsUi.Box(),
        _pid = OpsUi.Box(), _siteArea = OpsUi.Box(), _plotUse = OpsUi.Box(), _authority = OpsUi.Box(), _date = OpsUi.Box(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        _summary = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64 };
    readonly ComboBox _persona = new() { ItemsSource = new[] { "Project Manager", "Contractor" }, SelectedIndex = 0 };
    // page 2
    readonly TextBox _architect = OpsUi.Box(), _engineer = OpsUi.Box();
    // page 3
    readonly ComboBox _unit = new() { ItemsSource = ProjectConfig.UnitNames.ToArray(), SelectedIndex = 0 };
    readonly NumberBox _basements = OpsUi.Number(0, 0, 10), _upper = OpsUi.Number(1, 0, NewProjectBuilder.MaxFloors), _floorH = OpsUi.Number(3000, 1000, 20000, 100),
        _slab = OpsUi.Number(150, 50, 1000, 10), _beam = OpsUi.Number(450, 100, 2000, 25);
    readonly CheckBox _terrace = new() { Content = "Terrace level", IsChecked = true }, _onePerFloor = new() { Content = "One level per floor", IsChecked = true };
    readonly StackPanel _levelRows = new() { Spacing = 4 };
    readonly List<(TextBox Name, TextBox Code, NumberBox Elevation, NumberBox Height)> _levels = new();
    // page 4
    readonly CheckBox _standard = new() { Content = "standard", IsChecked = true }, _permit = new() { Content = "permit" }, _presentation = new() { Content = "presentation" };
    readonly CheckBox _tabs = new() { Content = "The five discipline tabs (Working drawing, Electrical, Plumbing, Interior, False ceiling)", IsChecked = true };
    readonly TextBox _pattern = OpsUi.Box("{D}-{N}");
    readonly NumberBox _width = OpsUi.Number(3, 1, 8);
    // page 5
    readonly TextBlock _review = OpsUi.Text("");
    readonly TextBlock _problems = OpsUi.Text("");

    /// <summary>The path of the created project file, or null when the wizard was cancelled.</summary>
    public string? CreatedPath { get; private set; }

    public NewProjectWindow(string? suggestedFolder = null) : base("New project", 760, 680)
    {
        _folder.Text = suggestedFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        AutomationProperties.SetAutomationId(Root, "NewProjectWizard");

        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 0), Spacing = 2 };
        var title = OpsUi.Text("Create project", "PageTitleStyle");
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        head.Children.Add(title);
        head.Children.Add(_step);

        _pages.Add(PageProject());
        _pages.Add(PageTeam());
        _pages.Add(PageBuilding());
        _pages.Add(PageStandards());
        _pages.Add(PageReview());

        var body = new Grid { RowSpacing = 8 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(head);
        var scroll = new ScrollViewer { Content = _pageHost, Padding = new Thickness(16, 0, 16, 0), HorizontalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        SetBody(body);

        _cancel = OpsUi.Button("Cancel", false, "NewProject.Cancel", Close);
        _back = OpsUi.Button("Back", false, "NewProject.Back", () => Go(_page - 1));
        _next = OpsUi.Button("Next", true, "NewProject.Next", () => Go(_page + 1));
        _create = OpsUi.Button("Create project", true, "NewProject.Create", CreateNow);
        foreach (var b in new[] { _cancel, _back, _next, _create }) Footer.Children.Add(b);
        Go(0);
    }

    // ---- pages

    static StackPanel Row(params UIElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    UIElement PageProject()
    {
        var p = new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 0, 12) };
        p.Children.Add(OpsUi.Text("Where the project file is made, and what it is about. MynaCAD reads these details; they are changed here, in MynaOps.", "CaptionSecondaryStyle"));
        var folder = OpsUi.Field("Folder", _folder, "NewProject.Folder", 440);
        var browse = OpsUi.Button("Browse…", false, "NewProject.Browse", async () => await BrowseAsync());
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(Row(OpsUi.Field("Project file name", _file, "NewProject.File", 260), OpsUi.Field("Project name", _name, "NewProject.Name", 260)));
        p.Children.Add(Row(folder, browse));
        p.Children.Add(Row(OpsUi.Field("Client", _client, "NewProject.Client"), OpsUi.Field("Owner", _owner, "NewProject.Owner")));
        p.Children.Add(Row(OpsUi.Field("Location", _location, "NewProject.Location"), OpsUi.Field("Property ID", _pid, "NewProject.Pid")));
        p.Children.Add(OpsUi.Field("Site address", _address, "NewProject.Address", 532));
        p.Children.Add(Row(OpsUi.Field("Site area", _siteArea, "NewProject.SiteArea"), OpsUi.Field("Plot use", _plotUse, "NewProject.PlotUse")));
        p.Children.Add(Row(OpsUi.Field("Authority", _authority, "NewProject.Authority"), OpsUi.Field("Date", _date, "NewProject.Date")));
        p.Children.Add(OpsUi.Field("Summary", _summary, "NewProject.Summary", 532));
        p.Children.Add(OpsUi.Field("Persona (whose letterhead the project is set up for)", _persona, "NewProject.Persona", 260));
        _name.TextChanged += (_, _) =>
        {
            // the file name follows the project name until it is typed in
            if (!_fileTouched) _file.Text = SafeFile(_name.Text);
        };
        _file.TextChanged += (_, _) => { if (_file.FocusState != FocusState.Unfocused) _fileTouched = true; };
        return p;
    }

    bool _fileTouched;

    static string SafeFile(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    UIElement PageTeam()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(OpsUi.Text("The design team named on the drawings.", "CaptionSecondaryStyle"));
        p.Children.Add(OpsUi.Field("Architect", _architect, "NewProject.Architect", 360));
        p.Children.Add(OpsUi.Field("Engineer", _engineer, "NewProject.Engineer", 360));
        p.Children.Add(OpsUi.Text("The company's own details are on the Settings page once the project is open.", "CaptionSecondaryStyle"));
        return p;
    }

    UIElement PageBuilding()
    {
        var p = new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 0, 12) };
        p.Children.Add(OpsUi.Text("The drawing unit and the levels MynaCAD will create as level tabs. The unit is locked once MynaCAD has drawn something.", "CaptionSecondaryStyle"));
        p.Children.Add(Row(OpsUi.Field("Drawing unit", _unit, "NewProject.Unit", 120), OpsUi.Field("Basements", _basements, "NewProject.Basements", 120),
            OpsUi.Field("Floors above the ground floor", _upper, "NewProject.Upper", 160)));
        p.Children.Add(Row(OpsUi.Field("Floor-to-floor height (mm)", _floorH, "NewProject.FloorHeight", 160), OpsUi.Field("Slab (mm)", _slab, "NewProject.Slab", 110),
            OpsUi.Field("Beam depth (mm)", _beam, "NewProject.Beam", 110)));
        AutomationProperties.SetAutomationId(_terrace, "NewProject.Terrace");
        AutomationProperties.SetAutomationId(_onePerFloor, "NewProject.OnePerFloor");
        p.Children.Add(Row(_terrace, _onePerFloor));
        var regen = OpsUi.Button("Make the list from these numbers", false, "NewProject.Regenerate", Regenerate);
        p.Children.Add(regen);
        var heading = OpsUi.Text("Levels (bottom to top)", "FormSectionStyle");
        p.Children.Add(heading);
        AutomationProperties.SetAutomationId(_levelRows, "NewProject.Levels");
        p.Children.Add(_levelRows);
        p.Children.Add(OpsUi.Button("Add a level", false, "NewProject.AddLevel", () => AddLevelRow(new LevelSpec("New level", "", _levels.Count == 0 ? 0 : _levels[^1].Elevation.Value + _levels[^1].Height.Value, _floorH.Value))));
        return p;
    }

    UIElement PageStandards()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(OpsUi.Text("Layer sets, discipline tabs and the sheet numbering MynaCAD applies.", "CaptionSecondaryStyle"));
        AutomationProperties.SetAutomationId(_standard, "NewProject.LayerStandard");
        AutomationProperties.SetAutomationId(_permit, "NewProject.LayerPermit");
        AutomationProperties.SetAutomationId(_presentation, "NewProject.LayerPresentation");
        AutomationProperties.SetAutomationId(_tabs, "NewProject.Tabs");
        p.Children.Add(OpsUi.Text("Layer sets", "FormSectionStyle"));
        p.Children.Add(Row(_standard, _permit, _presentation));
        p.Children.Add(_tabs);
        p.Children.Add(OpsUi.Text("Sheet numbering", "FormSectionStyle"));
        p.Children.Add(Row(OpsUi.Field("Pattern ({D} discipline, {N} number)", _pattern, "NewProject.SchemePattern", 220), OpsUi.Field("Digits", _width, "NewProject.SchemeWidth", 90)));
        return p;
    }

    UIElement PageReview()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(OpsUi.Text("Check the project, then create it. The project file is made with this configuration; MynaCAD applies it when it opens the project.", "CaptionSecondaryStyle"));
        AutomationProperties.SetAutomationId(_review, "NewProject.Review");
        AutomationProperties.SetAutomationId(_problems, "NewProject.Problems");
        p.Children.Add(_review);
        p.Children.Add(_problems);
        return p;
    }

    // ---- behaviour

    void Go(int page)
    {
        if (page < 0 || page >= _pages.Count) return;
        if (page == 2 && _levels.Count == 0) Regenerate();
        _page = page;
        _pageHost.Children.Clear();
        _pageHost.Children.Add(_pages[page]);
        _step.Text = $"Step {page + 1} of {_pages.Count}: {_pageNames[page]}";
        AutomationProperties.SetName(_step, _step.Text);
        if (page == _pages.Count - 1) RefreshReview();
        _back.IsEnabled = page > 0;
        _next.Visibility = page < _pages.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        _create.Visibility = page == _pages.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task BrowseAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) _folder.Text = folder.Path;
    }

    void Regenerate()
    {
        var r = Collect(useLevelRows: false);
        _levels.Clear();
        _levelRows.Children.Clear();
        foreach (var l in NewProjectBuilder.GenerateLevels(r)) AddLevelRow(l);
    }

    void AddLevelRow(LevelSpec l)
    {
        var name = OpsUi.Box(l.Name);
        var code = OpsUi.Box(l.Code);
        var elev = OpsUi.Number(l.ElevationMm, -1_000_000, 1_000_000, 100);
        var height = OpsUi.Number(l.HeightMm, 0, 100_000, 100);
        var i = _levels.Count + 1;
        AutomationProperties.SetAutomationId(name, $"NewProject.Level{i}.Name");
        AutomationProperties.SetName(name, $"Level {i} name");
        AutomationProperties.SetAutomationId(code, $"NewProject.Level{i}.Code");
        AutomationProperties.SetName(code, $"Level {i} code");
        AutomationProperties.SetAutomationId(elev, $"NewProject.Level{i}.Elevation");
        AutomationProperties.SetName(elev, $"Level {i} elevation (mm)");
        AutomationProperties.SetAutomationId(height, $"NewProject.Level{i}.Height");
        AutomationProperties.SetName(height, $"Level {i} height (mm)");
        name.Width = 200; code.Width = 70; elev.Width = 130; height.Width = 130;
        var remove = OpsUi.Button("Remove", false, $"NewProject.Level{i}.Remove", () => { });
        var entry = (name, code, elev, height);
        var row = Row(name, code, elev, height, remove);
        remove.Click += (_, _) => { _levels.Remove(entry); _levelRows.Children.Remove(row); };
        _levels.Add(entry);
        _levelRows.Children.Add(row);
    }

    static string V(TextBox t) => (t.Text ?? "").Trim();

    NewProjectRequest Collect(bool useLevelRows = true)
    {
        var sets = new List<string>();
        if (_standard.IsChecked == true) sets.Add("standard");
        if (_permit.IsChecked == true) sets.Add("permit");
        if (_presentation.IsChecked == true) sets.Add("presentation");
        var scheme = NewProjectBuilder.DefaultScheme with { Pattern = V(_pattern), Width = (uint)Math.Clamp(double.IsNaN(_width.Value) ? 3 : _width.Value, 0, 8) };
        var r = new NewProjectRequest
        {
            Folder = V(_folder),
            FileName = V(_file),
            Project = new ConfigProject
            {
                Title = V(_name), Client = V(_client), Owner = V(_owner), Location = V(_location), Address = V(_address), Pid = V(_pid), SiteArea = V(_siteArea),
                PlotUse = V(_plotUse), Authority = V(_authority), Date = V(_date), Summary = (_summary.Text ?? "").Trim(), Persona = (string)(_persona.SelectedItem ?? "Project Manager"),
            },
            Team = new ConfigTeam { Architect = V(_architect), Engineer = V(_engineer) },
            DrawingUnit = (string)(_unit.SelectedItem ?? "mm"),
            Basements = (int)Num(_basements, 0),
            UpperFloors = (int)Num(_upper, 1),
            Terrace = _terrace.IsChecked == true,
            FloorHeightMm = Num(_floorH, 3000),
            SlabMm = Num(_slab, 150),
            BeamMm = Num(_beam, 450),
            OneLevelPerFloor = _onePerFloor.IsChecked == true,
            LayerSets = sets,
            StandardTabs = _tabs.IsChecked == true,
            Scheme = scheme,
        };
        if (useLevelRows && _levels.Count > 0)
            r.Levels = _levels.Select(l => new LevelSpec(V(l.Name), V(l.Code), Num(l.Elevation, 0), Num(l.Height, 0))).ToList();
        return r;
    }

    static double Num(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;

    void RefreshReview()
    {
        var r = Collect();
        var lines = new List<string>
        {
            $"Project: {r.Project.Title}  ·  {r.Project.Persona}",
            $"File: {NewProjectBuilder.PathOf(r)}",
            $"Client: {Dash(r.Project.Client)}   Location: {Dash(r.Project.Location)}   Architect: {Dash(r.Team.Architect)}   Engineer: {Dash(r.Team.Engineer)}",
            $"Drawing unit: {r.DrawingUnit}",
            $"Levels ({(r.Levels ?? NewProjectBuilder.GenerateLevels(r)).Count}): " + string.Join(", ", (r.Levels ?? NewProjectBuilder.GenerateLevels(r)).Select(l => l.Name)),
            $"Layer sets: {(r.LayerSets.Count == 0 ? "none" : string.Join(", ", r.LayerSets))}   Discipline tabs: {(r.StandardTabs ? "five" : "none")}   Sheet numbers: {r.Scheme.Pattern}, {r.Scheme.Width} digits",
        };
        _review.Text = string.Join("\n", lines);
        var problems = NewProjectBuilder.Problems(r);
        _problems.Text = problems.Count == 0 ? "" : "Before the project can be created:\n - " + string.Join("\n - ", problems);
        _create.IsEnabled = problems.Count == 0;
    }

    static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    void CreateNow()
    {
        var r = Collect();
        try
        {
            CreatedPath = NewProjectBuilder.Create(r).Path;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            _problems.Text = "The project was not created: " + ex.Message;
        }
    }
}
