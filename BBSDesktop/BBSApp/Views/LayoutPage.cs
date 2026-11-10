// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using BBSApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Office.Shared;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp.Views;

/// <summary>
/// Drawing output. Print blocks are named in MynaCAD. This page places one on a layout of a project map,
/// fills the nameplate, and writes a sheet PDF. The PDF records the sheet; it does not redraw the model.
/// </summary>
public sealed class LayoutPage : Page
{
    readonly ListView _maps = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly ListView _layouts = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly ListView _blocks = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly TextBox _title = new();
    readonly TextBox _number = new();
    readonly TextBox _scale = new();
    readonly TextBox _drawnBy = new();
    readonly TextBox _date = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    readonly List<DraftPrintBlock> _printBlocks = new();
    bool _filling;

    public LayoutPage()
    {
        var heading = new TextBlock
        {
            Text = "Layout",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        AutomationProperties.SetName(_maps, "Project maps");
        AutomationProperties.SetName(_layouts, "Layouts");
        AutomationProperties.SetName(_blocks, "Print blocks");

        var left = new Grid { RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mapHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        mapHead.Children.Add(Label("Project map"));
        var newMap = new Button { Content = "New map" };
        var newLayout = new Button { Content = "New layout" };
        newMap.Click += async (_, _) => await NewMapAsync();
        newLayout.Click += async (_, _) => await NewLayoutAsync();
        mapHead.Children.Add(newMap);
        mapHead.Children.Add(newLayout);
        Grid.SetRow(mapHead, 0);
        Grid.SetRow(_maps, 1);
        var layoutLabel = Label("Layout");
        Grid.SetRow(layoutLabel, 2);
        Grid.SetRow(_layouts, 3);
        left.Children.Add(mapHead);
        left.Children.Add(_maps);
        left.Children.Add(layoutLabel);
        left.Children.Add(_layouts);

        var plate = new StackPanel { Spacing = 6 };
        plate.Children.Add(Label("Nameplate"));
        plate.Children.Add(Field("Drawing title", _title));
        plate.Children.Add(Field("Sheet number", _number));
        plate.Children.Add(Field("Scale", _scale));
        plate.Children.Add(Field("Drawn by", _drawnBy));
        plate.Children.Add(Field("Date", _date));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var insert = new Button { Content = "Insert print block" };
        var update = new Button { Content = "Update nameplate" };
        var export = new Button { Content = "Export PDF" };
        insert.Click += (_, _) => InsertBlock();
        update.Click += (_, _) => UpdateNameplate();
        export.Click += async (_, _) => await ExportAsync();
        actions.Children.Add(insert);
        actions.Children.Add(update);
        actions.Children.Add(export);
        plate.Children.Add(actions);
        Grid.SetRow(plate, 5);
        left.Children.Add(plate);

        var right = new Grid { RowSpacing = 8 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var blockHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        blockHead.Children.Add(Label("Print blocks"));
        var reload = new Button { Content = "Reload" };
        reload.Click += (_, _) => LoadPrintBlocks();
        blockHead.Children.Add(reload);
        Grid.SetRow(blockHead, 0);
        Grid.SetRow(_blocks, 1);
        right.Children.Add(blockHead);
        right.Children.Add(_blocks);

        var body = new Grid { ColumnSpacing = 20 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Print blocks are created in MynaCAD. Select a project map and a layout, such as Ground floor working drawing, insert the related print block, update the nameplate, then export the PDF.",
        };
        Grid.SetRow(heading, 0);
        Grid.SetRow(intro, 1);
        Grid.SetRow(body, 2);
        Grid.SetRow(_status, 3);
        root.Children.Add(heading);
        root.Children.Add(intro);
        root.Children.Add(body);
        root.Children.Add(_status);

        Content = new ScrollViewer { Padding = new Thickness(24), Content = root };

        _maps.SelectionChanged += (_, _) => ShowLayouts(keepLayout: false);
        _layouts.SelectionChanged += (_, _) => ShowNameplate();
        Loaded += (_, _) =>
        {
            ShowMaps();
            LoadPrintBlocks();
        };
    }

    static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static StackPanel Field(string caption, TextBox box)
    {
        box.PlaceholderText = caption;
        AutomationProperties.SetName(box, caption);
        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = caption, Opacity = 0.8 }, box },
        };
    }

    ProjectStore Store => ProjectStore.Current;

