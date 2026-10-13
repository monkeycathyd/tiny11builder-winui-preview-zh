namespace Tiny11Builder.Core;

public static class PathSafety
{
    public static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("请使用绝对本地路径。");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("不支持网络路径和备用数据流。");
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsInside(string candidate, string parent) =>
        Full(candidate).StartsWith(Full(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RejectReparsePoints(string path)
    {
        for (var current = Full(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"构建路径不支持重解析点：{current}");
    }

    public static void Validate(BuildRecipe recipe)
    {
        recipe.ValidateOptions();
        ValidateWorkspace(recipe.SourcePath, recipe.WorkspacePath);
        ValidateOutput(recipe);
    }

    /// <summary>
    /// 恢复已保存任务时的校验。映像提交之后就不再需要原始介质，因此与 <see cref="Validate"/> 不同，
    /// 这里不要求源存在；但仍会保证工作区和输出的安全。
    /// </summary>
    public static void ValidateForResume(BuildRecipe recipe)
    {
        recipe.ValidateOptions();
        ValidateWorkspaceLocation(Full(recipe.SourcePath), Full(recipe.WorkspacePath));
        ValidateOutput(recipe);
    }

    private static void ValidateOutput(BuildRecipe recipe)
    {
        var source = Full(recipe.SourcePath);
        var output = Full(recipe.OutputPath);
        RejectReparsePoints(output);
        foreach (var protectedPath in ProtectedDirectories())
            if (output.Equals(protectedPath, StringComparison.OrdinalIgnoreCase) || IsInside(output, protectedPath))
                throw new InvalidDataException("输出不能位于系统目录内。");
        if (!Path.GetExtension(output).Equals(".iso", StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new InvalidDataException("输出必须是新的 .iso 文件。绝不覆盖现有文件。");
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(source) && IsInside(output, source)))
            throw new InvalidDataException("请让源介质与输出保持分离。");
    }

    public static void ValidateWorkspace(string sourcePath, string workspacePath)
    {
        var source = Full(sourcePath);
        var work = Full(workspacePath);
        foreach (var path in new[] { source, work }) RejectReparsePoints(path);
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("Windows 介质源不存在。", source);
        if (File.Exists(source) && !Path.GetExtension(source).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 Windows ISO 或已解压的安装介质文件夹。");
        ValidateWorkspaceLocation(source, work);
    }

    private static void ValidateWorkspaceLocation(string source, string work)
    {
        RejectReparsePoints(work);
        if (work.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(work)!), StringComparison.OrdinalIgnoreCase) ||
            work.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择一个专用的工作区文件夹，而不是驱动器根目录或个人资料文件夹根目录。");
        foreach (var protectedPath in ProtectedDirectories())
            if (work.Equals(protectedPath, StringComparison.OrdinalIgnoreCase) || IsInside(work, protectedPath))
                throw new InvalidDataException("工作区不能位于系统目录内。");
        if (File.Exists(work)) throw new InvalidDataException("工作区必须是目录，而不是现有文件。");
        if (work.Equals(source, StringComparison.OrdinalIgnoreCase) || (Directory.Exists(source) && IsInside(work, source)))
            throw new InvalidDataException("请让源介质与工作区保持分离。");
    }

    private static IEnumerable<string> ProtectedDirectories() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    }.Where(p => !string.IsNullOrEmpty(p));

    public static void DeleteOwnedDirectory(string path, string jobDirectory)
    {
        var job = Full(jobDirectory);
        var leaf = Path.GetFileName(job);
        if (!leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _) ||
            !File.Exists(Path.Combine(job, "job.json")) || !IsInside(path, job))
            throw new InvalidDataException("拒绝删除不属于此构建的目录。");
        RejectReparsePoints(path);
        if (!Directory.Exists(path)) return;
        ValidateTree(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("清理工作区时拒绝跟随重解析点。");
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }
}
