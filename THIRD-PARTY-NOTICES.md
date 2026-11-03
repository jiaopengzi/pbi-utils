# 第三方来源与许可

pbi-utils 自有代码采用根目录 `LICENSE` 中的 MIT 许可。第三方组件和资源保持各自版权与许可，本文件不重新授权它们。

**Microsoft AMO/ADOMD 组件条款**：这两个组件的原始 Microsoft EULA 含独立分发要求，安装向导需明确接受其条款；MIT 许可不覆盖这两个组件。完整英文及微软提供的中文条款随许可包保留。

发布包中的 `THIRD-PARTY-NOTICES.txt` 包含本说明、实际分发依赖的精确版本索引及许可/NOTICE 全文；`licenses/` 保留原文和机器清单。构建从实际 NuGet 发布依赖与前端生产依赖闭包收集，不将仅用于开发、测试的工具当作产品运行依赖。自包含包同时携带 .NET 两个运行时框架的许可与第三方声明；外部安装的 .NET Desktop Runtime、WebView2 Runtime、Power BI Desktop 适用各自条款。

本地 Markdown 编辑使用 CodeMirror 的 `@codemirror/lang-markdown` (MIT), 精确版本见 `web/package.json` 和锁文件; 许可原文为 `web/public/licenses/codemirror-lang-markdown-MIT.txt`. DAX/M 主题颜色适配自用户指定 blog-client 使用的 `highlightjs-dax` 1.1.0 与 `highlightjs-m` 1.0.0 的 `dist/theme.css` (MIT, Copyright (c) 2026 焦棚子), 保留本项目 CodeMirror 词法解析及安全代码预览; 对应许可原文为 `web/public/licenses/highlightjs-dax-MIT.txt` 与 `web/public/licenses/highlightjs-m-MIT.txt`, 均随页面资源发布.

