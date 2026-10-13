using System.Globalization;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed class DismClient(string executable, IProcessRunner runner, Action<string>? output = null,
    DismExecutionSettings? settings = null)
{
    private bool optimizedMount = settings?.OptimizedMount == true;
    public string Executable { get; } = executable;
    public void DisableOptimizedMount() => optimizedMount = false;

    public async Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        List<string> effective = ["/English"];
        if (settings is not null)
        {
            effective.Add($"/ScratchDir:{settings.ScratchDirectory}");
            effective.Add($"/LogPath:{settings.LogPath}");
            effective.Add("/LogLevel:2");
        }
        effective.AddRange(arguments);
        if (optimizedMount && arguments.Contains("/Mount-Image", StringComparer.OrdinalIgnoreCase))
            effective.Add("/Optimize");
        var result = await runner.RunAsync(Executable, effective, output, token).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 3010))
            throw new DismCommandException(result.ExitCode, result.Output);
        return result.Output;
    }

    public async Task<IReadOnlyList<WindowsImage>> GetImagesAsync(string imageFile, CancellationToken token)
    {
        var summary = await RunAsync(token, "/Get-WimInfo", $"/WimFile:{imageFile}").ConfigureAwait(false);
        var indexes = Regex.Matches(summary, @"(?m)^\s*Index\s*:\s*(\d+)\s*$")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        if (indexes.Length == 0) throw new InvalidDataException("DISM 未返回任何映像索引。");
        var images = new List<WindowsImage>();
        foreach (var index in indexes)
        {
            images.Add(await GetImageAsync(imageFile, index, token).ConfigureAwait(false));
        }
        return images;
    }

    public async Task<WindowsImage> GetImageAsync(string imageFile, int index, CancellationToken token)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        var detail = await RunAsync(token, "/Get-WimInfo", $"/WimFile:{imageFile}", $"/Index:{index}").ConfigureAwait(false);
        return ParseImage(index, detail);
    }

    public async Task<string> DisableFeaturesAsync(string mount, IReadOnlyList<string> featureNames, CancellationToken token) =>
        (await DisableFeaturesWithStatesAsync(mount, featureNames, token).ConfigureAwait(false)).Output;

    /// <summary>
    /// Removes several features with one DISM command, then reads the real state of each. Plain "Disabled" is accepted and
    /// reported as such: DISM keeps the payload of some features, so demanding "payload removed" would reject valid images.
    /// Anything still enabled, or missing from the inventory, fails the batch.
    /// </summary>
    public async Task<FeatureBatchResult> DisableFeaturesWithStatesAsync(string mount, IReadOnlyList<string> featureNames, CancellationToken token)
    {
        if (featureNames.Count == 0) throw new ArgumentException("至少需要一个功能。", nameof(featureNames));
        var result = await RunAsync(token, [$"/Image:{mount}", "/Disable-Feature",
            .. featureNames.Select(name => $"/FeatureName:{name}"), "/Remove", "/NoRestart"]).ConfigureAwait(false);
        var features = ParseFeatures(await RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
        var items = featureNames.Select(name =>
        {
            features.TryGetValue(name, out var state);
            return new FeatureRemovalVerification(name, state, ClassifyFeatureState(state));
        }).ToArray();
        var rejected = items.Where(item => !item.Accepted).ToArray();
        if (rejected.Length > 0)
            throw new InvalidDataException("功能批处理未能禁用所有请求的功能：" +
                string.Join("；", rejected.Select(item => $"{item.Name} => {item.Describe()}")));
        return new(result, items);
    }

    /// <summary>Documented repeated <c>/CapabilityName</c> arguments remove several capabilities in one DISM command.</summary>
    public async Task<BatchRemovalResult> RemoveCapabilitiesAsync(string mount, IReadOnlyList<string> capabilityNames, CancellationToken token)
    {
        var names = capabilityNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) throw new ArgumentException("至少需要一个功能。", nameof(capabilityNames));
        // RunAsync accepts exit codes 0 and 3010, so success is decided by the post-removal inventory, never by the exit code alone.
        var output = await RunAsync(token, [$"/Image:{mount}", "/Remove-Capability",
            .. names.Select(name => $"/CapabilityName:{name}"), "/NoRestart"]).ConfigureAwait(false);
        var inventory = ImageInventory.ParseIdentityStates(
            await RunAsync(token, $"/Image:{mount}", "/Get-Capabilities").ConfigureAwait(false),
            "Capability Identity", ImageItemKind.Capability);
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory) states.TryAdd(item.Identity, item.Detail);
        var items = names.Select(name =>
        {
            states.TryGetValue(name, out var state);
            return new RemovalVerification(name, state, ClassifyCapabilityState(state));
        }).ToArray();
        var unproven = items.Where(item => item.Proof != RemovalProof.Absent).ToArray();
        if (unproven.Length > 0)
            throw new InvalidDataException("功能批处理未能证明已移除：" +
                string.Join("；", unproven.Select(item => $"{item.Name} => {item.Describe()}")));
        return new(output, items);
    }

    /// <summary>Only "Not Present" proves a capability is gone; installed, pending, missing and unrecognized states are not proof.</summary>
    public static RemovalProof ClassifyCapabilityState(string? state)
    {
        var normalized = Regex.Replace(state ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        return normalized switch
        {
            "not present" => RemovalProof.Absent,
            "installed" or "staged" or "superseded" or "partially installed" => RemovalProof.Present,
            _ when normalized.EndsWith("pending", StringComparison.Ordinal) => RemovalProof.Pending,
            _ => RemovalProof.Unknown
        };
    }

    public static FeatureRemovalState ClassifyFeatureState(string? state)
    {
        var normalized = Regex.Replace(state ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        if (normalized.Length == 0) return FeatureRemovalState.Unknown;
        if (normalized.StartsWith("disabled", StringComparison.Ordinal))
            return normalized.Contains("payload removed", StringComparison.Ordinal) ?
                FeatureRemovalState.PayloadRemoved : FeatureRemovalState.DisabledPayloadRetained;
        if (normalized == "disable pending") return FeatureRemovalState.DisablePending;
        return normalized.StartsWith("enable", StringComparison.Ordinal) ? FeatureRemovalState.Enabled : FeatureRemovalState.Unknown;
    }

    public static WindowsImage ParseImage(int index, string text)
    {
        string Field(string name, bool allowEmpty = false)
        {
            // The tool banner uses "Version:", while image metadata uses "Version :".
            var separator = name == "Version" ? @"[ \t]+:" : @"[ \t]*:";
            var fields = Regex.Matches(text, $@"(?m)^[ \t]*{Regex.Escape(name)}{separator}[ \t]*(.*?)[ \t\r]*$");
            var m = fields.Count > 0 ? fields[^1] : Match.Empty;
            return m.Success && (allowEmpty || m.Groups[1].Value.Length > 0) ? m.Groups[1].Value.Trim() :
                throw new InvalidDataException($"DISM 映像元数据缺少“{name}”。");
        }
        var version = Version.TryParse(Field("Version"), out var v) ? v :
            throw new InvalidDataException("DISM 返回了无效的 Windows 版本。");
        // DISM reports the servicing revision separately on many releases.
        var revision = Regex.Match(text, @"(?m)^\s*ServicePack Build\s*:\s*(\d+)\s*$");
        if (revision.Success) version = new(version.Major, version.Minor, version.Build,
            int.Parse(revision.Groups[1].Value, CultureInfo.InvariantCulture));
        var sizeText = Regex.Replace(Field("Size"), @"[^\d]", "");
        var architecture = Field("Architecture").ToLowerInvariant();
        if (architecture == "amd64") architecture = "x64";
        return new(index, Field("Name"), Field("Description", allowEmpty: true), architecture, version,
            Field("Edition"), Field("Installation"), long.Parse(sizeText, CultureInfo.InvariantCulture));
    }

    public static IReadOnlyList<ProvisionedApp> ParseApps(string text)
    {
        var apps = new List<ProvisionedApp>();
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var display = Regex.Match(line, @"^\s*DisplayName\s*:\s*(.+?)\s*$");
            if (display.Success) name = display.Groups[1].Value;
            var package = Regex.Match(line, @"^\s*PackageName\s*:\s*(.+?)\s*$");
            if (!package.Success) continue;
            if (name is null) throw new InvalidDataException("DISM 返回了缺少标识的应用程序包。");
            apps.Add(new(name, package.Groups[1].Value));
            name = null;
        }
        return apps;
    }

    public static IReadOnlyDictionary<string, string> ParseFeatures(string text)
    {
        var features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            var feature = Regex.Match(line, @"^\s*Feature Name\s*:\s*(.+?)\s*$");
            if (feature.Success) name = feature.Groups[1].Value;
            var state = Regex.Match(line, @"^\s*State\s*:\s*(.+?)\s*$");
            if (state.Success && name is not null)
            {
                features.Add(name, state.Groups[1].Value);
                name = null;
            }
        }
        if (features.Count == 0) throw new InvalidDataException("DISM 未返回任何可选功能清单。");
        return features;
    }
}

