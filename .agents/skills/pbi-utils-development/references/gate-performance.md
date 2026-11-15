# 门禁编排与耗时优化

调整 `verifyAndBuild`/`release` 的阶段编排、并行通道、优先级、测试并发、挂起保护或打包顺序, 以及排查门禁耗时、取消和残留时阅读. 门禁语义、交付与证据规则以根 `AGENTS.md` 和 [validation-and-delivery.md](validation-and-delivery.md) 为准. 本文说明当前编排、优化方法、注意事项和验证清单, 不记录单次耗时.

## 实现入口

| 关注点 | 位置 |
| --- | --- |
| 规范阶段顺序、通道与优先级、并行阈值 | `scripts/CommandRegistry.psm1` 的 `$script:GateStages`、`$script:GateLanes`、`$script:ParallelGateMinProcessors`、`Test-PbiParallelGate` |
| 门禁编排、等待与失败汇总 | `Invoke-PbiVerifyAndBuild`、`Start-PbiGateStage`、`Assert-PbiGateRunning`、`Wait-PbiGateLanes`、`Stop-PbiVerificationGate`、`Resolve-PbiGateFailure`、`Complete-PbiFrontendGate` |
| 快速失败看护 | `scripts/GateWatchdog.psm1` 的 `Invoke-PbiGateWatchdog`、`Invoke-PbiGateAbort`、`Read-PbiLaneResult`; 主流程原生命令在门禁中由 `Invoke-PbiGateNative` 持有句柄运行, 构建阶段之间的检查点为 `Assert-PbiActiveGate` |
| 通道子进程 | `scripts/lane.ps1` 调用 `Invoke-PbiEntry -Lane`, 结果写入 `%TEMP%\pbi-utils-gate-<32 位十六进制>\<32 位十六进制>.json` |
| 构建阶段与内部回调 | `scripts/build.ps1` 的 `AfterCompile`、`BeforeDelivery` |
| release 预生成、预取与许可跳过 | `Invoke-PbiRelease`、`Invoke-PbiInstallerStep`、`scripts/package.ps1`、`scripts/InstallerMetadata.psm1` |
| 测试编译与输出隔离 | `scripts/test.ps1`、`scripts/CSharpTestProjects.proj`、`tests/PbiUtils.Cli.Tests/PbiUtils.Cli.Tests.csproj` |
| 挂起保护 | `CliTests.ScriptCollectionTimeout`、`web/vite.config.ts` 的 `testTimeout` |
| 编排回归 | `scripts/tests/` 下的 `InlineGateLanes.ps1`、`contracts.ps1`、`gate-failure-tests.ps1`、`frontend-check-tests.ps1`、`external-tool-workflow-tests.ps1`、`publish-tests.ps1`、`installer-package-tests.ps1 -Scenario DeferredPackage`、`entry-stop-tests.ps1` |

## 当前编排

- 启动前: 主流程核验所需工具与含测试项目的 C# 还原资产 (release 按 All 范围), 前置不足立即以 3 结束, 不启动通道、预取或编译. 通道内仍会复核, 但不能代替这一步.
- 并行模式 (当前进程可用逻辑处理器不少于 16 个): 先启动 checkC# 与 testC# 通道, 取得前端输入指纹后启动 checkVue 与 testVue, 同时执行 C# 发布. 发布、共享布局和许可完成后等待两个 Vue 通道, 指纹一致才复用本轮 lint 执行前端构建. 前端并入 staging 并写清单后执行交付前回调 (release 在此预生成安装包), 再等待全部通道, 最后交付发布目录; release 随后交付安装包.
- 逐个模式 (少于 16 个): checkC#、testC#、C# 发布与许可、checkVue、testVue、前端构建、清单与交付依次执行. 失败、取消与交付语义和并行模式相同.
- 快速失败: 首个真实通道启动时在独立 runspace 启动看护, 每 100 毫秒检查通道进程. 通道以非 0 且非 130 退出, 或以 0 退出却没有一致的结果记录时, 看护记录首个失败来源并立即终止全部通道进程树 (`Process.Kill(true)` 作用于本轮启动并持有句柄的通道, 已退出的通道只清理仍挂在其下的子进程). 主流程在门禁中以持有根进程句柄的方式运行原生命令 (别名先解析到最终目标; `.exe` 直接启动, 批处理由 CreateProcess 经 cmd.exe 执行, `.ps1` 经当前 pwsh `-File` 执行; 函数等进程内命令保持原方式), 运行期间及输出结束后都每 100 毫秒检查门禁, 只结束本命令自有的进程树: 其他阶段失败时抛出带 `PbiGateTerminated` 标记的异常, 通道以 130 取消时抛出取消异常使门禁排空. 只有结束时根进程仍在运行且退出码为 -1 才判定为被结束, 此前已自行结束的命令按真实退出码汇总. 主流程以非取消失败结束时同样立即终止在途通道. 启动新通道、C# 编译、构建阶段之间及交付前回调前均检查已有失败, 看护未运行时检查点按门禁自身失败报告. 主流程正在执行 PowerShell 步骤时于下一个检查点退出, 最长延迟为该步骤剩余时间. 汇总时被终止的阶段单独列出, 真实失败按规范顺序报告首个. 检查点与收尾同样取消优先: 已结束通道中有以 130 取消的, 检查点按取消抛出, 门禁选择排空而不是终止. 取消时在同一把锁中设置排空标记, 看护不再终止, 入口等待各通道响应取消.
- 优先级: 并行模式下 checkC# 为 BelowNormal, 其余通道为 Normal; Core 范围的 checkC# 逐项目格式检查本身最长, 保持 Normal; 逐个模式下各通道独占本机, 均为 Normal.
- 计时输出: `构建计时` 依次为 prepare、sdk-publish、shared-layout、licenses、frontend-gate、manifest、verification-wait (仅有交付前回调时)、delivery、cleanup. `打包计时` 依次为 source-validation、payload-snapshot、payload-license、runtime-metadata、nsis-inputs、nsis-compile、release-wait (仅 release 预生成时)、delivery-verification、cleanup. 通道耗时输出为 `并行计时` 或 `通道计时`.

