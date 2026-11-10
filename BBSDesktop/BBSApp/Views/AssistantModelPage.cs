// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Office.Ai;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BBSApp.Views;

/// <summary>
/// Edit &gt; Assistant model (or a click on the AI line in the status bar): where the AI agent's model is, whether MynaOps is hosting the agent,
/// and two ways to get the model: download it (the same pinned file, size and SHA-256 the installer's "model" task uses, into the same shared
/// folder <c>%LOCALAPPDATA%\HumanCentricWorks\models</c>) or locate a copy elsewhere. MynaCAD uses the agent MynaOps hosts, so it needs nothing here.
/// </summary>
public sealed class AssistantModelPage : Page
{
    static CancellationTokenSource? _download;   // one download per app, kept when the page is left
    static string _downloadLine = "";

    readonly TextBlock _state = Text("", "BodyTextStyle");
    readonly TextBlock _where = Text("", "BodyTextStyle");
    readonly TextBlock _progressText = Text("", "BodyTextStyle");
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Button _downloadButton, _cancelButton, _locateButton, _forgetButton;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public AssistantModelPage()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var page = new StackPanel { Spacing = 12, Padding = new Thickness(4, 4, 4, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Text("The AI agent (Olmo 3 7B) runs on this computer only. MynaOps hosts it for itself and for MynaCAD: this window when it is "
            + "open, otherwise MynaOps without a window, which MynaCAD starts when it needs the agent and which ends by itself when neither app is open.",
            "CaptionSecondaryStyle"));
        _state.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_state, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        page.Children.Add(_state);
        _where.TextWrapping = TextWrapping.Wrap;
        _where.IsTextSelectionEnabled = true;
        page.Children.Add(_where);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _downloadButton = Button($"Download the model ({LocalModel.Bytes / 1_000_000_000.0:0.0} GB)", true, DownloadAsync);
        _cancelButton = Button("Stop download", false, () => { _download?.Cancel(); return Task.CompletedTask; });
        _locateButton = Button("Locate a model file…", false, LocateAsync);
        _forgetButton = Button("Forget the located file", false, () => { LocalModel.SetLocated(null); Changed(); return Task.CompletedTask; });
        buttons.Children.Add(_downloadButton);
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_locateButton);
        buttons.Children.Add(_forgetButton);
        page.Children.Add(buttons);
        page.Children.Add(_progress);
        _progressText.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_progressText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        page.Children.Add(_progressText);
        page.Children.Add(Text($"The file is {LocalModel.FileName} from unsloth/Olmo-3-7B-Instruct-GGUF (Apache-2.0), checked against its SHA-256 before it is "
            + "used. The MynaOps and MynaCAD installer can download the same file (task \"Download the assistant model\").", "CaptionSecondaryStyle"));

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled };
        _tick.Tick += (_, _) => Show();
        Loaded += (_, _) => { Show(); _tick.Start(); };
        Unloaded += (_, _) => _tick.Stop();
    }

    static TextBlock Text(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Application.Current.Resources.TryGetValue(style, out var s) && s is Style st) t.Style = st;
        return t;
    }

    static Button Button(string label, bool primary, Func<Task> run)
    {
        var b = new Button { Content = label };
        if (Application.Current.Resources.TryGetValue(primary ? "HcwPrimaryButton" : "HcwGhostButton", out var s) && s is Style st) b.Style = st;
        b.Click += async (_, _) => await run();
        return b;
    }

    void Show()
    {
        var model = LocalModel.Find();
        var problem = model is null ? null : LocalModel.Problem(model);
        var server = LocalModel.FindServer();
        var located = LocalModel.ReadPointer(LocalModel.PointerFile);
        string hosting = SuiteAiCoordinator.IsHost
            ? "This MynaOps window hosts the AI agent on 127.0.0.1:8742."
            : AiHostRecord.Read() is { } r && r.Pid != Environment.ProcessId
                ? (r.Mode == "headless" ? "MynaOps without a window hosts the AI agent (started for MynaCAD)." : "Another MynaOps window hosts the AI agent.")
                : "The AI agent is not running now; it starts with the first question.";
        _state.Text = model is null
            ? "The assistant model is not installed. " + hosting
            : problem is not null ? problem + " " + hosting
            : server is null ? $"The model is installed, but the model runtime (llama-server) is missing. {hosting}"
            : "The assistant model is installed. " + hosting;
        _where.Text = $"Model: {model ?? Path.Combine(LocalModel.SharedDirectory, LocalModel.FileName) + " (missing)"}\nRuntime: {server ?? "missing"}"
            + (located is null ? "" : $"\nLocated file: {located}");
        bool downloading = _download is not null;
        _downloadButton.IsEnabled = !downloading && (model is null || problem is not null);
        _cancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _locateButton.IsEnabled = !downloading;
        _forgetButton.Visibility = located is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _progressText.Text = _downloadLine;
    }

    void Changed()
    {
        App.MainWindow?.ForgetAssistant();
        Show();
    }

    async Task DownloadAsync()
    {
        if (_download is not null) return;
        _download = new CancellationTokenSource();
        var ct = _download.Token;
        _downloadLine = "Starting the download…";
        Show();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            double pct = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            _progress.Value = pct;
            _downloadLine = $"Downloaded {p.Done / 1_000_000.0:N0} of {p.Total / 1_000_000.0:N0} MB ({pct.ToString("0", CultureInfo.CurrentCulture)} %).";
            _progressText.Text = _downloadLine;
        });
        try
        {
            var path = await Task.Run(() => ModelDownload.RunAsync(progress, ct), ct);
            _downloadLine = $"The model is installed: {path}.";
        }
        catch (OperationCanceledException)
        {
            _downloadLine = "The download was stopped. Nothing was kept.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _downloadLine = "The download failed: " + ex.Message;
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
        Changed();
    }

    async Task LocateAsync()
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".gguf");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            LocalModel.SetLocated(file.Path);
            _downloadLine = $"Using {file.Path}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _downloadLine = ex.Message;
        }
        Changed();
    }
}
