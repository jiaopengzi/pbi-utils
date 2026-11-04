# 安装器、软件更新与远端发布

修改安装维护、软件更新、发布标签或 GitHub/Gitee 分发时读取. 本文说明现有流程, 不授予安装、系统登记、提交、推送或创建 Release 的权限. 完整构建与真实验收分层见 [validation-and-delivery.md](validation-and-delivery.md).

## 权威入口

| 事项 | 当前源码/契约 |
| --- | --- |
| 开发动作和参数 | `scripts/CommandRegistry.psm1`, `run.ps1 -List/-Help` |
| NSIS 打包 | `scripts/package.ps1`, `scripts/installer/installer.nsi` |
| 前置运行时 | `scripts/installer/Prerequisites.psm1`, `scripts/installer/Ensure-Prerequisites.ps1` |
| 安装归属、文件替换、回滚 | `scripts/installer/Installation.psm1`, `scripts/installer/InstallationPayload.psm1`, `scripts/installer/InstallationShell.psm1` |
| Power BI 外部工具 | `scripts/installer/ExternalToolRegistration.psm1`, `scripts/installer/ExternalToolDescriptor.psm1` |
| 更新查询与排序 | `src/PbiUtils.Application/Updates/`, `src/PbiUtils.Infrastructure/Updates/` |
| 更新 UI、CLI、MCP | `web/src/app/SoftwareUpdatePanel.vue`, `src/PbiUtils.Infrastructure/Commands/ProductUpdateCommands.cs`, `src/PbiUtils.Infrastructure/Mcp/McpBusinessRuntime.Core.cs` |
| 版本、标签、说明及归档 | `Directory.Build.props`, `CHANGELOG.md`, `scripts/ReleaseAutomation.psm1`, `.gitalias/savetag.sh` |
| 远端构建与发布 | `.github/workflows/build.yaml`, `.github/actions/push-github/action.yml`, `.github/actions/push-gitee/action.yml` |

## 安装与更新职责

- `release` 执行完整 Release/All 门禁再生成 NSIS 安装包; `package` 只使用同版本已验证 Release/All 产物. 两者默认 without-dotnet, 可选择 with-dotnet, 本地动作不签名、运行安装器或上传. 输出位于 `artifacts/release/win-x64/<模式>`.
- package 在独立副本中按当前许可来源重新生成许可与清单, 保持原构建目录不变. 实际依赖或资源身份变化需重新构建; 不用旧许可掩盖缺失或 hash 不一致. 许可维护入口为 `licenses/README.md`.
- 安装器面向当前用户, 默认 `%LOCALAPPDATA%\Programs\pbi-utils`, 通过 HKCU 安装登记回填已有目录, 并提供目录选择页. 路径必须通过本机绝对路径、链接、目录占用和安装身份校验; 默认目录不是唯一允许目录. 程序在安装根的 `app/`, 维护载荷独立存放.
- 安装/升级/同版本修复先核验归属、占用及完整备份, 再移除旧清单全部自有文件并写入新文件; 拒绝降级, 失败恢复本轮更改. 未知文件、用户修改的同名快捷方式/外部工具登记不能覆盖. 不强杀产品、MCP、Host 或 Power BI 进程.
- 卸载只处理本安装拥有的内容, 保留应用用户数据和共享运行时. 两个快捷方式选项独立保存本轮选择, 卸载核对两处归属; 安装目录选择与应用用户数据根是不同概念.
- NSIS 在许可页后、目录选择和完整载荷解压前检查前置运行时. without-dotnet 检查匹配的 x64 .NET Desktop Runtime, 两种模式均检查 Evergreen WebView2. 缺失时可显式安装、复检或打开固定微软页面; 实际签名、hash、版本和退出状态须核验. 微软安装程序启动后等待退出和复检, 不强行中断.
- 静默安装先要求 `/ACCEPTMICROSOFTTERMS=1`, 只检查已有运行时, 缺失返回前置条件错误, 不隐式下载. 系统目录登记无权限时非交互明确失败. 不将静默安装当作绕过许可或系统授权的入口.
- 开发外部工具 `pbi-utils-dev.pbitool.json` 与安装入口 `pbi-utils.pbitool.json` 独立. 前者由成功的 `all -Scope All` 更新; release/package 不修改它. 安装器事务维护后者, 按需仅登记 helper 提权. 外部工具传入 `--server`/`--database`, GUI 必须核实本机会话、回环引擎及数据库后连接, 不因参数存在自动修改模型.

## 软件更新