## 优化方法

### 先测量再改动

优化前后使用相同命令、范围、部署模式和机器负载. 保存带逐行时间戳的完整日志, 读取阶段计时与本轮 TRX, 同时按秒采样整机 CPU 和可用内存. 在独立 pwsh 中记录日志, 证据保存在本任务的 `.docs/<任务>/` 目录:

```powershell
$log = '.docs/<任务>/release-timing.log'
$watch = [Diagnostics.Stopwatch]::StartNew()
pwsh -NoProfile -File ./run.ps1 -Choice release -DotNetMode without-dotnet -NonInteractive 2>&1 |
    ForEach-Object { '[{0,7:F1}] {1}' -f $watch.Elapsed.TotalSeconds, $_ } | Set-Content -Path $log -Encoding utf8
```

CPU 与内存用后台 ThreadJob 读取 `Win32_PerfFormattedData_PerfOS_Processor` (`Name='_Total'`) 的 `PercentProcessorTime` 和 `Win32_OperatingSystem` 的 `FreePhysicalMemory`.

- 单独运行各阶段, 记录墙钟和 CPU 核秒 (扣除空闲基线后的平均整机占用率 × 逻辑处理器数 × 墙钟). 全部阶段核秒之和除以逻辑处理器数是并行可达的下限, 最长依赖链是关键路径. 两者接近且门禁期间整机 CPU 已饱和时, 继续提速只能减少工作量或缩短关键链.
- 用 TRX 按耗时排序, 定位最慢用例、串行集合和尾部等待; 用例耗时之和与测试实际经过的时间分开统计. 共用真实用户数据的串行集合在争用下常决定 testC# 的下限.
- 结论基于多次运行和分段计时. 单次耗时、波动和原始 FAIL 写入任务报告.

### 按瓶颈选择手段

- CPU 有余量且阶段独立: 拆成继承控制台的子进程通道并行, 每行加阶段前缀, 结果经本轮私有目录回传, 缺失或损坏按失败处理. 任一失败立即终止其余在途通道与主流程在途原生命令, 不再启动新阶段, 按规范顺序报告首个真实失败; 取消优先并排空.
- CPU 已饱和: 增加通道、Vitest worker 或测试并发是零和. 调低有余量的重型通道优先级, 关键路径通道保持 Normal; 降级后核对轻量通道是否被饿到逼近关键路径.
- 尾部串行工作: 只依赖已确定输入的工作提前到等待期执行. 许可只读取本轮 deps、仓库许可来源和前端依赖元数据, 在前端等待前生成; 安装包从本轮已写清单的私有 staging 预先生成并持有打包锁, 全部通过且发布目录交付后才交付, 失败或取消时丢弃, 已有产物不变.
- 网络前置: 门禁开始时在后台预取只读公开数据, 只存本轮临时目录; 使用前复核输入 (如 SDK 绑定) 未变, 预取失败时回到原步骤在线读取, 保留原阶段和错误.
- 重复工作: 仅在本轮内容哈希能证明结果相同时跳过, 例如 release staging 的许可输入与仓库一致时不重复生成安装许可. 不用修改时间或跨轮缓存判断, 跳过路径仍复核输入.
- 并发读写冲突: 并行阶段不得读写同一输出. 测试构建传 `IncludePbiFrontend=false`, 不收集 `web/dist`; 非 RID 测试输出与 `-r win-x64` 发布输出分离, CLI 测试复制 Host 输出时排除 `win-x64/**`. 出现偶发缺文件或文件锁时, 先查 glob 与输出目录是否重叠.

