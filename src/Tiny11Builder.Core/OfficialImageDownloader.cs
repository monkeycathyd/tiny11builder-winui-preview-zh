using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tiny11Builder.Core;

public sealed record DownloadResult(string Path, string Sha256, bool VerifiedAgainstPublishedHash);
public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond)
{
    public double? Percent => Total > 0 ? Bytes * 100d / Total : null;
}

public sealed class OfficialImageDownloader(HttpClient client)
{
    public const string DownloadPage = "https://www.microsoft.com/software-download/windows11";
    public const string ArmDownloadPage = "https://www.microsoft.com/software-download/windows11arm64";
    private const long MaxSize = 32L * 1024 * 1024 * 1024;
    private sealed record PartialInfo(string Resource, string? ETag, long? Length);

    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromHours(4) };

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.IdnHost is not ("software.download.prss.microsoft.com" or "software-download.microsoft.com" or "download.microsoft.com") ||
            !uri.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请粘贴由 Microsoft 下载页面生成的直接 HTTPS .iso 链接。不接受第三方主机。");
        return uri;
    }

    public static string? ValidateHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var normalized = hash.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("发布的 SHA-256 必须正好包含 64 个十六进制字符。");
        return normalized;
    }

    public async Task<DownloadResult> DownloadAsync(string url, string destination, string? expectedHash,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var uri = ValidateUrl(url);
        expectedHash = ValidateHash(expectedHash);
        destination = PathSafety.Full(destination);
        PathSafety.RejectReparsePoints(destination);
        if (File.Exists(destination) || !destination.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择新的 ISO 文件名；现有下载永远不会被覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        var metadata = part + ".json";
        PathSafety.RejectReparsePoints(part);
        PathSafety.RejectReparsePoints(metadata);
        var resource = uri.GetLeftPart(UriPartial.Path);
        PartialInfo? saved = null;
        if (File.Exists(part))
        {
            saved = File.Exists(metadata) ? JsonSerializer.Deserialize<PartialInfo>(await File.ReadAllTextAsync(metadata, token)) : null;
            if (saved?.Resource != resource)
                throw new IOException("已存在属于其他下载的未完成文件。请选择其他目标位置。");
        }
        var offset = saved?.ETag is not null && !saved.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ?
            new FileInfo(part).Length : 0;
        using var response = await SendAsync(uri, offset, saved?.ETag, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            response.Content.Headers.ContentRange?.Length == offset && offset > 0)
            return await FinishAsync(part, destination, metadata, expectedHash, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset || saved?.ETag != response.Headers.ETag?.ToString())
                throw new IOException("Microsoft 映像在下载过程中发生了变化。请选择新的目标位置并重新开始。");
        }
        else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else throw new IOException($"意外的下载响应：{response.StatusCode}");
        var total = response.Content.Headers.ContentRange?.Length ??
            (response.Content.Headers.ContentLength is long length ? length + offset : (long?)null);
        if (total is > MaxSize || total is <= 0) throw new IOException("ISO 大小无效或超过 32 GiB。");
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (total is long size && drive.AvailableFreeSpace < size - offset + 1024L * 1024 * 1024)
            throw new IOException("空间不足，无法容纳下载内容及 1 GiB 安全余量。");
        await File.WriteAllTextAsync(metadata,
            JsonSerializer.Serialize(new PartialInfo(resource, response.Headers.ETag?.ToString(), total)), token).ConfigureAwait(false);
        await using (var file = new FileStream(part, offset > 0 ? FileMode.Open : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            file.Position = offset;
            await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[1024 * 1024];
            var clock = Stopwatch.StartNew();
            long readTotal = offset;
            while (true)
            {
                var count = await content.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                readTotal += count;
                if (readTotal > MaxSize || (total is long limit && readTotal > limit))
                    throw new IOException("服务器发送的数据超过了声明的 ISO 大小。");
                await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                progress?.Report(new(readTotal, total, (readTotal - offset) / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }
            await file.FlushAsync(token).ConfigureAwait(false);
            if (total is long expected && readTotal != expected)
                throw new IOException("下载未完成。请重试以续传未完成文件。");
        }
        return await FinishAsync(part, destination, metadata, expectedHash, token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long offset, string? etag, CancellationToken token)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag!));
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null) throw new IOException("Microsoft 返回了没有目标位置的重定向。");
            uri = ValidateUrl((next.IsAbsoluteUri ? next : new Uri(uri, next)).AbsoluteUri);
        }
        throw new IOException("下载重定向次数过多。");
    }

    private static async Task<DownloadResult> FinishAsync(string part, string destination, string metadata,
        string? expectedHash, CancellationToken token)
    {
        await using var stream = File.OpenRead(part);
        await IsoStructure.ValidateAsync(stream, token).ConfigureAwait(false);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (expectedHash is not null && hash != expectedHash)
            throw new InvalidDataException("SHA-256 不匹配。该 ISO 未被接受；未完成文件已保留。");
        stream.Close();
        File.Move(part, destination, overwrite: false);
        File.Delete(metadata);
        return new(destination, hash, expectedHash is not null);
    }
}
