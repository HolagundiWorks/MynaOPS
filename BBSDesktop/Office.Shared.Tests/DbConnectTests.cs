// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Office.Shared.DbConnect;
using Xunit;

namespace Office.Shared.Tests;

public class DbConnectTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "falcon-db-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------ fakes

    sealed class FakeKivo : IBrokerTransport
    {
        public int Asks;
        public bool Reachable = true;
        public Func<int, string> Ask = n => $$"""{"v":1,"ok":true,"token":"token-{{n}}","expires_utc":"2026-11-09T00:00:00Z","user":"owner"}""";

        public string Request(string message)
        {
            if (!Reachable) throw new IOException("no pipe");
            var v = JsonNode.Parse(message)!.AsObject();
            switch (v["op"]!.GetValue<string>())
            {
                case "status": return """{"v":1,"ok":true,"signed_in":true}""";
                case "ask_session":
                    Assert.Equal("falcon", v["app"]!.GetValue<string>());
                    Assert.Equal("db_connect", v["purpose"]!.GetValue<string>());
                    return Ask(Interlocked.Increment(ref Asks) - 1);
                default: throw new InvalidOperationException("unexpected op");
            }
        }
    }

    sealed record Seen(string Method, string Path, string Auth, JsonNode? Body);

    sealed class FakeMyna : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly List<Seen> Requests = new();
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public FakeMyna(Func<Seen, (int Code, string Body)> respond)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                    _ = Task.Run(() => Handle(client, respond));
                }
            });
        }

        void Handle(TcpClient client, Func<Seen, (int Code, string Body)> respond)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var first = reader.ReadLine() ?? "";
                var parts = first.Split(' ');
                string auth = "";
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    if (line.StartsWith("authorization:", StringComparison.OrdinalIgnoreCase)) auth = line["authorization:".Length..].Trim();
                    if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line["content-length:".Length..].Trim());
                }

                var buffer = new char[length];
                var read = 0;
                while (read < length) read += reader.Read(buffer, read, length - read);
                JsonNode? body = null;
                if (length > 0) body = JsonNode.Parse(new string(buffer));
                var seen = new Seen(parts[0], parts.Length > 1 ? parts[1] : "", auth, body);
                lock (Requests) Requests.Add(seen);
                var (code, text) = respond(seen);
                var bytes = Encoding.UTF8.GetBytes(text);
                var head = $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(head));
                stream.Write(bytes);
            }
        }

        public List<Seen> Snapshot() { lock (Requests) return Requests.ToList(); }
        public void Dispose() => _listener.Stop();
    }

    DbConnector Connector(FakeKivo kivo, string url, bool enabled = true) =>
        new(kivo, new DbServiceClient(url), () => throw new FileNotFoundException("not installed"), kivoWait: TimeSpan.FromMilliseconds(50),
            pushDelay: TimeSpan.FromMilliseconds(30), enabled: enabled);

    (string Path, string Id) Project()
    {
        var path = Path.Combine(_dir, "house.osp");
        using var odp = OdpFile.Create(path, "House");
        return (path, odp.Meta().ProjectId);
    }

    static void WaitFor(Func<bool> ok)
    {
        for (var i = 0; i < 200; i++)
        {
            if (ok()) return;
            Thread.Sleep(25);
        }

        Assert.Fail("timed out");
    }

    const string Connected = """{"connected":true,"remote_revision":1,"action":"none"}""";

    // ------------------------------------------------------------ tests

    [Fact]
    public void The_status_text_says_what_a_person_can_do()
    {
        Assert.Equal("", DbSnapshot.Idle.Label);
        Assert.Equal("Database: connected", new DbSnapshot(DbState.Connected, null, "", 0).Label);
        Assert.Equal("Database: connected, 2 to send", new DbSnapshot(DbState.Connected, null, "", 2).Label);
        Assert.Equal("Database: sign in to Kivo", new DbSnapshot(DbState.SignedOut, null, "", 0).Label);
        Assert.Equal("Database: offline", new DbSnapshot(DbState.Offline, null, "", 0).Label);
    }

    [Fact]
    public void Opening_a_project_asks_Kivo_then_tells_the_service_with_that_token()
    {
        var (path, id) = Project();
        using var myna = new FakeMyna(_ => (200, Connected));
        var c = Connector(new FakeKivo(), myna.Url);

        c.ConnectBlocking(path);

        Assert.Equal(DbState.Connected, c.Snapshot.State);
        Assert.Equal("House", c.Snapshot.Project);
        var seen = Assert.Single(myna.Snapshot());
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/v1/projects/connect", seen.Path);
        Assert.Equal("Bearer token-0", seen.Auth);
        Assert.Equal(id, seen.Body!["project_id"]!.GetValue<string>());
        Assert.Equal(path, seen.Body["osp_path"]!.GetValue<string>());
        Assert.Equal("House", seen.Body["name"]!.GetValue<string>());
    }

    [Fact]
    public void Nobody_signed_in_is_reported_and_the_service_is_not_called()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(_ => (200, "{}"));
        var kivo = new FakeKivo { Ask = _ => """{"v":1,"ok":false,"error":"signed_out","message":"Kivo is not signed in."}""" };
        var c = Connector(kivo, myna.Url);
        c.ConnectBlocking(path);
        Assert.Equal(DbState.SignedOut, c.Snapshot.State);
        Assert.Equal("Database: sign in to Kivo", c.Snapshot.Label);
        Assert.Empty(myna.Snapshot());
    }

    [Fact]
    public void A_closed_sign_in_is_the_same_as_signed_out()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(_ => (200, "{}"));
        var c = Connector(new FakeKivo { Ask = _ => """{"v":1,"ok":false,"error":"cancelled","message":"closed"}""" }, myna.Url);
        c.ConnectBlocking(path);
        Assert.Equal(DbState.SignedOut, c.Snapshot.State);
    }

    [Fact]
    public void A_role_without_access_is_a_problem_not_a_retry()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(_ => (200, "{}"));
        var kivo = new FakeKivo { Ask = _ => """{"v":1,"ok":false,"error":"denied","message":"Your role cannot do this."}""" };
        var c = Connector(kivo, myna.Url);
        c.ConnectBlocking(path);
        Assert.Equal(DbState.Problem, c.Snapshot.State);
        Assert.Equal(1, kivo.Asks);
    }

    [Fact]
    public void No_Kivo_means_offline_and_the_file_is_not_touched()
    {
        var (path, _) = Project();
        var before = File.ReadAllBytes(path);
        var c = Connector(new FakeKivo { Reachable = false }, "http://127.0.0.1:9");
        c.ConnectBlocking(path);
        Assert.Equal(DbState.Offline, c.Snapshot.State);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void No_Myna_means_offline_with_a_hint()
    {
        var (path, _) = Project();
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var c = Connector(new FakeKivo(), $"http://127.0.0.1:{port}");
        c.ConnectBlocking(path);
        Assert.Equal(DbState.Offline, c.Snapshot.State);
        Assert.Contains("Start Myna", c.Snapshot.Detail);
    }

    [Fact]
    public void A_refused_token_is_replaced_once_by_asking_Kivo_again()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(r => r.Auth == "Bearer token-0" ? (401, """{"ok":false,"error":"bad_token"}""") : (200, Connected));
        var kivo = new FakeKivo();
        var c = Connector(kivo, myna.Url);
        c.ConnectBlocking(path);
        Assert.Equal(DbState.Connected, c.Snapshot.State);
        Assert.Equal(2, kivo.Asks);
        Assert.Equal("Bearer token-1", myna.Snapshot().Last().Auth);
    }

    [Fact]
    public void A_newer_shared_copy_is_reported_and_never_applied()
    {
        var (path, _) = Project();
        var before = File.ReadAllBytes(path);
        using var myna = new FakeMyna(_ => (200, """{"connected":true,"remote_revision":9,"action":"pull"}"""));
        var c = Connector(new FakeKivo(), myna.Url);
        c.ConnectBlocking(path);
        Assert.Equal(DbState.Connected, c.Snapshot.State);
        Assert.Contains("newer revision", c.Snapshot.Detail);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(myna.Snapshot());
    }

    [Fact]
    public void A_service_that_wants_a_push_gets_the_current_revision()
    {
        var (path, id) = Project();
        using var myna = new FakeMyna(r => r.Path switch
        {
            "/v1/projects/connect" => (200, """{"connected":true,"remote_revision":0,"action":"push"}"""),
            var p when p.EndsWith("/push") => (200, """{"remote_revision":1}"""),
            _ => (200, """{"local_revision":1,"remote_revision":1,"state":"synced","pending":0}"""),
        });
        var c = Connector(new FakeKivo(), myna.Url);
        c.ConnectBlocking(path);
        var push = myna.Snapshot().Single(s => s.Path.EndsWith("/push"));
        Assert.Equal($"/v1/projects/{id}/push", push.Path);
        Assert.Equal("Bearer token-0", push.Auth);
        Assert.NotNull(push.Body!["revision"]);
        Assert.Equal(DbState.Connected, c.Snapshot.State);
    }

    [Fact]
    public void A_conflict_on_push_keeps_both_copies_and_says_so()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(r => r.Path == "/v1/projects/connect" ? (200, Connected) : (409, """{"ok":false,"error":"conflict"}"""));
        var c = Connector(new FakeKivo(), myna.Url);
        c.ConnectBlocking(path);
        c.PushNow(3);
        Assert.Equal(DbState.Connected, c.Snapshot.State);
        Assert.Contains("newer revision", c.Snapshot.Detail);
        Assert.Contains("Nothing was overwritten", c.Snapshot.Detail);
    }

    [Fact]
    public void Several_saves_in_a_row_go_as_one_push()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(r => r.Path switch
        {
            "/v1/projects/connect" => (200, Connected),
            var p when p.EndsWith("/push") => (200, """{"remote_revision":2}"""),
            _ => (200, """{"local_revision":2,"remote_revision":2,"state":"synced","pending":0}"""),
        });
        var c = Connector(new FakeKivo(), myna.Url);
        c.ConnectBlocking(path);
        for (var i = 0; i < 3; i++) c.ProjectSaved(path);
        WaitFor(() => myna.Snapshot().Any(s => s.Path.EndsWith("/push")));
        Thread.Sleep(250);
        Assert.Single(myna.Snapshot(), s => s.Path.EndsWith("/push"));
    }

    [Fact]
    public void A_disabled_connector_never_talks_to_anyone()
    {
        var (path, _) = Project();
        using var myna = new FakeMyna(_ => (200, "{}"));
        var kivo = new FakeKivo();
        var c = Connector(kivo, myna.Url, enabled: false);
        c.ProjectOpened(path);
        c.ProjectSaved(path);
        Thread.Sleep(200);
        Assert.Equal(0, kivo.Asks);
        Assert.Empty(myna.Snapshot());
        Assert.Equal(DbState.Idle, c.Snapshot.State);
    }

    [Fact]
    public void A_file_that_is_not_a_project_is_ignored()
    {
        using var myna = new FakeMyna(_ => (200, "{}"));
        var c = Connector(new FakeKivo(), myna.Url);
        c.ConnectBlocking(@"C:\drawings\plan.dxf");
        Assert.Equal(DbState.Idle, c.Snapshot.State);
    }

    [Fact]
    public void The_session_token_never_shows_in_text_output()
    {
        Assert.DoesNotContain("secret-token", new BrokerSession("secret-token", "x", "owner").ToString());
    }
}
