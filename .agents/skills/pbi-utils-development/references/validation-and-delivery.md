# 验证、发布与交付

## 开发命令速查

在 Windows 上用 PowerShell 7 打开仓库根目录。运行 `.\run.ps1` 打开交互菜单，可输入动作名或编号，输入 `q` 退出。菜单按开发流程排列，编号从 0 顺序生成。直接执行使用 `.\run.ps1 -Choice <动作名>`，自动化建议用动作名并加 `-NonInteractive`。用 `.\run.ps1 -Help` 或 `.\run.ps1 -List` 查看完整动作与可用参数。

### 日常开发与验证

| 目的 | 命令 |
| --- | --- |
| 首次使用或依赖有变时还原 C# 和 Vue 依赖 | `.\run.ps1 -Choice setupAll` |
| 启动真实桌面开发环境 | `.\run.ps1 -Choice devDesktop` |
| 在浏览器中用模拟数据预览界面 | `.\run.ps1 -Choice devWeb` |
| 按需格式化 C# 和 Vue 源码 | `.\run.ps1 -Choice formatAll` |
| 只读检查 C# 和 Vue | `.\run.ps1 -Choice checkAll` |
| 只改 C# 时按需检查或运行相关项目测试 | `.\run.ps1 -Choice 'checkC#'` 或 `dotnet test tests/PbiUtils.UnitTests --no-restore --filter "FullyQualifiedName~<测试类>"` |
| 只改 Vue 时按需检查或运行相关文件测试 | `.\run.ps1 -Choice checkVue` 或 `pnpm --dir web exec vitest run <测试文件>` |
| 运行 C# 和 Vue 测试 | `.\run.ps1 -Choice testAll` |
| 构建 Release 应用产物 | `.\run.ps1 -Choice buildAll -Configuration Release` |
| 完整检查、测试并构建 Debug 应用产物 | `.\run.ps1 -Choice verifyAndBuild` |

`verifyAndBuild -Scope All` 交付后会注册 **pbi-utils (开发)**。重启 Power BI Desktop 后，可从“外部工具”连接当前报表。注册可能需要管理员权限。使用 `-NonInteractive` 时，若缺少系统目录写权限，登记阶段会报错，构建产物仍可使用。

开发中可以用 `devDesktop` 或 `devWeb` 预览，再运行本次改动对应的检查和测试。需要核验整个应用时运行 `verifyAndBuild`，需要交付安装器时运行 `release`。C# 定向测试按修改范围选用单元、CLI 或桌面测试项目；Vue 文件路径相对于 `web/`。

- `-Scope` 默认为 `All`。选 `Core` 时，C# 范围包含共享业务、CLI、Host、Windows 适配和相应测试。`All` 还覆盖桌面项目及其测试。单独的 Vue 动作没有 `-Scope` 参数。
- `-Configuration` 默认为 `Debug`，产物在 `artifacts/build/debug/win-x64`。选 `Release` 后，产物在 `artifacts/build/release/win-x64/<模式>`。
- `-DotNetMode` 每次选一种模式。Debug 构建默认 `with-dotnet`，Release 构建和两个打包动作默认 `without-dotnet`。
- `.\run.ps1 -Choice clean -WhatIf` 可预览要清理的生成目录。

### 生成轻量安装包

已有可用的 `Release/All`、`without-dotnet` 构建产物时，直接用本机 NSIS 的 `makensis` 打包。

```powershell
.\run.ps1 -Choice package -DotNetMode without-dotnet
```

打包从 `artifacts/build/release/win-x64/without-dotnet` 读取产物，对照根目录 `version` 核验数字版本和完整标签，并检查 SDK。安装器使用该目录中已构建的程序集和 Vue 资源。需要把本次源码修改纳入安装器时，先按需运行 `setupAll` 还原依赖，再用下面的命令完成 Release 检查、测试、构建和打包。

```powershell
.\run.ps1 -Choice release -DotNetMode without-dotnet
```

两条命令都将安装器 `*-setup.exe`、打包信息和 SHA256 写入 `artifacts/release/win-x64/without-dotnet`。轻量包省去 .NET 运行时，目标电脑需安装匹配的 x64 .NET Desktop Runtime。需要随包带上 .NET 时，选择 `with-dotnet` 并使用同模式产物。两种模式都需要 Evergreen WebView2 Runtime。

## 选择门禁

以本次影响范围决定检查. 文档变更核对规则、引用和 skill 格式; 产品源码、契约及发布行为变更执行对应回归与完整发布门禁. 不为提升报告结论重放未授权真实模型写入、登录或付费 API.

