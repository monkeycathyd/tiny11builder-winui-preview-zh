using System.Runtime.Versioning;
using System.ComponentModel;

namespace Tiny11Builder.Core;

public enum PendingState { Clear, Pending, Unknown }
public sealed record PendingAssessment(PendingState State, string Reason);
public sealed record ImageCleanupResult(string Path, BuildOperationState State, int Files, long LogicalBytes, string Detail,
    PendingState? Pending = null);

[SupportedOSPlatform("windows")]
public sealed class OfflineDataCleaner(Func<string, PendingAssessment>? assessPending = null)
{
    public OfflineDataCleaner(Func<string, bool> hasPendingRegistryOperations)
        : this(mount => new PendingAssessment(hasPendingRegistryOperations(mount) ? PendingState.Pending : PendingState.Clear,
            "注册表评估")) { }

    private sealed record Target(string Path, bool ServicingSensitive = false, bool ChildrenOnly = true,
        string? Prefix = null, string[]? Extensions = null);

    private static readonly Target[] Targets =
    [
        new(@"Windows\SoftwareDistribution\Download", ServicingSensitive: true),
        new(@"Windows\Temp", ServicingSensitive: true),
        new(@"Users\Default\AppData\Local\Temp", ServicingSensitive: true),
        new(@"Windows\Logs\CBS", Prefix: "CbsPersist_", Extensions: [".cab", ".log"]),
        new(@"Windows\Logs\DISM", Extensions: [".bak"]),
        new(@"Windows\MEMORY.DMP", ChildrenOnly: false),
        new(@"Windows\Minidump", Extensions: [".dmp"]),
        new(@"ProgramData\Microsoft\Windows\WER\ReportArchive"),
        new(@"ProgramData\Microsoft\Windows\WER\ReportQueue")
    ];

    public IReadOnlyList<ImageCleanupResult> Clean(string mount, Action<string> log, CancellationToken token)
    {
        ImageOperations.RequireOwnedMount(mount);
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        token.ThrowIfCancellationRequested();
        var marker = new[] { @"Windows\WinSxS\pending.xml", @"Windows\WinSxS\reboot.xml" }
            .FirstOrDefault(path => ImageFileAccess.Exists(Path.Combine(mount, path)));
        var pending = marker is not null ? new(PendingState.Pending, marker) :
            (assessPending ?? AssessRegistryPending)(mount);
        if (pending.State == PendingState.Unknown)
            log($"警告：无法校验待处理的服务堆栈维护状态：{pending.Reason}。临时/更新组件负载已保留。");
        var results = new List<ImageCleanupResult>();
        foreach (var target in Targets)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(mount, target.Path);
            if (pending.State != PendingState.Clear && target.ServicingSensitive)
            {
                var reason = pending.State == PendingState.Pending ? "已保留，因为此映像存在待处理的服务堆栈维护操作。" :
                    $"已保留，因为无法校验待处理的服务堆栈维护状态：{pending.Reason}";
                log($"已跳过清理：{target.Path}：{reason}");
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, reason, pending.State));
                continue;
            }
            if (!ImageFileAccess.Exists(path))
            {
                log($"已跳过清理：{target.Path}：不存在。");
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, "不存在。"));
                continue;
            }
            PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
            ImageFileAccess.ValidateFile(path);
            var entries = target.ChildrenOnly && Directory.Exists(path) ?
                Directory.EnumerateFileSystemEntries(path).Where(entry =>
                    (target.Prefix is null || Path.GetFileName(entry).StartsWith(target.Prefix, StringComparison.OrdinalIgnoreCase)) &&
                    (target.Extensions is null || File.Exists(entry) &&
                        target.Extensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))).ToArray() :
                target.ChildrenOnly ? throw new InvalidDataException($"应为离线清理目录：{target.Path}") : [path];
            long bytes = 0;
            var files = 0;
            foreach (var entry in entries) Measure(entry, ref files, ref bytes, token);
            if (files == 0 && entries.Length == 0)
            {
                results.Add(new(target.Path, BuildOperationState.Skipped, 0, 0, "没有冗余文件。"));
                log($"已跳过清理：{target.Path}：没有冗余文件。");
                continue;
            }
            log($"计划删除组件负载：{target.Path}");
            log($"正在删除组件负载：{target.Path}");
            foreach (var entry in entries) ImageOperations.DeleteTree(entry, turboMode: true, token);
            results.Add(new(target.Path, BuildOperationState.Completed, files, bytes, "已删除缓存或诊断文件；根目录保留。"));
            log($"组件负载已删除：{target.Path}");
            log($"清理已从 {target.Path} 删除 {files} 个文件和 {bytes} 个逻辑字节。");
        }
        return results;
    }

    internal static PendingAssessment AssessRegistryPending(string mount)
    {
        var source = Path.Combine(mount, @"Windows\System32\config\SOFTWARE");
        if (!ImageFileAccess.Exists(source)) return new(PendingState.Unknown, "缺少 SOFTWARE 配置单元。");
        var directory = Path.Combine(Path.GetDirectoryName(mount)!, "pending-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "SOFTWARE");
        try
        {
            // Loading a hive can replay transaction logs even when its registry handle is read-only.
            foreach (var suffix in new[] { "", ".LOG1", ".LOG2" })
                if (ImageFileAccess.Exists(source + suffix))
                {
                    ImageFileAccess.ValidateFile(source + suffix);
                    File.Copy(source + suffix, copy + suffix);
                    File.SetAttributes(copy + suffix, File.GetAttributes(copy + suffix) & ~FileAttributes.ReadOnly);
                }
            using var hive = OfflineRegistry.Load(copy, writable: false);
            const string cbsPath = @"Microsoft\Windows\CurrentVersion\Component Based Servicing";
            using var cbs = hive.Root.OpenSubKey(cbsPath);
            if (cbs is null) return new(PendingState.Unknown, "SOFTWARE 配置单元中没有服务堆栈维护清单。");
            foreach (var subkey in new[]
            {
                cbsPath + @"\RebootPending", cbsPath + @"\PackagesPending", cbsPath + @"\RebootInProgress",
                @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"
            })
            {
                using var key = hive.Root.OpenSubKey(subkey);
                if (key is not null) return new(PendingState.Pending, subkey);
            }
            return new(PendingState.Clear, "没有待处理的服务堆栈维护标记。");
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 1009 or 1017)
        {
            return new(PendingState.Unknown, $"SOFTWARE 配置单元无法读取（注册表错误 {error.NativeErrorCode}）。");
        }
        finally
        {
            PathSafety.DeleteOwnedDirectory(directory, Path.GetDirectoryName(mount)!);
        }
    }

    private static void Measure(string path, ref int files, ref long bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ImageFileAccess.ValidateFile(path);
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Measure(entry, ref files, ref bytes, token);
        }
        else
        {
            files++;
            bytes = checked(bytes + new FileInfo(path).Length);
        }
    }
}