### 不采用或需用户决策

- 跨轮缓存测试或检查结果: 与本轮执行证据规则冲突, 需用户先修改规则.
- 拆分共用真实用户数据的串行测试集合: 需先改为隔离用户根, 并保留跨进程验收租约.
- 把格式检查并入编译: 部分代码样式与行尾检查只由 `dotnet format` 执行.
- 调整 NSIS 压缩参数: 会改变交付产物.
- 提高分块阈值、缩短或取消超时、跳过用例或放宽断言: 不作为提速手段.

## 注意事项

- 前置条件: 工具、SDK 与还原资产等能廉价确定的前提由主流程在启动通道前统一核验. 只靠通道发现时, 其他已启动的通道会被立即终止, 已花费的启动与编译工作白费, 报告也来自单个通道而不是统一前置检查, 例如 clean 后 C# 通道立即失败.
- 进程优先级: Windows 子进程继承 BelowNormal (及 Idle), 不继承 AboveNormal; 降低通道优先级可覆盖其全部工具进程, 调高关键通道则无法传给它启动的工具.
- 取消与清理: 遵循 validation-and-delivery.md 的 PowerShell Ctrl+C 约定. 新增预生成、预取或临时目录时, 清理与丢弃同样放在 finally, 并以完成标记区分交付、失败与取消.
- 回调作用域: `AfterCompile`/`BeforeDelivery` 在被点源的 `build.ps1` 中执行, 依赖动态作用域读取调用方变量. 回调捕获的变量使用独特名称 (如 `$verificationGate`、`$deliveryCallback`、`$pbiBeforeDelivery`、`$releasePackageContext`), 避免被同名变量遮蔽. 不用 `GetNewClosure()`, 它把回调绑定到新的动态模块, 测试在模块作用域定义的替身不再生效.
- 测试替身: 普通函数会把未声明的命名参数放进 `$args`, 高级函数则报 `NamedParameterNotFound`; 生产调用新增参数时同步替身签名. `package.ps1` 在 `CommandRegistry` 模块作用域点源执行, 安装包测试在该作用域替换 `Copy-Item`、`Remove-Item` 和 `Invoke-PbiNative`.
- 编排测试与机器: 进程内同步执行通道的测试点源 `InlineGateLanes.ps1`, 固定为并行判定; 逐个模式由测试显式替换 `Test-PbiParallelGate`, 真实入口子进程测试在注入边界中固定, 断言不随测试机处理器数变化. 新增编排测试脚本遵循 validation-and-delivery.md 的编码与挂起保护约定.
- 挂起保护: 并行争用和较小机器会成倍放大单条用例与脚本合集的耗时. 调整并发、阈值或通道后, 对照 TRX 与 Vitest 输出中的最慢用例核对预算余量, 预算不足时调整共享预算常量或为个别慢用例显式声明.
- 负载相关诊断: 前端构建把日志器警告视为失败. rolldown 的 `PLUGIN_TIMINGS` 仅在构建超过 3 秒且插件耗时超过链接阶段 100 倍时出现, 结果取决于机器与负载, 生产构建以 `checks.bundlerTimings: false` 关闭, 其他诊断不过滤. 类似提示先在单 CPU、无其他负载下复现, 确认与负载相关再处理.
- 进程与残留: 失败时立即终止本轮持有句柄的在途通道与主流程原生命令的进程树, 包括仍挂在其下的 MSBuild 节点与 VBCSCompiler; 已结束命令遗留的可复用节点不在任何进程树下, 按 SDK 正常行为空闲后退出. 取消时排空. 不按名称或"最近启动"推断归属, 也不结束 dotnet/node 等同名进程. 每次运行都会在 TEMP 留下空的随机目录与 workload 日志, 判断残留时与基线运行对比; 快速失败与取消都会中断测试夹具的临时目录清理及前端构建写盘, 结束后核实归属再清理本任务产生的部分, 被中断的 `web/dist` 在独立 buildC# 使用前先重建.
- 看护状态: 主流程与看护 runspace 共享门禁上下文的同步 `State`, 终止与取消排空在同一把锁中决定先后. 仍在运行的通道先写入 `Terminated` 再终止, 已在终止前写回结果的通道仍按真实结果汇总. 主流程原生命令的终止由主流程自身完成, 不跨线程查找进程.
- 内存: 并行门禁明显抬高峰值内存. 采样最低可用内存, 余量不足时优先逐个执行或减少并发.

