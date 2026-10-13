# 署名与分发

> 以下中文说明为便于阅读而添加；如与文末英文原文有歧义，以英文原文为准。

本应用是基于 [ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder)
的行为重新实现的原生程序；原始的 PowerShell 构建脚本及其声明均予以保留。
[S9 WoA 安装器](https://github.com/ntdevlabs/exynos9810-woa) 仅作为设计参考，
其源码并未随本应用一同分发。

本次汉化只修改界面文本、提示信息与文档，不改变上述归属关系，也不改变任何许可状态。

上游检出中没有独立的许可证文件。本次重写并未为上游材料指定新的许可证。
在公开发布你自己的贡献之前，请先确认获得许可并选择合适的许可证；
仅仅把代码放在公开的 GitHub 仓库上，并不等于授予再分发权利。

发行包中包含 .NET 与 Windows App SDK 的运行时依赖，
它们的许可证文本、第三方声明或 NuGet 许可证元数据都收录在 `licenses` 目录下。
Windows 安装介质与 ADK 可执行文件不随包分发。
Windows 的下载、使用与激活仍受微软的条款约束。

---

# Attribution and distribution

This application is a native rewrite based on the behavior of
[ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder).
The original PowerShell builders and their notices are retained.
The [S9 WoA installer](https://github.com/ntdevlabs/exynos9810-woa) was a design
reference; its source is not bundled with this application.

This upstream checkout contains no standalone license file. No new license
has been assigned to the upstream material by this rewrite. Confirm permission
and choose an appropriate license for your own contributions before public
redistribution; a public GitHub repository alone does not grant redistribution
rights.

Release packages include the .NET and Windows App SDK runtime dependencies.
Their license texts, third-party notices, or NuGet license metadata are included
under `licenses`.
Windows installation media and ADK executables are not bundled. Windows
downloads, use and activation remain subject to Microsoft's terms.