UDF 说明文档使用本地打包的 [Marked](https://github.com/markedjs/marked) (MIT) 和 [DOMPurify](https://github.com/cure53/DOMPurify) (按 Apache-2.0 选项使用) 渲染与净化 Markdown, 精确版本见 `web/package.json` 和锁文件. 许可原文分别随资源保存在 `web/public/licenses/marked-MIT.txt` 和 `web/public/licenses/dompurify-Apache-2.0.txt`. 实现参考用户所有的 blog-client `v1.0.0` 中 `pkg/marked` 的解析与渲染分层, 保持本项目的本地资源与受限导航边界.

代码字体使用 JetBrains Mono Medium (JBMono), SIL Open Font License 1.1。字体文件按用户要求复用 blog-client 提交 `4df1e327` 的 `public/fonts/JetBrainsMono-Medium.woff2`, SHA-256 为 `086c48dfbea9ddaff1320f7e09399b8e2924e88ce67453721255db3bdbb5a353`。字体嵌入本地前端资源, 许可原文随发布保存在 `web/public/licenses/JetBrainsMono-OFL.txt`。上游来源: [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono), [OFL 原文](https://github.com/JetBrains/JetBrainsMono/blob/master/OFL.txt)。

Power BI 共享/个人工作区新增 Microsoft.AnalysisServices.AdomdClient, 版本由中央配置和锁文件管理, 许可按该 NuGet 包 Microsoft 条款。容量选择和受限架构读取参考 Bravo 同一固定提交的 AnalyzeModelService、ConnectionWrapper 与 TabularDatabase.CreateFromDmvSchema, 未调用其业务数据导出或统计查询。

Power BI Service 连接参考 [sql-bi/Bravo 固定提交](https://github.com/sql-bi/Bravo/tree/fbc2b12950ddb4344ae71f16de8861407c32050e) 的 `CloudAuthenticationClient`、`CloudConfigurationService`、`CloudApiClient` 与模型连接协议。按本项目共享用例、来源白名单和后端句柄边界重新实现, 未复制其 UI 或运行时服务容器。上游许可见 [Bravo LICENSE](https://github.com/sql-bi/Bravo/blob/fbc2b12950ddb4344ae71f16de8861407c32050e/LICENSE)。Microsoft.Identity.Client 按 NuGet 声明使用 MIT 许可, 对应固定上游原文随许可包提供, 实际版本仅见 [Directory.Packages.props](Directory.Packages.props) 与锁文件。其系统浏览器登录和内存缓存不启用本项目遥测上传。

本地 MCP 使用 [官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 的 ModelContextProtocol.Core，版本及传递依赖以 Directory.Packages.props 与 packages.lock.json 为准。当前锁定版本的 NuGet 元数据声明 Apache-2.0；上游完整 LICENSE 同时说明从 MIT 迁移的范围并保留旧贡献的 MIT 文本，发布包保留完整原文，不简化为统一 MIT。SDK 所需 Microsoft.Extensions.AI.Abstractions 是协议抽象依赖，产品不嵌入聊天模型客户端。Serilog 相关运行库保留 Apache-2.0 许可。Microsoft.AnalysisServices 和 AdomdClient 保留对应 Microsoft 分发条款；WebView2 SDK 保留该版本包内 LICENSE 与 NOTICE，与单独安装的 WebView2 Runtime 区分。

日期表生成器所用第三方模板的原始许可保存在 `src/PbiUtils.Application/Calendar/Resources/calendar-LICENSE.txt`，发布许可包同时保留该原文。Bravo 参考代码的固定版本许可、主题改编、字体及图标原始版权也纳入离线声明；上游已声明的权利限制保持。

开发期样式检查使用 Microsoft 的 [vscode-css-languageservice](https://github.com/microsoft/vscode-css-languageservice) 与 [vscode-languageserver-textdocument](https://github.com/microsoft/vscode-languageserver-node/tree/main/textDocument), 均为 MIT 许可, 版本由 `web/package.json` 和锁文件固定。仅在本地源码检查与测试中复用 CSS/SCSS 诊断, 不进入产品前端或 C# 运行时, 不联网读取资源。

前端 UI 使用 Element Plus（MIT），组件按需导入，SCSS 主题沿用产品品牌变量；版本见 `web/package.json` 和锁文件，许可原文随页面资源保存于 `web/public/licenses/element-plus.txt`。代码编辑使用 CodeMirror 6 的 state/view/commands/language/search 与 Lezer 高亮（MIT），DAX、Power Query M、TMDL 的词法扩展由本项目实现；精确版本见 `web/package.json` 和锁文件，许可原文保存于 `web/public/licenses/` 并随页面资源发布。图标来源与许可见 `web/public/icons/LICENSE.txt`；J 品牌来源见 `BRANDING.md`。

示例生成业务规则、基础地理/姓名数据和完整 PBIP 模板迁移自用户所有的 [Power-BI-custom-sample-data 固定提交](https://github.com/jiaopengzi/Power-BI-custom-sample-data/tree/197aaf4b6c3fac28990814851640c21796d49263)。上游代码版权署名保留在翻译后的中文注释和来源说明中；不引入 Fyne、Go 构建链或旧版字体。模板的 Microsoft Fluent2 主题和 Power BI 导出资源保持原始来源，不作为原创或另行赋予开源许可。完整资源 SHA-256 清单和排除的缓存文件见 `src/PbiUtils.Infrastructure/SampleData/manifest.json` 与该目录 README。

内置新建模板 `blank` 是用户提供的 PBIP 素材，来源见 `src/PbiUtils.Infrastructure/Scaffolding/Assets/README.md`。原素材未附独立许可证，保留原作者注释、主题和报表内容，不据本项目 MIT 推定其第三方授权。此处记录来源与已知许可边界，不补发或宣称获得新增授权。

Windows 安装/卸载程序包含 NSIS 的安装器 stub 和 LZMA 压缩模块；其 COPYING 原文随许可包保留，包括适用于安装器的许可与 LZMA 特别例外。NSIS 编译器是构建工具，不作为产品运行依赖分发。

System.Security.Cryptography.ProtectedData（版本见根目录 [Directory.Packages.props](Directory.Packages.props)）：Microsoft/.NET，MIT；仅用于Windows当前用户本地密钥保护。来源 https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData ，源码/许可 https://github.com/dotnet/runtime 。