开发入口为 PowerShell 7 `run.ps1`。动作、参数、菜单顺序和工具要求由 `scripts/CommandRegistry.psm1` 及实际脚本维护。自动化用动作名，`-List` 或 `-Help` 可查询当前用法。

```powershell
pwsh -NoProfile -File ./run.ps1 -Choice verifyAndBuild -Scope All -Configuration Release -NonInteractive
```

`verifyAndBuild` 以并行门禁执行：启动前先核验所需工具与含测试项目的 C# 还原资产（`release` 按 All 范围），缺失时立即以 3 结束，不启动任何通道、预取或编译。随后 `checkC#`、`testC#`、`checkVue`、`testVue` 作为四个子进程通道与 C# 编译同时运行，各通道输出带阶段前缀。当前进程可用逻辑处理器少于 16 个（含亲和性与作业限制，例如 CI 小型运行器）时，同一组通道按 `checkC#`、`testC#`、C# 编译、`checkVue`、`testVue` 顺序逐个执行并保持正常优先级，避免争用放大用例耗时。`checkVue` 和 `testVue` 通过且同轮前端输入未变后执行 `buildVue`，全部通道通过后才交付发布目录。`-Scope All` 在交付成功后注册开发外部工具。需要生成 NSIS 安装包时，使用 `package` 或 `release`。任一阶段失败后不再开始新阶段，在途通道排空后按 checkC#、testC#、buildC#、checkVue、testVue、buildVue、package 的顺序报告首个失败阶段、原因和退出码。真实 Ctrl+C 使入口与各通道排空后以 130 结束，脚本约定见下方 PowerShell 条目。调整编排、并行阈值、优先级、挂起保护或构建耗时前阅读 [gate-performance.md](gate-performance.md)。`Scope` 和 `Configuration` 按各动作支持范围传递，`check` 动作只接收适用的 `Scope`。

首次准备、依赖或工具链变化、还原资产缺失时显式运行 `setupC#`、`setupVue` 或 `setupAll`。资产完整且依赖未变时可直接运行后续动作。独立产品诊断使用 CLI `doctor`。

- C# 工具链使用 `global.json` 的精确绑定; 缺失/无效/不匹配时报告配置、期望、可见和实际 SDK, 不自动改绑定、滚动或安装. SDK 查询的真实原生错误保留.
- clean 后必须先 `pwsh -NoProfile -File ./run.ps1 -Choice 'setupC#' -Scope All -NonInteractive`. NETSDK1004 是缺少还原前提, 还原后再运行 `verifyAndBuild` 或 `release`.
- check 只读, C# 使用建议级诊断并核报告; 无修复器也不能忽略失败. format 是显式源码写入; 不自动修复无关文件或禁用规则.
- `buildC#`、`buildAll` 和 `verifyAndBuild` 接受 `-DotNetMode with-dotnet|without-dotnet`，一次仅构建所选模式。Release 默认 `without-dotnet`，Debug 默认 `with-dotnet`。`buildC#` 只构建 C#，桌面资源未准备时应明确失败。`verifyAndBuild` 和 `buildAll` 在 C# 编译后只执行一次前端流程。依赖还原、格式化、清理、安装、上传和软件更新分别通过对应入口显式执行。开发登记由 `verifyAndBuild -Scope All` 在成功交付后执行。登记失败时保留已完成产物并返回非零退出码。非交互模式下需要预先具备系统目录写权限，交互模式仅在固定登记步骤按需提权。
- 参数错误 2、前置不足 3、执行异常 4、取消 130, 产品/其他原生非零码如实传递. stdout/stderr 保留, 不用 Invoke-Expression 或模块 exit 结束调用方, 入口脚本在自身 finally 中退出; try/finally 还原工作目录与临时环境.
- PowerShell 真实 Ctrl+C 与 `PowerShell.Stop()` 跳过 catch, 只执行 finally, `pwsh -File` 被停止时默认以 0 结束. 清理、回滚、释放锁和通道取消结果放在 finally, 以完成标记区分成功、失败与取消. 入口脚本预设 `$pbiExitCode = 130`, 赋值为 `Invoke-PbiEntry` 的返回值, 在 catch 中把意外异常记为 4, 并在 finally 中 exit; 新入口加入 `scripts/tests/entry-stop-tests.ps1`. 自动化回归用 `PowerShell.Stop()`, 取消验收另在独立控制台发送真实 Ctrl+C, 方法见 [gate-performance.md](gate-performance.md).
- C# 测试启动的 PowerShell 脚本: 多场景合集使用 `CliTests.ScriptCollectionTimeout`, 单条 CLI 仍为 30 秒. 隐藏控制台中的 pwsh 默认使用系统代码页, 输出断言文本前把 `[Console]::OutputEncoding` 设为 UTF-8, 并在 finally 恢复. Windows PowerShell 5.1 按 ANSI 读取无 BOM 文件, 由它执行且含非 ASCII 的脚本和模块保留 UTF-8 BOM; 修改后核对 BOM 与行尾和原文件一致. Vitest 默认 5 秒, 本项目统一 `testTimeout` 为 15 秒, 更慢的用例显式声明. 超时只防挂起, 不作性能断言.
- 成功以权威工具退出码和本轮结构化结果共同判定. C# 编译器警告按错误处理; testC# 使用本轮唯一 TRX, 零用例、全部跳过、缺失/损坏报告和平台错误均失败, 部分跳过单列 UNRUN. checkC# 的格式/分析器结果与 test/build 的编译结果分别说明.
- 前端测试默认显示控制台, 禁止 `.only`, 无用例或未处理异常失败. 未预期 console.warn/error、Vue 告警和错误必须失败; 故意负向诊断由测试在具体动作中按完整参数和次数声明, 未发生也失败. 不用全局静默或宽泛白名单掩盖问题.
- 每个文件系统模块显式建立终止错误策略; 枚举失败不得生成缺项清单. 发布后的清理单独失败也须非零, 与主错误同时出现时补充清理信息但保留首个阶段和退出码.
- 前端诊断守卫在业务依赖导入前安装, 覆盖清理和窗口 error/unhandledrejection; 结果报告器拒绝全部 skip/todo, 部分跳过单列 UNRUN. 正式构建均为独立Vite CLI, 日志器warn/warnOnce/error立即标记本进程失败且保留既有非零码, 原生晚期吞异常也不能exit0. error只输出, 由构建器继续抛解析/生成/写盘首因, 不另抛通用横幅覆盖. 该策略不承诺程序内多轮build() API的promise拒绝/退出码重置. 保留500kB阈值, 写盘前按所有压缩后chunk的UTF-8字节硬校验, 以真实CLI的入口/动态块/多字节超限及正常构建验证退出码与原因. 分包按共享依赖归属和纯字典数据边界, 用真实产物核字节、循环及重复模块, 保留原页面/草稿生命周期, 不用机械切片或提高阈值掩盖问题.

