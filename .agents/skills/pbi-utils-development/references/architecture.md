# 项目结构与依赖边界

涉及模块定位、依赖、入口契约或工具链时读取本文件. 模型写入细则继续按任务读取其他参考文件, 不将本文件当作运行时提示词发送给产品模型.

## C# 项目

| 项目 | 职责 | 允许的直接项目引用 |
| --- | --- | --- |
| `PbiUtils.Core` | 稳定输入、结果与错误契约, 无 UI、存储或 SDK 能力 | 无 |
| `PbiUtils.Application` | 共享业务用例、校验、格式化、策略与适配接口 | `Core` |
| `PbiUtils.Infrastructure` | 文件/TOM 离线适配、持久化、提供方与 Service 外部适配、日志实现 | `Application` |
| `PbiUtils.Windows` | Desktop 发现与身份、在线模型适配、官方 Bridge、文件保存、SSAS Windows 身份 | `Application`, `Infrastructure` |
| `PbiUtils.Cli` | 产品控制台入口与依赖组合 | `Infrastructure` |
| `PbiUtils.Host` | 无 UI 共享业务后台、每客户端上下文、写入协调与排空 | `Windows`, `Infrastructure` |
| `PbiUtils.Desktop` | WinForms/WebView2 外壳、可信 UI 回调与受限系统集成 | `Windows`, `Infrastructure` |

- 项目仅支持 Windows 安装、运行与开发验证. Windows 目标框架不代表共享用例可以依赖窗口、进程或桌面 SDK.
- Core 不依赖文件系统、UI、WebView2、Tabular 模型 SDK、LLM SDK 或日志库. Application 不直接加载 Windows、TOM 或文件适配; 通过应用契约请求能力.
- TOM 离线读写位于 Infrastructure, Desktop 在线模型及进程适配位于 Windows. 不沿用“所有模型 SDK 仅 Windows 使用”或“Windows 只读”的旧描述.
- CLI、Desktop 与 Host 是独立入口, CLI/GUI 经受限本机通道请求 Host; 不互相引用产品程序集. 业务组合与客户端上下文由 Host 拥有, MCP/GUI/相关 CLI 复用同一共享用例. Core 构建范围含 CLI、Host 及 Windows 适配, 不含桌面 UI/WebView2/web.
- 依赖约束同时检查项目文件与编译程序集的实际类型引用. 不用关闭规则或新增空接口绕过边界.

入口: `src/*/*.csproj`, `tests/PbiUtils.UnitTests/ArchitectureTests.cs`.

## 模块地图

| 领域 | 共享用例/策略 | 外部适配与显示入口 |
| --- | --- | --- |
| 基础诊断 | `src/PbiUtils.Application/Diagnostics/DoctorService.cs` | CLI、桥接及关于页 |
| 模型连接与摘要 | `src/PbiUtils.Application/Connections/ModelConnectionService.cs`, `src/PbiUtils.Application/Connections/PowerBiServiceConnection.cs` | `src/PbiUtils.Windows/`, `src/PbiUtils.Infrastructure/Connections/PowerBiService/PowerBiCloudPolicy.cs` |
| 模型编辑与保存 | `src/PbiUtils.Application/Models/Editing/ModelEditService.cs`, `src/PbiUtils.Application/Models/Editing/ToolWriteAuthorization.cs` | `src/PbiUtils.Windows/Models/Connected/DesktopModelEditing.cs`, `src/PbiUtils.Windows/Desktop/Files/DesktopFileSync.cs` |
| 新建 | `src/PbiUtils.Application/Scaffolding/` | `src/PbiUtils.Infrastructure/Scaffolding/`, `web/src/features/scaffold/` |
| 持续模型配置 | `src/PbiUtils.Application/Models/Configuration/` | `src/PbiUtils.Infrastructure/Models/Configuration/`, `web/src/features/configuration/` |
| 模板度量 | `src/PbiUtils.Application/TemplateMeasures/` | `src/PbiUtils.Infrastructure/TemplateMeasures/` |
| 日期与格式化 | `src/PbiUtils.Application/Calendar/ScaffoldCalendarProgramCompiler.cs`, `src/PbiUtils.Application/Code/Dax/DaxFormatter.cs`, `src/PbiUtils.Application/Code/PowerQuery/PowerQueryFormatter.cs` | `src/PbiUtils.Infrastructure/Calendar/CalendarProgramPlanner.cs`, `web/src/features/code/` |
| 示例数据 | `src/PbiUtils.Application/SampleData/` | `src/PbiUtils.Infrastructure/SampleData/`, `web/src/features/sample/` |
| UDF | `src/PbiUtils.Application/Udf/` | `src/PbiUtils.Infrastructure/Udf/`, 共享新建/模型配置页面 |
| 宿主 UI 与桥接 | `src/PbiUtils.Host/Bridge/`, `src/PbiUtils.Infrastructure/Gui/`, `src/PbiUtils.Infrastructure/Hosting/` | `web/src/bridge/`, `web/src/ui/`, `web/src/i18n/` |
| 共享可靠执行 | `src/PbiUtils.Infrastructure/Execution/` | GUI、CLI 与 MCP 共用回执及终态; MCP 可发现子集仍在 `Mcp/` |
| 开发与验证 | `run.ps1`, `scripts/` | `tests/`, `web/tests/**/*.test.ts`, `web/tests/` |

