// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BBSApp;

/// <summary>
/// The AI agent's state in the status bar. MynaOps hosts the agent (Olmo 3 7B on 127.0.0.1:8742) for itself and MynaCAD: this window, or a
/// MynaOps running without a window (<c>--ai-host</c>). The line is checked every 10 seconds off the UI thread; clicking it opens
/// Edit &gt; Assistant model.
/// </summary>
public sealed partial class MainWindow
{
    private readonly TextBlock _aiStatusText = new()
    {
        Text = "AI: …",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button? _aiStatusButton;

    private Button _aiStatus => _aiStatusButton ??= MakeAiStatusButton();

    private Button MakeAiStatusButton()
    {
        var b = new Button
        {
            Content = _aiStatusText,
            Style = (Style)Application.Current.Resources["HcwGhostButton"],
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            MinWidth = 0,
        };
        AutomationProperties.SetName(b, "AI agent status");
        ToolTipService.SetToolTip(b, "AI agent");
        return b;
    }

    private DispatcherTimer? _aiStatusTimer;
    private int _aiStatusChecking;

    private void StartAiStatus()
    {
        _aiStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _aiStatusTimer.Tick += (_, _) => _ = RefreshAiStatusAsync();
        _aiStatusTimer.Start();
        Closed += (_, _) => _aiStatusTimer?.Stop();
        _ = RefreshAiStatusAsync();
    }

    /// <summary>Look again now (also after the model was downloaded or located).</summary>
    internal async Task RefreshAiStatusAsync()
    {
        if (Interlocked.Exchange(ref _aiStatusChecking, 1) == 1) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var (text, detail) = await Task.Run(() => Office.Ai.AiStatus.CheckAsync(cts.Token)).ConfigureAwait(true);
            _aiStatusText.Text = text;
            ToolTipService.SetToolTip(_aiStatus, detail);
            AutomationProperties.SetHelpText(_aiStatus, detail);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _aiStatusText.Text = "AI: checking…";
        }
        finally
        {
            Interlocked.Exchange(ref _aiStatusChecking, 0);
        }
    }

    /// <summary>The assistant page changed the model: the next question starts the assistant again with it.</summary>
    internal void ForgetAssistant()
    {
        _assistant = null;
        _ = RefreshAiStatusAsync();
    }
}
