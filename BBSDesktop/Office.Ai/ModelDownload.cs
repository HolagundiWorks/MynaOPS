// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Security.Cryptography;

namespace Office.Ai;

/// <summary>
/// Downloads the assistant model into the shared folder, as the MynaOps and MynaCAD installer's "model" task does: the pinned file from
/// <see cref="LocalModel.DownloadUrl"/>, checked against <see cref="LocalModel.Bytes"/> and <see cref="LocalModel.Sha256"/>, written as
/// <c>.part</c> and renamed only when whole. An existing whole file is kept.
/// </summary>
public static class ModelDownload
{
    public static string Target => Path.Combine(LocalModel.SharedDirectory, LocalModel.FileName);

    /// <summary>Download (or keep) the model; returns its path. <paramref name="progress"/> gets bytes done and the total.</summary>
    public static async Task<string> RunAsync(IProgress<(long Done, long Total)>? progress, CancellationToken ct, HttpMessageHandler? handler = null,
        string? target = null, string url = LocalModel.DownloadUrl, long bytes = LocalModel.Bytes, string sha256 = LocalModel.Sha256)
    {
        target ??= Target;
        if (File.Exists(target) && new FileInfo(target).Length == bytes)
            return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? bytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int n;
                long lastReport = 0;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await sink.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (done - lastReport >= 8 << 20 || done == total)
                    {
                        lastReport = done;
                        progress?.Report((done, total));
                    }
                }
            }
            progress?.Report((done, total));
            if (done != bytes)
                throw new InvalidOperationException($"The download stopped at {done:N0} of {bytes:N0} bytes. Try again.");
            var got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded file is not the expected model (SHA-256 differs). Try again.");
            File.Move(part, target, overwrite: true);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
