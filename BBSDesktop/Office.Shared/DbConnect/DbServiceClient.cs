// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Office.Shared.DbConnect;

public enum ServiceFailure
{
    /// <summary>Nothing is listening: Myna is not running.</summary>
    Unreachable,
    /// <summary>401: the token was refused, ask Kivo again.</summary>
    Unauthorized,
    /// <summary>409: the shared copy moved since this revision.</summary>
    Conflict,
    /// <summary>Any other answer.</summary>
    Http,
}

public sealed class ServiceException : Exception
{
    public ServiceFailure Failure { get; }

    public ServiceException(ServiceFailure failure, string message) : base(message) => Failure = failure;
}

public sealed record DbConnected(long? RemoteRevision, string Action);

public sealed record DbProjectStatus(long? LocalRevision, long? RemoteRevision, string State, long Pending);

/// <summary>Myna's database service on loopback HTTP (default <c>http://127.0.0.1:8743</c>). Contract: Kivo <c>docs/contracts/session-broker.md</c> section 2.</summary>
public sealed class DbServiceClient
{
    public const string DefaultUrl = "http://127.0.0.1:8743";
    readonly HttpClient _http;

    public DbServiceClient(string? baseUrl = null)
    {
        var url = (baseUrl ?? Environment.GetEnvironmentVariable("OSAS_DB_URL") ?? DefaultUrl).TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(url + "/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    async Task<JsonObject> Send(HttpMethod method, string path, string? token, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ServiceException(ServiceFailure.Unreachable, "The database service is not running (" + ex.Message + ").");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonObject? json = null;
            try { json = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
            return (int)response.StatusCode switch
            {
                >= 200 and < 300 => json ?? new JsonObject(),
                401 => throw new ServiceException(ServiceFailure.Unauthorized, "The session was refused."),
                409 => throw new ServiceException(ServiceFailure.Conflict, "The shared copy has a newer revision."),
                var code => throw new ServiceException(ServiceFailure.Http, $"The database service answered {code}: {json?["message"]?.GetValue<string>()}"),
            };
        }
    }

    public async Task<(string State, string Sync, long Pending)> Health()
    {
        var v = await Send(HttpMethod.Get, "v1/health", null, null).ConfigureAwait(false);
        return (v["state"]?.GetValue<string>() ?? "", v["sync"]?.GetValue<string>() ?? "", v["pending"]?.GetValue<long>() ?? 0);
    }

    public async Task<DbConnected> Connect(string token, string projectId, string name, string ospPath, long revision)
    {
        var v = await Send(HttpMethod.Post, "v1/projects/connect", token,
            new JsonObject { ["project_id"] = projectId, ["name"] = name, ["osp_path"] = ospPath, ["revision"] = revision }).ConfigureAwait(false);
        return new DbConnected(v["remote_revision"]?.GetValue<long>(), v["action"]?.GetValue<string>() ?? "none");
    }

    public async Task<DbProjectStatus> Project(string token, string projectId)
    {
        var v = await Send(HttpMethod.Get, $"v1/projects/{Uri.EscapeDataString(projectId)}", token, null).ConfigureAwait(false);
        return new DbProjectStatus(v["local_revision"]?.GetValue<long>(), v["remote_revision"]?.GetValue<long>(), v["state"]?.GetValue<string>() ?? "", v["pending"]?.GetValue<long>() ?? 0);
    }

    /// <summary>
    /// Send the saved revision; the service reads the file from the path registered at connect. Null means the service keeps project files on
    /// this PC (<c>local_only</c>): there is nothing to send and the local file is the shared database.
    /// </summary>
    public async Task<long?> Push(string token, string projectId, long revision)
    {
        var v = await Send(HttpMethod.Post, $"v1/projects/{Uri.EscapeDataString(projectId)}/push", token, new JsonObject { ["revision"] = revision }).ConfigureAwait(false);
        if (v["local_only"]?.GetValue<bool>() == true) return null;
        return v["remote_revision"]?.GetValue<long>() ?? revision;
    }
}
