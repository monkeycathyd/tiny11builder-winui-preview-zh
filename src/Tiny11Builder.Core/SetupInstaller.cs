using System.Text;

namespace Tiny11Builder.Core;

public static class SetupInstaller
{
    public static bool RequiresLegacy(BuildRecipe recipe) => recipe.Flavor == BuildFlavor.Core ||
        recipe.ExpertActions.Any(action => action.Kind == ImageItemKind.ComponentPayload && action.Mode == ExpertActionMode.Remove);

    internal static bool RequiresBootEdit(BuildRecipe recipe) =>
        recipe.SelectedOptions.Contains("hardware-bypass") || RequiresLegacy(recipe);

    internal static void ApplyLegacy(string mount, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 安装程序自定义需要 Windows。");
        ImageOperations.RequireOwnedMount(mount);
        // 在 24H2 及更高版本的介质中，对应的经典安装程序仍是 sources\setup.exe；
        // SetupHost.exe 是更新的启动工作流。不要从其他发行版借用二进制文件。
        var executable = new[] { @"sources\setup_old.exe", @"sources\setup.exe" }
            .FirstOrDefault(relative => File.Exists(Path.Combine(mount, relative))) ??
            throw new FileNotFoundException("此 Windows 安装程序映像没有旧版安装程序。Core/组件存储删除需要包含 sources\\setup.exe 的介质。");
        var program = Path.Combine(mount, executable);
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(program)!);
        ImageFileAccess.ValidateFile(program);
        if (new FileInfo(program).Length == 0)
            throw new InvalidDataException("Windows 安装程序映像中的旧版安装程序为空。");
        var path = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageFileAccess.DeleteEntry(path);
        }
        var content = "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMDRIVE%\\" + executable + "\r\n";
        File.WriteAllText(path, content, Encoding.ASCII);
        if (File.ReadAllText(path) != content) throw new IOException("无法验证旧版 Windows 安装程序的启动配置。");
        log($"已启用旧版安装程序：%SYSTEMDRIVE%\\{executable}。Core/组件存储删除需要它。");
    }
}
