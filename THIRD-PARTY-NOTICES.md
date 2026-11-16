# 第三方来源与许可

pbi-utils 自有代码采用 [MIT 许可](LICENSE)。第三方组件和资源适用各自的许可与版权声明。

## 随包提供的许可

发布目录中的 `THIRD-PARTY-NOTICES.txt` 提供精确版本索引及许可、NOTICE、COPYING 全文。`licenses/` 保存原文和校验清单。NuGet 索引按实际发布依赖生成。前端索引覆盖已安装的生产依赖、peer 依赖及其中已安装的可选依赖，保留打包优化前的完整许可覆盖。

- **Microsoft Analysis Services**。模型操作使用 Microsoft.AnalysisServices（AMO/TOM）和 Microsoft.AnalysisServices.AdomdClient。两者的英文及 Microsoft 提供的中文条款均随包保留。安装向导按实际分发组件显示接受范围。
- **身份认证、日志与本地 MCP**。MSAL 用于 Power BI Service 认证，ProtectedData 用于 Windows 当前用户的数据保护，Serilog 用于业务日志。本地 MCP 使用官方 C# SDK，其完整 LICENSE 保留 Apache-2.0 和既有 MIT 贡献的条款。SDK 的 Microsoft.Extensions.AI.Abstractions 随依赖提供。
- **Windows 桌面**。两个版本均包含 .NET apphost 启动代码及 WebView2 SDK。自包含版另附 .NET 与 Windows Desktop 运行时，并提供随包 Windows 原生 DLL 适用的 .NET Library 和 Windows SDK 条款。单独安装的 .NET Desktop Runtime、WebView2 Runtime 和 Power BI Desktop 各自适用其条款。详见 [Microsoft 的 Windows 文件级许可说明](https://github.com/dotnet/core/blob/main/license-information-windows.md)。
- **前端**。界面使用 Vue 和 Element Plus，编辑器使用 CodeMirror 6 与 Lezer，包括 Markdown 编辑支持。UDF 文档用 Marked 渲染，并用 DOMPurify 净化。DOMPurify 采用双许可中的 Apache-2.0 选项。许可包也收录 Vite、Vue 插件与 Rolldown 注入的前端辅助代码的许可。
- **安装器**。Windows 安装与卸载程序包含 NSIS 的安装器代码和 LZMA 压缩模块。随包提供对应编译版本的 COPYING 原文及适用的 LZMA 特别例外。

## 内嵌资源与改编来源

相对链接指向源码仓库。安装后的许可与声明可在 `app/THIRD-PARTY-NOTICES.txt` 离线查阅。

| 用途 | 来源与许可 |
| --- | --- |
| Power BI Service 环境发现、连接和架构读取 | 参考 [Bravo 固定提交](https://github.com/sql-bi/Bravo/tree/fbc2b12950ddb4344ae71f16de8861407c32050e) 中的云配置、客户端与架构读取实现，保留对应 MIT 原文。 |
| 日期表生成 | 日期字段定义参考 `fxCalendar.m`。生成器输出自包含的 Power Query M，需要时内嵌固定农历年规则。保留上游 [MIT 原文](src/PbiUtils.Application/Calendar/Resources/calendar-LICENSE.txt)。 |
| 零售示例 | 业务规则、地理及姓名数据和示例 PBIP 资源来自 [Power-BI-custom-sample-data 固定提交](https://github.com/jiaopengzi/Power-BI-custom-sample-data/tree/197aaf4b6c3fac28990814851640c21796d49263)，保留上游 MIT 原文。报表随附 Microsoft `CY21SU07` 基础主题和原有报表资源，见 [示例资源说明](src/PbiUtils.Infrastructure/SampleData/README.md)。 |
| 新建工程的 `blank` 模板 | 用户提供的 PBIP 素材，保留 Microsoft `Fluent2-CY26SU09` 主题及原有报表内容。素材暂无单独的许可文件，来源见 [新建资源说明](src/PbiUtils.Infrastructure/Scaffolding/Assets/README.md)。 |
| DAX、Power Query M 配色 | 参照 `highlightjs-dax` 1.1.0 和 `highlightjs-m` 1.0.0 的 MIT 主题。对应原文随本地页面资源提供。 |
| 代码字体 | JetBrains Mono Medium，SIL Open Font License 1.1。字节来自 blog-client 提交 `4df1e327`，SHA-256 为 `086c48dfbea9ddaff1320f7e09399b8e2924e88ce67453721255db3bdbb5a353`。 |
| 界面图标与产品标识 | 本地图标保留 Lucide（ISC 和 Feather 的 MIT）及 VSCode Icons 中 Power BI 图标的许可和署名。详见 [图标来源](web/public/icons/LICENSE.txt) 和 [品牌资源](BRANDING.md)。 |

许可原文、固定来源及校验值的维护方法见 [离线许可来源](licenses/README.md)。
