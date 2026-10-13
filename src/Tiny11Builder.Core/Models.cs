using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiny11Builder.Core;

public enum Compression { Recovery, Maximum, Fast }
public enum CleanupPreset { Balanced, Minimal, Original, Custom }
public enum BuildFlavor { Regular, Core }
public enum BuildMode { Tiny11, Tiny11Core, Custom }
public enum OptionKind { App, Feature, Capability, Policy, File }
public enum PolicyHive { Software, DefaultUser, System }
public enum ImageItemKind { ProvisionedApp, SystemApp, Feature, Capability, Package, Service, ComponentPayload }
public enum ExpertActionMode { Disable, Remove }
public sealed record ExpertAction(ImageItemKind Kind, string Identity, ExpertActionMode Mode);
public sealed record ImageInventoryItem(ImageItemKind Kind, string Identity, string Detail, bool Protected)
{
    public string Key => $"{Kind}:{Identity}";
}

public sealed record CleanupOption(
    string Id, string Title, string Description, string Category, OptionKind Kind,
    bool Balanced, bool Minimal, string[] Targets);

public sealed record WindowsImage(
    int Index, string Name, string Description, string Architecture,
    Version Version, string Edition, string Installation, long Size)
{
    public string Summary => $"{Name}  |  {Architecture}  |  内部版本 {Version.Build}.{Version.Revision}";
    public bool IsSupported => Index > 0 && Size > 0 && Version.Major == 10 && Version.Build >= 22621 &&
        Installation.Equals("Client", StringComparison.OrdinalIgnoreCase) && Architecture is "x64" or "arm64";

    public void Validate()
    {
        if (!IsSupported)
            throw new InvalidDataException(
                "请选择一个 Windows 11 客户端映像，内部版本 22621（22H2）或更高，架构为 x64 或 ARM64。");
    }
}

public sealed record ProvisionedApp(string DisplayName, string PackageName);
public enum BuildOperationState { Pending, Running, Completed, Skipped, Failed, Cancelled }
public sealed record BuildProgress(string Stage, string Message, double? Percent = null,
    string? OperationId = null, BuildOperationState? State = null, string? Detail = null);
public sealed record BuildResult(string IsoPath, string Sha256, string ReportPath, string WorkDirectory,
    TimeSpan Duration = default, long RemovedLogicalBytes = 0, long OmittedSetupBytes = 0)
{
    // ISO 发布之后出现的问题（报告、清理）或提示性数据中的问题。它们绝不会把已发布的 ISO 变成失败。
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
public sealed record PolicyValue(PolicyHive Hive, string Key, string Name, object Value);

public sealed record BuildRecipe
{
    public int SchemaVersion { get; init; } = 1;
    public string SourcePath { get; init; } = "";
    public int ImageIndex { get; init; } = 1;
    public string WorkspacePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string DismPath { get; init; } = "";
    public string OscdimgPath { get; init; } = "";
    public Compression Compression { get; init; } = Compression.Recovery;
    public BuildFlavor Flavor { get; init; }
    public BuildMode? Mode { get; init; }
    public bool CoreAcknowledged { get; init; }
    public bool ExpertAcknowledged { get; init; }
    public ExpertAction[] ExpertActions { get; init; } = [];
    public string[] SelectedOptions { get; init; } = [];
    public bool EnableNetFx3 { get; init; }
    public bool CompactInstallation { get; init; } = true;
    public bool PreferLocalAccount { get; init; }
    public bool KeepWorkingFiles { get; init; }
    public bool TurboMode { get; init; }
    public bool RemoveRedundantData { get; init; }
    public bool OmitNetFx3SetupPayload { get; init; }

    public void ValidateOptions()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Compression) || !Enum.IsDefined(Flavor) ||
            (Mode is { } mode && !Enum.IsDefined(mode)))
            throw new InvalidDataException("不支持此配方版本或压缩模式。");
        if (Flavor == BuildFlavor.Core && !CoreAcknowledged)
            throw new InvalidDataException("Core 需要确认已了解将失去更新、恢复和防病毒保护。");
        if (ExpertActions is null || ExpertActions.Any(action => action is null) ||
            (ExpertActions.Length > 0 && !ExpertAcknowledged))
            throw new InvalidDataException("自定义删除需要确认已了解映像可能无法启动的风险。");
        if (ExpertActions.Select(a => (a.Kind, a.Identity)).Distinct().Count() != ExpertActions.Length)
            throw new InvalidDataException("存在重复的自定义映像操作。");
        foreach (var action in ExpertActions)
            if (!Enum.IsDefined(action.Kind) || !Enum.IsDefined(action.Mode) || string.IsNullOrWhiteSpace(action.Identity) ||
                action.Identity.Contains('\\') || action.Identity.Contains('/') ||
                (action.Mode == ExpertActionMode.Disable && action.Kind is not (ImageItemKind.Feature or ImageItemKind.Service)))
                throw new InvalidDataException("无效的自定义映像操作。");
        if (ImageIndex < 1 || SelectedOptions is null ||
            SelectedOptions.Length != SelectedOptions.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("映像索引无效，或存在重复的清理选项。");
        foreach (var id in SelectedOptions)
            if (!CleanupCatalog.Options.Any(o => o.Id == id))
                throw new InvalidDataException($"未知的清理选项：{id}");
        if (Mode is BuildMode.Tiny11 or BuildMode.Tiny11Core &&
            (Flavor != (Mode == BuildMode.Tiny11Core ? BuildFlavor.Core : BuildFlavor.Regular) ||
             ExpertActions.Length > 0 ||
             !SelectedOptions.ToHashSet(StringComparer.Ordinal).SetEquals(CleanupCatalog.ForMode(Mode.Value))))
            throw new InvalidDataException("若要更改 Tiny11 或 Tiny11 Core 的删除集，请使用自定义模式。");
    }

    public static BuildRecipe Read(string path)
    {
        var recipe = JsonSerializer.Deserialize<BuildRecipe>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException("配方为空。");
        recipe.ValidateOptions();
        return recipe;
    }

    public void Write(string path)
    {
        ValidateOptions();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
    }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