文件数量与历史测试总数不是架构要求. 定位功能时查实际调用、资源嵌入、CLI 分发、桥接注册和测试引用, 不凭文件名含 demo、mock、prototype 判定废弃.

## 必须保留的入口契约

- `doctor` 是 CLI 与关于页复用的正式诊断能力. 早期 `hello`、`agent hello --provider mock` 及其桥接已获准移除, 不为测试或兼容重新引入; 显式开发 mock、测试替身和正式 MCP 保持各自边界.
- `src/PbiUtils.Core/OperationEvent.cs` 的通用 `OperationEvent` 服务所有宿主业务进度和桥接, 不是诊断专属代码. 保持请求 ID、连续序号、时间戳、开始/进度及唯一完成/失败/取消终态, 保持通用错误响应与稳定错误码.
- JSON 模式 stdout 恰好一个完整 JSON 文档, 进度与诊断走 stderr, 不混入日志或 ANSI. `--help`/`--version` 保持元命令语义. 退出码为成功 0、参数 2、前置条件 3、执行失败 4、取消 130.
- 实际桥接白名单以 `src/PbiUtils.Host/Bridge/Protocol/BridgeMethods.cs` 为准. 新增/删除 handler 同步 C# 分发、TypeScript 方法映射和 `tests/contracts/bridge.schema.json`; 由 `tests/PbiUtils.UnitTests/BridgeContractConsistencyTests.cs` 核验. 不把早期六方法列表作为当前全集.
- 前端生产构建缺少宿主桥接必须报错. 显式浏览器预览才使用 mock, 不能自动伪装成功.

MCP 入口和本机宿主通道见 [local-mcp.md](local-mcp.md). CLI 引用官方 MCP SDK, Host 组合共享业务, Desktop 不引用 CLI/Host 产品程序集, 三者经受限本机协议协作. 机器 schema 在构建期嵌入, 产品运行不读取测试目录.

## 配置是版本唯一来源

| 内容 | 权威位置 |
| --- | --- |
| .NET SDK 完整版本及滚动策略 | `global.json` |
| 目标框架、产品/程序集版本和公共构建属性 | `Directory.Build.props`, `Directory.Build.targets` |
| NuGet 精确版本及解析结果 | `Directory.Packages.props`, 各项目 `packages.lock.json` |
| Node 精确版本 | `web/package.json` 的 `engines.node` |
| pnpm 精确版本 | `web/package.json` 的 `packageManager` |
| 前端依赖及解析结果 | `web/package.json`, `web/pnpm-lock.yaml` |
| 品牌来源、固定上游及资源规则 | `BRANDING.md`, `web/public/branding/` |

- 文档与脚本读取或引用配置, 不复制一份容易过期的当前版本数字. 普通功能改动不顺带升级依赖.
- SDK 绑定缺失或不一致时报告配置、期望/可见/实际版本和修复建议; 不自动安装、更改绑定或静默滚动. 发布自包含运行时与开发 SDK 是不同边界.
- `run.ps1` 是推荐开发入口, 产品 CLI 是独立程序. 发布、安装器、更新、外部工具注册、付费 API 验收等按用户当前授权范围处理, 不新增一套通用审批流程.
- `.docs` 保持用户既有忽略约定, 不修改忽略文件. 机器契约与构建不能依赖私有 `.docs` 存在. 长任务计划、发现、进度和证据在 `.docs` 留存, 不硬依赖未安装的历史 checkpoint 技能、未知 hook 或不可验证的 token 阈值.
