using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private readonly IProcessRunner runner;
    private readonly IMediaSourceFactory mediaFactory;
    private readonly IOfflineRegistry registry;
    private readonly IImageOperations imageOperations;
    private readonly IBuildHost host;
    private readonly BuildEngineOptions options;

    public BuildEngine(IProcessRunner runner, IMediaSourceFactory mediaFactory, IOfflineRegistry registry,
        IImageOperations imageOperations, IBuildHost host, BuildEngineOptions? options = null)
    {
        this.runner = runner;
        this.mediaFactory = mediaFactory;
        this.registry = registry;
        this.imageOperations = imageOperations;
        this.host = host;
        this.options = options ?? new();
    }
    public async Task<IReadOnlyList<WindowsImage>> InspectAsync(string source, string dismPath, CancellationToken token)
    {
        PathSafety.RejectReparsePoints(source);
        using var media = mediaFactory.Open(PathSafety.Full(source));
        return await new DismClient(dismPath, runner).GetImagesAsync(InstallationMedia.ImageFile(media.RootPath), token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageInventoryItem>> InventoryAsync(BuildRecipe recipe,
        IProgress<BuildProgress>? progress, CancellationToken token)
    {
        recipe.ValidateOptions();
        PathSafety.ValidateWorkspace(recipe.SourcePath, recipe.WorkspacePath);
        host.ValidateTools(recipe with { OscdimgPath = "" });
        using var media = mediaFactory.Open(recipe.SourcePath);
        var dism = new DismClient(recipe.DismPath, runner);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var image = await dism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateImage(recipe with { OutputPath = "" }, image);
        // A read-only inventory never uses lazy mounting: it has no 87/4350 fallback, and a failed probe costs minutes.
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, allowOptimizedMount: false);
        using var job = BuildJob.Create(recipe.WorkspacePath, "inventory", execution.BufferedLog);
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(scratch);
        var inventoryDism = new DismClient(recipe.DismPath, runner, text =>
        {
            job.Log(text);
            progress?.Report(new("清单", text));
        }, new(scratch, Path.Combine(job.Directory, "dism.log"), OptimizedMount: false));
        var mount = Path.Combine(job.Directory, "mount");
        Directory.CreateDirectory(mount);
        var attempted = false;
        try
        {
            progress?.Report(new("清单", "正在以只读方式挂载映像，以列出其中的组件。"));
            var index = recipe.ImageIndex;
            var wim = sourceWim;
            if (Path.GetExtension(sourceWim).Equals(".esd", StringComparison.OrdinalIgnoreCase))
            {
                wim = Path.Combine(job.Directory, "working.wim");
                await inventoryDism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{index}",
                    $"/DestinationImageFile:{wim}", "/Compress:fast", "/CheckIntegrity").ConfigureAwait(false);
                index = 1;
            }
            attempted = true;
            await inventoryDism.RunAsync(token, "/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}",
                $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity").ConfigureAwait(false);
            var result = await ImageInventory.ReadAsync(inventoryDism, imageOperations, mount, token).ConfigureAwait(false);
            await inventoryDism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
            attempted = false;
            token.ThrowIfCancellationRequested();
            job.State("completed");
            PathSafety.DeleteOwnedDirectory(mount, job.Directory);
            PathSafety.DeleteOwnedDirectory(scratch, job.Directory);
            DeleteWorkingWim(job.Directory);
            return result;
        }
        catch (Exception error)
        {
            // Recovery first: nothing fallible (log, flush, job marker) may run before an owned mount is discarded.
            var cancelled = BuildFailureException.IsCancellationCause(error);
            var secondary = new List<Exception>();
            Exception? discardFailure = null;
            void Guard(Action step)
            {
                try { step(); }
                catch (Exception diagnostic) { secondary.Add(diagnostic); }
            }
            if (attempted)
            {
                if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "正在丢弃只读工作挂载",
                    OperationId: "cancel:mount", State: BuildOperationState.Running)));
                discardFailure = await TryDiscardAsync(inventoryDism, mount).ConfigureAwait(false);
                if (discardFailure is not null) secondary.Add(discardFailure);
            }
            if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "正在保存日志与作业停止状态",
                OperationId: "cancel:logs", State: BuildOperationState.Running)));
            Guard(() => job.Log(error.Message));
            if (discardFailure is not null) Guard(() => job.Log(DiscardFailureText(discardFailure)));
            Guard(job.Flush);
            Guard(() => job.State(discardFailure is not null ? "failed" : cancelled ? "cancelled" : "failed",
                discardFailure is not null ? "Recovery required" : null));
            var kind = cancelled ? "已取消" : "失败";
            throw new BuildFailureException(
                $"清点{kind}。源介质未被修改。日志与工作文件保留在 {job.Directory}。{error.Message}" +
                DiscardText(discardFailure, resumable: false) + SecondaryText(secondary, discardFailure),
                job.Directory, secondary.Count == 0 ? error : new AggregateException([error, .. secondary]),
                cancelled, discardFailure is not null);
        }
    }

    public async Task<BuildResult> BuildAsync(BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        PathSafety.Validate(recipe);
        if (string.IsNullOrEmpty(recipe.OscdimgPath)) throw new FileNotFoundException("创建可启动 ISO 需要 Windows ADK 的 oscdimg.exe。");
        host.ValidateTools(recipe);
        using var media = mediaFactory.Open(recipe.SourcePath);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var workingCompression = InstallationMedia.WorkingCompression(sourceWim);
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, options.AllowOptimizedMount);
        var preparationNotes = new List<string>();
        if (execution.OptimizedMount && MountOptimizationCache.IsUnavailable(recipe.WorkspacePath, recipe.DismPath, preparationNotes.Add))
            execution = execution with { OptimizedMount = false };
        workingCompression = execution.WorkingCompression(workingCompression);
        var metadataDism = new DismClient(recipe.DismPath, runner);
        var image = await metadataDism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateImage(recipe, image);
        var efiRelative = new[] { @"efi\microsoft\boot\efisys.bin", @"efi\microsoft\boot\efisys_noprompt.bin" }
            .FirstOrDefault(p => File.Exists(Path.Combine(media.RootPath, p))) ??
            throw new InvalidDataException("源介质中不包含受支持的 UEFI 启动映像。");
        var sourceInstall = new FileInfo(sourceWim);
        using var job = BuildJob.Create(recipe.WorkspacePath, "build", execution.BufferedLog);
        var staging = Path.Combine(job.Directory, "media");
        var mount = Path.Combine(job.Directory, "mount");
        var workingWim = Path.Combine(job.Directory, "working.wim");
        var temporaryIso = Path.Combine(job.Directory, "output.iso");
        var reportPath = Path.Combine(job.Directory, "report.json");
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(scratch);
        IReadOnlyDictionary<string, string>? prefetchedFeatures = null;
        var tracking = new BuildOperationTracker(progress, job.Log);
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(job.Directory, "dism.log"), execution.OptimizedMount));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = execution, Image = image,
            EfiRelative = efiRelative, Staging = staging, Mount = mount, WorkingWim = workingWim, TemporaryIso = temporaryIso,
            ReportPath = reportPath, Scratch = scratch, Elapsed = elapsed, UsedOptimizedMount = execution.OptimizedMount,
            SetupPending = SetupInstaller.RequiresBootEdit(recipe)
        };
        void Log(string text) => session.Log(text);
        void Stage(string next, string description) => session.Stage(next, description, token);
        try
        {
            foreach (var note in preparationNotes)
            {
                job.Log(note);
                progress?.Report(new("准备", note));
            }
            recipe.Write(Path.Combine(job.Directory, JobCheckpoint.RecipeFileName));
            var servicingActions = recipe.ExpertActions.Where(action => action.Kind is ImageItemKind.Package or
                ImageItemKind.ProvisionedApp or ImageItemKind.Capability or ImageItemKind.Feature).ToArray();
            foreach (var (name, title) in new[]
            {
                ("复制介质", "复制安装介质"), ("导出所选版本", "导出所选版本"),
                ("挂载", "挂载工作映像"), ("清单", "校验单项映像操作"),
                ("自定义维护", "应用单项服务堆栈维护操作"), ("移除应用", "移除所选应用"),
                ("可选功能", "移除可选的各项功能"),
                (".NET 3.5", "包含 .NET Framework 3.5"), ("Windows 设置", "应用 Windows 设置"),
                ("组件清理", "清理被取代的组件文件"), ("高级负载", "移除所选的组件负载文件"),
                ("Core 精简", "裁剪 Core 组件负载"), ("冗余数据", "移除冗余的离线数据"),
                ("提交", "保存映像更改"),
                ("安装映像", "应用 Windows 安装程序设置"), ("压缩", "压缩最终映像"),
                ("创建 ISO", "创建可启动 ISO"), ("校验输出", "校验并发布 ISO")
            }.Where(entry => entry.Item1 switch
            {
                "清单" => recipe.ExpertActions.Length > 0,
                "自定义维护" => servicingActions.Length > 0,
                ".NET 3.5" => recipe.EnableNetFx3,
                "组件清理" => recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core,
                "Core 精简" => recipe.Flavor == BuildFlavor.Core,
                "冗余数据" => recipe.RemoveRedundantData,
                "安装映像" => SetupInstaller.RequiresBootEdit(recipe),
                _ => true
            })) tracking.Plan("stage:" + name, name, title);
            Log($"目标：{image.Summary}。源介质从不会被修改。");
            Log($"执行方式：{(recipe.TurboMode ? "极速模式" : "标准模式")}；优化挂载={execution.OptimizedMount} " +
                $"({(!recipe.TurboMode ? "极速模式已关闭" : !options.AllowOptimizedMount ? "延迟挂载需显式启用，当前未启用" : "由主机启用")})； " +
                $"复制线程={execution.CopyWorkers}；中间压缩={workingCompression}。");
            Log("策略以离线注册表值的形式校验，并不保证 Windows 的实际行为。不同内部版本与版本的支持情况各异。");
            if (image.Version.Build > 26100) Log("更新的内部版本和 Insider 内部版本需要匹配的工具；此处并不表示已通过启动认证。");
            if (recipe.Flavor == BuildFlavor.Core || recipe.ExpertActions.Length > 0)
                Log("警告：Core/专家模式的输出可能无法启动、更新、恢复，也无法提供防病毒保护。请在隔离的虚拟机中测试。");
            Stage("复制介质", "复制安装介质，但不包含原始的多版本安装映像。");
            session.StagingResult = await MediaStager.CopyAsync(media.RootPath, staging, execution.CopyWorkers,
                recipe.OmitNetFx3SetupPayload, Log, token).ConfigureAwait(false);
            Stage("导出所选版本", "仅将所选版本导出为可写的 WIM。");
            Log(execution.FastWorkingImage ?
                "极速模式：使用快速中间压缩以加速提交。最终压缩仍按所选设置执行。" :
                $"中间压缩：{workingCompression}。保留现有 WIM 压缩，以避免重新压缩源映像。");
            await dism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{recipe.ImageIndex}",
                $"/DestinationImageFile:{workingWim}", workingCompression, "/CheckIntegrity").ConfigureAwait(false);
            Stage("挂载", "正在挂载工作副本。取消操作会等待正在进行的 DISM 操作结束。");
            session.Mounted = true;
            try
            {
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            catch (DismCommandException error) when (execution.OptimizedMount && error.ExitCode == 87 &&
                Regex.IsMatch(error.Output, @"optimize\s+option\s+is\s+unknown", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tracking.Skip("mount:lazy-check", "挂载", "校验优化挂载",
                    "此服务堆栈维护工具不支持优化挂载。将改为带完整性检查的常规重试。");
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
                token.ThrowIfCancellationRequested();
                dism.DisableOptimizedMount();
                session.UsedOptimizedMount = false;
                MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                session.Mounted = true;
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            if (session.UsedOptimizedMount)
            {
                try
                {
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
                catch (DismCommandException error) when (error.ExitCode == 4350)
                {
                    tracking.Skip("mount:lazy-check", "挂载", "校验优化挂载",
                        "此主机拒绝了延迟映像访问（DISM 4350）。在任何映像更改之前，先重试常规挂载。");
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
                    session.Mounted = false;
                    token.ThrowIfCancellationRequested();
                    dism.DisableOptimizedMount();
                    session.UsedOptimizedMount = false;
                    MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                    await tracking.RunAsync("mount:normal-retry", "挂载", "延迟挂载不可用；改为带完整性检查的常规挂载",
                        async () =>
                        {
                            session.Mounted = true;
                            await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1",
                                $"/MountDir:{mount}", "/CheckIntegrity").ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
            }
            IReadOnlyList<ImageInventoryItem> inventory = [];
            if (recipe.ExpertActions.Length > 0)
            {
                Stage("清单", "针对实际挂载的映像重新校验专家操作。");
                inventory = await ImageInventory.ReadAsync(dism, imageOperations, mount, token).ConfigureAwait(false);
                ImageInventory.ValidateActions(recipe.ExpertActions, inventory);
            }
            if (servicingActions.Length > 0)
            {
                Stage("自定义维护", "应用所选的程序包、应用与各项功能操作。");
                foreach (var action in servicingActions)
                    tracking.Plan($"expert:{action.Kind}:{action.Identity}", "自定义维护", $"{action.Mode} {action.Kind}：{action.Identity}");
                foreach (var action in servicingActions)
                    await tracking.RunAsync($"expert:{action.Kind}:{action.Identity}", "自定义维护",
                        $"{action.Mode} {action.Kind}：{action.Identity}",
                        () => ApplyDismActionAsync(dism, mount, action, token)).ConfigureAwait(false);
            }
            Stage("移除应用", "按精确标识移除所选的预配应用。");
            var appOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.App && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyList<ProvisionedApp> apps = appOptions.Length > 0 ?
                DismClient.ParseApps(await dism.RunAsync(token, $"/Image:{mount}", "/Get-ProvisionedAppxPackages").ConfigureAwait(false)) : [];
            var appsToRemove = apps.Where(app => appOptions.Any(option => CleanupCatalog.MatchesApp(option, app)))
                .DistinctBy(app => app.PackageName).ToArray();
            foreach (var app in appsToRemove) tracking.Plan("app:" + app.PackageName, "移除应用", $"移除 {app.DisplayName}（{app.PackageName}）");
            foreach (var option in appOptions)
            {
                var matching = apps.Where(a => CleanupCatalog.MatchesApp(option, a)).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-app:" + option.Id, "移除应用", option.Title, "此映像中不存在");
            }
            foreach (var app in appsToRemove)
                await tracking.RunAsync("app:" + app.PackageName, "移除应用", $"移除 {app.DisplayName}（{app.PackageName}）",
                    () => dism.RunAsync(token, $"/Image:{mount}", "/Remove-ProvisionedAppxPackage", $"/PackageName:{app.PackageName}")).ConfigureAwait(false);
            Stage("可选功能", "检测实际存在的各项功能；不可用的选项会如实报告，而不是凭空猜测。");
            var featureOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Feature && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            var capabilityOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Capability && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyDictionary<string, string> features = featureOptions.Length > 0 ?
                prefetchedFeatures is not null && servicingActions.Length == 0 ? prefetchedFeatures :
                    DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false)) :
                new Dictionary<string, string>();
            IReadOnlyList<ImageInventoryItem> capabilities = capabilityOptions.Length > 0 ?
                ImageInventory.ParseIdentityStates(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Capabilities").ConfigureAwait(false),
                    "Capability Identity", ImageItemKind.Capability) : [];
            var featureNames = featureOptions.SelectMany(o => o.Targets).Where(features.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var removalCapabilities = capabilities.Where(c => c.Detail.Equals("Installed", StringComparison.OrdinalIgnoreCase) &&
                capabilityOptions.Any(o => o.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                             c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(c => c.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in featureNames) tracking.Plan("feature:" + name, "可选功能", $"移除可选功能：{name}");
            foreach (var capability in removalCapabilities)
                tracking.Plan("capability:" + capability.Identity, "可选功能", $"移除功能：{capability.Identity}");
            foreach (var option in featureOptions)
            {
                var matching = option.Targets.Where(features.ContainsKey).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-feature:" + option.Id, "可选功能", option.Title, "此映像中不存在");
            }
            var activeFeatures = featureNames.Where(feature => !features[feature].Equals("Disabled with Payload Removed", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var feature in featureNames.Except(activeFeatures, StringComparer.OrdinalIgnoreCase))
                tracking.Skip("feature:" + feature, "可选功能", feature, "组件负载已移除");
            if (recipe.TurboMode && activeFeatures.Length > 1)
            {
                Log($"极速模式功能批处理：{string.Join(", ", activeFeatures)}");
                FeatureBatchResult? featureBatch = null;
                await tracking.RunBatchAsync(activeFeatures.Select(feature => ("feature:" + feature, $"移除可选功能：{feature}")).ToArray(),
                    "可选功能", async () =>
                    {
                        featureBatch = await dism.DisableFeaturesWithStatesAsync(mount, activeFeatures, token).ConfigureAwait(false);
                    },
                    detailFor: id => featureBatch?.Items.FirstOrDefault(item => string.Equals("feature:" + item.Name, id, StringComparison.Ordinal))
                        is { } verified ? Sentence(verified.Describe()) : null).ConfigureAwait(false);
                // Plain "Disabled" is reported as such, never as "payload removed": DISM keeps the payload of some features.
                foreach (var verified in featureBatch!.Items) Log($"功能 {verified.Name}：{verified.Describe()}。");
                session.Features.AddRange(featureBatch.Items);
            }
            else
                foreach (var feature in activeFeatures)
                    await tracking.RunAsync("feature:" + feature, "可选功能", $"移除可选功能：{feature}",
                        () => dism.RunAsync(token, $"/Image:{mount}", "/Disable-Feature", $"/FeatureName:{feature}", "/Remove", "/NoRestart")).ConfigureAwait(false);
            foreach (var option in capabilityOptions)
            {
                if (!removalCapabilities.Any(c => option.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                                         c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                    tracking.Skip("missing-capability:" + option.Id, "可选功能", option.Title, "没有匹配的已安装功能");
            }
            if (recipe.TurboMode && removalCapabilities.Length > 0)
            {
                var capabilityNames = removalCapabilities.Select(c => c.Identity).ToArray();
                Log($"极速模式功能批处理（在一条命令中重复使用 /CapabilityName）：{string.Join(", ", capabilityNames)}");
                BatchRemovalResult? capabilityBatch = null;
                await tracking.RunBatchAsync(removalCapabilities.Select(c => ("capability:" + c.Identity, $"移除功能：{c.Identity}")).ToArray(),
                    "可选功能", async () =>
                    {
                        capabilityBatch = await dism.RemoveCapabilitiesAsync(mount, capabilityNames, token).ConfigureAwait(false);
                    }, "功能",
                    id => capabilityBatch?.Items.FirstOrDefault(item => string.Equals("capability:" + item.Name, id, StringComparison.OrdinalIgnoreCase))
                        is { } verified ? Sentence("已确认 " + verified.Describe()) : null).ConfigureAwait(false);
                foreach (var verified in capabilityBatch!.Items) Log($"已确认移除功能：{verified.Name} => {verified.Describe()}");
                session.Capabilities.AddRange(capabilityBatch.Items);
            }
            else
                foreach (var capability in removalCapabilities)
                    await tracking.RunAsync("capability:" + capability.Identity, "可选功能", $"移除功能：{capability.Identity}",
                        () => dism.RunAsync(token, $"/Image:{mount}", "/Remove-Capability", $"/CapabilityName:{capability.Identity}", "/NoRestart")).ConfigureAwait(false);
            if (recipe.EnableNetFx3)
            {
                Stage(".NET 3.5", "从匹配的源介质启用 .NET 3.5，而不使用 Windows Update。");
                var sxs = Path.Combine(media.RootPath, @"sources\sxs");
                if (!Directory.Exists(sxs)) throw new DirectoryNotFoundException("源介质中没有用于 .NET 3.5 的 sources\\sxs 组件负载。");
                await dism.RunAsync(token, $"/Image:{mount}", "/Enable-Feature", "/FeatureName:NetFx3", "/All",
                    "/LimitAccess", $"/Source:{sxs}", "/NoRestart").ConfigureAwait(false);
            }
            Stage("Windows 设置", "将所选注册表设置应用到离线映像。");
            await tracking.RunAsync("registry:settings", "Windows 设置", "应用并校验离线 Windows 设置",
                () =>
                {
                    registry.Apply(mount, recipe, Log);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(staging, "autounattend.xml"), UnattendBuilder.Create(image, recipe));
            if (recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core)
            {
                Stage("组件清理", recipe.Flavor == BuildFlavor.Core ?
                    "在裁剪 Core 组件负载之前清理并重置组件基础。" : "清理被取代的组件，但不执行 ResetBase。");
                await dism.RunAsync(token, recipe.Flavor == BuildFlavor.Core ?
                    [$"/Image:{mount}", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"] :
                    [$"/Image:{mount}", "/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
            }
            Stage("高级负载", "仅对离线副本应用原版构建器与专家模式的组件负载移除。");
            imageOperations.RemoveOriginalFiles(mount, image.Architecture, recipe.SelectedOptions, Log, recipe.TurboMode);
            if (recipe.Flavor == BuildFlavor.Core)
            {
                Stage("Core 精简", "裁剪 Core 组件负载与恢复文件。");
                imageOperations.TrimCore(mount, image.Architecture, Log, token, recipe.TurboMode);
            }
            imageOperations.ApplyNativeActions(mount, recipe.ExpertActions, Log, recipe.TurboMode);
            if (recipe.RemoveRedundantData)
            {
                Stage("冗余数据", "移除离线缓存、归档日志与崩溃报告。");
                session.Cleanup = imageOperations.CleanRedundantData(mount, Log, token);
                foreach (var item in session.Cleanup.Where(item => item.State == BuildOperationState.Skipped))
                    tracking.Skip("cleanup:" + item.Path, "冗余数据", $"清理 {item.Path}", item.Detail);
            }
            Stage("提交", "提交已修改的工作映像。");
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity").ConfigureAwait(false);
            session.Mounted = false;
            // The checkpoint is durable before any later stage can observe a cancellation request.
            await SaveCommittedCheckpointAsync(session, new SourceIdentity
            {
                Location = recipe.SourcePath, ImageIndex = recipe.ImageIndex, InstallFile = Path.GetFileName(sourceWim),
                InstallLength = sourceInstall.Length, InstallLastWriteUtc = new(sourceInstall.LastWriteTimeUtc, TimeSpan.Zero)
            }).ConfigureAwait(false);
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }
    public Task RecoverAsync(string jobFile, string dismPath, CancellationToken token) =>
        RecoverAsync(jobFile, dismPath, token, discardCommitted: false);

    /// <summary>
    /// Discards an interrupted job's mounted working copy and staged media. A job holding verified saved progress is
    /// refused unless <paramref name="discardCommitted"/> explicitly accepts losing it; Resume is the way to keep it.
    /// Jobs from before checkpoints existed are never adopted or deleted automatically.
    /// </summary>
    public async Task RecoverAsync(string jobFile, string dismPath, CancellationToken token, bool discardCommitted)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("请在 tiny11 所拥有的作业目录中选择 job.json。");
        var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(jobFile), JsonDefaults.Options)
            ?? throw new InvalidDataException("作业标记无效。");
        if (state.Id != leaf[7..] || state.Status == "completed") throw new InvalidDataException("这不是一个被中断的作业。");
        PathSafety.RejectReparsePoints(Path.Combine(directory, "job.lock"));
        // An active build holds this lock, including across separate app instances.
        using var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Refuse before anything is touched: a checkpoint means a verified committed image that Resume can finish.
        var checkpointFile = Path.Combine(directory, JobCheckpoint.FileName);
        PathSafety.RejectReparsePoints(checkpointFile);
        var checkpointed = File.Exists(checkpointFile);
        if (checkpointed && !discardCommitted)
            throw new CheckpointedJobException("此作业保存了已校验的进度（已提交的 Windows 映像及其已暂存的安装文件）。" +
                "请使用“继续”在不重复这些工作的情况下完成它。恢复操作会永久删除已保存的映像，因此需要明确确认。");
        if (OperatingSystem.IsWindows()) OfflineHive.RecoverJob(directory);
        var mount = Path.Combine(directory, "mount");
        var dism = new DismClient(dismPath, runner);
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The checkpoint leaves its resumable name BEFORE the data it describes is deleted, so no resume can see a half-deleted job.
        if (checkpointed) CheckpointStore.Retire(directory, "checkpoint.discarded.json");
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.DeleteOwnedDirectory(Path.Combine(directory, name), directory);
        DeleteWorkingWim(directory);
        File.WriteAllText(jobFile, JsonSerializer.Serialize(state with { Status = "recovered", Stage = "已丢弃工作副本" }, JsonDefaults.Options));
    }

    private static async Task ApplyDismActionAsync(DismClient dism, string mount, ExpertAction action, CancellationToken token)
    {
        var args = action.Kind switch
        {
            ImageItemKind.ProvisionedApp => new[] { "/Remove-ProvisionedAppxPackage", $"/PackageName:{action.Identity}" },
            ImageItemKind.Package => ["/Remove-Package", $"/PackageName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Capability => ["/Remove-Capability", $"/CapabilityName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Feature => action.Mode == ExpertActionMode.Remove ?
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/Remove", "/NoRestart"] :
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/NoRestart"],
            _ => throw new InvalidDataException("不是 DISM 操作。")
        };
        await dism.RunAsync(token, [$"/Image:{mount}", .. args]).ConfigureAwait(false);
    }

    /// <summary>Discards an owned mount without throwing: the caller records a failure as a secondary problem and demands recovery.</summary>
    private static async Task<Exception?> TryDiscardAsync(DismClient dism, string mount)
    {
        try
        {
            await DiscardMountAsync(dism, mount).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanup)
        {
            return cleanup;
        }
    }

    private static async Task DiscardMountAsync(DismClient dism, string mount)
    {
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
    }

    private static string DiscardFailureText(Exception discardFailure) =>
        $"丢弃失败：{discardFailure.Message}。请使用“恢复工作区”处理此 job.json；切勿使用全局 DISM 清理。";

    private static string DiscardText(Exception? discardFailure, bool resumable) => discardFailure is null ? "" : resumable ?
        $" 其已挂载的安装映像无法丢弃（{discardFailure.Message}）。继续操作会先再次尝试丢弃它，然后再继续；" +
        "切勿使用全局 DISM 清理。" :
        $" 需要恢复：其已挂载的映像无法丢弃（{discardFailure.Message}）。" +
        "请为此作业选择“恢复工作区”；切勿使用全局 DISM 清理。";

    private static string SecondaryText(IReadOnlyList<Exception> secondary, Exception? discardFailure)
    {
        var diagnostics = secondary.Where(problem => !ReferenceEquals(problem, discardFailure)).Select(problem => problem.Message).ToArray();
        return diagnostics.Length == 0 ? "" : " 记录失败时还出现了其他问题：" + string.Join("；", diagnostics);
    }

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + "。";

    private static async Task<bool> IsMountedAsync(DismClient dism, string mount)
    {
        var text = await dism.RunAsync(CancellationToken.None, "/Get-MountedWimInfo").ConfigureAwait(false);
        return Regex.Matches(text, @"(?m)^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$")
            .Any(m => Path.GetFullPath(m.Groups[1].Value).TrimEnd('\\')
                .Equals(Path.GetFullPath(mount).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteWorkingWim(string jobDirectory)
    {
        var file = Path.Combine(jobDirectory, "working.wim");
        if (File.Exists(file)) File.Delete(file);
    }
}