    void ShowMaps()
    {
        var selected = SelectedMap?.Id;
        _maps.ItemsSource = Store.Sheets.Maps.Select(m => m.Name).ToList();
        var index = Store.Sheets.Maps.FindIndex(m => m.Id == selected);
        if (index >= 0) _maps.SelectedIndex = index;
        else if (Store.Sheets.Maps.Count > 0) _maps.SelectedIndex = 0;
        else ShowLayouts(keepLayout: false);
    }

    void ShowLayouts(bool keepLayout)
    {
        var map = SelectedMap;
        var selected = keepLayout ? SelectedLayout?.Id : null;
        _layouts.ItemsSource = map is null
            ? new List<string>()
            : map.Layouts.Select(LayoutLine).ToList();
        if (map is null)
        {
            ShowNameplate();
            return;
        }
        var index = selected is null ? -1 : map.Layouts.FindIndex(l => l.Id == selected);
        if (index >= 0) _layouts.SelectedIndex = index;
        else if (map.Layouts.Count > 0) _layouts.SelectedIndex = 0;
        else ShowNameplate();
    }

    static string LayoutLine(PlacedLayout layout)
    {
        var placed = string.IsNullOrEmpty(layout.PrintBlock) ? "no print block" : layout.PrintBlock;
        return layout.Name + "  ·  " + placed;
    }

    void ShowNameplate()
    {
        var layout = SelectedLayout;
        _filling = true;
        _title.Text = layout?.Title ?? "";
        _number.Text = layout?.Number ?? "";
        _scale.Text = layout?.Scale ?? "";
        _drawnBy.Text = layout?.DrawnBy ?? "";
        _date.Text = layout?.Date ?? "";
        _filling = false;
        _title.IsEnabled = _number.IsEnabled = _scale.IsEnabled = _drawnBy.IsEnabled = _date.IsEnabled = layout is not null;
    }

    ProjectMapSheet? SelectedMap =>
        _maps.SelectedIndex >= 0 && _maps.SelectedIndex < Store.Sheets.Maps.Count
            ? Store.Sheets.Maps[_maps.SelectedIndex]
            : null;

    PlacedLayout? SelectedLayout
    {
        get
        {
            var map = SelectedMap;
            if (map is null || _layouts.SelectedIndex < 0 || _layouts.SelectedIndex >= map.Layouts.Count) return null;
            return map.Layouts[_layouts.SelectedIndex];
        }
    }

    DraftPrintBlock? SelectedBlock =>
        _blocks.SelectedIndex >= 0 && _blocks.SelectedIndex < _printBlocks.Count
            ? _printBlocks[_blocks.SelectedIndex]
            : null;

    async Task NewMapAsync()
    {
        if (Store.Sheets.Maps.Count >= SheetLayoutBook.MaxMaps)
        {
            SetStatus("A project holds up to " + SheetLayoutBook.MaxMaps + " project maps.");
            return;
        }
        var name = await AskAsync("Project map", "Issue set");
        if (name is null) return;
        if (!AcceptName(name, out var why))
        {
            SetStatus(why);
            return;
        }
        Store.Sheets.Maps.Add(new ProjectMapSheet { Name = name });
        Store.Notify();
        ShowMaps();
        _maps.SelectedIndex = Store.Sheets.Maps.Count - 1;
        SetStatus("Project map \"" + name + "\" is stored in this project. Save the project file to keep it.");
    }

    async Task NewLayoutAsync()
    {
        var map = SelectedMap;
        if (map is null)
        {
            SetStatus("Select a project map first.");
            return;
        }
        if (map.Layouts.Count >= SheetLayoutBook.MaxLayouts)
        {
            SetStatus("A project map holds up to " + SheetLayoutBook.MaxLayouts + " layouts.");
            return;
        }
        var suggested = SelectedBlock is { } block ? Relate(block.Description) : "Ground floor working drawing";
        if (suggested.Length == 0) suggested = "Ground floor working drawing";
        var name = await AskAsync("Layout", suggested);
        if (name is null) return;
        if (!AcceptName(name, out var why))
        {
            SetStatus(why);
            return;
        }
        var info = Store.Info;
        map.Layouts.Add(new PlacedLayout
        {
            Name = name,
            Title = name,
            Scale = "1:100",
            DrawnBy = info.PreparedByName,
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
        });
        Store.Notify();
        ShowLayouts(keepLayout: false);
        _layouts.SelectedIndex = map.Layouts.Count - 1;
        SetStatus("Layout \"" + name + "\" added. Insert the print block, then update the nameplate.");
    }