## 发布与运行时检查

- Debug 固定输出 `artifacts/build/debug/win-x64`; Release 仅输出选中的 `artifacts/build/release/win-x64/<模式>`. 每包内 `pbi-utils.exe`、`PbiUtils.Host.exe`、`PbiUtils.Desktop.exe` 与 `web/` 同根并共享依赖, 不保留 `cli/`、`host/`、`desktop/` 子目录入口. 两种部署各自执行真实 dotnet publish, 不能删文件伪造框架依赖版. staging 汇集本轮入口及完整前端后交付; `manifest.json` 只接受 `schemaVersion=3`、`layout=shared` 与 `deploymentMode=self-contained/framework-dependent`, 按 Core/All 范围及实际 runtimeconfig/deps 验证模式、必要入口、精确文件清单和 hash.
- staging 完成校验后才开始交付, 固定目录保留互斥、文件锁、覆盖回滚及首错退出码. 未选 Release 模式不更新, Debug/Release 互不覆盖; Debug 同一目录保存最近一次所选模式.
- 在原目录逐文件覆盖、删除过期文件、manifest 最后更新; 不重命名根目录或子目录, 不用更换发布目录掩盖锁问题.
- 替换前备份受影响旧文件, 普通失败回滚本轮文件; 回滚失败保留备份并说明产物状态, 不宣称多文件崩溃原子性.
- 真实 EXE/DLL 锁、链接、类型冲突或不可写应明确失败并核查所有者, 不改 ACL、不杀 Explorer/用户应用. 仅打开目录的句柄与真实文件锁分别判断.
- 影响发布后执行:

  ```powershell
  pwsh -NoProfile -File ./scripts/tests/published-smoke.ps1
  ```

