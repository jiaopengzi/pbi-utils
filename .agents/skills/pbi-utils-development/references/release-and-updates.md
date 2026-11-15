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
| 版本、标签、说明及归档 | `version`, `CHANGELOG.md`, `scripts/ReleaseAutomation.psm1`, `.gitalias/savetag.sh` |
| 远端构建与发布 | `.github/workflows/build.yaml`, `.github/actions/push-github/action.yml`, `.github/actions/push-gitee/action.yml` |

## 安装与更新职责

- `release` 执行完整 Release/All 门禁 (逻辑处理器充足时并行, 不足时通道逐个执行), 在等待测试期间从本轮已写清单的私有 staging 预先生成 NSIS 安装包, 全部通过且发布目录交付后再交付安装包, 失败或取消时丢弃未交付安装包; `package` 只使用同版本已验证 Release/All 产物. 两者默认 without-dotnet, 可选择 with-dotnet, 本地动作不签名、运行安装器或上传. 输出位于 `artifacts/release/win-x64/<模式>`.
- package 在独立副本中按当前许可来源重新生成许可与清单, 保持原构建目录不变. `release` 的本轮 staging 中根许可文档、来源清单及清单原文与仓库一致时沿用同轮生成的许可, 否则同样完整刷新. 向导在同一许可页展示项目及第三方许可全文. 实际依赖或资源身份变化需重新构建; 不用旧许可掩盖缺失或 hash 不一致. 许可维护入口为 `licenses/README.md`.
- 安装器面向当前用户, 默认 `%LOCALAPPDATA%\Programs\pbi-utils`, 通过 HKCU 安装登记回填已有目录, 并提供目录选择页. 路径必须通过本机绝对路径、链接、目录占用和安装身份校验; 默认目录不是唯一允许目录. 程序在安装根的 `app/`, 维护载荷独立存放.
- 安装/升级/同版本修复先核验归属、占用及完整备份, 再移除旧清单全部自有文件并写入新文件; 拒绝降级, 失败恢复本轮更改. 未知文件、用户修改的同名快捷方式/外部工具登记不能覆盖. 不强杀产品、MCP、Host 或 Power BI 进程.
- 卸载核对两处快捷方式归属并保留共享运行时, 默认保留应用用户数据. 交互式卸载可明确勾选删除当前 Windows 用户的 `.pbi-utils` 整个目录, 包括 `runtime`. 清理期间以同用户内核互斥锁阻止新应用会话打开运行协调文件, 核验已有租约占用、回执、文件和链接后按原句柄删除. 安装目录选择与应用用户数据根独立, 静默卸载保留用户数据.
- NSIS 在许可页后、目录选择和完整载荷解压前检查前置运行时. without-dotnet 检查匹配的 x64 .NET Desktop Runtime, 两种模式均检查 Evergreen WebView2. 缺失时可显式安装、复检或打开固定微软页面; 实际签名、hash、版本和退出状态须核验. 微软安装程序启动后等待退出和复检, 不强行中断.
- 静默安装先要求 `/ACCEPTMICROSOFTTERMS=1`, 只检查已有运行时, 缺失返回前置条件错误, 不隐式下载. 系统目录登记无权限时非交互明确失败. 不将静默安装当作绕过许可或系统授权的入口.
- 开发外部工具 `pbi-utils-dev.pbitool.json` 与安装入口 `pbi-utils.pbitool.json` 独立. `verifyAndBuild -Scope All` 成功交付后更新开发登记. 安装器事务维护安装登记, 仅登记 helper 按需提权. 外部工具传入 `--server`/`--database`, GUI 必须核实本机会话、回环引擎及数据库后连接, 不因参数存在自动修改模型.

## 软件更新

- 产品更新在设置页, 安装器只负责前置环境与安装维护. 自动检查默认开启, 默认 GitHub, 可切换 Gitee; 各来源最多每 24 小时自动检查一次, 手动可重查. 持久化由 `LocalProductUpdateStore` 负责, 不与 UI 偏好另建相互矛盾的来源.
- 固定仓库为 jiaopengzi/pbi-utils. 当前发布使用 `vX.Y.Z` 和 `vX.Y.Z-rcN`, 已发布的 `desktop-v<数字版本>` 和 `desktop-v<数字版本>-v2.<正整数>` 标签继续可查询. 排除草稿, 按数字版本降序排列; 同版本正式优先, 同发布类型下现行标签优先, 同格式下预发布轮次按数字降序.
- `available` 仅由最新发布数字版本大于当前版本判定; 同一数字版本的预发布轮次可通过完整标签核对. 当前、最新及列表内的发布版本字段均返回精确标签. 更新检查升级时保留更新偏好, 重新获取旧数字版本缓存.
- 设置页软件更新的当前版本和运行环境的应用版本使用程序集内嵌的完整发布标签, 最新发布版本使用所选来源返回的精确标签. 同时显示状态和本地检查时间. 发布页按钮不依赖检查成功, 始终打开所选来源的 releases 根页. 无发布、网络失败与已经最新分别呈现; 不自动下载、执行安装器或重启.
- CLI `pbi-utils updates check --source github|gitee --json` 和只读 MCP `updates_check` 复用共享查询, 返回有界发布列表及说明截断标记. MCP 不开放浏览器或安装操作. 两来源分别查询, 不跨源拼接结果或静默切源; 分页/响应预算超限明确失败.

