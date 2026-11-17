# 离线许可来源

## 来源与归属

| 位置 | 维护内容 |
| --- | --- |
| [项目许可](../LICENSE) | pbi-utils 自有代码的 MIT 原文。 |
| [第三方来源说明](../THIRD-PARTY-NOTICES.md) | 随项目发布的组件、资源与改编来源。 |
| [catalog.json](catalog.json) | 用精确身份、来源和 SHA-256 绑定必需的补充文件。 |
| [source/](source/) | 来自精确上游版本或提交的许可、NOTICE 和可离线阅读的补充文本。文件名按组件和用途固定，精确版本、来源和 SHA-256 由清单记录。 |
| 资源目录 | 字体、图标、配色和日期字段来源的许可随对应资源维护，由清单直接引用。 |

核对每种组件时优先使用对应精确版本包内的 LICENSE、NOTICE、COPYING 和版权文件。CodeMirror、Lezer、Element Plus、Marked 和 DOMPurify 的随包许可原文直接来自 npm 包。

- Analysis Services 的 Microsoft 英文与中文条款原始 RTF 及便于阅读的 TXT 都保留。转换来源见清单。WebView2 SDK 的 Microsoft 条款和声明来自对应 NuGet 包。
- ModelContextProtocol.Core 的上游 LICENSE 包含现行 Apache 条款、既有 MIT 条款及适用范围说明。
- 两种 .NET 模式的可执行入口均内嵌 Windows x64 apphost。维护清单时以实际选用的 `AppHostPack` 为准，收录对应 .NET Runtime 源的 MIT 许可和第三方声明。
- 自包含版的 Windows Desktop runtime 包补充对应 `dotnet/dotnet` 固定提交的 WPF 和 WinForms NOTICE。`coreclr.dll`、`Microsoft.DiaSymReader.Native.amd64.dll`、`PresentationNative_cor3.dll`、`vcruntime140_cor3.dll` 和 `wpfgfx_cor3.dll` 适用 .NET Library 条款，`D3DCompiler_47_cor3.dll` 适用 Windows SDK 条款。完整条款按实际随包的 runtimepack 收录。适用文件见 [Microsoft 的 Windows 文件级说明](https://github.com/dotnet/core/blob/main/license-information-windows.md)。
- Microsoft.Identity.Client.NativeInterop 和 lodash-unified 的包元数据声明 MIT。本目录的补充文件同时记录发行方提供的版权或作者信息及标准 MIT 全文。
- NSIS 的 COPYING 保留固定版本原文。打包时按实际编译器版本核验。
- Vite 的预加载代码、`@vitejs/plugin-vue` 的导出辅助代码和 Rolldown 的运行时辅助代码进入前端产物。All 范围按锁定版本收录相应原文。Rolldown 保留其 LICENSE 引用的 THIRD-PARTY-LICENSE。
- 开发期样式检查用的 `vscode-css-languageservice` 与 `vscode-languageserver-textdocument` 均为 MIT，精确版本见 [前端锁文件](../web/pnpm-lock.yaml)。它们供 [样式检查脚本](../web/scripts/check-styles.ts) 使用。

## 生成与核验

`scripts/ThirdPartyLicenses.psm1` 的 `Export-PbiLicenseBundle` 离线读取本地输入。

1. 从合并后的发布 `*.deps.json` 收集实际 NuGet 包和 runtimepack，核验 nuspec 的身份与恢复时的内容标识。
2. All 范围从前端 `dependencies` 出发，遍历已安装的生产、peer 与可选依赖。解析 npm 别名，合并同一身份的 peer 实例，核验仓库和已安装锁文件。根 `devDependencies` 不参与该闭包。
3. 收录包内原文、清单列出的离线补充及适用于当前范围的随包资源。缺失全文、未知许可、依赖缺失和身份或 SHA-256 漂移会使构建失败。

输出包括根目录的 `LICENSE`、`THIRD-PARTY-NOTICES.md`、带 UTF-8 BOM 的聚合 `THIRD-PARTY-NOTICES.txt` 和 `licenses/manifest.json`。`licenses/inputs/` 保存生成所用的清单和适用的前端依赖文件。分发原文使用 `licenses/<生态>/<身份散列前 16 位>/` 下的短文件名，以适应 Windows 安装器暂存路径。清单记录组件全名、精确版本、原文名称、来源、长度与 SHA-256。

独立执行 `package` 时先核验已验证的发布目录，再以当前来源刷新私有打包副本的许可与清单。依赖集合或内嵌资源身份变化时须重新构建。安装器编译和交付时都会复核所用许可输入。门禁等待期间来源变化时，重新打包才能交付。

NSIS 许可页和安装后的 `app/THIRD-PARTY-NOTICES.txt` 使用同一份全文。轻量包的勾选项和静默参数 `/ACCEPTMICROSOFTTERMS=1` 对应 AMO/ADOMD 条款。自包含包还纳入所附 Windows 原生运行时的 .NET Library 和 Windows SDK 条款。

## 变更验证

更新依赖声明、锁文件或 SDK 绑定后，若本地依赖尚未还原或已过期，先按需运行 `.\run.ps1 -Choice setupAll`、`.\run.ps1 -Choice 'setupC#'` 或 `.\run.ps1 -Choice setupVue`。依赖准备好后，在仓库根运行许可同步命令。

~~~powershell
.\run.ps1 -Choice setupLicense
~~~

该动作读取已还原的 win-x64 目标、当前 SDK 为各入口及两种部署方式选用的 apphost 和运行时包，以及本地已安装的锁定前端构建工具，核对上游原文和已有 SHA-256。已知 NuGet 补充来源的固定提交变化时会读取新版原文。SDK 或 apphost 来源变化时会下载官方 Windows x64 SDK ZIP，核验 SHA-512、根目录许可及 apphost 模板。原文相同则更新清单中的版本和来源，发布仍复用离线生成及校验流程。

审阅清单差异并与依赖变更一起提交。遇到原文、许可证声明或来源变化时，按诊断核对完整条款和版权，更新对应补充文件及清单后再运行命令。AMO/ADOMD 自定义条款、NSIS 和复制到仓库的资源，按实际分发版本维护。移除组件时核对直接依赖、传递依赖、内嵌资源及安装器的实际使用，共用许可仍有引用时继续保留。

运行许可来源、许可包生成与校验测试，并按 [仓库约定](../AGENTS.md) 核验两种 .NET 模式的分发内容。