- smoke 默认验证 Release without-dotnet, `-DotNetMode with-dotnet` 验证另一模式, `-Configuration Debug` 验证开发包, `-PublishRoot` 验证指定包. 直接运行产品 CLI/MCP, 保持环境隔离/manifest 校验, 核对自包含模式从发布目录加载 .NET、框架依赖模式从系统 shared 加载; 不恢复已移除的开发包装命令. 发布启动成功不等于无 SDK 物理机器或无外网实测.
- with-dotnet 自带 .NET, without-dotnet 需要匹配版本的 x64 .NET Desktop Runtime, 不能用普通 .NET/ASP.NET Runtime 或已安装 Power BI 推定前提满足. Evergreen WebView2 Runtime 对两包均为独立依赖. release=完整 Release/All 门禁 (逻辑处理器不足时通道逐个执行), 等待测试期间从本轮已写清单的 staging 预先生成安装包, 全部通过且发布目录交付后再交付安装包, 失败或取消时丢弃未交付安装包; package 仅从已验证产物编译 NSIS. 使用已有 makensis, 不安装开发工具或自动修改系统策略. 本地命令只生成安装包, 不签名/运行/上传. 用户启动安装器后才按需安装官方前置运行时并请求必要授权.
- NSIS 安装维护脚本兼容 Windows PowerShell 5.1. 检测实际 x64 桌面运行时, SDK 仅用于构建时解析官方下载 URL/hash; 已满足则不下载. 当前用户安装默认 `%LOCALAPPDATA%\Programs\pbi-utils`, 目录页允许选择安全本机路径并回填已有安装登记; 安装/修复/升级注册独立的 Power BI 外部工具并在卸载时仅移除自有登记, 必要时仅注册 helper 提权; 注册失败参与事务回滚. 升级/修复/降级拒绝、进程和文件占用、精确归属、失败恢复及卸载保留数据需行为测试. 编译安装器和模拟适配测试不等于真实干净机器安装/升级/卸载验收.

软件更新、安装替换、标签、ZIP 分发及 GitHub/Gitee 独立发布见 [release-and-updates.md](release-and-updates.md). 本地 release 生成安装器与远端 Release 发布是两个独立动作.

## 验收分层

涉及 MCP 时同时运行 `scripts/tests/mcp-published-smoke.ps1`（已由 published-smoke 调用）, 核实官方 stdio 发现、业务工具调用、默认只读拒绝、EOF排空和正式包依赖. `desktop_*` 的本机管道、共享后台与独立客户端生命周期、模型免确认及独立风险确认需另做真实 Windows 验收, 不把 SDK 客户端/原始协议测试称为第三方 AI 客户端已配置成功.

| 层级 | 能证明的范围 |
| --- | --- |
| 单元/契约/CLI测试 | 实际被测逻辑、取消门闩、输入边界和契约 |
| 浏览器 mock/生产资源显式模拟 | 模拟前端行为、布局、双语、交互 |
| 发布 WebView2 | 真实发布资源/宿主与实际执行的操作 |
| 隔离真实 Desktop | 本轮实际提交、计算、完整回读、保存/重开, 各动作单列 |
| 真实外部服务/网络 | 实际账户/云/服务、断链恢复等独立证据 |
| 物理异机/断网 | 仅该环境实际执行的结果 |

- 文档开始捕获 window.error/unhandledrejection, 同时记录 console/pageerror; 不漏 ResizeObserver ErrorEvent. 浏览器检查不替代真实 Windows 宿主.
- 双语、1422×800/较宽尺寸、侧栏展开/折叠按 UI 影响选取; 保留草稿、编辑器、焦点/搜索/输入法. 显示修复不为取证重复真实修改.
- 验证共享影响及相邻边界: 参数空/上限/重复、过期/目标变化、失败/取消/超时、重复请求/迟到回复、提交后排空、存储冲突/未知版本、恢复不重放. 用可控门闩验证时序, 不以加长 sleep 掩盖问题.
- 结果仅 PASS/FAIL/UNRUN, 附实际命令、退出码、平台、模拟标志和证据路径. 原始 FAIL 保留, 后续修复另记; UNRUN 不等于通过, 历史报告不继承本轮 PASS.

## 源码/资源清理与收尾

- 产品 clean 仅白名单 `artifacts`、`web/dist`、`web/coverage`、注册项目 bin/obj/TestResults. 校验根标记、规范路径及全链路无 reparse/link, 先整体验证、删除前重检, WhatIf 零删除. 不删除源码、依赖、锁文件、全局缓存、用户数据或旧项目.
- 源码整理另用用户确认的精确清单, 不把 product clean 当源码删除工具. 删除前复核候选当前身份/hash 与联动引用, 发现新增用户改动先保留并调整方案.
- 记录本任务创建的进程/PID/启动时间/命令行、窗口、端口、profile、测试产物/回执及用户数据基线. 结束时优先正常关闭, 排空在途工作, 确认端口/句柄/文件锁释放.
- 不按进程名批量结束 node/dotnet/WebView2/Explorer/Desktop, 不处理归属不明共享节点或用户进程. 终端不留在发布目录或后代目录. 清理失败或策略拒绝保留并如实报告, 不通过别名/替代工具绕过.
- 不回滚覆盖用户并发偏好; 精确识别本任务新增配置/历史/回执. 备份与保留副本范围需有归属证据.
- 交付列完成内容、文件变动、验证/证据、未完成项及原因、自有资源清理; 不擅自提交/推送, 不报告无证据的生产可用或完整替代.