    void InsertBlock()
    {
        var layout = SelectedLayout;
        if (layout is null)
        {
            SetStatus("Select a layout first.");
            return;
        }
        var block = Related(layout) ?? SelectedBlock;
        if (block is null)
        {
            SetStatus("No print block matches this layout. Select one on the right, or create it in MynaCAD and reload.");
            return;
        }
        layout.PrintBlock = block.Name;
        layout.PrintDescription = block.Description;
        layout.PrintDrawing = block.Drawing;
        if (string.IsNullOrWhiteSpace(layout.Title)) layout.Title = layout.Name;
        Store.Notify();
        ShowLayouts(keepLayout: true);
        ShowNameplate();
        SetStatus("Inserted print block \"" + block.Name + "\" on \"" + layout.Name + "\".");
    }

    DraftPrintBlock? Related(PlacedLayout layout)
    {
        var want = Relate(layout.Name);
        if (want.Length == 0) return null;
        return _printBlocks.FirstOrDefault(b =>
            string.Equals(Relate(b.Description), want, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Relate(b.Name), want, StringComparison.OrdinalIgnoreCase));
    }

    static string Relate(string text)
    {
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).Trim();
    }

    void UpdateNameplate()
    {
        if (_filling) return;
        var layout = SelectedLayout;
        if (layout is null)
        {
            SetStatus("Select a layout first.");
            return;
        }
        layout.Title = _title.Text.Trim();
        layout.Number = _number.Text.Trim();
        layout.Scale = _scale.Text.Trim();
        layout.DrawnBy = _drawnBy.Text.Trim();
        layout.Date = _date.Text.Trim();
        Store.Notify();
        SetStatus("Nameplate updated for \"" + layout.Name + "\". Save the project file to keep it.");
    }

    async Task ExportAsync()
    {
        var map = SelectedMap;
        var layout = SelectedLayout;
        if (map is null || layout is null)
        {
            SetStatus("Select a project map and a layout first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(layout.PrintBlock))
        {
            SetStatus("Insert the print block before exporting.");
            return;
        }
        UpdateNameplate();
        var picker = new FileSavePicker();
        var window = App.MainWindow;
        if (window is null)
        {
            SetStatus("The window is not ready for a save dialog.");
            return;
        }
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        picker.SuggestedFileName = SafeFile(layout.Name);
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (PdfExport.ExportSheetLayout(file.Path, Store, map, layout, out var err))
            SetStatus("Saved " + file.Path + ". The PDF is the sheet record. The drawing geometry stays in the project file.");
        else
            SetStatus(err ?? "The PDF was not written.");
    }

    void LoadPrintBlocks()
    {
        _printBlocks.Clear();
        var path = ProjectFiles.IsOdp(Store.FilePath) ? Store.FilePath : null;
        if (path is null || !File.Exists(path))
        {
            _blocks.ItemsSource = new List<string>();
            SetStatus("Open the project file. Print blocks are created in MynaCAD and saved into that file.");
            return;
        }
        try
        {
            using var file = OdpFile.Open(path);
            foreach (var name in file.DrawingNames())
            {
                var drawing = file.Drawing(name);
                if (drawing is null) continue;
                _printBlocks.AddRange(DraftPrintBlocks.Read(drawing.Bytes, name));
            }
        }
        catch (Exception ex)
        {
            _blocks.ItemsSource = new List<string>();
            SetStatus("The drawing could not be read: " + ex.Message);
            return;
        }
        _blocks.ItemsSource = _printBlocks.Select(b =>
            string.IsNullOrEmpty(b.Description) ? b.Name : b.Name + "  —  " + b.Description).ToList();
        if (_printBlocks.Count == 0)
            SetStatus("No print blocks in this drawing. Create one in MynaCAD, save the project file, then reload. A DWG that does not carry the project text will not list them.");
        else
            SetStatus(_printBlocks.Count + " print block(s) from the project file.");
    }

    async Task<string?> AskAsync(string title, string initial)
    {
        if (XamlRoot is null)
        {
            SetStatus("The page is not ready for a dialog.");
            return null;
        }
        var box = new TextBox { Text = initial };
        AutomationProperties.SetName(box, title);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    static bool AcceptName(string name, out string why)
    {
        if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
        {
            why = "The name must be 1 to 80 characters.";
            return false;
        }
        why = "";
        return true;
    }

    static string SafeFile(string name)
    {
        var cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned.Length == 0 ? "layout" : cleaned;
    }

    void SetStatus(string text) => _status.Text = text;
}
