using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private sealed record VerifiedResume(ResumeCheckpoint Checkpoint, bool SetupPending, bool SetupAlreadyApplied,
        bool StaleSetupMount, bool StaleHiveJournals);

    /// <summary>
    /// Finishes an interrupted build from its verified checkpoint without repeating the finished work: after the
    /// installation image was committed (Setup image, Compress, ISO) or after the final image was compressed (ISO only).
    /// Every check runs before anything is changed; a refusal is an <see cref="InvalidDataException"/> with a
    /// user-facing message. Only the output path, servicing tools, final compression (before it ran) and
    /// <see cref="BuildRecipe.KeepWorkingFiles"/> may differ from the job's original recipe.
    /// </summary>
    public async Task<BuildResult> ResumeAsync(string jobFile, BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var elapsed = Stopwatch.StartNew();
        var directory = ResolveJobDirectory(jobFile);
        using var job = BuildJob.Open(directory, bufferedLog: recipe.TurboMode);
        var tracking = new BuildOperationTracker(progress, job.Log);
        VerifiedResume verified;
        try
        {
            job.Log($"=== 已请求继续构建 {DateTimeOffset.UtcNow:O}：输出 {recipe.OutputPath}；压缩 {recipe.Compression}；" +
                $"保留工作文件 {recipe.KeepWorkingFiles}；dism {recipe.DismPath}；oscdimg {recipe.OscdimgPath} ===");
            verified = await VerifyResumeAsync(job, tracking, recipe, token).ConfigureAwait(false);
        }
        catch (Exception refusal)
        {
            // Nothing has been changed. The refusal is rethrown unchanged; a failing diagnostic is attached to it, never lost.
            try
            {
                job.Log("已拒绝继续构建：" + refusal.Message);
                job.Flush();
                tracking.Fail(refusal, BuildFailureException.IsCancellationCause(refusal));
            }
            catch (Exception diagnostic) { refusal.Data["Tiny11Builder.DiagnosticFailure"] = diagnostic.Message; }
            throw;
        }

        // ---- every check passed; changes start here ----
        var checkpoint = verified.Checkpoint;
        var mount = Path.Combine(directory, "mount");
        var scratch = Path.Combine(directory, "scratch");
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(directory, "dism.log"), OptimizedMount: false));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = checkpoint.Report.Execution, Image = checkpoint.Image,
            EfiRelative = checkpoint.EfiBootImage, Staging = Path.Combine(directory, "media"), Mount = mount,
            WorkingWim = Path.Combine(directory, "working.wim"), TemporaryIso = Path.Combine(directory, "output.iso"),
            ReportPath = Path.Combine(directory, "report.json"), Scratch = scratch, Elapsed = elapsed,
            PriorActive = TimeSpan.FromMilliseconds(checkpoint.ActiveMilliseconds), ResumedFrom = checkpoint.Phase,
            UsedOptimizedMount = checkpoint.Report.UsedOptimizedMount, StagingResult = checkpoint.Report.SetupMedia,
            Cleanup = checkpoint.Report.RedundantData, Checkpoint = checkpoint, SetupPending = verified.SetupPending,
            CompressPending = checkpoint.Phase == ResumePhase.Committed,
            StageName = BuildProgressText.ResumeCheckStage
        };
        session.Features.AddRange(checkpoint.Report.Features);
        session.Capabilities.AddRange(checkpoint.Report.Capabilities);
        try
        {
            job.State("running", BuildProgressText.ResumeCheckStage);
            Directory.CreateDirectory(scratch);
            Directory.CreateDirectory(mount);
            if (verified.StaleHiveJournals && OperatingSystem.IsWindows())
            {
                session.Log("正在卸载上次中断运行遗留的离线注册表挂载。");
                OfflineHive.RecoverJob(directory);
            }
            if (verified.StaleSetupMount)
            {
                session.Log("正在丢弃上次中断运行遗留的已挂载 Windows 安装映像。已提交的安装映像不受影响。");
                session.Mounted = true;
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
            }
            if (verified.SetupAlreadyApplied)
            {
                session.Log("已编辑的 Windows 安装映像已就位；正在记录该状态。");
                var bootWim = Path.Combine(session.Staging, @"sources\boot.wim");
                UpdateCheckpoint(session, saved => saved with
                {
                    SetupState = SetupProgress.Applied,
                    StagedMedia = ReplaceEntry(saved.StagedMedia, new(@"sources\boot.wim", new FileInfo(bootWim).Length, saved.EditedBootSha256!))
                });
            }
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }

    private async Task<VerifiedResume> VerifyResumeAsync(BuildJob job, BuildOperationTracker tracking, BuildRecipe requested,
        CancellationToken token)
    {
        var directory = job.Directory;
        var state = job.Current;
        if (state.Id != Path.GetFileName(directory)["tiny11-".Length..] || state.Purpose != "build")
            throw new InvalidDataException("此作业不是被中断的构建，因此无法继续。");
        if (state.Status == "completed") throw new InvalidDataException("此构建已经完成，没有可继续的内容。");
        if (state.Status == "recovered")
            throw new InvalidDataException("此作业已被恢复，其保存的工作已被丢弃，因此无法继续。");
        var checkpoint = CheckpointStore.Read(directory);
        tracking.Restore(checkpoint.Operations);
        tracking.BeginStage(BuildProgressText.ResumeCheckStage, "继续之前，先校验已保存的映像、安装文件和设置。");
        var committed = checkpoint.Phase == ResumePhase.Committed;
        if (!committed && (checkpoint.SetupState is SetupProgress.Pending or SetupProgress.Replacing) &&
            !SetupInstaller.RequiresLegacy(requested))
            throw new InvalidDataException("已保存的进度不一致：映像已压缩，但 Windows 安装映像尚未处理完成。");

        // ---- settings: the recipe identity, frozen fields, paths and tools (cheap checks first) ----
        var recipeFile = Path.Combine(directory, JobCheckpoint.RecipeFileName);
        PathSafety.RejectReparsePoints(recipeFile);
        if (!File.Exists(recipeFile))
            throw new InvalidDataException("此作业的原始配方（recipe.json）缺失，因此无法信任已保存的工作。");
        var recipeBytes = await File.ReadAllBytesAsync(recipeFile, token).ConfigureAwait(false);
        if (!Digest.HashBytes(recipeBytes).Equals(checkpoint.RecipeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("此作业的原始配方在保存进度之后发生了更改，因此无法信任已保存的工作。");
        BuildRecipe original;
        try
        {
            original = JsonSerializer.Deserialize<BuildRecipe>(recipeBytes, JsonDefaults.Options) ??
                throw new InvalidDataException("此作业的原始配方为空。");
        }
        catch (JsonException error) { throw new InvalidDataException("此作业的原始配方已损坏：" + error.Message, error); }
        original.ValidateOptions();
        var differences = FrozenDifferences(original, requested);
        if (differences.Length > 0)
            throw new InvalidDataException($"继续构建所用的配方更改了此作业中已冻结的设置（{string.Join(", ", differences)}）。" +
                "只有输出位置、服务堆栈维护工具、最终压缩方式和是否保留工作文件可以更改。");
        if (!committed && requested.Compression != checkpoint.CompressedWith)
            throw new InvalidDataException($"最终映像已使用 {checkpoint.CompressedWith} 压缩；" +
                $"无法使用 {requested.Compression} 继续构建。");
        if (!Path.GetDirectoryName(directory)!.Equals(PathSafety.Full(original.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("此作业不在其配方所指定的工作区内。");
        if (SetupInstaller.RequiresBootEdit(original) != (checkpoint.SetupState != SetupProgress.NotRequired) &&
            !(SetupInstaller.RequiresLegacy(original) && !original.SelectedOptions.Contains("hardware-bypass") &&
                checkpoint.SetupState == SetupProgress.NotRequired && !checkpoint.Report.LegacyInstallerEnabled))
            throw new InvalidDataException("已保存的安装映像状态与此作业的配方不匹配，因此无法信任已保存的工作。");
        if (string.IsNullOrEmpty(requested.OscdimgPath))
            throw new InvalidDataException("创建可启动 ISO 需要 Windows ADK 的 oscdimg.exe。");
        PathSafety.ValidateForResume(requested);
        host.ValidateTools(requested);
        host.ValidateResume(requested, checkpoint.Image, (committed ? checkpoint.WorkingImage.Length * 2 : checkpoint.CompressedImage!.Length) + (3L << 30));
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.RejectReparsePoints(Path.Combine(directory, name));

        // ---- nothing of this job may still be mounted or active ----
        var jobMount = Path.Combine(directory, "mount");
        var dism = new DismClient(requested.DismPath, runner);
        var staleSetupMount = false;
        foreach (var (mountDirectory, imageFile) in ParseMounted(await dism.RunAsync(token, "/Get-MountedWimInfo").ConfigureAwait(false)))
        {
            if (!IsUnder(mountDirectory, directory) && !(imageFile is not null && IsUnder(imageFile, directory))) continue;
            // Only the disposable private copy of the Setup image may be a leftover mount; it is discarded, never committed.
            var transient = SamePath(mountDirectory, jobMount) && imageFile is not null &&
                SamePath(imageFile, Path.Combine(directory, "setup", "boot.wim"));
            if (!transient)
                throw new InvalidDataException($"此作业的某个映像仍处于挂载状态（{mountDirectory}）。" +
                    "只有未挂载且已提交的映像才能继续构建；请先卸载它。");
            staleSetupMount = true;
        }
        if (!staleSetupMount && Directory.Exists(jobMount) && Directory.EnumerateFileSystemEntries(jobMount).Any())
            throw new InvalidDataException("此作业的挂载文件夹不为空，可能仍有映像处于挂载状态。已拒绝继续构建。");
        var staleHiveJournals = Directory.EnumerateFiles(directory, "Tiny11Builder_Offline_*.hive.json").Any();

        // ---- the saved image (positive proof: size, header, SHA-256 and identity, not a timestamp) ----
        if (committed)
        {
            var working = Path.Combine(directory, "working.wim");
            PathSafety.RejectReparsePoints(working);
            if (!File.Exists(working)) throw new InvalidDataException("已保存的工作映像（working.wim）缺失。");
            if (new FileInfo(working).Length != checkpoint.WorkingImage.Length)
                throw new InvalidDataException("已保存的工作映像的大小与提交时不一致。");
            Digest.RequireCompleteWim(working, "已保存的工作映像");
            await tracking.RunAsync("resume:image", BuildProgressText.ResumeCheckStage, "校验已保存的 Windows 映像", async () =>
            {
                var (_, sha) = await Digest.HashAsync(working, percent => tracking.Output($"正在校验已保存的 Windows 映像：{percent:0}%"), token)
                    .ConfigureAwait(false);
                if (!sha.Equals(checkpoint.WorkingImage.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已保存的工作映像在提交之后发生了更改（其 SHA-256 不一致），因此无法信任它。");
            }).ConfigureAwait(false);
            RequireSameImage(await dism.GetImagesAsync(working, token).ConfigureAwait(false), checkpoint.Image, "已保存的工作映像");
        }

        // ---- the staged media: every file listed by length and SHA-256, nothing else present ----
        var alternates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (checkpoint.SetupState == SetupProgress.Replacing) alternates[@"sources\boot.wim"] = checkpoint.EditedBootSha256!;
        var tolerated = committed ?
            new HashSet<string>([@"sources\install.wim", @"sources\install.esd"], StringComparer.OrdinalIgnoreCase) :
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> matchedAlternate = [];
        var lastReported = 0d;
        await tracking.RunAsync("resume:media", BuildProgressText.ResumeCheckStage, "校验已保存的安装文件", async () =>
        {
            matchedAlternate = await MediaManifest.VerifyAsync(Path.Combine(directory, "media"), checkpoint.StagedMedia, alternates, tolerated,
                checkpoint.Report.Execution.CopyWorkers, percent =>
                {
                    if (percent < lastReported + 5 && percent < 100) return;
                    lastReported = percent;
                    tracking.Output($"正在校验已保存的安装文件：{percent:0}%");
                }, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!committed)
        {
            var compressed = Path.Combine(directory, "media", checkpoint.CompressedImage!.RelativePath);
            Digest.RequireCompleteWim(compressed, "已保存的压缩安装映像");
            RequireSameImage(await dism.GetImagesAsync(compressed, token).ConfigureAwait(false), checkpoint.Image,
                "已保存的压缩安装映像");
        }
        var setupAlreadyApplied = checkpoint.SetupState == SetupProgress.Replacing &&
            matchedAlternate.Contains(@"sources\boot.wim", StringComparer.OrdinalIgnoreCase);
        var setupPending = checkpoint.SetupState == SetupProgress.Pending ||
            (checkpoint.SetupState == SetupProgress.Replacing && !setupAlreadyApplied) ||
            (SetupInstaller.RequiresLegacy(original) && !checkpoint.Report.LegacyInstallerEnabled);
        return new(checkpoint, setupPending, setupAlreadyApplied, staleSetupMount, staleHiveJournals);
    }

    private static string ResolveJobDirectory(string jobFile)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("请在 tiny11 所拥有的作业目录中选择 job.json。");
        return directory;
    }

    private static void RequireSameImage(IReadOnlyList<WindowsImage> images, WindowsImage expected, string what)
    {
        if (images.Count != 1) throw new InvalidDataException($"{what}必须恰好包含一个映像，但实际包含 {images.Count} 个。");
        var actual = images[0];
        if (actual.Architecture != expected.Architecture || actual.Version.Build != expected.Version.Build ||
            !actual.Edition.Equals(expected.Edition, StringComparison.OrdinalIgnoreCase) ||
            !actual.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) ||
            !actual.Installation.Equals(expected.Installation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what}是另一个 Windows 映像（{actual.Summary}），与此作业所构建的映像（{expected.Summary}）不同。");
    }

    // Frozen by default: a field added to BuildRecipe later cannot change on resume unless it is listed as free here.
    private static BuildRecipe Freeze(BuildRecipe recipe) => recipe with
    {
        OutputPath = "", DismPath = "", OscdimgPath = "", Compression = default, KeepWorkingFiles = false,
        SourcePath = NormalizePath(recipe.SourcePath), WorkspacePath = NormalizePath(recipe.WorkspacePath),
        SelectedOptions = [.. recipe.SelectedOptions.Order(StringComparer.Ordinal)],
        ExpertActions = [.. recipe.ExpertActions.OrderBy(action => action.Kind).ThenBy(action => action.Identity, StringComparer.Ordinal)
            .ThenBy(action => action.Mode)]
    };

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string[] FrozenDifferences(BuildRecipe original, BuildRecipe requested)
    {
        var left = JsonSerializer.SerializeToNode(Freeze(original), JsonDefaults.Options)!.AsObject();
        var right = JsonSerializer.SerializeToNode(Freeze(requested), JsonDefaults.Options)!.AsObject();
        return left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key))
            .Where(name => left[name]?.ToJsonString() != right[name]?.ToJsonString()).ToArray();
    }

    private static IReadOnlyList<(string MountDirectory, string? ImageFile)> ParseMounted(string text)
    {
        var entries = new List<(string, string?)>();
        string? directory = null;
        string? file = null;
        foreach (var line in text.Split('\n'))
        {
            var mount = Regex.Match(line, @"^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (mount.Success)
            {
                if (directory is not null) entries.Add((directory, file));
                directory = mount.Groups[1].Value;
                file = null;
                continue;
            }
            var image = Regex.Match(line, @"^[ \t]*Image File[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (image.Success) file = image.Groups[1].Value;
        }
        if (directory is not null) entries.Add((directory, file));
        return entries;
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsUnder(string path, string directory)
    {
        try { return SamePath(path, directory) || PathSafety.IsInside(path, directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException) { return false; }
    }
}
