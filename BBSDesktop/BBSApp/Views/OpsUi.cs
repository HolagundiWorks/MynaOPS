// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BBSApp.Views;

/// <summary>Building blocks for the Ops-first pages and windows (project home, wizard, adoption): the suite's existing styles and sizes, nothing new.</summary>
internal static class OpsUi
{
    public static Style? StyleOf(string key) => Application.Current.Resources.TryGetValue(key, out var s) && s is Style st ? st : null;

    public static TextBlock Text(string text, string style = "BodyTextStyle", bool wrap = true)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (StyleOf(style) is { } st) t.Style = st;
        return t;
    }

    public static Border Card(string title, UIElement body, string? automationId = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = Text(title, "FormSectionStyle");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(body);
        var card = new Border { Child = stack };
        if (StyleOf("FluentCardStyle") is { } st) card.Style = st;
        AutomationProperties.SetName(card, title);
        if (automationId is not null) AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    public static Button Button(string label, bool primary, string automationId, Action click)
    {
        var b = new Button { Content = label };
        if (StyleOf(primary ? "HcwPrimaryButton" : "HcwGhostButton") is { } st) b.Style = st;
        AutomationProperties.SetAutomationId(b, automationId);
        AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A label above its control (compact: caption over a 26 px control).</summary>
    public static StackPanel Field(string label, FrameworkElement control, string automationId, double width = 260)
    {
        AutomationProperties.SetAutomationId(control, automationId);
        AutomationProperties.SetName(control, label);
        control.Width = width;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        var s = new StackPanel { Spacing = 2 };
        s.Children.Add(Text(label, "CaptionSecondaryStyle"));
        s.Children.Add(control);
        return s;
    }

    public static TextBox Box(string value = "", string placeholder = "") => new() { Text = value, PlaceholderText = placeholder };

    public static NumberBox Number(double value, double min, double max, double step = 1) =>
        new() { Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    /// <summary>A grid of label and value rows (the Project database page's "facts").</summary>
    public static Grid Facts(IEnumerable<(string Label, string Value, string? Id)> rows)
    {
        var g = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        foreach (var (label, value, id) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, "CaptionSecondaryStyle");
            var v = Text(value);
            v.IsTextSelectionEnabled = true;
            if (id is not null) { AutomationProperties.SetAutomationId(v, id); AutomationProperties.SetName(v, value); }
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
            r++;
        }
        return g;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(System.Globalization.CultureInfo.CurrentCulture) + " bytes",
    };

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string body, string action, string cancel = "Cancel")
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                PrimaryButtonText = action,
                CloseButtonText = cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root,
            };
            return await dlg.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
    }
}

/// <summary>
/// A standard window for the Ops-first flows: the system caption and buttons, resizable, the suite's Fog canvas and theme, a footer for its buttons.
/// It is made owned and modal by the main window when it can be (<see cref="ShowModalAsync"/>); the work is awaited as a task.
/// </summary>
internal abstract class OpsWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    const int GwlpHwndParent = -8;

    readonly TaskCompletionSource _closed = new();
    protected readonly Grid Root = new() { RowSpacing = 0 };
    protected readonly StackPanel Footer = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 12) };

    protected OpsWindow(string title, int width, int height)
    {
        Title = title;
        SystemBackdrop = null;
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.Background = O.Ui.SuiteAppearance.TryBrush("HcwFogBrush") ?? (Brush)Application.Current.Resources["HcwFogBrush"];
        Grid.SetRow(Footer, 1);
        Root.Children.Add(Footer);
        Content = Root;
        Root.RequestedTheme = O.Ui.SuiteAppearance.Current;
        AppWindow.Resize(new SizeInt32(width, height));
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        Closed += (_, _) => _closed.TrySetResult();
    }

    protected void SetBody(UIElement body)
    {
        Grid.SetRow((FrameworkElement)body, 0);
        Root.Children.Add(body);
    }

    /// <summary>Show the window above the main window (owned and modal when the platform allows it) and return when it is closed.</summary>
    public Task ShowModalAsync(Window? owner)
    {
        Activate();
        try
        {
            if (owner is not null && AppWindow.Presenter is OverlappedPresenter p)
            {
                SetWindowLongPtr(WindowNative.GetWindowHandle(this), GwlpHwndParent, WindowNative.GetWindowHandle(owner));
                p.IsModal = true;
                p.IsMinimizable = false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException) { /* an ordinary window, not modal */ }
        return _closed.Task;
    }
}