## 标签、归档与工作流

- 根目录 `version` 跟踪当前源码的完整构建标签, 文件只含一行. 普通构建和打包从该文件派生产品数字版本. `main` 的新正式标签使用 `vX.Y.Z`, `v2` 的新预发布标签使用 `vX.Y.Z-rcN`, `N` 为正整数.
- 准备发布时, 在 `CHANGELOG.md` 顶部填写 `## [<精确标签>] - YYYY-MM-DD` 及实际变更说明, 确保对应章节唯一、日期有效且正文非空. 在 `main` 或 `v2` 运行 `git savetag --prepare`, 脚本校验工作区的最新章节和分支, 只生成 `version`, 不访问远端、不暂存也不提交. 审阅并提交这两个文件后再发版.
- `git savetag --dry-run` 校验干净分支和 HEAD, 从该提交读取 `version` 与 `CHANGELOG.md`, 核对两者及拟发布标签一致并预览发布. 无参数运行会创建注释标签并原子推送精确分支与标签. `git savetag` 使用显式配置的本地别名, 也可直接以 Git Bash 运行 `bash ./.gitalias/savetag.sh` 并传入相同参数. 推送失败保留已建标签, 核实远端状态后再处理. 已有标签不可覆盖或重用.
- 当前 CI 分发 `pbi-utils-<发布标签>-win-x64-without-dotnet.zip`, ZIP 根仅包含对应的 `pbi-utils-<发布标签>-win-x64-without-dotnet-setup.exe`. 例如 `v0.1.0-rc1` 的 ZIP 为 `pbi-utils-v0.1.0-rc1-win-x64-without-dotnet.zip`. 历史 `desktop-v` 标签的附件复验使用原有数字版本文件名. 本地 JSON、SHA256 侧文件、锁与中间产物不入包. 归档前后核验真实 EXE 字节, 发布说明的 SHA256 对应该 EXE.
- `build.yaml` 在新格式发布标签推送时构建发布, 核验触发标签与其目标提交中的 `version` 和 CHANGELOG 最新章节一致, 并检查提交属于对应来源分支. `main`/`v2` PR 与不指定已有标签的手动运行只做相应构建验证. `v2` 修改 `.github/**` 的提交可通过精确 `[release-retry: <预发布标签>]` 标记触发附件复验.
- `build` 负责门禁、NSIS 包、CLI/MCP smoke 和唯一发布产物; `push-github`、`push-gitee` 分别只依赖 build, 可并行执行, 复用同一 ZIP、发布说明和完整性清单. 产物上传、远端公开附件回读与本地构建成功分开记录.
- 指定已有 `release_tag` 的重试受 `v2` 和预发布限制. 从原标签读取精确发行章节并下载校验已有 ZIP, 已有历史标签无需补 `version`. 此流程复用已有安装包, 跳过 GitHub 发布, 只执行 Gitee 复验/发布. 不创建或移动标签.
- Gitee 按标签串行保护. 只读查询/公开下载可对暂时性网络故障有限重试; 创建、修改和上传不自动重试, 超时后先核对远端事实. 已有相同附件校验后复用, 冲突明确失败. 不用令牌探测或无限网络重试替代实际发布证据.
- `.github/workflows/sync_gitee.yaml` 仅从 main 同步代码分支, 不负责标签或 Release. 非 main 手动运行跳过, 不使用同步密钥. 工作流网页手动入口可见性取决于默认分支登记, 本地 YAML 存在不代表网页已经可用.

文档修订只核对以上入口与引用; 修改安装、更新或发布代码时运行相应行为测试与完整门禁. 编译安装器、隔离事务测试、真实安装/升级/卸载和异机环境分别报告, 不从一次 package 成功推定全部通过.
