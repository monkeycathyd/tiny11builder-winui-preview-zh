namespace Tiny11Builder.Core;

public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupOption> Options { get; } =
    [
        App("copilot-app", "Copilot 应用", "移除预装的 Copilot 应用。保留共享运行时和 Shell 组件。", true, true,
            "Microsoft.Copilot", "Microsoft.Windows.Copilot", "Microsoft.549981C3F5F10"),
        App("outlook", "新版 Outlook 和邮件", "移除 Outlook 以及已停用的邮件 / 日历应用。", true, true,
            "Microsoft.OutlookForWindows", "microsoft.windowscommunicationsapps"),
        App("teams", "Teams", "移除个人版 Teams 客户端。", true, true, "MSTeams", "MicrosoftTeams", "Microsoft.Windows.Teams"),
        App("xbox", "Xbox 和游戏叠加层", "移除 Xbox 应用和游戏栏。游戏登录和录制可能无法使用。", true, true,
            "Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.Xbox.TCUI", "Microsoft.XboxGameOverlay",
            "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay"),
        App("news-weather", "新闻、天气和 Bing 应用", "移除打包的资讯应用；本地 Windows 搜索保持不变。", true, true,
            "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch"),
        App("office-promos", "Microsoft 365 推广应用", "移除 Office Hub 和 Office 推送通知推广应用。", true, true,
            "Microsoft.MicrosoftOfficeHub", "Microsoft.OfficePushNotificationUtility"),
        App("clipchamp", "Clipchamp", "移除内置的视频编辑器。", true, true, "Clipchamp.Clipchamp"),
        App("solitaire", "Solitaire", "移除内置游戏。", true, true, "Microsoft.MicrosoftSolitaireCollection"),
        App("phone", "手机连接和跨设备应用", "移除手机集成应用。跨设备功能可能停止工作。", true, true,
            "Microsoft.YourPhone", "Microsoft.Windows.CrossDevice"),
        App("feedback", "反馈、提示和帮助", "移除反馈中心、获取帮助和入门应用。", true, true,
            "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.Getstarted"),
        App("devhome", "Dev Home 和 Power Automate", "移除 Dev Home 和 Power Automate Desktop。", true, true,
            "Microsoft.Windows.DevHome", "Microsoft.PowerAutomateDesktop"),
        App("social", "People、To Do、Microsoft Family 和 Skype", "移除可选的日程管理和通信应用。", true, true,
            "Microsoft.People", "Microsoft.Todos", "Microsoft.SkypeApp", "MicrosoftCorporationII.MicrosoftFamily"),
        App("maps", "地图和混合现实", "移除已停用的地图和混合现实客户端。", true, true,
            "Microsoft.WindowsMaps", "Microsoft.MixedReality.Portal"),
        App("media", "媒体播放器与电影和电视", "移除内置媒体播放应用；编解码器和共享框架保留。", false, true,
            "Microsoft.ZuneMusic", "Microsoft.ZuneVideo"),
        App("utilities", "时钟、录音机和便笺", "移除可选个人实用工具。", false, true,
            "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder", "Microsoft.MicrosoftStickyNotes"),
        App("quickassist", "快速助手", "移除 Microsoft 远程协助客户端。", false, true, "MicrosoftCorporationII.QuickAssist"),
        App("paint", "Paint、3D Viewer 和 OneNote", "移除 Paint、3D Viewer 和 OneNote。不勾选则保留。", false, false,
            "Microsoft.Paint", "Microsoft.MSPaint", "Microsoft.Microsoft3DViewer", "Microsoft.Office.OneNote"),
        App("camera", "相机", "移除相机应用，不影响摄像头驱动程序。", false, false, "Microsoft.WindowsCamera"),
        App("terminal", "Windows 终端", "移除 Windows 终端。PowerShell 和命令提示符保留。", false, false, "Microsoft.WindowsTerminal"),
        App("oem", "OEM 推广应用", "移除 Intel 管理状态和 Dolby 推广应用，不影响驱动程序。", false, false,
            "AppUp.IntelManagementandSecurityStatus", "DolbyLaboratories.DolbyAccess", "DolbyLaboratories.DolbyDigitalPlusDecoderOEM"),
        new("edge-files", "移除 Edge 和 WebView2", "删除 Edge 和 WebView2 文件。需要 WebView2 的应用可能停止工作。更新可能会重新安装 Edge。",
            "高级 / 原始构建器", OptionKind.File, false, false, []),
        new("onedrive-files", "移除 OneDrive 安装程序二进制文件", "从脱机映像中移除两种架构各自的 OneDrive 安装程序。",
            "高级 / 原始构建器", OptionKind.File, false, false, []),
        new("language-extras", "语音、OCR 和手写组件负载", "移除所有映像语言中的可选语言功能；保留基本语言支持。",
            "可选功能", OptionKind.Capability, false, false,
            ["Language.Handwriting", "Language.OCR", "Language.Speech", "Language.TextToSpeech"]),
        new("legacy-capabilities", "已停用的附件和壁纸组件负载", "在已安装的情况下移除 WordPad、步骤记录器、数学识别器、旧版媒体播放器和扩展壁纸。",
            "可选功能", OptionKind.Capability, false, true,
            ["Microsoft.Windows.WordPad", "App.StepsRecorder", "MathRecognizer", "Media.WindowsMediaPlayer", "Microsoft.Windows.Wallpaper.Extended"]),
        Feature("xps", "XPS 打印", "禁用可选的 XPS 打印并移除其组件负载。", false, true, "Printing-XPSServices-Features"),
        Feature("workfolders", "工作文件夹", "禁用企业工作文件夹同步并移除其组件负载。", false, true, "WorkFolders-Client"),
        Feature("legacy", "旧版 Internet Explorer", "在存在的情况下禁用已停用的 Internet Explorer 可选功能。", true, true,
            "Internet-Explorer-Optional-amd64", "Internet-Explorer-Optional-arm64"),
        Feature("recall-feature", "Recall 可选功能", "仅当此映像向 DISM 公开 Recall 时，才禁用并移除其组件负载。", true, true, "Recall"),
        Policy("ads", "广告、推荐和推广应用", "抑制消费类内容、设置建议、文件资源管理器推广和欢迎提示。", true, true),
        Policy("privacy", "可选诊断和个性化", "保留必需的诊断数据。禁用广告 ID、个性化建议和输入收集。", true, true),
        Policy("copilot", "禁用 Copilot 和 Recall", "应用策略以禁用 Copilot 和 Recall。支持情况因内部版本和版本类型而异。", true, true),
        Policy("widgets", "小组件和任务栏资讯", "通过策略禁用小组件并隐藏其任务栏入口。", true, true),
        Policy("search", "Web 搜索和搜索亮点", "关闭 Web 建议、云搜索和动态搜索亮点；保留本地搜索。", true, true),
        Policy("onedrive", "OneDrive 同步和备份提示", "通过策略禁用 OneDrive 同步。OneDrive 二进制文件仍会保留；仅在不需要同步时使用。", true, true),
        Policy("edge", "Edge 首次运行和推广界面", "抑制首次运行、侧边栏和推荐内容。为兼容应用保留 Edge 和 WebView2。", true, true),
        Policy("oobe", "安装程序隐私和完成设置提醒", "抑制隐私设置和登录后的设置建议，不创建账户，也不接受 EULA。", true, true),
        Policy("cleanup", "组件清理", "常规模式保留服务堆栈维护。Core 模式还会在不可逆的组件精简前重置更新基线。", true, true),
        Policy("hardware-bypass", "绕过安装程序硬件要求", "在安装程序和 Windows 中设置 CPU、内存、TPM、存储和安全启动绕过值。不受支持的硬件可能无法正常工作。", false, false),
        Policy("reserved-storage", "禁用保留存储", "停止为更新保留磁盘空间。这可能在服务堆栈维护期间减少可用空间。", false, false),
        Policy("device-encryption", "禁用自动设备加密", "阻止自动 BitLocker 设备加密；如果默认需要加密，请勿使用。", false, false)
    ];

    private static CleanupOption App(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, title, description, "内置应用", OptionKind.App, balanced, minimal, targets);
    private static CleanupOption Feature(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, title, description, "可选功能", OptionKind.Feature, balanced, minimal, targets);
    private static CleanupOption Policy(string id, string title, string description, bool balanced, bool minimal)
        => new(id, title, description, "Windows 设置", OptionKind.Policy, balanced, minimal, []);

    public static string[] ForPreset(CleanupPreset preset) => Options
        .Where(o => preset switch
        {
            CleanupPreset.Balanced => o.Balanced,
            CleanupPreset.Minimal => o.Minimal,
            CleanupPreset.Original => o.Kind == OptionKind.App || o.Id is not ("language-extras" or "legacy-capabilities"),
            _ => false
        })
        .Select(o => o.Id).ToArray();

    public static string[] ForMode(BuildMode mode) => mode switch
    {
        BuildMode.Tiny11 => ForPreset(CleanupPreset.Original),
        BuildMode.Tiny11Core => Options.Where(o => o.Minimal || o.Id is
            "language-extras" or "edge-files" or "onedrive-files" or
            "hardware-bypass" or "reserved-storage" or "device-encryption")
            .Select(o => o.Id).ToArray(),
        BuildMode.Custom => [],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static BuildMode ModeForRecipe(BuildRecipe recipe)
    {
        if (recipe.Mode is { } mode) return mode;
        var preset = recipe.Flavor == BuildFlavor.Core ? BuildMode.Tiny11Core : BuildMode.Tiny11;
        return recipe.ExpertActions.Length == 0 &&
            recipe.SelectedOptions.ToHashSet(StringComparer.Ordinal).SetEquals(ForMode(preset))
            ? preset : BuildMode.Custom;
    }

    public static bool MatchesApp(CleanupOption option, ProvisionedApp app) =>
        option.Kind == OptionKind.App && option.Targets.Any(target =>
            app.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            app.PackageName.StartsWith(target + "_", StringComparison.OrdinalIgnoreCase));
}