## 改动同步

调整通道、阶段或打包顺序时同步以下内容, 以测试为准而不是以文档中的旧顺序为准:

- `$script:GateStages`、`$script:GateLanes`、`ConvertTo-PbiLaneOptions` 允许列表及 `command-entry-tests.ps1` 的通道参数矩阵.
- 通道新增前提时, 同步 `Invoke-PbiAction` 的前置检查与 `contracts.ps1` 的缺失资产用例.
- `InlineGateLanes.ps1`、`contracts.ps1`、`frontend-check-tests.ps1`、`external-tool-workflow-tests.ps1` 中的预期顺序, `publish-tests.ps1` 与 `installer-package-tests.ps1` 中的阶段列表.
- 新增入口脚本加入 `entry-stop-tests.ps1`, 新增多场景脚本合集使用 `CliTests.ScriptCollectionTimeout`.
- 新增门禁脚本或模块时同步 `gate-failure-tests.ps1` 私有副本的复制清单; 构建脚本新增门禁模块函数调用时, 同步 `publish-tests.ps1` 在模块外点源构建脚本所需的替身.
- 根 `AGENTS.md` 的门禁说明、本文和 [validation-and-delivery.md](validation-and-delivery.md).

## 验证清单

1. 运行上表的编排回归, 覆盖前置不足时不启动任何子进程、并行与逐个顺序、首个失败后不再启动新阶段、并发失败按规范顺序汇总且排除被终止阶段、取消优先、交付前回调成功与失败、预生成交付与丢弃、许可跳过与刷新, 以及全部入口在停止时以 130 结束. `gate-failure-tests.ps1` 的快速失败场景让 4 个阶段各自运行 600 秒的真实子进程后再注入通道失败或主流程失败, 核对立即退出、首个失败退出码、被终止阶段列表及没有遗留子进程; `contracts.ps1` 覆盖门禁中原生命令的输出转发与退出码、运行中失败结束整棵进程树、关闭输出后仍运行、批处理与别名入口、通道取消转为取消异常、检查点在已有取消时按取消抛出并选择排空, 以及根进程已自行退出时保留真实退出码.
2. 完整 release 至少运行两次, 对照阶段计时、通道计时、TRX 实际用例数和最慢用例; 影响发布时再运行 `scripts/tests/published-smoke.ps1`.
3. 模拟较小机器. .NET 的 `Environment.ProcessorCount` 取逻辑处理器、处理器亲和性和作业 CPU 限制中的最小值, 并在运行时启动时固定, 因此在启动 release 前限制当前 pwsh 的亲和性, 由全部子进程继承. 同时关闭 MSBuild 节点复用与共享编译, 避免复用不受限的旧进程或遗留受限进程. 分别验证阈值以上 (并行) 与以下 (逐个), 在独立 pwsh 中运行:

```powershell
[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr]0xF
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
pwsh -NoProfile -File ./run.ps1 -Choice release -DotNetMode without-dotnet -NonInteractive
```

4. 真实 Ctrl+C. 以隐藏窗口单独启动 release, 再用独立 pwsh 进程运行下面的发送方, 至少覆盖门禁中段和预生成安装包期间. 发送方会脱离自身控制台并附加到目标控制台, 自身忽略该事件, 因此不能在门禁进程内执行. 核对入口与通道退出码 130、排空时间、启动后新建的进程是否残留、TEMP 与产物目录新增项, 以及发布清单和安装包哈希不变.

```powershell
param([int]$TargetPid)
Add-Type -Namespace PbiCtrlC -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(System.IntPtr handler, bool add);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
'@
$null = [PbiCtrlC.Native]::FreeConsole()
if (![PbiCtrlC.Native]::AttachConsole([uint32]$TargetPid)) { exit 10 }
$null = [PbiCtrlC.Native]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
if (![PbiCtrlC.Native]::GenerateConsoleCtrlEvent(0, 0)) { exit 11 }
Start-Sleep -Milliseconds 500
$null = [PbiCtrlC.Native]::FreeConsole()
```

5. 结束前确认测试宿主、通道与工具进程均已退出, 门禁、预取、安装包与发布临时目录已清理, 打包锁已释放.
