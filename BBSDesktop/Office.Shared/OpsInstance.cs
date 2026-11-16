// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Office.Shared;

/// <summary>
/// The running MynaOps receives a file request from a second start (a double-clicked project, a drawing handed over by MynaCAD) so that no second window
/// opens (docs/ops-first-workflow.md 16.4). A named pipe for the current user: one JSON line in, one JSON line out.
/// <code>{"cmd":"open","path":"C:\\p\\house.osp"}   {"cmd":"import-pick","path":"C:\\p\\plan.dxf"}   {"cmd":"activate"}   gives {"ok":true} or {"ok":false,"error":"..."}</code>
/// </summary>
public sealed class OpsInstance : IDisposable
{
    const int MaxLine = 16 * 1024;
    readonly string _name;
    readonly Func<StartupRequest, bool> _handler;
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    /// <summary>The pipe name for this user; <paramref name="user"/> is for tests.</summary>
    public static string PipeName(string? user = null)
    {
        var who = (user ?? Environment.UserDomainName + "\\" + Environment.UserName).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..16].ToLowerInvariant();
        return "MynaOps." + hash;
    }

    /// <param name="handler">Called for each valid request (any thread); true when it took the request.</param>
    /// <param name="pipeName">Tests only.</param>
    public OpsInstance(Func<StartupRequest, bool> handler, string? pipeName = null)
    {
        _handler = handler;
        _name = pipeName ?? PipeName();
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        string? line;
        try { line = await ReadLineAsync(server, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        var reply = Handle(line);
        var bytes = Encoding.UTF8.GetBytes(reply + "\n");
        try
        {
            await server.WriteAsync(bytes, ct).ConfigureAwait(false);
            await server.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    public string Handle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Fail("empty request");
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var c) || c.ValueKind != JsonValueKind.String) return Fail("a request is an object with a cmd");
            string? path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            StartupRequest? request = c.GetString() switch
            {
                "activate" => StartupRequest.None,
                "open" => StartupRequest.Make(StartupKind.OpenProject, path),
                "import-pick" => StartupRequest.Make(StartupKind.ImportPick, path),
                _ => null,
            };
            if (request is null) return Fail("unknown request, or the file is not an existing project or drawing");
            return _handler(request) ? "{\"ok\":true}" : Fail("MynaOps could not take the request now");
        }
        catch (JsonException) { return Fail("not json"); }
    }

    static string Fail(string why) => JsonSerializer.Serialize(new { ok = false, error = why });

    static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < MaxLine)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0 || one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
    }

    /// <summary>
    /// Hand a request to a MynaOps that already runs. True when one answered ok (the caller then exits); false when none runs or it refused.
    /// </summary>
    public static bool TryForward(StartupRequest request, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? PipeName(), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
            var line = request.Kind switch
            {
                StartupKind.OpenProject => JsonSerializer.Serialize(new { cmd = "open", path = request.Path }),
                StartupKind.ImportPick => JsonSerializer.Serialize(new { cmd = "import-pick", path = request.Path }),
                _ => "{\"cmd\":\"activate\"}",
            };
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            using var cts = new CancellationTokenSource(timeout);
            var reply = ReadLineAsync(client, cts.Token).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(reply ?? "");
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