- 产品更新在设置页, 安装器只负责前置环境与安装维护. 自动检查默认开启, 默认 GitHub, 可切换 Gitee; 各来源最多每 24 小时自动检查一次, 手动可重查. 持久化由 `LocalProductUpdateStore` 负责, 不与 UI 偏好另建相互矛盾的来源.
- 固定仓库为 jiaopengzi/pbi-utils. 接受公开 `desktop-v<数字版本>` 和 `desktop-v<数字版本>-v2.<正整数>` 标签, 排除草稿与旧产品标签. 按数字版本降序、同版本正式优先、预发布轮次数字降序排列, 不依赖 API 默认顺序.
- 当前程序只提供数字版本, `available` 仅由最新发布数字版本大于当前版本判定; 不能承诺识别已安装的 v2 轮次或同数字版本预发布之间的升级. 列表保留预发布身份, 用户可在发布页选择.
- GUI 显示当前/最新版本、状态和本地检查时间; 不展示完整发布列表或说明. 发布页按钮不依赖检查成功, 始终打开所选来源的 releases 根页. 无发布、网络失败与已经最新分别呈现; 不自动下载、执行安装器或重启.
- CLI `pbi-utils updates check --source github|gitee --json` 和只读 MCP `updates_check` 复用共享查询, 返回有界发布列表及说明截断标记. MCP 不开放浏览器或安装操作. 两来源分别查询, 不跨源拼接结果或静默切源; 分页/响应预算超限明确失败.

## 标签、归档与工作流

- 产品数字版本唯一来源为 `Directory.Build.props`. main 的稳定标签为 `desktop-v<版本>`, v2 流程预发布为 `desktop-v<版本>-v2.N`. 标签须匹配版本与来源分支; 不自动改版本或移动已发布标签.
- CHANGELOG 最新章节在前, 使用 `## [<精确标签>] - YYYY-MM-DD`, 对应章节唯一、日期有效且非空. 不将历史说明或其他标签的内容用于当前发布.
- `.gitalias/savetag.sh` 读取已提交 CHANGELOG 的最新章节, 核验干净分支/HEAD 和版本. `bash ./.gitalias/savetag.sh --dry-run` 只预览; 无参数会创建注释标签并原子推送精确分支与标签, 属于发布动作. `git savetag` 依赖用户显式配置的本地别名, 不假设已安装或擅自写 Git 配置. 推送失败保留已建标签并核实远端, 不覆盖/重用标签.
- 当前 CI 只分发 `pbi-utils-<版本>-win-x64-without-dotnet.zip`, ZIP 根仅包含对应的 `pbi-utils-<版本>-win-x64-without-dotnet-setup.exe`. 本地 JSON、SHA256 侧文件、锁与中间产物不入包. 归档前后核验真实 EXE 字节, 发布说明的 SHA256 对应 EXE, 不是 ZIP.
- `build.yaml` 在 desktop 标签推送时构建发布; main/v2 PR 与不指定已有标签的手动运行只做相应构建验证. 当前还允许 v2 修改 `.github/**` 的提交通过精确 `[release-retry: <预发布标签>]` 标记触发附件复验; 不能将其概括为所有分支 push 都构建或发版.
- `build` 负责门禁、NSIS 包、CLI/MCP smoke 和唯一发布产物; `push-github`、`push-gitee` 分别只依赖 build, 可并行执行, 复用同一 ZIP、发布说明和完整性清单. 产物上传、远端公开附件回读与本地构建成功分开记录.
- 指定已有 `release_tag` 的重试受 v2/预发布限制: 从原标签读取规范并下载校验已有 ZIP, 不重新构建, 跳过 GitHub 发布, 只执行 Gitee 复验/发布. 不创建或移动标签, 不把另一次构建替换成原附件.
- Gitee 按标签串行保护. 只读查询/公开下载可对暂时性网络故障有限重试; 创建、修改和上传不自动重试, 超时后先核对远端事实. 已有相同附件校验后复用, 冲突明确失败. 不用令牌探测或无限网络重试替代实际发布证据.
- `.github/workflows/sync_gitee.yaml` 仅从 main 同步代码分支, 不负责标签或 Release. 非 main 手动运行跳过, 不使用同步密钥. 工作流网页手动入口可见性取决于默认分支登记, 本地 YAML 存在不代表网页已经可用.

文档修订只核对以上入口与引用; 修改安装、更新或发布代码时运行相应行为测试与完整门禁. 编译安装器、隔离事务测试、真实安装/升级/卸载和异机环境分别报告, 不从一次 package 成功推定全部通过.
