// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace BBSApp;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // MynaOps hosts the AI agent for itself and MynaCAD. Without a window: O-CoreOp.exe --ai-host (MynaCAD starts it when no host answers;
        // it ends by itself when idle). --ai-host-stop asks a running one to end (the installer uses it). Neither starts WinUI.
        if (Office.Ai.HeadlessAiHost.IsStopArgument(args))
        {
            Office.Ai.HeadlessAiHost.RequestStop();
            return 0;
        }
        if (Office.Ai.HeadlessAiHost.IsHostArgument(args))
            return Office.Ai.HeadlessAiHost.Run();

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
