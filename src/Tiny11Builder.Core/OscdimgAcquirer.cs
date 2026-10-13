using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
public sealed class OscdimgAcquirer(HttpClient client, Func<string>? findInstalled = null)
{
    public const string DownloadUrl = "https://msdl.microsoft.com/download/symbols/oscdimg.exe/688CABB065000/oscdimg.exe";
    public const string ApprovedSha256 = "2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861";

    public async Task<string> EnsureAsync(string toolsDirectory, IProgress<string>? progress, CancellationToken token)
    {
        var installed = findInstalled is null ? ToolLocator.FindOscdimg() : findInstalled();
        if (installed.Length > 0) { ToolLocator.Validate(installed, "oscdimg.exe"); return installed; }
        toolsDirectory = PathSafety.Full(toolsDirectory);
        PathSafety.RejectReparsePoints(toolsDirectory);
        var destination = Path.Combine(toolsDirectory, "oscdimg.exe");
        if (File.Exists(destination)) { ToolLocator.Validate(destination, "oscdimg.exe"); return destination; }
        Directory.CreateDirectory(toolsDirectory);
        progress?.Report("正在下载 Microsoft 的 ISO 创建工具。");
        using var response = await DownloadAsync(token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await content.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > 1024 * 1024) throw new InvalidDataException("该 ISO 工具超出了预期大小。");
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ApprovedSha256)
            throw new InvalidDataException("Microsoft ISO 工具的校验和不匹配。它未被保存，也未被执行。");
        var temporary = Path.Combine(toolsDirectory, "oscdimg-" + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        ToolLocator.Validate(destination, "oscdimg.exe");
        progress?.Report("ISO 创建工具已就绪。");
        return destination;
    }

    private async Task<HttpResponseMessage> DownloadAsync(CancellationToken token)
    {
        var uri = new Uri(DownloadUrl);
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                uri.IdnHost is not ("msdl.microsoft.com" or "vsblobprodscussu5shard61.blob.core.windows.net"))
                throw new InvalidDataException("ISO 工具下载离开了其批准的 Microsoft 符号服务器主机。");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null) throw new IOException("Microsoft 返回了空的 ISO 工具重定向。");
            uri = next.IsAbsoluteUri ? next : new(uri, next);
        }
        throw new IOException("Microsoft ISO 工具的重定向次数过多。");
    }
}
