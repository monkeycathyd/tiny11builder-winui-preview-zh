using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Tiny11Builder.Core;

public interface IImageOperations
{
    IReadOnlyList<ImageInventoryItem> ReadNativeInventory(string mount);
    void ApplyNativeActions(string mount, IReadOnlyList<ExpertAction> actions, Action<string> log, bool turboMode = false);
    void RemoveOriginalFiles(string mount, string architecture, IReadOnlyList<string> options, Action<string> log, bool turboMode = false);
    void TrimCore(string mount, string architecture, Action<string> log, CancellationToken token, bool turboMode = false);
    IReadOnlyList<ImageCleanupResult> CleanRedundantData(string mount, Action<string> log, CancellationToken token);
}

[SupportedOSPlatform("windows")]
public sealed class ImageOperations : IImageOperations
{
    private static readonly HashSet<string> CoreMetadataDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "Catalogs", "FileMaps", "Fusion", "InstallTemp", "Manifests", "SettingsManifests", "Temp" };
    private static readonly Regex CoreRuntimePattern = new(
        @"(?:common-controls|c\.\.-controls|gdiplus|isolationautomation|utomation\.proxystub|microsoft\.vc\d+\.crt|servicing|s\.\.(?:ngstack|stack))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<ImageCleanupResult> CleanRedundantData(string mount, Action<string> log, CancellationToken token) =>
        new OfflineDataCleaner().Clean(mount, log, token);

    public IReadOnlyList<ImageInventoryItem> ReadNativeInventory(string mount)
    {
        RequireOwnedMount(mount);
        var items = new List<ImageInventoryItem>();
        var hiveDirectory = Path.Combine(Path.GetDirectoryName(mount)!, "inventory-hive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hiveDirectory);
        var hiveCopy = Path.Combine(hiveDirectory, "SYSTEM");
        var sourceHive = Path.Combine(mount, @"Windows\System32\config\SYSTEM");
        ImageFileAccess.ValidateFile(sourceHive);
        try
        {
            foreach (var suffix in new[] { "", ".LOG1", ".LOG2" })
                if (ImageFileAccess.Exists(sourceHive + suffix))
                {
                    ImageFileAccess.ValidateFile(sourceHive + suffix);
                    File.Copy(sourceHive + suffix, hiveCopy + suffix);
                    File.SetAttributes(hiveCopy + suffix, File.GetAttributes(hiveCopy + suffix) & ~FileAttributes.ReadOnly);
                }
            using var systemHive = OfflineRegistry.Load(hiveCopy, writable: false);
            var system = systemHive.Root;
            using var select = system.OpenSubKey("Select");
            var current = select?.GetValue("Current") is int number ? number : 1;
            using var services = system.OpenSubKey($@"ControlSet{current:D3}\Services")
                ?? throw new InvalidDataException("该映像没有服务清单。");
            foreach (var name in services.GetSubKeyNames())
            {
                using var service = services.OpenSubKey(name)!;
                var start = service.GetValue("Start") is int startValue ? startValue : -1;
                var type = service.GetValue("Type") is int serviceType ? serviceType : 0;
                var detail = $"启动={start}；类型={type}；{service.GetValue("DisplayName") ?? name}";
                items.Add(new(ImageItemKind.Service, name, detail, true));
            }
        }
        finally
        {
            PathSafety.DeleteOwnedDirectory(hiveDirectory, Path.GetDirectoryName(mount)!);
        }
        foreach (var (relative, kind) in new[]
        {
            (@"Windows\SystemApps", ImageItemKind.SystemApp),
            (@"Windows\WinSxS", ImageItemKind.ComponentPayload)
        })
        {
            var directory = Path.Combine(mount, relative);
            if (!Directory.Exists(directory)) continue;
            foreach (var child in Directory.EnumerateDirectories(directory))
                items.Add(new(kind, Path.GetFileName(child), "受保护的组件负载。删除可能破坏启动、服务堆栈维护、安全性或外壳。", true));
        }
        return items;
    }

    public void ApplyNativeActions(string mount, IReadOnlyList<ExpertAction> actions, Action<string> log, bool turboMode = false)
    {
        RequireOwnedMount(mount);
        foreach (var action in actions.Where(a => a.Kind is ImageItemKind.SystemApp or ImageItemKind.ComponentPayload))
            Remove(mount, Path.Combine(action.Kind == ImageItemKind.SystemApp ? @"Windows\SystemApps" : @"Windows\WinSxS", action.Identity), log, turboMode: turboMode);
        var serviceActions = actions.Where(a => a.Kind == ImageItemKind.Service).ToArray();
        if (serviceActions.Length == 0) return;
        using var privileges = new PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        using var systemHive = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SYSTEM"));
        var system = systemHive.Root;
        // Apply to every actual control set, not to the host's CurrentControlSet.
        foreach (var control in system.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^ControlSet\d{3}$")))
        {
            using var services = OfflineRegistry.OpenWriteKey(system, control + @"\Services");
            foreach (var action in serviceActions)
            {
                if (!services.GetSubKeyNames().Contains(action.Identity, StringComparer.OrdinalIgnoreCase))
                { log($"在 {control} 中不存在：服务 {action.Identity}"); continue; }
                if (action.Mode == ExpertActionMode.Disable)
                {
                    using var service = OfflineRegistry.OpenWriteKey(services, action.Identity);
                    service.SetValue("Start", 4, RegistryValueKind.DWord);
                }
                else
                {
                    OfflineRegistry.DeleteKeyTree(services, action.Identity);
                }
                log($"专家操作 {action.Mode}：{control}\\Services\\{action.Identity}。共享的可执行文件不会被删除。");
            }
        }
    }

    public void RemoveOriginalFiles(string mount, string architecture, IReadOnlyList<string> options, Action<string> log, bool turboMode = false)
    {
        RequireOwnedMount(mount);
        if (options.Contains("onedrive-files"))
            foreach (var path in new[] { @"Windows\System32\OneDriveSetup.exe", @"Windows\SysWOW64\OneDriveSetup.exe" })
                Remove(mount, path, log, turboMode: turboMode);
        if (!options.Contains("edge-files")) return;
        foreach (var basePath in new[] { "Program Files", "Program Files (x86)" })
            foreach (var name in new[] { "Edge", "EdgeCore", "EdgeUpdate", "EdgeWebView" })
                Remove(mount, Path.Combine(basePath, "Microsoft", name), log, turboMode: turboMode);
        Remove(mount, @"Windows\System32\Microsoft-Edge-Webview", log, turboMode: turboMode);
        var store = Path.Combine(mount, @"Windows\WinSxS");
        foreach (var path in Directory.EnumerateDirectories(store).Where(p =>
                     Path.GetFileName(p).Contains("microsoft-edge-webview", StringComparison.OrdinalIgnoreCase)).ToArray())
            Remove(mount, Path.GetRelativePath(mount, path), log, turboMode: turboMode);
    }

    public static bool PreserveCoreDirectory(string name)
    {
        return CoreMetadataDirectories.Contains(name) || CoreRuntimePattern.IsMatch(name);
    }

    public void TrimCore(string mount, string architecture, Action<string> log, CancellationToken token, bool turboMode = false)
    {
        RequireOwnedMount(mount);
        var store = Path.Combine(mount, @"Windows\WinSxS");
        var directories = Directory.EnumerateDirectories(store).ToArray();
        var nativePrefix = architecture == "x64" ? "amd64_" : "arm64_";
        foreach (var family in new[] { "common-controls", "gdiplus", "servicing" })
            if (!directories.Any(d => Path.GetFileName(d).StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase) &&
                                      Path.GetFileName(d).Contains(family, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Core 无法在此构建中识别 {architecture} {family} 运行时。未删除任何组件负载。");
        var removable = directories.Where(d => !PreserveCoreDirectory(Path.GetFileName(d))).ToArray();
        foreach (var path in removable) log($"计划删除组件负载：{Path.GetRelativePath(mount, path)}");
        if (File.Exists(Path.Combine(mount, @"Windows\System32\Recovery\winre.wim")))
            log(@"计划删除组件负载：Windows\System32\Recovery\winre.wim");
        using var privileges = new PrivilegeScope();
        foreach (var path in removable)
        {
            token.ThrowIfCancellationRequested();
            Remove(mount, Path.GetRelativePath(mount, path), log, privilegesEnabled: true, turboMode: turboMode, token: token);
        }
        Remove(mount, @"Windows\System32\Recovery\winre.wim", log, privilegesEnabled: true, turboMode: turboMode, token: token);
        log("Core：组件负载已精简。此映像无法进行服务堆栈维护，也没有 Windows 恢复映像。");
    }

    internal static void RequireOwnedMount(string mount)
    {
        PathSafety.RejectReparsePoints(mount);
        var job = Path.GetDirectoryName(PathSafety.Full(mount))!;
        var leaf = Path.GetFileName(job);
        if (!leaf.StartsWith("tiny11-", StringComparison.Ordinal) || !Guid.TryParseExact(leaf[7..], "N", out _) ||
            !File.Exists(Path.Combine(job, "job.json")) ||
            !Path.GetFileName(mount).Equals("mount", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(mount, @"Windows\System32\config\SYSTEM")))
            throw new InvalidDataException("拒绝修改此构建的离线映像挂载点之外的文件。");
        PathSafety.RejectReparsePoints(Path.Combine(job, "job.json"));
    }

    private static void Remove(string mount, string relative, Action<string> log, bool privilegesEnabled = false,
        bool turboMode = false, CancellationToken token = default)
    {
        var path = Path.GetFullPath(Path.Combine(mount, relative));
        if (!PathSafety.IsInside(path, mount)) throw new InvalidDataException("映像组件负载路径超出了离线挂载点。");
        using var privileges = privilegesEnabled ? null : new PrivilegeScope();
        if (!ImageFileAccess.Exists(path)) { log($"不存在：{relative}"); return; }
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        ImageFileAccess.ValidateFile(path);
        log($"正在删除组件负载：{relative}");
        DeleteTree(path, turboMode, token);
        log($"组件负载已删除：{relative}");
    }

    internal static void DeleteTree(string path, bool turboMode = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ImageFileAccess.ValidateFile(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteTree(child, turboMode, token);
        ImageFileAccess.DeleteEntry(path);
    }

    internal sealed class PrivilegeScope : IDisposable
    {
        private static readonly object privilegeGate = new();
        private readonly SafeAccessTokenHandle token;
        private readonly List<TokenPrivileges> previous = [];
        public PrivilegeScope(params string[] names)
        {
            Monitor.Enter(privilegeGate);
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out token))
            {
                Monitor.Exit(privilegeGate);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                foreach (var name in names.Length == 0 ? new[] { "SeBackupPrivilege", "SeRestorePrivilege" } : names)
                {
                    if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                    Marshal.SetLastPInvokeError(0);
                    if (!AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivileges>(), out var old, out _) ||
                        Marshal.GetLastWin32Error() != 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法为离线删除启用 {name}。");
                    previous.Add(old);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try
            {
                foreach (var item in previous)
                {
                    var state = item;
                    if (!AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivileges>(), out _, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法还原进程特权。");
                }
            }
            finally { token.Dispose(); Monitor.Exit(privilegeGate); }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool all, ref TokenPrivileges state,
        int size, out TokenPrivileges previous, out int returned);
}
