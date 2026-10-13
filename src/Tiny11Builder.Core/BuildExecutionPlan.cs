namespace Tiny11Builder.Core;

/// <summary>刻意<b>不</b>属于持久化的 <see cref="BuildRecipe"/> 的宿主级开关。</summary>
/// <param name="AllowOptimizedMount">
/// 开发用接缝。除非宿主明确选择启用，否则绝不使用延迟（<c>/Optimize</c>）挂载。在不支持该功能的宿主上，
/// 一次盲目探测让真实构建多花了约 12 分钟（挂载 8 分 31 秒、首次访问时 DISM 报 4350、丢弃 3 分 53 秒）。
/// </param>
public sealed record BuildEngineOptions(bool AllowOptimizedMount = false);

public sealed record BuildExecutionPlan(bool OptimizedMount, bool FastWorkingImage, int CopyWorkers, bool BufferedLog)
{
    public static BuildExecutionPlan Create(BuildRecipe recipe, int logicalProcessors, bool allowOptimizedMount = false)
    {
        if (logicalProcessors < 1) throw new ArgumentOutOfRangeException(nameof(logicalProcessors));
        return new(recipe.TurboMode && allowOptimizedMount,
            recipe.TurboMode && (recipe.Flavor == BuildFlavor.Core ||
                recipe.Compression == Compression.Fast ||
                recipe.ExpertActions.Any(a => a.Kind == ImageItemKind.ComponentPayload)),
            recipe.TurboMode ? Math.Clamp(logicalProcessors / 2, 4, 8) : 4,
            recipe.TurboMode);
    }

    public string WorkingCompression(string sourceCompression) =>
        FastWorkingImage ? "/Compress:fast" : sourceCompression;
}

public sealed record DismExecutionSettings(string ScratchDirectory, string LogPath, bool OptimizedMount);