public enum RemovalProof { Absent, Pending, Present, Unknown }

public sealed record RemovalVerification(string Name, string? State, RemovalProof Proof)
{
    public string Describe() => Proof switch
    {
        RemovalProof.Absent => $"不存在（状态“{State}”）",
        RemovalProof.Pending => $"移除待处理（状态“{State}”）；脱机映像无法证明其已被移除",
        RemovalProof.Present => $"仍然存在（状态“{State}”）",
        _ => State is null ? "在移除后的清单中缺失" : $"无法识别的状态“{State}”"
    };
}

public sealed record BatchRemovalResult(string Output, IReadOnlyList<RemovalVerification> Items);

public enum FeatureRemovalState { PayloadRemoved, DisabledPayloadRetained, DisablePending, Enabled, Unknown }

public sealed record FeatureRemovalVerification(string Name, string? State, FeatureRemovalState Outcome)
{
    public bool Accepted => Outcome is FeatureRemovalState.PayloadRemoved or FeatureRemovalState.DisabledPayloadRetained or
        FeatureRemovalState.DisablePending;

    public string Describe() => Outcome switch
    {
        FeatureRemovalState.PayloadRemoved => "已禁用，且其负载已被移除",
        FeatureRemovalState.DisabledPayloadRetained => $"已禁用；DISM 在映像中保留了其负载（状态“{State}”）",
        FeatureRemovalState.DisablePending => $"禁用待处理（状态“{State}”）；映像联机进行服务堆栈维护时才会完成",
        FeatureRemovalState.Enabled => $"仍然启用（状态“{State}”）",
        _ => State is null ? "在移除后的清单中缺失" : $"无法识别的状态“{State}”"
    };
}

public sealed record FeatureBatchResult(string Output, IReadOnlyList<FeatureRemovalVerification> Items);

public sealed class DismCommandException(int exitCode, string output)
    : IOException($"DISM 失败（退出代码 {exitCode}）。{output.Trim()}")
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}
