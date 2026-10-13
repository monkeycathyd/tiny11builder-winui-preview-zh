# Tiny11 Builder：用法与开发说明

以下命令请在仓库根目录执行。下载地址、视频和截图见[项目主页](../README.md)。

**这是一个带 C# 映像构建引擎的 WinUI 3 桌面应用。** 它的分步式界面布局参考了
[S9 WoA 安装器](https://github.com/ntdevlabs/exynos9810-woa)。
它不包装、也不调用原来的 PowerShell 脚本；为兼容性考虑，原始脚本仍然原样保留在仓库中。

## 应用能做什么

| 模式 | 行为 |
| --- | --- |
| Tiny11 | 固定的“原版风格”移除集合：内置应用、Edge/WebView2 与 OneDrive 二进制文件、硬件检查相关设置、保留存储与自动加密设置。保留更新、Defender、WinRE、应用商店和共享运行时。想修改移除项请选择“自定义”。 |
| Tiny11 Core | 在重置组件库基础之前先应用更改。保留 common-controls/GDI+/CRT/服务堆栈维护相关文件，删除其他 WinSxS 组件文件夹，移除 WinRE 并关闭 Windows 更新与 Defender。这些更改**无法撤销**，仅用于测试虚拟机。 |
| 自定义 | 初始不选中任何移除项。可以逐组选择移除范围，也可以列出预装应用、受保护的系统应用、可选功能、语言功能、CBS 程序包、服务、驱动程序和 WinSxS 组件文件夹。功能与服务还提供“仅禁用”方式。Core 那种不可逆的移除是单独的一项，默认不勾选，并带同样的警告与确认要求。 |

这些预设是“原版风格”的，并不是脚本的逐条命令复制。Tiny11 会在不使用 `/ResetBase`
的情况下清理被取代的组件，从而保留卸载已有更新的能力。Core 则会重置组件库基础，
保留更多自动检测到的运行时/服务堆栈家族，并在删除 WinRE 时不像原脚本那样留下空占位目录。
这些兼容性取舍未因可靠性改进而改变；要进一步精简，需要单独进行安装/启动测试。

六步向导提供：

- 一个整宽欢迎页，带可点击的构建模式卡片。点击卡片即选定该模式并进入 ISO 选择。
  窗口较窄时卡片会自动纵向堆叠；页面向下滚动时，右侧那个很大的浅色编号（1-6）会淡出。
  返回按钮位于左上角，支持 Alt+左方向键。所有前进操作（包括“开始”和“构建”）都在右下角。
  没有多余的页脚条或重复的步骤计数。
- 一键下载微软最新发布的 Windows 11 ISO，自动使用宿主机的架构与显示语言。
  不支持的语言会回退到英文，并给出明确的说明。无需填写 URL，也不弹出保存对话框；
  下载文件存放在所选工作区的 `downloads` 目录中。微软的可用性/区域限制会被如实报告，而不会绕过。
- 微软官方 ISO 下载支持速度/进度显示、基于强 ETag 的断点续传、ISO 结构检查，
  以及可选用已发布的 SHA-256 进行校验。只接受微软的 ISO 主机，包括其每一次重定向。
  会过期的查询令牌不会被持久保存；续传元数据只保留微软资源主机/路径、ETag 和长度。
- 支持本地 ISO 与已解压的安装介质，支持 WIM/ESD 识别、版本选择，并自动以只读方式做原生
  ISO 挂载。找到 Windows 11 专业版时优先选中，否则选中索引 1。
  载入已保存的配方时，如果原来的版本仍存在，则沿用该版本。
- 三张构建模式卡片：Tiny11、Tiny11 Core 和自定义。逐项保留/移除控件只在“自定义”中显示。
  Core 在继续前必须经过一个明确的同意对话框；取消则视为未批准 Core。
  列出完整映像清单需要先显示专家警告，并在构建前单独确认风险。
- Core 与自定义的 WinSxS/组件负载移除始终使用旧版 Windows 安装程序，在 24H2 及更新的介质上也是如此。
  其专用的 Setup 启动映像会先运行 `wpeinit.exe`，再运行与之匹配的 `sources\setup.exe`
  （存在 `setup_old.exe` 时用后者），而不是较新的 SetupHost 流程。
  这是自动行为，不是“硬件绕过”选项；不会去下载其他 Windows 版本的安装程序。
  介质中缺少这个经典可执行文件时会明确失败。即使某个 Core 任务的最终安装映像已经压缩完成，
  续做时也会应用这一更改。它通过
  [Winpeshl.ini](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpeshlini-reference-launching-an-app-when-winpe-starts)
  配置启动后 ISO 的 WinPE 外壳；这不是就地升级的变通手段。
- 可选地从匹配的 `sources\sxs` 安装 .NET 3.5、紧凑安装，以及尽力而为的本地账户设置。
  不会注入账户、密码、产品密钥、自动登录或 EULA 接受。
- 自动检测映像工具并检查版本兼容性。oscdimg 会在本地查找，或从微软符号服务器下载，
  并带有精确的 SHA-256 固定校验。没有工具选择控件，也不修改脚本执行策略。
- 带开关的图文安装选项卡片、结构化的检查确认页，以及可展开的构建阶段
  （包含待处理、进行中、已完成、已跳过和失败的操作）。应用、功能和功能的移除都会显示确切的标识名。
  控制台风格的进度条只保留在磁盘日志中，不出现在界面里。
  总体进度按阶段边界推进，而不是按时间估算。条目归组在其所属阶段下；
  一个条目失败后不会再作为该阶段的第二次失败重复计数。停止后未开始的工作显示为**未运行**。
  构建页把总体进度、当前操作、输出详情和可展开的操作历史分开呈现，各区块之间留出更多空间。
- 与应用架构匹配的安装 XML、x64 与 ARM64 的 UEFI 启动介质、
  源介质包含时在 x64 上保留 BIOS 启动项，以及 Recovery ESD、最大压缩 WIM 或快速 WIM 压缩。
- 配方导入/导出、实时日志、安全边界取消、持久化任务状态、用于**继续构建**的已校验进度、
  输出 SHA-256、JSON 报告，以及独立的中断工作区丢弃/恢复功能。

## 极速模式与更小的映像

设置页有三个相互独立的开关：**极速模式**、**移除冗余映像数据**和**省略 .NET 3.5 安装负载**。
它们默认都关闭，因此已有的配方和固定的 Tiny11/Core 移除集合行为保持不变。

| 设置 | 改变什么 | 代价 |
| --- | --- | --- |
| 极速模式 | 串行化的功能/能力批处理并回读状态校验，最多八个复制工作线程，降低日志 I/O。 | 占用更多 CPU/I/O 和临时空间；实际收益取决于映像与宿主机。 |
| 极速 + Core 或快速 WIM 输出 | 使用快速压缩的可写中间 WIM，而不是把 Core 的大量更改重新压缩进 LZX 工作映像。 | 初次转换可能更慢，工作 WIM 也更大。最终压缩仍按所选方式执行。 |
| Recovery ESD | 对最终版本使用 solid 恢复压缩。 | 微软工具产出的体积最小，通常也是压缩阶段最慢的。极速模式不会悄悄替换它。 |
| 最大压缩 WIM | 压缩率较高的可挂载 WIM。 | 更便于后续维护，但通常比 Recovery ESD 大。 |
| 快速 WIM | 更快的最终压缩。 | ISO 更大；当构建时间比存储空间更重要时使用。 |
| 移除冗余映像数据 | 移除已下载的更新缓存、临时文件、归档的 CBS/DISM 日志、崩溃转储以及 Windows 错误报告归档/队列。 | 记录的是逻辑移除字节数，并不等于压缩后 ISO 能省下的体积预测。 |
| 省略 .NET 3.5 安装负载 | 不把 `sources\sxs` 放进输出 ISO。构建期间仍可从原始源启用 .NET 3.5。 | 之后要添加就需要其他匹配介质或 Windows 更新；Core 之后无法再添加功能。 |

构建和清单默认使用普通挂载，极速模式下也是如此。
工具版本变化时不会自动尝试延迟挂载（`/Optimize`）：在本机上一次生产环境探测就耗时超过十二分钟。
开发用的可选开关保留了已知错误的回退逻辑和带版本号的 `mount-optimization.json` 建议缓存。
建议缓存无效或无法访问时，只会禁用延迟挂载并给出诊断信息，而不会中断构建。
极速模式不会针对同一个已挂载映像并行执行维护操作、不会关闭宿主机杀毒软件、
不会改动操作系统电源设置，也不会移除完整性校验。批处理会跟踪每个功能并回读其状态；
一个批次失败就报告为失败，而不会算成若干独立成功。

清理器使用固定的白名单。它保留缓存/日志根目录、当前的 `CBS.log` 与 `dism.log`、
更新数据库、注册表文件、驱动程序仓库、Windows Installer 缓存以及维护清单。
当 `pending.xml` 或 `reboot.xml` 表明存在待处理的服务堆栈维护，
或离线注册表中有维护/更新重启标记时，下载与临时负载会被保留。
如果配置单元缺失或损坏导致无法校验，敏感目标会被保留并给出警告，
而不会报告为“已成功移除”。注册表检查使用私有的配置单元/日志副本，
因此加载它不会把事务回放到映像中。其他 I/O 或访问错误仍然算作失败。
在删除清理目标之前，未知的符号链接和目录联接会被拒绝。
它不会扫描正在运行的系统去找可删除的东西。

标准模式和极速模式都通过备份/还原句柄删除离线条目，
不会更改它们的 ACL，也不会清除与剩余硬链接共享的只读属性。
两者都支持超长映像路径，并拒绝非映像的重解析点。
每次构建都会记录每个操作的耗时、`timings.json` 中每个阶段的耗时、
已复制/已省略的安装程序字节数，以及报告中的清理文件数与字节数。失败报告同样保留耗时数据。

两种模式下都会应用一些通用优化：不使用快速工作映像时保留源 WIM 压缩；
重复目标只处理一次；已移除的负载会跳过；预检只重新查询所选版本；
省略不必要的功能清单；安装文件按“最大优先”复制；同卷发布 ISO 时用重命名代替复制。
日志对任务私有，暂存文件使用该任务的 NTFS 工作区。
进度计时器只在操作进行时运行，并成批处理，而不是在应用空闲时持续唤醒。

在采用共享删除路径之前，一项三次运行、4000 个文件的基准测试测得：
旧的“重写 ACL”路径耗时 3.55–4.12 秒，原生删除耗时 0.69–0.75 秒。
这不是当前两种模式的对比。真实的 977 文件 ISO 暂存使用四个工作线程耗时 1.74–5.90 秒，
八个线程耗时 1.68–1.85 秒；结果取决于缓存与宿主机，并不是通用的提速承诺。
省略该映像的可选 .NET 负载避免了复制 71,656,916 字节（约 68.3 MiB）。

一次端到端的 26H2 x64 Core 构建（相同移除集合、Recovery ESD 压缩）在
**31 分 44 秒**内完成，而本机此前的一次运行是 **90 分 31 秒**。
Core 精简耗时从约 4 分 38 秒降至 37 秒；提交从约 29 分 38 秒降至 3 分 54 秒。
初次转换为快速工作 WIM 反而更慢（3 分 8 秒，而非 33 秒）。
这些是缓存/系统负载不同的开发实测数据，不是有保证的提速倍数。
本次没有使用优化的延迟挂载，因为该宿主机的提供程序拒绝了它。

最终得到的极速模式 ISO 为 **3,259,584,512 字节（3.04 GiB）**，此前为 3,331,207,168 字节。
约 68.3 MiB 来自省略可选安装负载；这个干净的微软源本来就没有额外的缓存文件可移除，
因此那次构建没有宣称任何缓存节省。ISO 的专业版、内部版本、压缩方式与 SHA-256 均已校验；
虚拟机启动认证仍是另一回事。

**这是一个映像构建工具，不是安装器，也不是对正在运行的系统做精简。**
它绝不会刻意针对正在运行的 Windows 安装、格式化磁盘或修改源介质。
只有新建的工作副本会被处理。已存在的输出 ISO 会被拒绝，而不是覆盖。

离线注册表编辑会把复制出来的映像配置单元临时挂载到唯一命名的
`HKLM\Tiny11Builder_Offline_<guid>` 键下，并显式卸载。
宿主机已有的注册表设置不会被修改。配置单元加载/卸载失败会停止操作，
并报告确切的临时键名，而不是隐藏错误。
每个任务的配置单元日志让工作区恢复只卸载该任务的临时键——即使在配置单元卸载前应用被中断。
DWORD 和字符串写入使用原生 `RegSetValueExW` API，句柄以限定范围的备份/还原特权打开，
并且每个写入的值都会被回读。小组件通过微软文档化的
`SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests=0` 策略禁用。
受保护的 `Explorer\Advanced\TaskbarDa` 偏好项被刻意不写入：较新的宿主机即使通过原生 API 也可能拒绝它。
宿主机的保护服务不会被关闭。该策略作用于小组件及其任务栏内容；支持情况仍随 Windows 版本而异。

Core 负载删除使用扩展长度的 Windows 路径，因此很长的 WinSxS 文件名不会触发旧的 260 字符限制。
符号链接与目录联接的限制保持不变。

这些加速依赖微软文档化的
[/Mount-Image /Optimize](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14)
和
[多功能 /Disable-Feature](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options)
选项。功能批处理针对默认的 Foundation 程序包；专家模式下单独的维护操作仍然串行执行。

离线映像文件合法地使用 WIM/WOF 重解析标记。在归属的映像内部，这两种
非名称代理的映像提供程序标记是被接受的；普通符号链接和目录联接仍然会被拒绝。
源、工作区和输出路径保留更严格的重解析点检查。

## Windows 11 版本兼容性

引擎接受 **Windows 11 客户端版本 22621（22H2）及更新版本**，包括
23H2、24H2、25H2、26H2 和 Insider/Canary 介质，架构支持 **x64 与 ARM64**，
没有硬编码的最高内部版本号。映像架构、版本与内部版本都从 DISM 读取，
而不是根据 ISO 文件名推断。程序包/功能/特性的标识来自已挂载的映像，
因此缺失的条目会记录为*不存在*，而不是靠猜测。

被接受**并不等于认证每一个版本、每一种自定义移除组合都能启动**。
这个新实现需要针对各发行版/通道的代表性介质进行虚拟机验证。
单元测试覆盖构建元数据，以及跨发行版和更新/Canary 风格内部版本号的模拟流水线；
可选的集成测试会以只读方式使用真实介质。

Windows 10（包括 22H2）可以借助兼容的微软部署工具构建 Windows 11 映像；
要求不在于宿主机的发行版本号。应用检查的是实际的服务堆栈维护提供程序，
而不只是 `dism.exe` 启动器的版本。已知的启用包共享服务堆栈基础：
23H2（22631）使用 22621；25H2（26200）与 26H2（26300）使用 26100。
因此不会仅因为 26H2 映像报告 26300 就拒绝 26100 的工具。请保持 Windows/ADK 维护更新为最新。

微软的 [ADK 支持列表](https://learn.microsoft.com/windows-hardware/get-started/adk-install)
中，26100 ADK 覆盖 24H2、25H2 与 26H2；
[DISM 支持的平台](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-supported-platforms)
说明了可以在 Windows 10 上使用 Windows 11 部署工具。
未知的更新/Canary 版本不会归入这些家族，仍可能需要预览版 ADK 或更新的工具提供程序。
当工具不满足该服务堆栈基础时，应用会在做任何修改之前停止。
它会自动检测已安装的工具，没有手动选择工具的地方。
微软也决定哪些稳定版/Insider ISO 可用：应用不声称能下载未发布的 Canary 发行版，
也不使用非官方的 UUP 镜像。主“下载”按钮会自动解析当前公开发行版。
可选的“其他官方下载”区域仍接受其他语言或 Insider 介质的手动链接。
微软可能要求你在其 Insider 页面上登录；应用不处理你的凭据。

**策略与具体版本/SKU 有关。** 离线注册表写入成功会经过校验，
但这并不能证明家庭版、专业版、企业版或未来版本都会遵循每一条策略。
Balanced 配置中保留了必需的诊断数据。Windows 功能更新可能会恢复应用和设置。
本地账户与硬件检查设置属于尽力而为，并不保证在新版本上一定绕过。
任何修改都不会绕过 Windows 授权或激活。

**Core 与专家模式警告：** Core 没有可用的更新/组件服务堆栈维护，也没有恢复映像，
并且关闭了防病毒保护。遇到运行时布局未知的未来版本时，会在开始 Core 精简前拒绝。
专家模式刻意允许移除受保护和与启动相关的条目，且没有依赖关系求解器。
DISM 可能拒绝移除受保护的 CBS 程序包，此时构建会明确失败。
直接删除系统应用/WinSxS 负载是单独暴露的不受支持操作。
移除某个服务会删除其离线注册表登记项；共享的 `svchost.exe` 和其他共享二进制文件绝不会被盲目删除。
请在隔离的虚拟机中测试激进映像，并保留已知可用的介质。

## 构建与运行

要求：宿主机为 Windows 10 2004 或更新版本、.NET 8 SDK，以及与项目目标兼容的 Windows SDK。
应用会选择已安装的最新的微软维护工具，并自动准备 oscdimg。
构建更新的映像仍需要足够新的 Windows 宿主机或 ADK。
Windows App SDK 与 .NET 运行时在发布的应用程序文件夹中自包含。

已安装的工具需要可信的微软签名。微软符号服务器上的 oscdimg 副本不含嵌入式证书，
因此只接受精确批准的 2.56 二进制文件：SHA-256
`2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861`。
下载仅限于批准的微软符号服务器资源及其观察到的微软 blob 重定向。
校验和或重定向不符时会在不执行的情况下终止获取。这不是通用的“允许未签名工具”例外。

```powershell
dotnet build .\Tiny11Builder.sln -c Release
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\Tiny11Builder-x64
.\artifacts\Tiny11Builder-x64\Tiny11Builder.exe
```

ARM64 宿主机：

```powershell
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-arm64 -p:Platform=ARM64 -o .\artifacts\Tiny11Builder-arm64
```

应用**启动时始终需要管理员权限**。Windows 会在打开前请求 UAC 批准（或管理员账户的凭据）；
拒绝即表示应用不会启动。这一点嵌入在可执行文件中，不是可选的“重启”按钮，也不是绕过 UAC。
请启动 `Tiny11Builder.exe`，而不是托管 DLL。构建仍然需要明确确认并确认风险。
文件/文件夹选择器在高权限应用中可正常工作。

## GitHub 源码与发行包

应提交：源码、测试、解决方案、脚本、README、NOTICE 和 `.github` 配置。
生成的 `bin`、`obj`、`artifacts`、测试结果和 Windows 映像都被忽略。
不要把你的工作介质、含个人路径的配方或构建日志放进公开上传内容中。

GitHub Actions 工作流在 Windows 上运行测试，并构建自包含的 x64 与 ARM64 ZIP。
它只上传工作流产物，不发布 GitHub Release、不更改权限，也不打包 Windows ISO/ADK 工具。
原生测试需要具备管理员权限的 Windows 运行器。真实映像测试默认不运行，需显式启用。

在管理员权限的 PowerShell 中本地构建同样的压缩包：

```powershell
.\scripts\package.ps1 -Architecture x64
.\scripts\package.ps1 -Architecture arm64
```

压缩包与 SHA-256 文件出现在 `artifacts\release`。脚本会校验可执行文件架构、
嵌入的管理员清单和捆绑的运行时，并包含依赖项的许可证声明。
它会拒绝覆盖该版本已存在的压缩包。请把整个 ZIP 解压到文件夹后再打开 `Tiny11Builder.exe`；
只复制 EXE 是不够的。

当前应用版本为 **0.1.1-zh.1**（简体中文汉化版）。发布的包未经签名，也不是微软认证的 Windows 映像。
公开再分发前请阅读 [NOTICE.md](../NOTICE.md)：这个上游检出没有独立的许可证文件，
本次重写也没有为别人的源码指定许可证。
现有的 `origin` 指向上游项目；推送前请选择你自己的 GitHub 仓库或 fork。
打包脚本中没有任何推送代码的行为。

默认工作区是 **`C:\tiny11`**，输出 ISO 也放在那里。
请使用 **NTFS 工作区**，通常需要 40–70 GiB 可用空间。
由于 WIM 挂载的限制，ReFS、FAT32 和网络工作区会被拒绝。输出也不能放在 FAT32 上。
支持工作区与输出位于同一磁盘。网络路径、备用数据流、源/工作区/输出中的重解析点，
以及把系统目录当作输出/工作区，都会被拒绝。输入必须包含 `boot.wim`、
`install.wim` 或 `install.esd`，以及 UEFI 启动介质；不支持拆分式 `install.swm` 输入。
只导出单个版本可以避免先复制多版本安装映像、之后又把它删掉。

## 日志、配方与恢复

状态与缓存的工具位于 `%LOCALAPPDATA%\Tiny11Builder`；工作介质默认在 `C:\tiny11`，可以更改。
每个操作都会创建一个唯一的 `tiny11-<guid>` 子目录。一次构建会保留
`job.json`、`recipe.json`、`build.log`、`dism.log`、`operations.json`、
`timings.json` 和 `report.json`。
`operations.json` 也会记录失败的构建，包括确切的操作和错误详情。
原始磁盘日志保留异常和控制台输出。
成功时，除非选择了**保留工作介质**，工作介质会被删除；日志与报告保留。
失败时工作文件会保留，应用会给出它们的路径。
配方包含本地路径和选择项，分享前请先检查。
切换构建模式会替换之前的移除选择。自定义模式初始不选中任何移除组、高级操作或 Core 移除。
单个组件操作会叠加在已选中的组之上：要保留某个组件，就不要勾选它所在的组和它自己那一项。
保存的配方会保留所选模式和确切的移除项。旧配方仍可接受；
被修改过的 Core 配方会以“自定义 + 启用 Core 移除”的方式载入，绝不会被悄悄丢弃。

取消**不会**杀掉正在运行的 DISM 挂载/提交进程。它会等待该操作结束，
然后在安全边界丢弃本任务的挂载。操作进行中无法关闭应用。
ISO 创建和下载流可以取消。已完成的阶段会在取消生效前记录到下一个边界。
失败/取消时工作文件与日志都会保留。
失败提示提供**打开日志**。红色**安全取消**按钮在工作进行时始终位于右上角。
点击后先请求确认；默认按钮是“继续构建/继续”，不会发送取消。
确认后同一对话框保持打开，并显示进度条，直到当前操作结束、所属挂载被丢弃、日志被保存。
等待进度条是不确定状态，而不是猜测的关机百分比。
运行完全结束后，对话框会显示实际结果。清理失败或需要恢复会显示为错误，绝不会显示为“安全完成”。
如果操作在确认框打开期间就已完成，则不会取消任何东西。
取消清理期间，Escape 和关闭应用都会被阻止。

安装映像成功提交后，任务会保存 `checkpoint.json`，
其中包含原始配方哈希、映像标识、映像哈希以及已暂存安装文件的哈希清单。
最终压缩后会有第二个检查点，允许在不重新压缩的情况下重试 ISO 创建。
检查点在持有任务独占锁时以原子方式刷新和替换。
安装映像的编辑使用私有副本，在替换前先提交并校验。

使用**继续构建**并选择已停止任务的 `job.json`，重启应用后同样可以这样做。
引擎会在继续之前校验检查点、哈希、工具和任务归属。
它会拒绝已更改的移除选择、损坏或过期的进度、仍在活动的任务以及不安全路径。
有效提交之后不再需要原始源。界面会用新找到的工具还原原始配方；
如果输出已存在，请选择新的 ISO 名称。继续构建的确认框默认按钮是“取消”，
并重复提示适用的 Core/自定义风险。

没有检查点的旧的中断任务无法自动继续，即使它们恰好包含已提交的 WIM 也不行。
在为“继续构建”选择它们时，既不会被接管也不会被删除。需要的话请自行保留其工作文件。

发布永远不会覆盖已存在的 ISO。跨卷复制会在最终重命名前刷新并校验哈希。
输出发生竞争时，已校验的 ISO 会保留在任务的 `output.iso`，构建仍可继续。
成功报告只在发布之后写入。发布后的日志/报告/清理问题会以警告形式显示，
而不会被误报为已发布 ISO 的失败。

如果应用或电脑崩溃，请使用**恢复工作区**，选择该任务的 `job.json`，
并明确确认丢弃其工作副本。恢复会拒绝仍在活动且被锁定的任务以及已完成的任务。
如果存在已保存的进度，界面会提供**改为继续构建**，绝不会自动删除已提交的映像。
恢复只处理该任务的挂载与暂存目录；它从不使用全局 DISM 挂载清理，
并会保留源、日志、配方和已有输出。

## 测试

请在具备管理员权限的终端中运行测试。原生文件与注册表测试需要 Windows 备份和还原特权。

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj
```

一个小型原生映像提供程序检查会把一个合成目录捕获到它自己的临时 WIM 中，
删除一个离线组件、提交、再以只读方式重新挂载，以校验保留下来的硬链接的内容、
权限和只读状态。它不会用到你的 Windows ISO：

```powershell
$env:TINY11_NATIVE_WIM_CHECK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Shared_deletion_survives_a_synthetic_WIM_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_NATIVE_WIM_CHECK=1
Remove-Item Env:\TINY11_NATIVE_WIM_CHECK
```

旧版安装程序的集成可以用真实 ISO 检查。该测试只把 `boot.wim` 复制到一个可丢弃的任务中，
应用启动配置、提交、以只读方式重新挂载，并检查保存的配置和映像数量。
它不会改动原始 ISO；它不在虚拟机中认证 Windows 安装程序：

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Legacy_startup_survives_a_real_Setup_image_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_SETUP_ISO_CHECK=C:\media\Windows11.iso
```

真实映像测试默认不运行，需要有效的 Windows 11 WIM。它以**只读**方式挂载 WIM、
列出其组件，并在注册表文件的独立副本上测试策略与服务更改。
随后丢弃挂载，并检查源时间戳未被改动：

```powershell
$env:TINY11_TEST_WIM = 'C:\media\sources\install.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Read_only_real_Windows_image_integration_when_configured
```

应用只是 `src\Tiny11Builder.Core` 之上的轻量 WinUI 展示层；测试无需图形界面即可运行。
实时微软目录与工具检查默认不运行，需显式启用：

```powershell
$env:TINY11_TEST_DOWNLOADS = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Live_official_catalog_and_pinned_tool_when_configured
```

它会解析 x64 与 ARM64 的 ISO 链接，并下载/校验 oscdimg，但不执行它，也不下载完整的 Windows ISO。
Windows 介质和部署可执行文件不由本仓库再分发。

可写配置单元的回归测试同样默认不运行。它把完整的 Core 策略集合应用到一个临时挂载，
校验“小组件”策略，然后丢弃更改：

```powershell
$env:TINY11_PERMISSION_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Writable_mount_permission_regression_when_configured
```

此测试请使用工作用的 WIM 副本。完整 Core ISO 校验会读取一个明确指定的已保存配方，
在 `C:\tiny11` 下写出一个单独的 ISO，且不声称做过虚拟机启动验证：

```powershell
$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

一次 Windows 11 专业版 26H2 x64、内部版本 26300.9457 的端到端构建，
使用原始 Core 移除集合、紧凑安装和 Recovery ESD 压缩完成。
生成的 ISO 为 3,331,207,168 字节（3.10 GiB），只包含索引 1 处的单个安装版本。
源介质为 8.31 GiB 且为多版本，因此这个体积对比还包含了“只导出专业版”这一因素，
以及移除和压缩。虚拟机启动验证与 ISO 创建仍是两件事。

可选的极速模式基准测试：

```powershell
$env:TINY11_TURBO_BENCHMARK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Native_file_deletion_benchmark_when_configured

$env:TINY11_TURBO_TEST_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Real_Turbo_mount_staging_and_cleanup_benchmark_when_configured

$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
$env:TINY11_TEST_TURBO = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

最后一条命令使用单独的 `C:\tiny11\turbo-verification` 工作区和
`C:\tiny11\Tiny11Core-26300-turbo.iso` 输出。它保留恢复压缩、
启用极速模式与冗余数据清理，并省略可选的安装负载。
挂载基准测试请使用工作副本 WIM，并让每个测试在结束进程前丢弃自己的挂载。

---

# 原始的 PowerShell 版 tiny11builder
*用于构建精简 Windows 11 映像的脚本——现在用 **PowerShell** 编写！*

## 简介
原始的 PowerShell 脚本使用 DISM 和 oscdimg 创建修改过的 Windows 11 安装映像。
`tiny11maker.ps1` 保留更新支持；`tiny11Coremaker.ps1` 移除它。

现在你可以在**任何** Windows 11 发行版（不只是某个特定内部版本）上使用它，
也支持**任何**语言和架构。这得益于 PowerShell 相比旧的批处理版本大幅改进的脚本能力。

脚本使用 DISM 恢复压缩和 Windows ADK 中的 **oscdimg.exe** 来创建 ISO。
随附的无人参与应答文件配置紧凑安装和本地账户设置。
在较新的 Windows 版本上，本地账户绕过可能失效。

**tiny11 Core** 移除组件库文件和 Windows 恢复支持，并关闭 Windows 更新与 Defender。
之后你无法再添加语言、更新或功能。请仅用于测试虚拟机。

---

## ⚠️ 脚本版本
- **tiny11maker.ps1**：常规脚本，移除大量臃肿内容但保持系统可维护。
  创建后仍可以添加语言、更新和功能。推荐日常使用。
- ⚠️ **tiny11coremaker.ps1**：Core 脚本，移除更多臃肿内容，但也移除了映像的可维护能力。
  创建后无法添加语言、更新或功能。推荐用于快速测试或开发用途。

## 使用说明
1. 从[微软网站](https://www.microsoft.com/software-download/windows11)或 [Rufus](https://github.com/pbatard/rufus) 下载 Windows 11
2. 用文件资源管理器挂载下载的 ISO 映像。
3. 以管理员身份打开 **PowerShell 5.1**。
5. 更改脚本执行策略：
```powershell
Set-ExecutionPolicy Bypass -Scope Process
```
> 使用 `-Scope Process` 可以保持你原有的策略不变，因为该更改只在当前 PowerShell 会话中有效。

6. 启动脚本：
```powershell
C:/path/to/your/tiny11/script.ps1 -ISO <letter> -SCRATCH <letter>
```
> 你可以通过运行 `get-help` 命令查看脚本的帮助。

6. 选择映像挂载所在的驱动器号（只填字母，不要冒号）
7. 选择要作为基础的 SKU。
8. 剩下的就是等待 :)
9. 映像完成后，你会在脚本解压所在的文件夹中看到名为 tiny11.iso 的文件

---

## 移除了什么
<table>
  <tbody>
    <tr>
      <th>Tiny11maker</th>
      <th>Tiny11coremaker</th>
    </tr>
    <tr>
      <td>
        <ul>
          <li>Clipchamp</li>
          <li>新闻</li>
          <li>天气</li>
          <li>Xbox</li>
          <li>获取帮助</li>
          <li>入门</li>
          <li>Office Hub</li>
          <li>纸牌</li>
          <li>人脉</li>
          <li>PowerAutomate</li>
          <li>待办事项</li>
          <li>闹钟</li>
          <li>邮件和日历</li>
          <li>反馈中心</li>
          <li>地图</li>
          <li>录音机</li>
          <li>手机连接</li>
          <li>媒体播放器</li>
          <li>快速助手</li>
          <li>Internet Explorer</li>
          <li>平板电脑数学输入面板</li>
          <li>Edge</li>
          <li>OneDrive</li>
        </ul>
      </td>
      <td>
        <ul>
          <li>包含常规 tiny 的全部项目，另加</li>
          <li>Windows 组件库（WinSxS）</li>
          <li>Windows Defender（仅禁用，需要时可重新启用）</li>
          <li>Windows 更新（没有 WinSxS 就无法工作，启用它会让系统处于故障状态）</li>
          <li>WinRE</li>
        </ul>
      </td>
    </tr>
  </tbody>
</table>

请记住，**在 tiny11 core 中你无法再把功能加回来**！<br>
创建映像的过程中会询问你是否启用 .net 3.5 支持！

---

## 已知问题
- 虽然移除了 Edge，但“设置”中仍会残留一些条目，不过应用本身已被删除。
- 你可能需要先更新 Winget，才能通过 Microsoft Store 安装任何应用。
- Outlook 和 Dev Home 过一段时间可能会重新出现。这是一场持续的拉锯战，不过最新的脚本更新会更有力地阻止它。
- 如果在 arm64 上使用此脚本，运行过程中可能会瞥见一个错误。这是因为 arm64 映像的 System32 文件夹中没有包含 OneDriveSetup.exe。

---

## 计划实现的功能
- ~~禁用遥测~~（已在 04-29-24 版本中实现！）
- ~~更多广告抑制~~（已在 09-06-25 版本中部分实现！）
- 改进语言与架构检测
- 更灵活地选择保留与删除
- 也许做个图形界面？？？

目前大致就这些！
## ❤️ 支持本项目

如果这个项目帮到了你，请考虑给予支持！一点捐助能让我投入更多时间做这样的项目。
谢谢！

**[Patreon](http://patreon.com/ntdev) | [PayPal](http://paypal.me/ntdev2) | [Ko-fi](http://ko-fi.com/ntdev)**
感谢试用，也欢迎告诉我你的感受！
