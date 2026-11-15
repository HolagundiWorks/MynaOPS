// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Office.Shared;

// Log: CADSTUB_LOG (default %TEMP%\cadstub.log). One line per event: "<time> pid=<pid> <text>".
var logPath = Environment.GetEnvironmentVariable("CADSTUB_LOG") ?? Path.Combine(Path.GetTempPath(), "cadstub.log");
void Log(string s)
{
    try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} pid={Environment.ProcessId} {s}{Environment.NewLine}"); } catch (IOException) { }
}

Log("start args: " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
string? project = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--project") project = args[i + 1];
if (!args.Contains("--managed") || project is null || !project.EndsWith(".osp", StringComparison.OrdinalIgnoreCase))
{
    Log("refused: needs --managed --project <file.osp>");
    return 2;
}
project = Path.GetFullPath(project);

var lease = ProjectLease.TryAcquire(project, ProjectLease.AppCad);
if (lease is null)
{
    // a second start for the same project: hand over to the running one and leave
    Log("lease held: sending activate to the running instance");
    try
    {
        using var c = new NamedPipeClientStream(".", NamedCadPipe.PipeName(project), PipeDirection.InOut);
        c.Connect(2000);
        var b = Encoding.UTF8.GetBytes("{\"cmd\":\"activate\"}\n");
        c.Write(b, 0, b.Length);
        c.Flush();
    }
    catch (Exception ex) when (ex is IOException or TimeoutException) { Log("pipe failed: " + ex.Message); return 3; }
    return 0;
}

var quit = new ManualResetEventSlim(false);
var pipe = NamedCadPipe.PipeName(project);
var server = Task.Run(async () =>
{
    while (!quit.IsSet)
    {
        await using var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try { await s.WaitForConnectionAsync(); } catch (IOException) { continue; }
        try
        {
            using var r = new StreamReader(s, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await r.ReadLineAsync();
            Log("pipe: " + line);
            var cmd = line is null ? "" : (JsonNode.Parse(line) as JsonObject)?["cmd"]?.GetValue<string>() ?? "";
            var reply = cmd == "status" ? "{\"ok\":true,\"project\":" + JsonValue.Create(project)!.ToJsonString() + "}" : "{\"ok\":true}";
            var bytes = Encoding.UTF8.GetBytes(reply + "\n");
            await s.WriteAsync(bytes);
            await s.FlushAsync();
            if (cmd == "close")
            {
                await Task.Delay(400);      // the real one asks about unsaved work here
                Log("closing");
                quit.Set();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { Log("pipe error: " + ex.Message); }
    }
});
Log("holding " + ProjectLease.LockPath(project, ProjectLease.AppCad));
quit.Wait();
lease.Dispose();
Log("lease released, exiting");
return 0;
