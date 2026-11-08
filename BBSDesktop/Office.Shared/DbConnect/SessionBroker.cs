// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Office.Shared.DbConnect;

/// <summary>One request, one reply, both UTF-8 JSON. An interface so the protocol can be tested without a pipe.</summary>
public interface IBrokerTransport
{
    string Request(string message);
}

/// <summary>Kivo's named pipe <c>osas-session-broker</c> (current user only, message mode).</summary>
public sealed class PipeBrokerTransport : IBrokerTransport
{
    public const string PipeName = "osas-session-broker";

    public string Request(string message)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(3000);
        pipe.ReadMode = PipeTransmissionMode.Message;
        var bytes = Encoding.UTF8.GetBytes(message);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
        var buffer = new byte[64 * 1024];
        using var all = new MemoryStream();
        do
        {
            var n = pipe.Read(buffer, 0, buffer.Length);
            if (n == 0) break;
            all.Write(buffer, 0, n);
        } while (!pipe.IsMessageComplete);
        return Encoding.UTF8.GetString(all.ToArray());
    }
}

public enum BrokerFailure
{
    /// <summary>The pipe is not there or Kivo did not answer.</summary>
    Unavailable,
    /// <summary>Nobody is signed in to Kivo (or the sign-in that was asked for did not finish).</summary>
    SignedOut,
    /// <summary>The person closed the sign-in.</summary>
    Cancelled,
    /// <summary>The user's role does not allow the purpose.</summary>
    Denied,
    /// <summary>An answer this client does not understand.</summary>
    Protocol,
}

public sealed class BrokerException : Exception
{
    public BrokerFailure Failure { get; }

    public BrokerException(BrokerFailure failure, string message) : base(message) => Failure = failure;
}

/// <summary>A session handed out by Kivo. The token is a credential: it is never printed.</summary>
public sealed record BrokerSession(string Token, string ExpiresUtc, string User)
{
    public override string ToString() => $"BrokerSession(user={User}, expires={ExpiresUtc}, token=<hidden>)";
}

/// <summary>The session broker client. Contract: Kivo <c>docs/contracts/session-broker.md</c>.</summary>
public static class SessionBroker
{
    static JsonObject Call(IBrokerTransport t, JsonObject request)
    {
        string text;
        try
        {
            text = t.Request(request.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new BrokerException(BrokerFailure.Unavailable, "Kivo is not available: " + ex.Message);
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new BrokerException(BrokerFailure.Protocol, "Kivo's answer is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new BrokerException(BrokerFailure.Protocol, "Unexpected answer from Kivo: " + ex.Message);
        }
    }

    static BrokerException ErrorOf(JsonObject reply)
    {
        var message = reply["message"]?.GetValue<string>() ?? "";
        return (reply["error"]?.GetValue<string>() ?? "") switch
        {
            "signed_out" => new BrokerException(BrokerFailure.SignedOut, "Kivo is not signed in."),
            "cancelled" => new BrokerException(BrokerFailure.Cancelled, "The sign-in was cancelled."),
            "denied" => new BrokerException(BrokerFailure.Denied, message),
            var other => new BrokerException(BrokerFailure.Protocol, other.Length == 0 ? "No error code." : $"{other}: {message}"),
        };
    }

    /// <summary>Never opens a window: is Kivo there, and is anyone signed in?</summary>
    public static bool Status(IBrokerTransport t)
    {
        var reply = Call(t, new JsonObject { ["v"] = 1, ["op"] = "status" });
        if (reply["ok"]?.GetValue<bool>() != true) throw ErrorOf(reply);
        return reply["signed_in"]?.GetValue<bool>() ?? false;
    }

    /// <summary>Ask for a session for <paramref name="purpose"/>. With <paramref name="interactive"/> Kivo shows its sign-in when nobody is signed in.</summary>
    public static BrokerSession AskSession(IBrokerTransport t, string app, string purpose, string? projectId, bool interactive)
    {
        var request = new JsonObject { ["v"] = 1, ["op"] = "ask_session", ["app"] = app, ["purpose"] = purpose, ["interactive"] = interactive };
        if (projectId is not null) request["project_id"] = projectId;
        var reply = Call(t, request);
        if (reply["ok"]?.GetValue<bool>() != true) throw ErrorOf(reply);
        var token = reply["token"]?.GetValue<string>() ?? "";
        if (token.Length == 0) throw new BrokerException(BrokerFailure.Protocol, "No token in Kivo's answer.");
        return new BrokerSession(token, reply["expires_utc"]?.GetValue<string>() ?? "", reply["user"]?.GetValue<string>() ?? "");
    }

    /// <summary>Wait until Kivo answers, starting it (hidden) when the pipe is not there. Gives up after <paramref name="wait"/>.</summary>
    public static void EnsureBroker(IBrokerTransport t, Action launch, TimeSpan wait)
    {
        try
        {
            Status(t);
            return;
        }
        catch (BrokerException) { }

        try
        {
            launch();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new BrokerException(BrokerFailure.Unavailable, "Kivo could not be started: " + ex.Message);
        }

        var started = DateTime.UtcNow;
        while (true)
        {
            try
            {
                Status(t);
                return;
            }
            catch (BrokerException) when (DateTime.UtcNow - started < wait)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Start Kivo without a window: <c>O-Studio.exe --broker</c> from the usual install folder, or the file named in <c>OSTUDIO_KIVO_EXE</c>.</summary>
    public static void LaunchKivo()
    {
        var exe = Environment.GetEnvironmentVariable("OSTUDIO_KIVO_EXE");
        if (string.IsNullOrEmpty(exe))
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "O-Studio", "O-Studio.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Kivo is not installed.", exe);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--broker") { UseShellExecute = false });
    }
}
