using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

public interface IBuildHost
{
    void ValidateTools(BuildRecipe recipe);
    void ValidateImage(BuildRecipe recipe, WindowsImage image);

    /// <summary>
    /// 在恢复已保存的构建之前检查宿主。默认实现让较旧的宿主继续可用；<see cref="BuildHost"/>
    /// 会重写它，使恢复过程不必受全新构建的可用空间门槛限制。
    /// </summary>
    void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes) => ValidateImage(recipe, image);
}

[SupportedOSPlatform("windows")]
public sealed class BuildHost : IBuildHost
{
    public void ValidateTools(BuildRecipe recipe)
    {
        if (!ToolLocator.IsAdministrator) throw new UnauthorizedAccessException("映像服务需要管理员权限。");
        ToolLocator.Validate(recipe.DismPath, "dism.exe");
        if (!string.IsNullOrEmpty(recipe.OscdimgPath)) ToolLocator.Validate(recipe.OscdimgPath, "oscdimg.exe");
    }

    public void ValidateImage(BuildRecipe recipe, WindowsImage image)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        var required = Math.Max(40L * 1024 * 1024 * 1024, image.Size * 3 + 10L * 1024 * 1024 * 1024);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"处理此映像时，工作区至少需要 {required / (1024 * 1024 * 1024)} GiB 可用空间。");
        ValidateOutputDrive(recipe, drive);
    }

    public void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        if (drive.AvailableFreeSpace < requiredWorkspaceBytes)
            throw new IOException($"完成此构建时，工作区至少需要 {(requiredWorkspaceBytes + (1L << 30) - 1) >> 30} GiB 可用空间。");
        ValidateOutputDrive(recipe, drive);
    }

    private static void RequireNtfs(DriveInfo drive)
    {
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("工作区必须位于 NTFS 上。不支持在 ReFS、FAT32 和网络驱动器上挂载 WIM。");
    }

    private static void ValidateOutputDrive(BuildRecipe recipe, DriveInfo workspace)
    {
        if (string.IsNullOrEmpty(recipe.OutputPath)) return;
        var output = new DriveInfo(Path.GetPathRoot(recipe.OutputPath)!);
        if (output.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            throw new IOException("输出驱动器不能是 FAT32；Windows ISO 通常会超过它 4 GiB 的限制。");
        if (!output.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase) &&
            output.AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new IOException("输出驱动器至少需要 12 GiB 可用空间。");
    }
}
