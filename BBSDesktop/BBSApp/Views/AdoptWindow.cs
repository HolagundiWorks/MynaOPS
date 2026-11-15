// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Office.Shared;

namespace BBSApp.Views;

/// <summary>
/// The Adopt page (docs/ops-first-workflow.md 4.5): a project file that has no configuration (made by MynaCAD, or edited by MynaOps before) is
/// shown with what is in it, and nothing is written until <b>Adopt</b>. Adopting makes a manual backup, writes <c>config.*</c> in one revision and leaves
/// the drawing as it is. The project name and details can be corrected here first.
/// </summary>
internal sealed class AdoptWindow : OpsWindow
{
    readonly string _path;
    readonly AdoptionProposal _proposal;
    readonly TextBox _title = OpsUi.Box(), _client = OpsUi.Box(), _location = OpsUi.Box();
    readonly TextBlock _result = OpsUi.Text("");

    /// <summary>True once the project was adopted (the file now has its configuration).</summary>
    public bool Adopted { get; private set; }

    public AdoptWindow(string path, AdoptionProposal proposal) : base("Adopt project settings", 720, 640)
    {
        _path = path;
        _proposal = proposal;
        var cfg = proposal.Config;
        AutomationProperties.SetAutomationId(Root, "AdoptWindow");

        var page = new StackPanel { Spacing = 10, Padding = new Thickness(16, 12, 16, 12) };
        var title = OpsUi.Text("Adopt project settings", "PageTitleStyle");
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        page.Children.Add(title);
        page.Children.Add(OpsUi.Text($"{Path.GetFileName(path)} has no project configuration yet. MynaOps writes it, and MynaCAD follows it. Adopting changes no drawing: "
            + "the levels, tabs and settings that are in the file are recorded as they are. A backup of the file is made first.", "CaptionSecondaryStyle"));

        page.Children.Add(OpsUi.Card("Where this comes from", OpsUi.Text(string.Join("; ", proposal.Sources) + (proposal.FromCad ? "" : ". MynaCAD has not described the drawing; open the project in MynaCAD once for a fuller proposal."))));

        _title.Text = cfg.Project?.Title ?? "";
        _client.Text = cfg.Project?.Client ?? "";
        _location.Text = cfg.Project?.Location ?? "";
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        details.Children.Add(OpsUi.Field("Project name", _title, "Adopt.Name"));
        details.Children.Add(OpsUi.Field("Client", _client, "Adopt.Client"));
        details.Children.Add(OpsUi.Field("Location", _location, "Adopt.Location"));
        page.Children.Add(OpsUi.Card("Project", details));

        var levels = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(levels, "Adopt.Levels");
        if (cfg.Levels!.Items.Count == 0) levels.Children.Add(OpsUi.Text("No level was found in the file."));
        foreach (var l in cfg.Levels.Items)
            levels.Children.Add(OpsUi.Text($"{l.Name}   id {l.Cid}   elevation {l.ElevationMm.ToString("0", CultureInfo.InvariantCulture)} mm   height {l.HeightMm.ToString("0", CultureInfo.InvariantCulture)} mm"));
        page.Children.Add(OpsUi.Card($"Levels ({cfg.Levels.Items.Count})", levels));

        var other = new StackPanel { Spacing = 2 };
        other.Children.Add(OpsUi.Text($"Drawing unit: {cfg.Units!.DrawingUnit}" + (cfg.Units.Locked ? " (locked: the drawing has content)" : "")));
        other.Children.Add(OpsUi.Text($"Layer sets: {(cfg.LayerSets!.Sets.Count == 0 ? "none found" : string.Join(", ", cfg.LayerSets.Sets))}"));
        other.Children.Add(OpsUi.Text($"Discipline tabs: {(cfg.Tabs!.Items.Count == 0 ? "none found" : string.Join(", ", cfg.Tabs.Items.Select(t => t.Name)))}"));
        other.Children.Add(OpsUi.Text($"Sheet numbering: {cfg.Sheets!.Scheme.Pattern}"));
        page.Children.Add(OpsUi.Card("Settings", other));

        if (proposal.Notes.Count > 0)
            page.Children.Add(OpsUi.Card("Notes", OpsUi.Text(" - " + string.Join("\n - ", proposal.Notes))));
        AutomationProperties.SetAutomationId(_result, "Adopt.Result");
        page.Children.Add(_result);
        SetBody(new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled });

        var cancel = OpsUi.Button("Cancel", false, "Adopt.Cancel", Close);
        var adopt = OpsUi.Button("Adopt", true, "Adopt.Adopt", () => AdoptNow());
        Footer.Children.Add(cancel);
        Footer.Children.Add(adopt);
    }

    void AdoptNow()
    {
        try
        {
            var c = _proposal.Config;
            var edited = c with { Project = c.Project! with { Title = _title.Text.Trim(), Client = _client.Text.Trim(), Location = _location.Text.Trim() } };
            var result = ProjectAdoption.Adopt(_path, edited);
            Adopted = true;
            Services.AppNotify.Success("Project adopted", $"Backup: {Path.GetFileName(result.Backup)}");
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            _result.Text = "Not adopted: " + ex.Message;
        }
    }
}
