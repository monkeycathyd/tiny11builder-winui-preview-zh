using System.Diagnostics;
using System.Text.Json;

namespace Tiny11Builder.Core;

internal static class MountOptimizationCache
{
    private sealed record Entry(string Tool, string Version, int ExitCode);
    private static string CacheFile(string workspace) => Path.Combine(PathSafety.Full(workspace), "mount-optimization.json");

    public static bool IsUnavailable(string workspace, string executable, Action<string> log)
    {
        if (!File.Exists(executable)) return false;
        var file = CacheFile(workspace);
        if (!File.Exists(file)) return false;
        Entry entry;
        try
        {
            PathSafety.RejectReparsePoints(file);
            entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), JsonDefaults.Options) ??
                throw new InvalidDataException("挂载优化缓存为空。");
            if (string.IsNullOrWhiteSpace(entry.Tool) || string.IsNullOrWhiteSpace(entry.Version) ||
                entry.ExitCode is not (87 or 4350))
                throw new InvalidDataException("挂载优化缓存缺少字段，或包含不受支持的字段。");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // 仅为提示性数据：它绝不能阻塞任务或让任务遗留下孤立的挂载。失败时按不可用处理（不做延迟挂载探测），
            // 记录日志，并且不改动该文件。
            log($"挂载优化提示性缓存无法使用（{error.GetType().Name}：{error.Message}），位置：{file}。" +
                "本次运行已停用优化挂载；该文件未被更改。");
            return true;
        }
        var unavailable = entry.Tool.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) &&
            entry.Version == VersionOf(executable);
        if (unavailable) log($"此工具版本已知不兼容，已停用优化挂载（观察到的退出代码 {entry.ExitCode}）。");
        return unavailable;
    }

    public static void RecordUnavailable(string workspace, string executable, int exitCode)
    {
        if (!File.Exists(executable)) return;
        if (exitCode is not (87 or 4350)) throw new ArgumentOutOfRangeException(nameof(exitCode));
        var file = CacheFile(workspace);
        PathSafety.RejectReparsePoints(file);
        var entry = new Entry(Path.GetFullPath(executable), VersionOf(executable), exitCode);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, JsonDefaults.Options));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>恢复路径上的尽力而为记录器：提示性缓存不可写时，绝不能让本来正常的构建失败。</summary>
    public static bool TryRecordUnavailable(string workspace, string executable, int exitCode, Action<string> log)
    {
        if (exitCode is not (87 or 4350)) throw new ArgumentOutOfRangeException(nameof(exitCode));
        try
        {
            RecordUnavailable(workspace, executable, exitCode);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"挂载优化提示性缓存无法更新（{error.GetType().Name}：{error.Message}）。" +
                "构建将以普通挂载继续；后续手动的运行时可能会再次探测。");
            return false;
        }
    }

    private static string VersionOf(string executable)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var files = new[] { executable, Path.Combine(folder, "wimgapi.dll"), Path.Combine(folder, "DismApi.dll"),
            Path.Combine(folder, @"Dism\dismcore.dll") };
        return string.Join("|", files.Where(File.Exists).Select(file =>
            Path.GetFileName(file) + ":" + FileVersionInfo.GetVersionInfo(file).FileVersion));
    }
}
