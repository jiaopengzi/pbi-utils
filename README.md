<!--
 * FilePath    : pbi-utils\README.md
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 说明项目范围, 统一开发入口, 产品命令与验证文档.
-->

# pbi-utils

仅支持 Windows 安装和运行。C# / .NET 共享核心、Vue 3 界面与薄 WinForms + WebView2 宿主。桌面选择 PBIP/PBIX 后通过 Power BI Desktop 打开并连接在线模型，也可连接已打开实例；CLI 保留 PBIP 离线读取与显式副本流程。支持示例数据、PBIP 脚手架、日期与时间智能、UDF 及本地 MCP。CLI、桌面与 MCP 复用同一应用服务。这不是旧 Python 工具的完整迁移或生产发布。

解决方案文件为 `pbi-utils.slnx`，包含共享业务、CLI、专用后台、桌面与测试项目；项目清单以解决方案为准。使用根目录 global.json 固定的 .NET SDK 加载，开发动作统一通过 `run.ps1` 执行。

## 开发快速开始

`run.ps1` 在涉及 C# 的动作开始前校验根目录 `global.json` 的 SDK 绑定；多个 SDK 并存时，只要绑定版本能正确选中就可继续。缺少绑定版本或实际选中版本不一致时停止执行，列出绑定版本、当前 dotnet 路径及其可见的 SDK，提示开发者检查是否安装原版本或更新 `sdk.version` 绑定。脚本不自动安装 SDK、选择更新版本或修改配置；确认升级并修改绑定后，先运行 `setupC#` 还原依赖，再运行 `all` 验证。仅前端、帮助和清理动作不要求 SDK。

需要 PowerShell 7、.NET SDK、Node 和 pnpm。SDK 精确版本见根目录 [global.json](global.json)；Node/pnpm 精确版本见 [web/package.json](web/package.json)，Node 读取 `engines.node`，pnpm 仅读取 `packageManager`。NuGet 依赖版本见根目录 [Directory.Packages.props](Directory.Packages.props)，目标框架和产品版本见根目录 [Directory.Build.props](Directory.Build.props)。桌面运行额外需要共享 Evergreen WebView2 Runtime。安装来源与锁定策略见 [工具链](.docs/toolchain.md)。setup 系列只检查和还原，不安装工具。

```powershell
pwsh -NoProfile -File ./run.ps1 -List
pwsh -NoProfile -File ./run.ps1 -Choice setupAll -Scope All -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice all -Scope All -Configuration Debug -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice all -Scope All -Configuration Release -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice all -Configuration Release -DotNetMode with-dotnet -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice release -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice package -NonInteractive
pwsh -NoProfile -File ./run.ps1 -Choice devDesktop
pwsh -NoProfile -File ./run.ps1 -Choice devWeb
```

`setup` 系列仅在首次准备环境、相关依赖或锁文件变化、工具链绑定调整后，以及依赖资产缺失时显式执行。依赖未变且资产完整时，日常开发直接重复运行 `all`，无需每轮运行 setup；按实际变化选择 `setupC#`、`setupVue` 或 `setupAll`。`all` 不隐式安装或还原依赖。

`all -Scope All` 成功交付后，自动把本轮桌面程序注册为 Power BI Desktop 的 **pbi-utils (开发)** 外部工具；Debug、Release 和两种 .NET 模式共用开发入口，更新为最近一次完整构建的路径。`Core` 没有桌面入口，因此不注册；`buildC#`、`buildAll`、`release` 和 `package` 不修改开发登记。注册步骤失败会使 `all` 返回非零，已完成的构建产物仍保留。

外部工具按[微软注册规范](https://learn.microsoft.com/en-us/power-bi/transform-model/desktop-external-tools-register)写入 `%CommonProgramFiles(x86)%\Microsoft Shared\Power BI Desktop\External Tools`。该目录通常需要管理员写权限；交互运行仅对注册步骤请求 Windows 授权，非交互运行无权限时明确失败，可在管理员 PowerShell 7 中重跑上述命令。登记内容已一致时无需重复写入。注册后需重新启动 Power BI Desktop 才会加载菜单入口。

点击外部工具会通过 `--server "%server%" --database "%database%"` 传入当前报表目标。应用仅接受本机回环地址，并核验当前 Windows 会话中的 Desktop 进程、引擎端口及数据库身份，再通过共享连接流程打开模型；目标失效或不匹配时显示错误，不自动改连其他模型。此启动动作只连接和读取，不修改或保存报表。

| 工作 | C# | Vue | 合并 |
| --- | --- | --- | --- |
| 还原依赖 | `setupC#` | `setupVue` | `setupAll` |
| 构建 | `buildC#` | `buildVue` | `buildAll` |
| 测试 | `testC#` | `testVue` | `testAll` |
| 格式化 | `formatC#` | `formatVue` | `formatAll` |
| 检查 | `checkC#` | `checkVue` | `checkAll` |

C# 名称建议加引号，例如 `-Choice 'buildC#'`。C# 动作只检查 .NET，Vue 动作只检查 Node/pnpm。合并动作先 C# 后 Vue；`buildAll` 先编译 C#，再构建 Vue，最后将本轮页面装入临时发布目录，校验清单后向原发布目录覆盖文件并删除过期文件，保留目录本身，不整体重命名。单独 `buildC# -Scope All` 要求已运行 `buildVue`，缺少页面产物时明确失败。`publish` 已并入构建，没有独立入口；运行环境诊断直接使用产品 CLI 的 `doctor` 命令。

`Scope` 只限定 Windows 下的 C# 项目，默认 All；Core 包含共享业务、CLI、无 UI 的 Host、Windows 适配和对应测试，All 加桌面及桌面测试。两种范围都发布 CLI 与 Host；Core 不包含桌面程序、WebView2 或 web 页面。Vue 动作不接受 Scope。`all` 依次完成 C# 检查、测试、编译，再完成 Vue 检查、测试、构建，最后交付；首错停止，不还原依赖、不格式化、不运行 clean。`-Configuration` 默认 Debug；Debug 默认 `with-dotnet`，Release 默认 `without-dotnet`。`all`、`buildAll`、`buildC#` 接受 `-DotNetMode with-dotnet|without-dotnet`，一次只编译所选模式，不更新另一模式的目录。参数不区分大小写。

开发输出保留正常和错误通道；交互终端成功为绿色、警告为黄色、失败为红色，重定向输出不添加颜色。失败提示包含具体阶段和原因。`setupC# -Scope All` 一次锁定还原解决方案，依赖已为最新时显示“无需还原”。文件资源管理器打开发布目录不阻止文件覆盖；若运行中的应用占用 EXE/DLL 等文件，仍需正常关闭该应用后重试。构建脚本不会自动关闭用户程序。

C# `checkC#` 统一检查格式、风格及质量分析器的建议级别，覆盖 IDE0305、SYSLIB1045、CA1873 等；没有自动修复器的诊断也会使检查失败。`formatC#` 显式应用可自动修复的建议，剩余项需手工修复后再检查。编译诊断由 `testC#`、`buildC#` 执行时核验，编译器警告统一按错误处理。C# 测试还会读取本轮唯一 TRX，核验实际执行数、结果和平台错误；零用例、全部跳过、报告缺失或损坏均失败，部分跳过明确记录为 UNRUN。集合表达式仅在具体类型一致时简化，避免更换接口集合的运行时类型。规则与验证见 [C# lint](.docs/csharp-lint.md)。

`.gitattributes` 与 `.editorconfig` 固定开发文本使用 LF，避免 Windows 检出或切换分支后出现 `ENDOFLINE`。内置 PBIP 模板和示例素材按来源清单保留 CRLF；三个混合行尾 CSV 和二进制资源保留原始字节，确保大小、哈希及模板基线稳定。Git 行尾规则不会追溯转换已有工作区，处理旧检出时须保留用户改动，并按来源清单核验素材，不对资源目录批量格式化。

前端 `checkVue` 检查格式和离线目录后执行统一 `lint`：Oxlint 检查脚本，Vue 编译器检查全部 SFC 的脚本宏、模板结构及表达式，VS Code 样式语言服务检查独立及内嵌 CSS/SCSS，`vue-tsc` 严格检查模板组件、指令、属性、事件和类型。模板分别采用保留原始 key 的函数模式及解析表达式的模块模式，避免动态重复 key 或表达式语法漏报；不执行生成代码。合法 ARIA、data 属性及明确的 DOM 透传属性有精确类型声明，不放开未知业务属性。独立 `testVue`、`buildVue` 和 `pnpm test/build` 仍先执行相同 `lint`。同一 `all` 内仅在检查前后及后续阶段输入快照一致时复用本轮已通过的检查，测试与构建直接调用原工具；输入或包脚本变化时回退完整包脚本，不跨轮缓存。源码、声明、配置、锁文件、离线目录和安装元数据按内容核对，普通已安装依赖按实际路径、长度和修改时间核对；不能完整确认的外部链接禁止复用。任何静态错误都会阻止对应测试或构建继续；单独运行 Vitest 只代表测试结果。测试中的未预期 `console.warn/error`、Vue 告警和错误均导致失败；负向用例须在具体动作中精确声明预期参数与次数，缺少预期诊断同样失败。测试显式显示控制台输出，禁止 `.only`，无用例或未处理异常返回失败。检查不依赖编辑器扩展；拼写插件、个人规则、真实 WebView2 和真实模型接入仍需单独验证。

前端测试同时捕获模块导入、清理期间和窗口 `error/unhandledrejection` 诊断，全部 skip/todo 会失败，部分跳过单列 UNRUN。`buildVue`、`pnpm build` 及同轮检查复用均执行独立 Vite CLI；进入日志器的 warn/warnOnce/error 立即标记进程失败，保留既有非零码，原生晚期回调吞异常也不能以退出码 0 交付。错误日志只保留输出，让构建器继续抛出解析、生成或写盘的原始异常，不以通用横幅覆盖首因。保留 500 kB 分块阈值，写盘前按每个压缩后 chunk 的 UTF-8 字节硬校验，入口、动态及共享块均不得超限。此诊断策略针对独立 CLI，不把程序内多轮 `build()` 的 promise 完成当作成功判据。生产资源参考 `blog-client` 的包隔离原则，按 Vue、CodeMirror、共享工具、Element Plus、纯语言字典分组缓存，保留原页面和偏好状态的生命周期；规则在 `web/scripts/chunking.ts`。发布文件枚举、读取、校验或清理失败均返回非零；已经交付文件后才发生的清理失败会明确说明产物已交付，并保留失败事实。

DOM 测试统一使用 jsdom，公共初始化在诊断守卫安装后仅补齐缺失的 Range 几何接口与剪贴板写入入口。几何桩返回零尺寸，不代表真实布局；复制用例须局部显式模拟并断言参数，未模拟的写入默认拒绝。具体支持代码在 `web/tests/support/domApis.ts`，布局、真实编辑器事件和宿主行为仍由浏览器与 WebView2 分层验收。

`clean` 会删除 `obj`，其中包含构建所需的 NuGet 依赖资产；清理后先运行 `pwsh -NoProfile -File ./run.ps1 -Choice 'setupC#' -Scope All`，再运行 `all`。缺少资产时会提前返回 3 并提示还原命令。`clean` 保留前端 `node_modules`，无需为此重新安装前端依赖。

无参数只在交互终端显示菜单，读取一次选择；`q` 退出。CI/输入重定向/NonInteractive 必须有 Choice。编号按菜单顺序自动从 0 连续递增，当前为 0–19；数字选择以当前菜单为准，自动化建议使用动作名；不适用、未知或缺值参数返回 2。完整动作矩阵由 `-Help` 生成，详见 [开发说明](.docs/development.md)。

## Power BI 模型连接

仅实时连接 Power BI Service 语义模型的报表不含可供本地工具读取的完整模型定义。PBIP 的 `definition.pbir` 使用 `datasetReference.byConnection` 时，文件入口直接提示远端引用；PBIX 不解包，等待 Desktop 的权威文件状态就绪后，若仍没有本地模型则结束连接等待。零模型实例不可连接，不再显示为可读写模型。需要查看云端模型时，请使用 Power BI Service 页签显式登录并选择原语义模型，实际可读字段与度量定义受权限和服务接口限制；云端仍只读。具有真实本地模型的 DirectQuery/复合模型不因使用远端数据源而被排除。

Desktop 自动发现、切页重验和手动刷新不逐个弹出实例问题；摘要显示发现数量与实际可连接数量，不可用实例在各自卡片中保留禁用状态及“查看原因”。原因说明带上报表标题和进程编号，等待、无本地模型、读取失败和多模型歧义分别显示；刷新在途或失败旧快照不展示过期原因。主动打开文件/连接失败及整个扫描失败仍显示操作反馈。

桌面默认进入连接页。连接页直接展示 Power BI Desktop、Power BI Service、SSAS / XMLA 三种连接来源页签。三种来源共用单行模型卡片，连接或查看操作位于行首，兼容级别、表、度量值及报表页面统计同行区隔展示；已连接卡片点击“查看模型”进入模型页。未读取的统计显示 `-`；在线报表页数未提供时按显示约定为 0，后台摘要保留未读取状态。自动发现当前 Windows 会话已打开的 Power BI Desktop，显示标题、PID 和回环端口，可手动刷新和取消；未启动、等待模型、单实例连接失败分别展示。桌面通过共享 `PowerBiService` 编排实例读取，CLI 的离线工程读取使用对应共享服务。

在“Power BI Desktop”连接区选择本机 `.pbip` 或 `.pbix` 文件，应用打开 Desktop，等待模型就绪后连接。同一文件已经打开时，依据完整文件路径、PBIP 报表绑定和进程身份复用该实例；已打开实例也可直接选择连接。未安装 Desktop 时提示安装 Microsoft 官方版本；自动文件连接需要 Desktop Bridge 可用，文件身份无法核验、重复打开或加载失败时保留原模型并显示操作建议。取消停止等待与本次连接，不关闭 Desktop；已启动的窗口可能继续加载。读取的是在线语义模型，不解析 PBIX 二进制或读取在线报表布局。实现和实际验证边界见 [本地文件统一连接](.docs/local-desktop-connection.md)。

远端连接使用统一 Vue 表单输入服务器与数据库；SSAS 可使用当前 Windows 身份，或指定目标服务器、可信域的 Windows 账号和非空密码，Power BI XMLA 使用组织身份。表单可记住最近20条连接，按连接加密保存密码，并提供删除记录和清除密码；已保存密码不会回填输入框，留空可复用，新输入可替换。新输入只经过本次受限本地桥接进入后端，提交或关闭后清空；保存值和密文不发回网页。指定身份通过后端 Windows 出站上下文应用，账号密码不进入连接串或日志，云令牌不保存。填写期间不创建连接请求或计时，点击连接后使用30秒连接时限，网络和认证失败有分阶段安全错误码；Debug 阶段日志永久保留并可通过设置页过滤。远端工作区只读，本地 Desktop 的模型读写与自动保存见下方说明。

连接和切换模型都在连接页完成，不再打开连接弹窗，顶栏保留当前模型状态、刷新和断开操作。PBIP/PBIX 都连接本地 Desktop 模型，支持既有本地模型读写；Power BI Service 和 SSAS / XMLA 只读，隐藏创建操作并保留已有定义查看。一个窗口只有一个当前模型，连接成功并排空原请求链后自动进入模型页，失败或提交前取消保留原模型和页面。当前来源、读写状态和未保存状态显示在顶栏，刷新读取当前模型，断开清除当前工作区。

同一连接和模型基线内，度量值、表格模型、模板度量值之间往返复用已读取的信息，不重复触发统计或模型配置加载。刷新模型、断开重连或模型修改后重新读取；失败与取消不缓存为成功。外部修改模板配置文件后可显式重读，应用前仍核验真实文件与模型基线。

连接页复用紧凑的文件选择和实例刷新操作栏；实例卡片按宿主核实的进程与引擎身份标记“已连接 / 未连接”，当前卡片的“查看模型”只导航，不重复连接。Service 当前目录条目按宿主绑定标识匹配，不用同名对象推断。连接页不再重复显示当前模型概览和独立统计模块。在途连接保留取消入口，确认取消后仍等待宿主终态；模型保存未完成时禁止切换来源和连接。实现和本轮证据见 [实例单行组件](.docs/model-instance-components.md)，前轮证据见 [连接页连接重构](.docs/home-connection-refactor.md)。

连接页“最近打开”按来源独立展示。Desktop 只显示最近20个 PBIP/PBIX 文件，点击后走同一 Desktop 打开与复用流程；文件连接的提示持续到宿主确认模型就绪并连接完成，随后更新实例列表。Service 只在登录后展示当前真实账号、当前云与最新权限目录核验后的成功模型连接记录；重开时再次核验权限。SSAS/XMLA 不展示该模块。两种列表复用展示组件及“设置 → 常规 → 显示最近打开的文件”全局开关，也可在标题右侧关闭。隐藏保留历史；保存成功后才更新显示状态，失败保留原偏好。

Service 最近重开会同步工作区和模型选择；再次点击当前模型只进入模型页。只读 ADOMD 架构读取遇到明确的 HTTP 连接中断时最多重建会话完整重读一次，认证、权限和超时等错误仍直接返回。真实 Desktop 加载、实例更新、世纪互联登录及云历史保存/重开的本轮结果见 [发布版重开验收](.docs/service-recent-reopen-acceptance.md)。

Power BI Service 支持全球版与世纪互联 (中国)。首次填写工作或学校邮箱并点击“登录”时显示环境检测提示；单个环境自动进入系统浏览器认证，多个环境显示下拉框，选择后手动点击同一个“登录”按钮。可记住最近20条邮箱及环境，后续直接复用该环境登录，也可删除记录；后端仍验证受支持云的认证配置，失效时明确提示，不静默切到其他云。登录后工作区和模型下拉框均支持搜索。认证、目录接口、XMLA 和共享容量架构连接均绑定同一云，无需填写应用 ID 或密钥。令牌只在后端内存，关闭程序需重新登录，系统浏览器可保留自身登录态。退出登录只断开当前 Service 模型，保留最近记录，本地 Desktop 模型保持。在线读取仍受许可证、Build 权限和租户策略约束，外部 SSAS/AAS、推送及 Excel 模型有明确不可连接提示。本轮实现与验证见 [连接页连接与最近记录](.docs/home-connection-loading-recents.md)，云兼容历史见 [世纪互联兼容](.docs/service-china-compatibility.md)。

前端统一使用 Element Plus，Vite 参考 blog-client `v1.0.0` SPA 的按需导入、SCSS 主题、别名与资源组织；保留深蓝金色主题和本地 SVG 图标。卡片、提示、页签、弹窗、文件差异预览与校验复用 `web/src/ui`。主窗口初始及最小内容区为 1422×800（800×1920/1080 取整推导宽度），原生标题栏和边框另计，侧栏无需滚动；响应式适配桌面缩放、最大化与还原，保留侧栏手动折叠和并排工作区。尺寸回归见 [桌面响应式验收](.docs/desktop-responsive-acceptance.md)。浏览器 `devWeb` 的实例为明确标注的模拟数据。本轮组件约定与证据见 [前端 UI 重构验收](.docs/frontend-ui-acceptance.md)，历史实例发现证据见 [Power BI 与响应式验收](.docs/powerbi-acceptance.md)。

所有应用自有表单、示例覆盖/清空确认、独立风险确认和关闭提示均由 Vue 实现，WinForms 仅承载外壳与 WebView2。普通模型保存直接执行，不再弹出通用保存确认。SSAS / XMLA 表单直接平铺在连接页来源页签；填写期间可正常导航，点击连接后才执行真实连接。所有应用模态弹窗水平、垂直居中，长正文滚动且保留可见操作区。标准文件/目录选择器、系统标题栏、微软登录和 Power BI Desktop 自身界面仍由系统或外部程序提供。新凭据通过宿主一次性表单或弹窗身份提交；既存凭据不回显，MCP 精确修改由后端核验并签发一次授权，无二次弹窗。WebView2 或前端初始化前的致命错误通过安全日志和非零退出码报告，不另建 WinForms 业务界面。

度量值编辑器只移除定义头所需的首个换行，不自动改写用户缩进；格式化保存的正文保留一个前导换行，使 Desktop 的 `名称 =` 独立成行，顶级表达式不额外缩进，内部语法缩进与多行字符串内容保持。格式字符串提供预定义数字、日期时间格式及常用自定义模式，名称支持中英文，实际格式标识保持原文；显示文件夹可从现有目录选择或输入新路径。切换度量值、清空选择或进入/退出新建前，未保存草稿会提示保存、放弃修改或取消；保存失败、取消或外部冲突会保留草稿。

## 产品运行

新建 PBIP 成功后的“Power BI Desktop 打开”与连接页文件入口共用打开、实例复用和模型连接流程。连接成功后更新当前模型、记入最近文件并进入模型页；等待期间可取消，失败或取消保留原连接。当前模型或新建工程尚有未完成保存时须先处理，再打开连接。

Service架构连接后续修复: 当前ADOMD SDK解析pbiazure参数时需要构造前提供凭据, 在构造后设置AccessToken会提前抛错。只对该协议采用Bravo的后端内存ClaimsToken初始化, 不持久化或回传令牌; XMLA保持AccessToken方式。当前修复版、SDK实证与真实复验边界见 [Round 2记录](.docs/service-connect-fix-round2.md)。

Power BI Service 按容量选择读取协议：支持 XMLA 的工作区使用集群 XMLA 端点及 TOM，普通/个人工作区使用与 Bravo 对应的 ADOMD 架构连接。架构连接显示服务实际返回的表、字段及度量值，包括隐藏度量值和已返回的 DAX、格式与显示文件夹；缺失表达式逐项标记，分区、兼容级别、关系、角色和 UDF 仍未提供；不查询业务数据。连接错误显示发生阶段、异常类型和安全错误码，不将全部失败归因为权限。用户已实际确认登录及目录可读，本次连接修复与待复验状态见 [Service 连接修复](.docs/service-connect-fix.md)。

构建按配置和所选模式输出目录，结束时打印实际字节和 MiB：

| 模式 | 目录 | .NET 运行前提 |
| --- | --- | --- |
| Debug 开发 | `artifacts/build/debug/win-x64` | 默认含运行时，可显式选择不含 |
| Release 含 .NET | `artifacts/build/release/win-x64/with-dotnet` | 已包含运行时 |
| Release 不含 .NET | `artifacts/build/release/win-x64/without-dotnet` | 目标电脑须安装匹配版本的 x64 .NET Desktop Runtime |

每个目录内，桌面入口 `PbiUtils.Desktop.exe`、CLI 入口 `pbi-utils.exe`、无 UI 业务后台 `PbiUtils.Host.exe` 和前端 `web/` 位于同一根目录，共享依赖，不保留 `desktop/`、`cli/`、`host/` 子目录入口。分发时保留所选目录的全部文件；`manifest.json` 使用 `schemaVersion=3`、`layout=shared`、`deploymentMode=self-contained/framework-dependent` 记录配置、部署方式、范围与精确文件哈希。两种包功能和资源一致，运行不需要 run.ps1、Node 或开发 SDK。`without-dotnet` 需要 .NET Desktop Runtime，普通 .NET Runtime 或 ASP.NET Core Runtime 不能替代桌面运行时；不能仅依据安装了 Power BI Desktop 判断前提满足。桌面两种包均需独立准备 Evergreen WebView2 Runtime。

Debug 与 Release 互不覆盖。所选模式的临时产物完成编译、前端和清单校验后才交付；固定目标逐文件覆盖、清理过期文件、清单最后写入，并保留互斥与失败回滚。Debug 的同一目录保存最近一次所选模式，Release 的两种模式使用各自目录。

`run.ps1 -Choice release` 执行完整 Release/All 校验、构建和 NSIS 安装包生成；已有同版本 Release/All 产物时，可用 `-Choice package` 只打安装包。两者默认 `without-dotnet`，均可显式传 `-DotNetMode with-dotnet`。本机须已安装 `makensis`，脚本不自动安装工具。安装包及 JSON/SHA256 说明输出到 `artifacts/release/win-x64/<模式>/pbi-utils-<版本>-win-x64-<模式>-setup.exe`。命令只生成本地文件，不执行安装、签名或对外上传。

每次 `package` 都读取当前仓库的 `LICENSE`、`THIRD-PARTY-NOTICES.md` 和已审核的第三方许可来源，在独立打包副本中重新生成完整许可包及文件清单；仅修改许可后无需重新编译程序，安装向导与安装后的文档使用同一份新许可。向导阅读副本带 UTF-8 编码标记以正确显示中文，安装后的 `LICENSE` 保留原始字节。此步骤需要已有的 Node、前端安装依赖及 NuGet 缓存，不隐式安装或还原。前端依赖声明/锁文件、实际依赖集合或内嵌资源身份与原构建不一致时要求先重新构建；来源缺失、哈希不符或打包期间发生变化时中止并保留旧安装包。原构建目录保持不变，安装包 JSON 的 `inputManifestSha256` 记录原发布清单，`sourceManifestSha256` 记录更新许可后实际安装载荷的清单。

安装完成会注册 **pbi-utils** 外部工具，指向实际安装的桌面程序并自动连接发起启动的报表模型。安装入口 `pbi-utils.pbitool.json` 与开发入口 `pbi-utils-dev.pbitool.json` 独立；升级或修复更新本安装登记，卸载只移除仍属于本安装的登记，未知同名文件和用户后续修改受到保护。注册纳入安装事务，取消管理员授权或注册失败会明确失败并尝试恢复本轮更改。安装器继续使用当前用户身份，仅固定的系统目录注册步骤按需提权。

安装包使用 NSIS 内置 LZMA 固实压缩（`SetCompressor /SOLID lzma`），字典大小为 32 MiB（`SetCompressorDictSize 32`）；配置在 `scripts/installer/installer.nsi`，当前 `package/release` 没有独立压缩参数。安装欢迎/完成页与卸载完成页使用仓库内的品牌竖图，来源、格式和再生成方式见 [品牌说明](BRANDING.md#nsis-安装向导竖图)。

安装和卸载向导的中文页面使用 Microsoft YaHei UI 10 磅，英文页面使用 Segoe UI 9 磅，控件与窗口按字体度量一起布局。选择安装位置后，可分别勾选“创建桌面快捷方式”和“创建开始菜单快捷方式”，默认均选中，返回上一步后保留本轮选择。入口分别位于当前用户桌面和开始菜单的 `pbi-utils` 文件夹，指向实际安装的桌面程序。修复或升级时取消勾选会移除本次安装已有的对应入口；卸载始终检查两个位置，仅清理本安装拥有的快捷方式，缺失入口不影响卸载。实际写入或删除前再次核对快捷方式快照，用户后续修改和其他软件的同名入口保持不变。

安装器面向当前用户，默认安装到 `%LOCALAPPDATA%\Programs\pbi-utils`，完整程序位于 `app` 子目录；桌面入口为 `app\PbiUtils.Desktop.exe`，CLI/MCP 为 `app\pbi-utils.exe`。维护文件与产品文件清单分开；当前代际安装沿用固定路径，支持升级和同版本修复，拒绝降级。旧安装身份不自动接管。桌面或开始菜单已有不属于本次安装的同名快捷方式时，保留原入口、在安装详情中提示跳过，并继续安装及创建其他未冲突入口；卸载和失败回滚也不会删除这些外部入口。可从安装目录的桌面入口或安装完成页启动新程序。升级和卸载要求正常关闭本软件及 MCP 连接，不强杀 Host 或 Power BI，也不清理用户数据或共享运行时。

安装器在许可页之后、目录选择和完整程序解压之前，用系统 Windows PowerShell 5.1 检查 x64 .NET Desktop Runtime 和 Evergreen WebView2。`without-dotnet` 按发布入口要求检测 .NET，`with-dotnet` 使用包内运行时；两种包都检查 WebView2。初次检查已满足时直接进入下一步；否则显示环境页，用绿色表示满足、琥珀色表示需要安装、红色表示失败，并保留明确的文字状态。缺失或检测失败时禁用下一步，可安装缺失组件、重新检查或打开微软官网手动安装。只先释放小型引导文件，离开环境页及完整解压前再次核验。下载阶段可取消；微软安装程序启动后等待退出并复检，不强行中断。管理员授权用于确实需要提权的前置组件及外部工具注册步骤；需重启时提示并禁用安装后自动启动。静默安装只检查已有依赖，缺失退出 3，不隐式下载或安装运行时；静默安装和卸载的外部工具登记也不弹出 UAC，无写权限时返回 3，可在管理员上下文重试。

构建时根据 `global.json` 从官方元数据解析对应 Desktop Runtime 的 URL/SHA512，终端不要求开发 SDK。网络、权限、签名、版本或安装失败分别报告，不混为程序占用。安装包继续采用 NSIS 和用户主动运行完整新版安装包的升级方式；本地生成的安装包签名状态以旁边 JSON 记录为准。更新提示和 HTTPS 下载不等于代码签名，未签名安装包仍可能出现 Windows 安全提示。

### 软件更新与发布约定

设置中的“软件更新”默认开启自动检查，默认来源为 GitHub，可切换至 Gitee。启动后后台检查，每个来源最多每 24 小时自动检查一次；“检查更新”可主动重试。紧凑面板显示当前版本、检查状态和本地时间（`YYYY-MM-DD HH:mm:ss`），更新说明可展开查看。结果区分有新版、当前版本、尚无本产品发布和检查失败，网络失败不等同于没有更新。通知每个版本在本次应用运行中最多一次。只有点击“下载安装包”或“打开发布页面”才在系统浏览器中打开对应地址，应用不自动下载、执行安装器或重启。重新启动后保存的结果只供查看，重新检查成功后才开放下载入口。

版本检查统一位于软件设置中，默认使用 GitHub；用户访问或下载不便时可选择 Gitee 并重新检查。NSIS 安装器只处理安装前运行环境检查和安装维护，不查询产品版本。CLI 可运行 `pbi-utils updates check --source github|gitee --json`；MCP 提供只读 `updates_check`，不提供浏览器或安装能力。

两个固定来源为 [GitHub](https://github.com/jiaopengzi/pbi-utils/releases) 和 [Gitee](https://gitee.com/jiaopengzi/pbi-utils/releases)。新 Windows 产品仅识别 `desktop-v<版本>` 正式 Release，例如 `desktop-v0.1.0`，排除旧 v1 标签、草稿和预发布。版本来自 `Directory.Build.props`，查询完整的有界分页后比较数字版本，不依赖 API 返回顺序；超过分页/响应预算明确失败。两来源分别查询，附件与说明必须属于同一来源、同一版本，不拼接不同发布的结果。

GitHub Actions 的 [build.yaml](.github/workflows/build.yaml) 仅在推送 `desktop-v*` 发布标签时自动构建发版；面向 `main`、`v2` 的 PR 和手动触发可单独运行验证。普通分支 push 不触发构建，避免提交和标签各跑一遍。流程统一包含固定工具链、`release` 门禁和发布后 CLI/MCP smoke。CI 从仓库配置读取 .NET SDK、Node、pnpm 和产品版本，使用与许可来源一致、归档 SHA256 固定的 NSIS；不执行产品安装或开发外部工具登记。通过后上传版本化 ZIP，PR/手动分支验证不创建 Release。

正式发布只分发 `pbi-utils-<版本>-win-x64-without-dotnet.zip`，由用户解压并运行安装器，运行时由安装器按需补齐。ZIP 根目录只包含本次 `package` 的安装器：

- `pbi-utils-<版本>-win-x64-without-dotnet-setup.exe`。

安装器 JSON、`.sha256` 侧文件、目录锁和中间产物保留在本地，不进入 ZIP。归档前后核验安装器字节及本地校验值，发布时从实际 ZIP 内的 EXE 计算 SHA256，将摘要和精确 EXE 文件名写入 GitHub/Gitee 发布说明；该摘要对应安装器，不是 ZIP。软件更新仅识别同源同版本的上述 ZIP；缺少、重复或地址不符时禁用下载。开发命令仍支持两种本地构建模式。更新检查仅访问匿名 Releases API，不读取仓库分支上的 changelog 文件，也不要求自建更新服务器。标签构建发布 GitHub 成功后，调用独立的 [publish-gitee.yaml](.github/workflows/publish-gitee.yaml) 同步同一 ZIP；本地 `run.ps1` 仍只生成文件，不创建远程 Release、不上传文件。

#### 标签、更新日志与 Git 别名

更新说明维护在 [CHANGELOG.md](CHANGELOG.md)，最新版本在前，章节标题固定为 `## [<精确标签>] - YYYY-MM-DD`，下方按 Added、Changed、Fixed、Removed 等实际变更分组。发布必须有唯一且非空的对应章节；CI 不用其他版本的说明代替。版本数字必须与 `Directory.Build.props` 一致，发布脚本不自动改版本或提交文件。

在仓库根目录配置本仓库的 alias，不修改全局 Git 配置：

```powershell
git config --local alias.savetag '!bash ./.gitalias/savetag.sh'
git savetag --dry-run
git savetag
```

`git savetag` 使用 Git Bash 和项目已有的 PowerShell 7（仅解析 XML 产品版本），从 CHANGELOG 的最新发布章节读取标签，要求工作区干净、位于 `main` 或 `v2`、标签尚未在本地或 origin 存在。先正常提交审核后的文件，再执行此命令。它创建 annotated tag，并将当前分支和此标签原子推送至 origin；不自动暂存/提交、不 force、不附带其他标签，失败时如实保留并报告本地状态。`--dry-run` 只校验和预览，不创建标签或推送。

- `main`：`desktop-v<版本>`，成功构建后创建正式 GitHub Release。
- `v2`：`desktop-v<版本>-v2.<正整数>`，例如 `desktop-v0.1.0-v2.1`，成功构建后创建非 latest 的 GitHub 预发布，软件更新会忽略它。

CI 还会核验标签提交确实属于对应来源分支，Release 正文取精确 CHANGELOG 章节，附件只有上述 ZIP。已有同名 GitHub Release 不自动覆盖。独立 Gitee 工作流只推送此次精确标签，不推送 `v2` 分支；使用已配置的 `GITEE_SSH_PRIVATE_KEY` 和 `GITEE_TOKEN`，预发布标记与 GitHub 一致。附件上传复用 [action-gitee-release v2.0.0](https://github.com/nICEnnnnnnnLee/action-gitee-release/releases/tag/v2.0.0)，明确指定数字发行版 ID 和唯一 ZIP，禁用自动重试并限制步骤时长。上传前检查 ZIP 不超过 100,000,000 字节，上传后从公开链接下载核验长度和 SHA256，最后才开放正式发行版。若 GitHub 已发布而 Gitee 失败，使用“重新运行失败的作业”，或单独运行 `Publish Gitee release` 并填写 `release_tag`，不重复构建或创建 GitHub Release。独立入口也会核验 GitHub 已有的发行版、原标签提交、单 EXE ZIP 和发行说明中的 EXE 摘要；已有精确 Gitee 附件只在字节完全一致时复用，冲突明确失败，不删除重建。

当前 `v2` 通过 `workflow_call` 在 GitHub 发布成功后调用该独立 YAML，避免仅在默认分支存在时才生效的 `workflow_run` 监听限制；未合并 `main` 时，手动入口的网页可见性仍由 GitHub 的默认分支登记规则决定。两条入口共用按标签串行的发布锁。

首次在 `v2` 上线时使用 push 和 tag 事件验证；手动触发入口须等工作流进入默认分支后可用。

[sync_gitee.yaml](.github/workflows/sync_gitee.yaml) 只在 `main` push 时自动运行，其他分支不会触发；手动选择非 `main` ref 时只运行跳过门禁，不读取密钥或同步任何 ref。只有 `main` 的 push/手动运行能使用 `GITEE_SSH_PRIVATE_KEY`，以远端租约保护将当前 `main` 镜像到 Gitee 的 `main`。该工作流不处理标签或 Release，发行版同步由 `publish-gitee.yaml` 负责。代码同步无需 `GITEE_TOKEN`。上线测试时执行的 `v2` 跳过结果不能证明合并后 `main` 的真实代码同步权限已通过。

```text
pbi-utils --help
pbi-utils --version
pbi-utils doctor --json
pbi-utils updates check --source github --json
pbi-utils new templates --json
```

`--help` 优先于 `--version`，两者优先于 `--json` 且不执行业务。业务 JSON stdout 恰好一个文档，进度去 stderr。退出码：0 成功，2 输入，3 前置条件，4 执行失败，130 取消。`doctor` 读取运行环境；`new templates` 读取内置和用户模板目录，不创建工程。

侧栏依次为连接、模型、报表、新建、示例、学习和设置。应用不再内嵌聊天、提供方、模型目录或 AI 密钥管理；第三方 AI 客户端通过本地 MCP 调用已有业务。主侧栏支持折叠，浏览器预览明确标注模拟状态。

设置页底部的本地数据分区提供独立的“重置全部本地数据”入口，不依赖 MCP 配置说明加载成功。两次宿主确认会说明设置、密钥、会话、草稿、缓存、模板、UDF、事务恢复材料、日志、WebView2 数据和自行放入 `.pbi-utils` 的文件将永久删除。宿主正常排空、关闭窗口并释放日志和浏览器后才清理，再以默认状态重新打开；外部 Power BI 模型、报表和工程不在清理范围，重置不会回滚已提交的模型修改。其他实例占用、目录链接或未解决的执行结果会拒绝重置；失败不显示为完整成功。实际删除需要在应用中完成两次确认，开发验证不会自动清空真实用户目录。

发布验证脚本 `scripts/tests/published-smoke.ps1` 默认验证 Release 的 `without-dotnet`，`-DotNetMode with-dotnet` 验证含运行时包，`-Configuration Debug` 验证开发包，`-PublishRoot <目录>` 验证指定包。它检查清单/哈希，隐藏开发工具后执行 CLI 与 MCP 诊断，并核对包含运行时的包从自身目录加载 .NET、不含运行时的包从系统 shared 加载 .NET。独立机器脚本 `scripts/tests/standalone-acceptance.ps1` 用于目标环境验收；缺失必需运行时必须明确失败。当前机器的 PATH 隔离不代表物理无 SDK 或断网机器验收；独立环境和桌面交互按实际证据分别记录。

## 许可与第三方声明

本项目自有代码采用 [MIT License](LICENSE)，Copyright (c) 2026 焦棚子(jiaopengzi)。第三方库、字体、图标、改编代码与模板资源保留各自版权和许可，见 [第三方来源说明](THIRD-PARTY-NOTICES.md)，不因项目采用 MIT 而重新授权。

两种发布模式均随程序携带 `LICENSE`、`THIRD-PARTY-NOTICES.md`、可离线阅读的完整 `THIRD-PARTY-NOTICES.txt` 和 `licenses/` 原文及机器清单。发布阶段依据实际 NuGet 运行依赖、前端生产依赖闭包和内嵌资源生成；自包含包另保留 .NET 运行时许可与第三方声明。缺失许可、版本或哈希不一致时拒绝继续打包。构建只读取已还原依赖与仓库中审核过的许可原文，不在构建过程中下载条款；新增或更新依赖时应同步核查许可。

NSIS 安装向导在安装前提供 MIT 和第三方声明阅读页。MIT 页使用“下一步”继续；第三方页按 Microsoft AMO/ADOMD 组件分发条款，须明确勾选接受这两个组件的条款后继续，其他组件保持各自许可。静默安装须先阅读条款并明确传入 `/S /ACCEPTMICROSOFTTERMS=1`，缺少同意选项返回 2，且不解包或安装。安装完成页可选择打开完整声明；安装后文件保存在 `%LOCALAPPDATA%\Programs\pbi-utils\app`，可用记事本打开 `.txt` 文件并浏览 `licenses` 文件夹。微软前置运行环境仍适用其自身条款。

## 本地 MCP

本地服务已包含在两种发布包的 CLI 中，以 `pbi-utils.exe mcp serve` 运行。使用标准输入/输出与客户端通信，不开放 HTTP 端口、不安装 Windows 常驻服务、不自动修改第三方客户端配置。设置页顶部“本地 MCP”展示简短接入说明及一个提示词，内含当前安装路径、完整启动配置、验证步骤和固定结果表，不另行解释启动参数，没有权限页签或第二个 JSON 复制入口。复制后交给支持本地 stdio MCP 的第三方 AI 客户端协助配置；复制本身不会启动服务或更改权限。桌面应用与 MCP 可同时运行，无需关闭 pbi-utils 界面。

默认只读。需要生成文件、修改本地 Desktop 模型、切换连接或管理本地设置时，在客户端配置的 `args` 添加 `--allow-write`。这是一次启动配置, 不是每次操作确认; 工具参数不能开启权限。启用后 MCP 在后台静默执行业务, 不弹通用操作确认、不唤起 pbi-utils 前台。Service/SSAS 始终由后端强制只读。脚手架和离线配置仍先预览、再用精确摘要应用; 模型仍绑定 before/after、一次授权、完整基线与可靠回执。兼容升级须在精确配置请求中明确 `allowCompatibilityUpgrade: true`, 不以通用确认代替。

```json
{
  "mcpServers": {
    "pbi-utils": {
      "command": "C:\\your-install-folder\\pbi-utils.exe",
      "args": ["mcp", "serve", "--allow-write"]
    }
  }
}
```

示例路径需替换为实际安装路径；设置页生成的路径可直接使用。此示例与设置页一致，启动时允许修改；手动省略 `--allow-write` 仍为默认只读，工具参数或提示词不能提升权限。普通 CLI 命令继续可用。MCP 模式 stdout 仅输出协议消息；日志和诊断不混入协议。服务随客户端关闭并排空在途提交。

### 客户端联通标准与流程

设置页提供含真实安装路径的简短接入提示词。客户端先检查程序存在并解析配置, 比较 `command` 和完整有序 `args`: 匹配即复用, 缺失才添加, 差异仅作必要修正, 保留其他服务。结果中的 `readback` 用行内 JSON 完整展示实际回读值, `match` 记录比较结果。

在当前客户端依次调用 `server_info {}`、`desktop_sessions {}`、`desktop_app_info {"parameters":{},"sessionId":"本次 sessions[0].id"}`。核验版本非空、传输为 `stdio`、`allowWrite=true`, 并使用本次返回的唯一非空上下文 id; 三项成功即可确认工具可用。仅验证接入, 不连接模型或执行业务写入。连接不可用时停止, 保留本次错误码并说明当前客户端必要操作, 未执行标为未验证, 不从历史日志或旧状态文件猜原因。

最终使用一张 3 列、7 数据行表格, 依次记录配置、工具发现、三项诊断、写权限和实际写入测试, 附结论、下一步及隐私说明。前六项全部通过才算通过, 有失败则失败, 其余待完成; 实际写入固定未验证。只统一必要信息, 不要求固定空行、字符编码或底层协议记录。详细维护与排障规则见[本地 MCP 开发说明](.agents/skills/pbi-utils-development/references/local-mcp.md); 提示词不保证第三方 AI 必然遵从格式。

设置 → 本地 MCP 的连接列表显示本用户、同安装副本已认证的 MCP 客户端，以及进程、连接时间、启动权限和在途请求数。客户端名称/版本由 MCP 客户端提供，仅作展示，不用于授权；未提供时保持未知。桌面可逐个断开，或“暂停接入”以阻止新 MCP 连接并排空全部现有 MCP。暂停跨桌面和后台退出保持，直到用户在桌面明确恢复；恢复不会自动连接模型或重放操作，AI 客户端按自己的方式重新连接。

断开先停止新请求、取消准备并等待已开始的提交、回读和回执完成，再退出对应 MCP 进程；“正在断开”及“正在退出”不等于进程已结束，不强杀后台或 AI 客户端。仅桌面拥有连接管理能力，MCP 即使允许修改也不能查看或关闭其他客户端。开发或更新前可先暂停接入并等待列表清空，再正常关闭桌面，以释放桌面及共享后台自身持有的发布文件。

核心工具覆盖工程读取、DAX/M 格式化、新建预览/创建、模板详情、日期与时间智能、既有 PBIP 配置、示例数据、UDF 目录/来源/本地函数及副本变更。`desktop_*` 工具复用 pbi-utils 宿主的现有业务，包括本地/Service/SSAS 连接、当前模型读取/编辑、表统计、CSV/XLSX 导出、配置、偏好和业务目录。

桌面应用、多个 MCP 客户端与相关 CLI 可同时运行。它们通过同用户、同安装副本且核验 PID/启动时间的本机通道连接按需启动的 `PbiUtils.Host.exe`；后台不加载 WebView2，不开放网络端口或安装常驻服务。每条连接拥有独立的模型目标、账号、草稿、计划、观察基线与权限，GUI 的写权限不传递给 MCP，也不能用其他客户端的 `sessionId` 访问其上下文。

共享后台统一协调写入、回执和未知结果阻断，写入冲突明确返回，旧观察基线不能靠服务端自动刷新而覆盖并发更改。客户端关闭或 EOF 只取消并排空自己的上下文，其他客户端继续工作；最后连接结束且在途操作排空后后台退出。后台故障使旧连接与观察票据失效，未知提交不自动重放。

对后台模型操作前, 先用 `desktop_project_inspect` 或当前配置上下文读取目标, 或明确执行连接。MCP 记录已观察的连接代际; 目标变化后旧请求返回 `STALE_CONNECTION`, 不修改同名对象。列会话、诊断和设置读取不会接纳新模型, 独立 created 配置仍按自己的 targetId 和基线处理。

每个 MCP 上下文初始未连接，可先调用 `desktop_powerbi_instances`，再以 `--allow-write` 调用 `desktop_project_connectLocal`。文件选择器类工具返回 `MCP_INTERACTION_REQUIRED`，可改用明确路径的文件工具或在同时运行的 GUI 独立操作，不等待不可见弹窗。只有显式请求 `desktop_powerbi_serviceSignIn` 才启动微软登录浏览器；账号与目标仅属于该 MCP 上下文，不自动采用 GUI 或其他 MCP 的登录和模型。

MCP 可通过 `desktop_project_openPath` 的 `parameters.path` 传入本机绝对 PBIP/PBIX 路径，复用现有 Desktop 打开、同文件实例复用、完整文件身份和取消流程；需要启动写权限，不经过文件选择器或通用确认。此入口仅允许已核验的本机 MCP，网页继续使用系统选择器。`desktop_powerbi_instances` 和连接结果提供 `filePath`、`fileIdentityStatus`、`fileIdentityErrorCode`；只有 `verified` 表示官方 Desktop Bridge 已核验路径，`unsaved`/`unavailable` 保持未知，不按窗口同名推断。`desktop_project_connectLocal` 需要同时提供发现结果的 `processId`（字符串）和 `instanceId`；实例重启或身份不匹配拒绝连接。

桌面模型连接、写入及 `desktop_project_inspect` 默认返回摘要，保留 `projectId`、`baselineId`、连接身份、统计及真实保存阶段。`desktop_project_inspect` 与 `desktop_configuration_context` 接受独立的可选 `read` 对象，不向宿主业务参数传递展示选项。例如 `{"parameters":{},"read":{"section":"measures","limit":25,"includeExpressions":true}}` 读取度量定义；`section` 支持 `summary`、`tables`、`columns`、`measures`、`functions`，配置上下文另支持 `configuration`。表列表不内嵌全部列；列分页须指定 `table`，度量也可按表筛选。默认每页25项，`limit` 为1–100；用 `nextOffset` 读取后续页时必须携带前页的 `read.projectId` 与 `read.baselineId`，身份变化拒绝组合旧页。表达式默认省略，明确 `includeExpressions: true` 才返回；单页超过128 KiB明确失败，要求缩小范围，不截断源码。宿主与 GUI 保留完整权威模型，不因 MCP 摘要而改变授权或计算。

精确行数统计对没有可查询列的纯度量表返回 `rowCount: null`、`rowCountUnavailableReason: "no_queryable_columns"`，界面显示“不适用”；其他选中表仍按相同身份和基线执行 COUNTROWS。元数据未提供和普通空表不能因此伪装为0或不适用。

页面几何、弹窗确认回复、密码提交、窗口销毁及全量用户数据重置属于内部控制，不作为 AI 工具。业务取消通过 MCP 取消请求传递；提交开始后等待实际结果，不重放未知提交。MCP 复用已有功能，不新增通用 SQL/XMLA/shell/任意文件工具，也不重新实现官方 Power BI MCP 的通用管理能力。

`desktop_project_connectRemote` 仅接受 `parameters: {"server":"10.0.0.1","database":"Model"}`，使用当前 Windows 身份连接 SSAS，不接受密码、用户名、表单身份或保存密码字段。Service 使用本上下文已登录账号的模型句柄连接。GUI 指定账号/密码表单仍是独立人工入口；MCP 不代理凭据输入，GUI 普通草稿保护和请求代际检查继续生效。

MCP 通信在本机完成，但第三方 AI 客户端可能把工具返回的模型信息发送到其模型提供方；由用户选择和管理客户端的数据规则。pbi-utils 不配置 AI 密钥，不自动向模型厂商发送内容。协议处理复用 [官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 的默认协商能力，不固定单一协议版本。当前 Codex 的 `initialize` 握手与 [MCP 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28) 的逐请求元数据入口均由 SDK 处理，并调用同一套业务、身份和权限检查。`server/discover` 只列出逐请求协议版本，不是握手可用版本清单。

遇到未知执行结果时，先用 `pbi-utils mcp executions --json` 查看回执，并人工核对实际模型和文件。人工核验完成后，可按 `--help` 使用 `mcp resolve`，提供精确回执摘要、核验结论、证据文件和 `--confirm`。该命令保留原始未知事实，仅登记人工结论及证据 SHA256、解除 MCP 阻塞，不重放操作，也不证明引擎计算或 Desktop 文件保存完成；现有模型待核验仍需在应用中处理。核销命令不开放为 MCP 工具。

### 命令行手动调试 stdio 与全部列表接口

安装 Node.js 后，也可在仓库根目录使用独立 Inspector CLI 获取工具并严格检查 schema：

```powershell
npx --yes @modelcontextprotocol/inspector@2.13.0 --cli .\artifacts\build\release\win-x64\without-dotnet\pbi-utils.exe mcp serve --protocol-era modern --method tools/list --strict --format json
```

首次运行会下载 Inspector 到 npm 缓存，不修改项目依赖。此示例显式选择 `modern` 验证逐请求协议；使用 `--protocol-era legacy` 可独立验证当前 Codex 同类的 `initialize` 握手入口，Inspector 默认使用该入口。工具位于返回 JSON 的 `result.tools`。服务统一以等价的单类型 `anyOf`、恒假对象和完整 JSON 类型域发布 schema；原有必填项、null、数字字符串、条件禁用字段及服务端输入校验不变。验收同时核对退出码和警告输出，`--strict` 退出码 0 本身不代表零警告；即使没有诊断，也不代表所有模型厂商都支持完整 JSON Schema。

先运行同一发布目录的 CLI，随后在它的标准输入逐行粘贴 JSON，每条请求为完整的一行；每次等待对应 `id` 的响应再继续。需要写模型时，在启动命令末尾添加 `--allow-write`，工具参数不能改变权限。不要把 JSON 当作 PowerShell 命令执行。

```powershell
& .\artifacts\build\release\win-x64\without-dotnet\pbi-utils.exe mcp serve
```

以下示例使用新版逐请求协议，不发送 `initialize` / `notifications/initialized`。可先调用 `server/discover` 查看逐请求支持版本、能力和服务身份；也可直接调用工具。此入口的每条请求都必须在 `params._meta` 中携带协议版本和客户端能力，建议同时提供客户端身份，不能只在首条请求声明。握手客户端则按 SDK 协商的版本完成 `initialize` / `notifications/initialized`，后续无需逐请求元数据；两种客户端各自使用独立连接，不把握手版本放入新版请求元数据：

```json
{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
```

按顺序发送所有服务端目录列表。`tools/list` 返回工具及参数结构；当前只声明 tools 能力，没有注册 resources、resource templates 或 prompts，后三条预期返回 JSON-RPC `error.code = -32601`（方法不支持），不是空列表成功。这些错误不影响后续工具调用。

```json
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
{"jsonrpc":"2.0","id":3,"method":"resources/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
{"jsonrpc":"2.0","id":4,"method":"resources/templates/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
{"jsonrpc":"2.0","id":5,"method":"prompts/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
```

| 列表方法 | 支持时的结果字段 | 当前产品结果 |
| --- | --- | --- |
| `tools/list` | `result.tools` | 实际工具清单 |
| `resources/list` | `result.resources` | `-32601`，未启用资源能力 |
| `resources/templates/list` | `result.resourceTemplates` | `-32601`，未启用资源模板 |
| `prompts/list` | `result.prompts` | `-32601`，未启用提示词能力 |
| `tasks/list` | 新版协议已移除此方法 | `-32601`，本产品未启用任务扩展 |
| `roots/list` | 客户端能力，不是本产品的服务端目录 | `-32601`，本产品不请求 roots |

下面两条仅用于核实已移除的任务列表及不支持的 roots 请求会明确报错：

```json
{"jsonrpc":"2.0","id":6,"method":"tasks/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
{"jsonrpc":"2.0","id":7,"method":"roots/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}}}}
```

支持分页的服务返回 `nextCursor` 时，用它的原始字符串请求下一页，不自行构造游标。当前工具列表一次返回完整清单，不返回 `nextCursor`；其他列表不支持，不应继续翻页。分页形式如下（仅在真实响应含游标时替换并发送）：

```json
{"jsonrpc":"2.0","id":8,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}},"cursor":"替换为上一页的 nextCursor"}}
```

再通过只读业务调用验证服务仍正常，以及后台宿主确实可用：

```json
{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}},"name":"server_info","arguments":{}}}
{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}},"name":"desktop_sessions","arguments":{}}}
{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"manual-stdio","version":"1"}},"name":"desktop_powerbi_instances","arguments":{"parameters":{}}}}
```

`desktop_sessions` 是通过 `tools/call` 调用的业务工具，返回当前客户端唯一上下文；不是另一个 JSON-RPC list 方法。桌面工具可在 `arguments` 中带本客户端返回的 `sessionId`，错误或其他客户端身份明确拒绝。正常结束时关闭标准输入（Windows 交互控制台可用 Ctrl+Z 后回车），等待本 CLI 与其上下文排空；其他客户端存在时共享后台继续运行。不要直接结束后台进程来判断保存结果。

全量 MCP 回归使用 [逐工具用例清单](tests/contracts/mcp-tool-cases.json)，新增、移除工具或改变权限时必须同步用例，不能只修改工具数量。常规 C# 门禁包含 `McpToolCoverageTests`：核心工具读取隔离 PBIP 夹具，全部写工具验证默认只读与参数不能提权；副本编辑、示例数据、本地 UDF 和来源配置在独占临时目录验证正向流程、精确摘要、基线及删除确认。桌面包装输入检查与真实桌面业务验收分开记录。

完成 Release 构建后，在仓库根目录执行下列命令，通过真实 stdio 和无 UI 共享 Host 逐项调用工具，同时验证两个 MCP 共存、上下文隔离、单客户端 EOF 和最后客户端排空。脚本不会关闭用户窗口、连接或修改用户模型、登录云端、刷新网络目录，也不开启 `--allow-write`。

```powershell
pwsh -NoProfile -File ./scripts/tests/mcp-background-smoke.ps1 -AllTools
```

结果保存在本轮 `artifacts/evidence/mcp-background-*/tool-coverage.json`，列出每个工具的 PASS/FAIL/UNRUN、耗时和失败原因，并区分业务成功、输入拒绝、前置条件拒绝、写权限拦截。写权限拦截通过不代表写入业务已实测；真实模型提交、云端和交互式确认仍须另行验收。`result.json` 同时记录宿主清理和生命周期结果。无须重复构建即可重跑脚本。

写入正向回归使用 [写工具行为清单](tests/contracts/mcp-write-cases.json)，覆盖实际文件创建/编辑/保存/删除、配置恢复、导出、来源刷新及宿主连接和设置。每项关联执行过的测试方法及理论展开，核验业务副作用、独立回读和完成回执；权限拒绝不计入正向覆盖。在仓库根执行：

```powershell
pwsh -NoProfile -File ./scripts/tests/mcp-write-positive.ps1
```

脚本顺序编译并运行两组测试（依赖须已通过项目 setup 还原），将 TRX 和逐工具结果保存到唯一 `artifacts/evidence/mcp-write-positive-*`。若当前源码已完成 Release 测试构建，可加 `-NoBuild`。所有业务文件和离线模型均在独占临时目录中；云认证、远端模型、HTTP、系统选择器及外部打开动作使用受控适配器，真实业务服务、确认边界、文件事务与回执仍执行。这组测试不代表已登录真实云账号或运行外部浏览器。

真实 Power BI Desktop 模型写入另用两阶段脚本。先关闭现有 Power BI Desktop 和 pbi-utils 窗口，然后执行 `pwsh -NoProfile -File ./scripts/tests/mcp-model-write-smoke.ps1 -Prepare`。它从内置模板创建唯一测试工程并启动 Desktop，打印下一条完整的 `-FixtureRecord` 命令；待工程加载完成后执行该命令。脚本同时核验文件路径、进程启动时间、引擎身份与唯一模型标记，开启写权限，将测试度量从 1 改成 42 并独立回读，然后断开并关闭自有 MCP 宿主。写入响应不确定时保留 unknown，禁止自动重放。测试 Desktop 保留用于显式执行 `EVALUATE ROW ( "McpProbeValue", [MCP Probe] )`、保存和重开验收；模型提交与 Desktop 文件保存分别记录，不接管现有用户模型。

需要第三方客户端复验时，执行 `pwsh -NoProfile -File ./scripts/tests/mcp-inspector-smoke.ps1`，再加 `-AllowWrite` 验证设置页推荐配置。该独立开发脚本通过 npx 使用固定版本 Inspector，分别检查新版逐请求与 initialize 握手、完整工具发现、严格 schema 零错误/零警告、实际启动权限，以及共享后台信息、格式化、日期预览只读调用；证据写入独立的 `artifacts/evidence/mcp-inspector-*`。两种启动方式均不执行写工具。Inspector 每条命令创建独立客户端，不跨命令复用 sessionId。它需要 Node/npx，首次使用可能下载工具；产品运行不依赖 Inspector。

已安装 Codex 的开发机还应运行 `pwsh -NoProfile -File ./scripts/tests/mcp-codex-smoke.ps1 -AllowWrite`，通过真实 Codex app-server 核验推荐启动配置的原生握手和完整工具发现；省略开关验证默认只读启动配置。该脚本仅对本次进程覆盖本地 MCP 启动路径和参数，不注册或修改客户端配置、不创建聊天、不调用模型及业务工具；未安装 Codex 时明确失败，不自动安装。其结果仅证明 Codex 原生发现，实际业务调用与运行时权限仍须在目标客户端按上述流程验证，Inspector 或原始协议通过不能替代该证据。

普通成功响应包含 `result.resultType = "complete"`，服务身份在 `result._meta["io.modelcontextprotocol/serverInfo"]` 中；工具列表同时提供 `ttlMs` 和 `cacheScope` 缓存提示。不支持的版本返回 `-32022` 并在 `error.data.supported` 列出支持版本；应升级客户端或选择共同支持的版本，不能退回旧握手。新版已移除 `ping`，可调用只读 `server_info` 工具检查服务。无会话指协议请求独立携带元数据，桌面目标绑定、写权限和业务回执仍由服务端维护。

协议字段参考官方 [版本规则](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning)、[stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)、[Resources](https://modelcontextprotocol.io/specification/2026-07-28/server/resources) 和 [Prompts](https://modelcontextprotocol.io/specification/2026-07-28/server/prompts)，产品支持情况由真实 stdio 回归测试核验，不以规范存在某方法推定本产品已实现。

回执 `completed` 表示完整成功，`failed` 表示已知部分失败，`needs_verification` 或 `commit_started` 则需要核验并阻止新写入。例如工程已创建但 Git 初始化失败时，工具返回错误并保留“工程已创建”的事实，不应重新创建或覆盖工程。

## 工程与模型工作流

模型页“表格模型”顶部统一选择表和导出格式，支持勾选、全选和单表导出。导出范围默认抽样，每表最多100行，可自行填写正整数或切换为全部数据；抽样在模型侧限制数量，不指定业务排序，少于上限时导出实际行数，保留重复记录。Excel 将所选表写入同一工作簿的独立工作表，可附加导出摘要页；超出行、列或单元格上限时通过浮动消息提示改用 CSV。CSV 使用 UTF-8 逗号分隔：单表选择文件，多表选择父目录后输出到全新子目录，每表一个文件，不打包 ZIP 或覆盖已有目录。导出读取当前连接引擎中的数据，不刷新数据源或修改模型。Power BI Service 的共享/个人工作区即使只提供部分元数据，也可通过当前账号的只读数据连接尝试导出；实际字段来自查询结果，不依赖架构显示名称，权限或查询失败会明确报错。元数据未知项和 Service 只读规则继续保留，前后核验仅覆盖实际可读取的架构。离线 PBIP 和经完整架构确认没有数据列的表不支持导出。导出期间在浮动操作消息中按实际复核完成的表数显示进度，并更新当前表已读取行数；文件发布前最高显示99%，成功以原请求终态为准。读取期间可取消，发布后等待完成；失败或取消保留已有文件。

表格模型首次读取或返回前台时会自动更新 Desktop 的只读统计。连接后模型定义变化时，统计会自动核验一次最新模型并同步表格，无需先手动刷新；持续变化、目标换绑或待保存/待核验状态仍明确保留处理入口。配置上下文的权威回读同步整个工作区，度量值草稿保留编辑起点；同一度量值发生外部冲突时保留输入并阻止覆盖。自动同步不重放精确计数、导出或模型写入，也不刷新业务数据源。

每张表以独立卡片展示，展开后将表元信息与表字段分区。列表和元信息显示行数、VertiPaq 存储占用及字段数。统计独立读取，不参与模型定义基线；未知项显示 `-`，有限 Service 的架构成员数以 `≈` 标明来源。勾选表后可显式精确统计当前账号可见行数，成功后去掉估计标记；RLS 等安全规则仍生效，这一计数不代表整个模型的物理存储行数。精确查询不自动运行，大型或 DirectQuery 模型可能需要较长时间。存储占用不是导出文件大小，非完整 Import 存储不推测全量。最近成功导出目录独立记录在用户目录 `.pbi-utils/model-export-location.json`，重启后仍可通过“打开导出目录”按钮访问。Excel或单表CSV记录父目录，多表CSV记录新输出目录本身；目录改变、失效或链接会重新核验，网页不能指定打开路径。目录记录保存失败只警告，不改变文件已经导出的事实。内部 `RowNumber` 字段不出现在表格字段列表和导出中，用户普通数据列即使同名仍保留，源模型不变。

模型页支持度量值新增、查看、属性更新、删除与批量操作。单选自动填充编辑栏；多选保持空白，只修改明确勾选的属性，勾选后留空可清空说明、格式或文件夹。每批最多128项，并受请求总大小限制；整批校验后一次提交，普通保存不叠加确认，不创建长期模型历史备份。删除前按整批最终状态核验存活模型引用，仍被引用或无法安全核实时拒绝整批；可同批解除引用或删除完整依赖集合，报表视觉对象引用仍需另行核查。本地 DAX 格式化支持单个编辑栏和批量预览，不联网、不发送模型定义；只调整空格、缩进和换行，不执行或验证计算结果。格式化后需点击保存，Service/SSAS 仍只读。实现及验证见 [度量值 CRUD 与本地格式化](.docs/measure-crud-formatting.md)。

代码编辑使用共享 CodeMirror 6 编辑器，DAX、Power Query M、TMDL 采用自定义本地语言扩展，编辑和预览共用词法规则及 JBMono 主题。度量值及 UDF 表达式已接入，后续 M/TMDL 编辑可复用 `CodeEditor` 的 `language` 参数。支持行号、搜索、括号匹配、中文输入和撤销；Tab 切换焦点，Ctrl + ] / [ 缩进。格式化继续由 C# 后端完成，不调用远程服务；普通保存经共享模型编辑服务直接提交，MCP 模型修改保留精确一次授权；不创建长期模型历史备份。DAX 布局参考 [SQLBI DAX Format](https://docs.sqlbi.com/dax-style/dax-format)，提供 short / long 开关，默认 long。Long 允许短调用和变量块内联，short 展开参数；两者均采用四空格缩进，并按参数配对、运算符与注释上下文换行。切换开关只改变下一次格式化的样式，不自动格式化或保存；单项、批量和新建度量共用所选样式。度量正文保留定义头后的 LF，顶级行顶格，只按内部语法层级缩进。自有生成表达式的变量按 [SQLBI 命名规范](https://docs.sqlbi.com/dax-style/dax-naming-conventions) 使用 PascalCase 与最终 Result；格式化用户表达式只调整布局，不自动重命名或改写引用。实现、边界及本轮验证见 [CodeMirror 代码编辑](.docs/codemirror-editor.md)，后端格式化调查见 [本地 DAX 格式化](.docs/dax-local-editor.md)。双样式的真实 SQLBI API 结果保存在 [离线回归基准](tests/PbiUtils.UnitTests/Fixtures/DaxFormatting/README.md)，测试和产品运行均不调用远端服务。

桌面通过 PBIP/PBIX 文件或已打开实例连接本地 Desktop 后，可编辑度量值属性、表说明、UDF、日期表和期间指标；在线连接不提供 PBIR 报表布局。编辑直接在绑定当前引擎的 TOM 原对象上校验和本地暂存，不创建私有校验模型克隆；通过独立只读连接核实原权威身份与完整基线，对同一原对象一次提交并独立回读完整定义。普通保存直接执行，设置页不提供“模型保存”分区或保存确认偏好；MCP 模型修改由宿主自动签发精确提案的一次授权，无二次弹窗，兼容级别升级等独立风险确认继续保留。不创建首次或逐轮的长期模型历史备份；必要的文件事务临时回滚材料、未知提交核验材料与业务回执按恢复需要保留，结果确定且恢复完成后清理临时材料。提交前失败或取消会结束本次事务与会话，不把本地暂存留进引擎。PBIP/PBIX 报表文件由用户在 Power BI Desktop 保存，模型修改不先改 PBIP 文件或调用 Desktop 重载、自动文件保存，也不提供连接模型的重试保存/放弃修改入口。提交结果未知时保持只读，核对后使用“重新读取模型”取得权威状态，不重放原提交。日期数据定义改变后需在 Desktop 显式刷新对应日期表，工具不自动刷新业务数据源。CLI 的离线 PBIP/TMDL 直接更新当前工程，文件写入、基线复核、临时回滚、原子替换与失败恢复仍复用共享服务。

表格模型的“编辑当前模型”在切换目标和属性时显示对应实际值，可直接编辑后点击“应用”。表说明使用本应用的 2000 字符编辑上限，超限会在准备修改前拒绝；已有说明不自动截断，DAX/UDF 代码容量保持原规则。无关统计更新、切换语言及进入设置后返回模型保留输入、选区及原生撤销历史；仅属性编辑器保持挂载，隐藏时禁用，不保留其他模型子页面或发起隐藏请求。编辑会话仅属于当前应用和模型，断开或换绑后清空，切换对象或属性时重建实际输入控件以隔离历史。当前字段发生外部冲突时保留草稿，可显式点击“载入当前值”复核后继续编辑。

Desktop 提交在原事务取得数据库提交共享锁后完成最终权威复核，并持锁至 Commit/Rollback，封闭检查到提交之间的外部写入窗口；锁不可用时拒绝提交。清理失败保留原取消或业务错误及未确认资源，重新读取或明确再次关闭只重试清理，不重放模型修改；所有拥有的编辑和预览会话排空后才报告关闭完成。

日期字段编号开关可覆盖已有标准生成列的编号并同步模型内可核实引用，人工别名保留；无法安全识别的外部 M 依赖或歧义引用会拒绝整批修改。时间智能回读当前完整生成方案，以本次勾选的最终集合新增、更新和删除输出；清空模板集删除已核验生成的期间指标，关闭配置段仅表示本次不提交。

模型配置的模板度量值、日期表、时间智能和 UDF 操作位于对应内容中。日期表与时间智能分别应用，仅校验和提交当前段，保留另一段草稿；时间智能使用当前模型已有的表、列和度量值。新建工程仍将开启的配置一次组合创建。启用与生成选项使用开关；消息统一浮动展示，保留恢复入口和禁用限制，切页或切换语言不重放已显示的静态说明。

Desktop 模型在外部刷新或修改后，操作准备阶段会自动读取最新模型定义；配置应用先更新目录与基线并保留表单输入，无需先手动点击“刷新模型”。批量度量值会核对所选对象是否变化，冲突时保留输入供复核；删除未使用 UDF 会在模型变化后重新分析并等待再次确认。MCP 在准备精确提案前更新基线，授权后仍重新核验。准备期间再次变化仍拒绝提交；待保存和待核验状态继续沿用原恢复入口。这里只读取模型元数据，不自动刷新业务数据源。

离线 PBIP/PBIR 文件保存还会核对实际模型目录中的待应用 Power Query 状态，不能只依据 Desktop 的“已保存”标志。文件恢复仅继续原事务未完成阶段，不重放已提交修改。连接模型的编辑依据实际引擎完整基线，不读取或覆盖 Desktop 尚未应用的查询草稿；请在 Desktop 核对并处理这些草稿，工具不自动应用、丢弃或刷新查询。

Power BI Service 与 SSAS/XMLA 仅查看已有定义，隐藏模板度量值、日期表/时间智能创建入口，UDF 保留查看。桌面移除旧的变更预览与副本流程；CLI 的显式副本命令仍保留独立确认流程。必要的事务恢复材料不包含 `cache.abf`、`localSettings.json` 等本地缓存状态，不作为长期模型历史备份。

侧栏搜索支持 `/` 快捷键、功能筛选和直接跳转，连接页常用入口已移除。侧栏提供连接、模型、报表、新建、示例、学习和设置入口；用户权限配置模块已移除。 学习页在独立的受限 WebView2 中浏览 `https://jiaopengzi.com/`，并提供默认浏览器打开按钮；外站没有应用消息桥接或宿主对象，应用自身仍禁止 iframe。浏览器模拟预览显示占位说明，不作为真实网站嵌入证据。

侧栏“新建”和“示例”分别进入独立工作区，不再使用二级页签。“新建”提供五步 PBIP 脚手架：必选模板，可选模拟数据、模板度量值、日期与时间智能、UDF。选择目录且基础项有效后，可在任意步骤创建，内部完成冻结校验与发布。内置空白、页面和零售三种模板；用户模板放入 `.pbi-utils/templates/pbip/<模板目录>`，每个目录只放一个完整 PBIP 工程，界面提供打开目录入口。旧初始化协议仍已移除。“示例”完整迁移 [Power-BI-custom-sample-data 固定提交](https://github.com/jiaopengzi/Power-BI-custom-sample-data/tree/197aaf4b6c3fac28990814851640c21796d49263) 的业务规则与必要资源，支持双语、日期窗口、产品/门店数量、入库周期、全量生成、增量续写、目录统计与清空。输出为 `data/` 下 11 张 BOM UTF-8 CSV 与 `pbip/demo.pbip` 完整语义模型和报表。默认目录为用户主目录下 `PowerBISampleData`；目录由原生窗口选择，覆盖与清空由独立 Vue 确认授权，进度来自实际阶段，发布后等待最终结果。实现、源清单与验证边界见 [示例数据迁移验收](.docs/sample-data-migration.md)。示例页按原版精简说明, 目录控件同行, 所有操作使用本地图标, 只显示单条进度；初始开始日期由结束日期减 1600 个日历日计算。界面修复与本轮验证见 [示例页修复](.docs/sample-data-refinement.md)。

```text
pbi-utils inspect --project <PBIP> --json
pbi-utils new templates --json
pbi-utils new plan --request <新建配置.json> --output <新目录> --json
pbi-utils new create --request <新建配置.json> --output <新目录> --approve <预览digest> --json
pbi-utils udf catalog search --query <文本> --json
pbi-utils udf catalog show --package <ID> --version <版本> --json
pbi-utils udf catalog search --source <来源ID> --source-baseline <来源基线> --query <文本> --json
pbi-utils udf local list --json
pbi-utils sample generate --output <示例目录> --request <参数.json> --json
pbi-utils sample append --output <示例目录> --request <参数.json> --json
pbi-utils sample inspect --output <示例目录> --json
pbi-utils sample clear --output <示例目录> --confirm --json
```


新建只发布到尚不存在的本机目录，不覆盖已有工程。`new templates` 返回当前模板 ID 和基线；最小新建配置如下，`templateBaseline` 使用实际返回值，`projectName` 与输出目录末级名称一致：

```json
{"schemaVersion":1,"templateId":"builtin:blank","templateBaseline":"<模板实际基线>","projectName":"MyPowerBIProject","locale":"zh-cn"}
```

可选段为 `measures`、`sample`、`calendar`、`periods`、`udfs`、`templateQueries`、`templateQueryGroups`，省略或 null 表示跳过对应配置或覆盖。预览生成真实隔离产物，准备完成后15分钟有效；创建须复用同一配置、目录和完整摘要。修改任何配置应重新预览。Git 默认关闭；若使用 `--git`，plan/create 两次都需提供，只初始化仓库，不暂存或提交。已发布后的核验或 Git 分项失败保留真实创建事实，再次请求不会重放发布。

新建的“模板”步骤可选 `Hour24`、`MQHY`、`RefreshTime` 和 `TypeDetails` 四项辅助表/查询，通过 `PQ(M)` 按钮预览只读代码，未勾选也可复制。前后端共享 `web/src/features/scaffold/templateQueries.json` 固定目录，代码中的业务列名保持原文；默认 `Hour24`、`MQHY`、`RefreshTime` 分组为 `model`，`TypeDetails` 为 `config`，可独立编辑，留空表示不分组。分组最多 200 个字符，不接受控制字符或非空的全空白名称；正常中文与内部空格保持。选择和分组草稿在步骤、语言和页面往返中保留，只有选中项的分组参与本次校验与创建。

选中的 `Hour24`、`MQHY`、`RefreshTime` 在语义模型中启用加载，使用完整类型列和 Import M 分区，分组作用于分区；`TypeDetails` 保留为不加载的查询。新建与示例统一使用 `Measure`、`Calendar`、`Hour24`、`MQHY`、`RefreshTime` 表名，零售日期镜像为 `CalendarMirror`。`RefreshTime` 通过本机 `DateTime.LocalNow()` 在实际刷新时生成文本。无示例数据时，仅勾选该辅助表才创建；开启示例数据时，基础度量所需的刷新表会自动保留，并复用已选表和分组。空白模板未选任何辅助项且未启用示例时保持空白。日期函数仅生成并核验当前 v5 定义，以显式最后一步返回类型包装结果；不保留旧生成器重建或迁移。元数据解析和回读不代表 Power Query 已执行。

CLI 可通过 `"templateQueries":["Hour24","MQHY","RefreshTime","TypeDetails"]` 选择，省略、null 或空数组均不增加查询；可用 `"templateQueryGroups":{"Hour24":"业务辅助","TypeDetails":""}` 覆盖已选项的分组，未提供覆盖沿用默认分组，旧最小配置继续有效。该入口仅用于新建工程，不扩展当前模型配置；内置原有辅助查询保持，同名用户对象由后端拒绝覆盖。

新建工程将 Power BI 文件放在 `pbip/`：入口为 `<项目名>.pbip`，绑定同层的 `<项目名>.Report` 和 `<项目名>.SemanticModel`。`data/`、`config/` 与 `pbip/` 同级。内置模板数据源改为项目内文件，未选择模拟数据时保留空表结构；零售示例未提供的组织等业务表仍为空，并在预览列出适配。选择模拟数据后复用“示例”共享生成器，原始十一张 CSV 位于 `data/`，模板适配 CSV 位于 `data/model/`，模板配置 CSV 位于 `data/config/`，不再生成 `data/sample/`；未选择时不生成原始十一表。`config/` 仅保留实际使用的配置 JSON；资产归属、UDF 精确安装锁及原始许可保存在模型注解中，不额外生成 scaffold 清单、README、配置归属清单、UDF 锁或许可目录。移动工程后，这些文件数据源需在 Power Query 更新一个指向项目根目录的 `PbiUtilsProjectRoot` 参数。

日期表分区调用独立的自包含 Power Query M 函数查询，不生成日期 CSV。函数固定命名为 `PbiUtilsCalendar`，调用为 `PbiUtilsCalendar(...)`；切换字段、编号、年份或语言复用同一函数。只管理当前固定名称和完整归属有效的函数；旧哈希名称不再自动迁移，未知同名对象不会被覆盖。同一模型只配置一张独立生成日期表；内置模板已包含 `Calendar`，另建不同名称的日期表会明确拒绝，保留原模型。函数按最终字段依赖生成代码，含农历字段时内嵌315字节固定年规则，覆盖公历2000–2099年；仅含公历字段时支持1900–2100年且不携带农历算法。相同显式参数、输出映射和工作日覆盖得到相同结果，不读取网络、文件、系统日期或区域设置，也不调用百度 API。新建与模型页共用“日期表 / 时间智能”两个页签、63字段平铺选择、推荐15字段、简单5字段、固定01–63编号开关及上次方案记忆；已有受保护列保留原名称与引用，编号开关只影响新增列。日期页底部使用共享编辑器只读展示并分别复制函数定义、调用代码，关键注释与参数说明跟随当前界面语言；预览不提交模型，日期段关闭时不随本次创建或应用写入。M 及 DAX 格式化都在本地完成，保留字符串、注释与标识符内容，不调用在线格式化 API。每周工作日可选，并支持 CSV/TSV 三列 `date,isWorkday,name` 内容导入，ISO日期如 `2026-10-01,false,假期`；确认前预览冲突，默认保留原草稿，单批最终最多10000日且规范化JSON不超过512KiB。工作周和用户覆盖是随M保存的业务快照，传统节日标签不代表官方调休安排。仅支持当前 schema3 独立函数日期表，不读取或迁移旧 CSV、内嵌分区或旧生成归属；函数和表的完整归属、绑定及冻结代码指纹同时核验。simple/full 快捷方案与显式字段均采用当前字段编号和名称规则。

模型页对已有且归属有效的日期表提交显式字段方案时，会移除未选中且没有已识别模型引用的生成列，保留列沿用原名称和源列。完整归属和逐列映射均有效的内置日期镜像随字段方案同步；内置 Q&A 只有与固定模板资源按实际列名重建后的完整内容一致时才同步，人工改动及未知内容保持保护。日期键、关系、表达式、层级、权限、透视、翻译及其他 Q&A 绑定继续受保护；整表或动态查询引用无法确认列粒度时保守保留全部字段，因此实际输出可能超过5或15列。零售模板因已有业务引用保留月初和年初字段，简单/推荐方案分别为7/17列。预览、确认摘要和实际候选共用同一列规划。报表视觉对象的列引用仍需另行核查，不能以 TOM 元数据验证代替 PBIX 报表检查。

时间智能提供51项。模型页按本次完整选择同步已核实的生成输出，取消期间或基础度量会移除不再需要的输出，依赖自动保留；明确启用且期间全不选时清空已核实输出，关闭时间智能仍表示跳过。只管理携带当前完整归属和来源注解的生成输出，旧输出不再自动推断或迁移，未知或人工改动对象保持保护；存活模型引用会阻止删除或移动，报表视觉对象引用仍需另行核查。DAXLib 以 package 为选择单元，可固定精确版本；所选版本的全部函数和必要依赖一并冻结，预发行版本及其声明依赖自动按固定版本解析，选择 UDF 时仅将新项目兼容级别提升至至少1702。本地函数的创建/更新/删除使用 `udf local create|update|delete`；更新与删除需当前 `--baseline`，删除另需 `--confirm`。生成定义可回读不代表 Power Query/DAX 引擎已实际执行。

新建内置素材位于 `src/PbiUtils.Infrastructure/Scaffolding/Assets/pbip/blank`，保留该模板的目录原名、原始内容和版权。用户模板继续从本地模板目录读取。资源目录与新建产物的职责见 [新建资源说明](src/PbiUtils.Infrastructure/Scaffolding/Assets/README.md)。
可选的 Git 初始化只在新工程的 `pbip/` 目录执行，不把 `data/`、`config/` 纳入该仓库。无论是否勾选初始化，都会生成 `pbip/.gitignore`，包含 `**/.pbi/localSettings.json` 和 `**/.pbi/cache.abf`；用户模板已有忽略规则保留，只补充缺失项。
桌面新建按“模板 → 模拟数据 → 模板度量值 → 日期与时间智能 → UDF”配置, 数字与文字所在的整步均可点击, 支持键盘往返并保留编辑, 当前步骤使用主题黄底圆圈、深蓝数字及加粗标题标明位置。直接点击步骤导航, 不另设上一步/下一步按钮; 目录选择、Git 初始化与创建按钮共用顶部吸附操作栏。Git 默认关闭, 未安装或检测失败时提示并停用初始化, 工程仍可创建。选择目录且模板、项目名称等基础项有效后, 可从任意步骤创建, 无需浏览全部步骤。只校验和应用已开启的可选配置, 关闭的配置保留编辑但不参与本次创建; 输入错误切换到对应步骤、显示字段错误并聚焦真实控件。创建动作内部准备并校验冻结计划后发布, 页面不提供整份工程的代码或项目文件预览。模板显示其目录原名, 不翻译; 用户模板目录与内置目录重名时须重命名后刷新。模板度量值提供 Desktop 文本度量值的14种标准数据分类, 使用双语标签并保留精确模型分类; 已移除“刷新时间”应用扩展, 已知旧偏好只读转换为未分类并保留其余自定义内容。

新建时开启模拟数据会覆盖模板中的同名模拟表数据源，并复用示例模型的关系和基础度量；模板额外字段保留兼容结构且值为空。模板度量值开启时，全模型同名度量按配置原位置覆盖，不存在则创建；不要求旧生成标记。完整有效配置在创建准备阶段记忆，包含新增、移除、关闭条目和空列表；关闭整个度量段不会清空上次配置。偏好保存使用内容基线和独占锁，不覆盖其他窗口的新配置。

模板度量值在新建和模型页共用 TMDL / Config 开关，默认 TMDL。新建两种模式均建立同级 `pbi-utils/` 与 `pbip/` 目录；TMDL 将参数值直接写入度量值，不生成配置文件。Config 使用 `pbi-utils/config.json` 的 `MeasureTemplates` 段，每项包含 `name`、`value`、`dataCategory`、`status`、`description`、`displayFolder`，由隐藏配置表加载，度量值使用 FILTER 和 CONCATENATEX 按名称筛选并拼接原值；ImageUrl 检测完整 SVG 后补 data URI 前缀，已有 data URI 保持。Config 表达式经本地 DAX 格式化后写入模型和 TMDL，不修改 JSON 原文。模型页读取文件中的当前配置，支持增删改查；手工编辑文件后可“重新读取模板配置”，写入时核对读取版本，拒绝覆盖后续并发修改。修改 JSON 后需在 Power BI Desktop 显式刷新配置表，应用不自动刷新数据源。模板度量值和参数对象按当前请求创建或覆盖，不因缺少生成标记或人工编辑而阻止切换；已有度量保留父表和绑定。切回 TMDL 时移除本功能配置段并清理无引用参数表；仍有其他引用的参数表保留隐藏值快照，继续完成切换。页面、权限和其他自定义配置保持，模式切换中仍被引用的旧项保留；同模式明确删除仍核对存活引用。宿主从已核实的本机 PBIP/PBIX 文件绑定确定配置根：普通 PBIP 和 PBIX 使用文件所在目录，PBIP 位于 `pbip/` 时使用上一级目录。无需预建 `pbi-utils/`，缺目录或文件时两种模式均可选，读取不创建文件，首次应用 Config 时创建配置目录和 JSON。路径类型冲突、链接或 JSON 无效时回退 TMDL 并保留原文件；无可信本机文件绑定和远端只读目标不开放 Config 写入。已连接 Desktop 的切换提交原引擎，PBIP/PBIX 仍由用户在 Desktop 保存。

创建后, 新建页保留创建结果及宿主绑定的项目目标, 提供打开项目目录、Power BI Desktop 打开和创建新项目; 不再提供追加配置或重新核验工程入口。只有创建并核验成功的目标可打开; Desktop 打开并连接成功后才切换工作区。模板度量值、日期与时间智能及 UDF 的后续增删改查统一在模型模块操作, 复用创建前的配置组件和生成规则。模拟数据仅对宿主已核实的本地 PBIP 文件目标提供。显式模板度量配置按本次选择直接创建或覆盖；其他功能的已有对象和文件更新仍需要可验证的生成归属。所有写入保持完整基线、必要事务恢复材料和提交校验。模型页普通保存直接提交并权威回读, 不创建长期模型历史备份; 离线文件事务仅补未完成阶段, 已连接 Desktop 提交前失败结束当前操作, 未知提交保持待核验并阻止重复写入。安装 UDF 先检查实际模型级别, 已达1702直接通过; 不足时再核实当前绑定 Desktop 的真实产品版本是否达到June2026正式版, 并确认本轮实际引擎支持升级。新版 Desktop 可保留1606旧模型, 安装函数时仍需独立确认升级; 离线低级别项目也需本次独立确认。Desktop 将兼容级别与完整 UDF 候选放在同一服务器事务提交, 再完整回读; 远端继续只读。

自定义模板管理使用 Vue 弹窗展示固定目录、模板状态与使用说明。首次打开管理目录时创建 `templates/pbip/readme.txt`, 已有文件及用户编辑保持不变; 刷新不按同名或新基线静默替换原选择。完整创建成功只显示一次完成反馈, 部分完成或待核验事实继续留页处理。生成归属和 Desktop 基线采用可写定义指纹, 保留表达式、业务注解及已有血缘校验; 文件保存失败仅完成同步尾部, 不重放模型修改。实现见 [261017-04](.docs/261017-04/reports/acceptance.md), 后续真实创建、连续配置、模型页写入和重开验证见 [隔离自检报告](.docs/261017-04-selfcheck/reports/acceptance.md)。

UDF 首次使用先选择来源并点击“更新来源”，将完整函数库、支持的全部历史版本、函数、依赖与许可缓存到当前用户 `.pbi-utils/udf/catalog/`。再次进入、搜索、查看标签和准备导入均只读取本地，只有手动更新才联网。最近成功更新时间表示缓存获取时间。更新先完整校验再原子切换；失败或取消保留旧缓存与原选择，不把旧零散查询缓存视为完整初始化。

UDF 目录以 package 为最小展示、搜索和选择单元，卡片展示包图标、名称、精确版本、版本更新日期、说明、标签、作者与函数数量。搜索覆盖包名称、说明、作者、标签及包内函数名称/说明，大小写无关并支持顺序模糊匹配，命中包只出现一次。卡片选择和“选择本页函数包”加入完整版本的全部函数，换版以新版本的完整函数集合替换；隐藏或跨页选择保留。默认50包/页，可选100、200、500包。所有来源的日期均来自已固定仓库提交中，该精确版本目录最后修改的 Git 提交者时间，不依赖 DAXLib 目录 API 或各托管平台的日期接口，也不代表正式发布日期。仅点击“更新来源”时，通过标准 Git smart HTTP 有界读取历史，并连同完整快照原子缓存；搜索、详情和切换版本均只读本地。日期规范为 UTC 保存，卡片按本机时区显示 `YYYY-MM-DD`，详情显示完整日期时间。合并提交若沿用父提交中相同的版本目录内容，则跟随首个内容相同的父提交；合并产生新的目录内容才取该合并提交的时间，不按时间戳大小判断最后修改。历史范围内无法证明的日期显示 `-`，不以仓库 HEAD、缓存获取时间或本地文件时间补造日期。

包详情使用最大1200px的共享弹窗，顶部横向展示图标、版本、日期、作者、标签及函数数量，Markdown使用完整正文宽度。优先完整渲染 `manifest.readme` 指向的包内 Markdown；未声明时查找根 README.md/readme.md，没有文档时渲染 manifest.description。全部函数原始代码仍可查看和复制；变更说明、依赖声明和包许可不再作为详情内独立模块重复展示，后台依赖解析、许可和精确锁保留。包详情每次打开或切换精确内容身份时从正文顶部显示，正文双轴归零；同包语言切换和普通重渲染保留当前阅读位置。标题、列表、表格、代码块与文档内锚点统一安全渲染，原始 HTML、远程媒体和危险协议不执行。

本地自定义函数按大小写无关名称归为一个逻辑包，不同版本作为tag收拢展示。浏览、搜索、分页和管理器每包只显示一次，tag按SemVer降序排列；明确显示的tag优先，没有显示草稿时优先已选tag，否则取最新稳定版或仅有预发行时的最高tag。查看、编辑与删除精确作用于当前tag，删除一版保留其他版本；同名同tag不能重复创建，新增/修改冲突保留旧数据。保存新tag会定位该tag，但不静默替换已有选择；明确勾选、换tag、本页或全量选择才原子替换同名旧版本，失败/取消保留旧选择。全量预览每个本地包只取当前tag，隐藏选择和其他来源不受影响。宿主精确版本ID与baseline继续约束CRUD及依赖，不按显示名称重绑依赖。

新建与模型复用同一选择区；新建随工程创建导入，模型在已选区点击“导入所选 UDF”。版本、来源或目录基线变化后仍须重新核验，不能静默换版或部分加入。已有本地包的跨tag导入复用完整生成归属和模型基线核验，并要求同批重解析已安装的本地函数以确认精确依赖；仍绑定旧tag的依赖明确拒绝，不按同名改绑。

来源管理支持遵循 [DAXLib 仓库布局](https://github.com/daxlib/daxlib)的 HTTP(S) Git 仓库, 包括 GitHub、Gitee、GitLab 和无需登录的内网仓库。默认选择 DAXLib (`https://github.com/daxlib/daxlib`), 新登记来源立即进入来源列表。地址支持 `.git`、项目多层路径及非默认端口; GitHub 仍使用 HTTPS 标准端口。可指定最多256字符的分支/标签/提交, 留空使用默认分支。GitHub 更新复用固定提交、完整树与归档验证; 其他仓库经 Git smart HTTP 获取并核验 commit/tree/blob 与 PACK 完整性, 从 `packages/<分组>/<包ID>/<版本>/manifest.daxlib` 和 `lib/functions.tmdl` 读取。不依赖本机 Git, 不执行仓库脚本、checkout 或子模块。保存配置不联网, 不保存账号/令牌, 不支持认证或 SSH。内置来源不可修改; 第三方来源按宿主稳定 ID 和配置基线隔离。来源间依赖和许可不按同名回退; 允许内网地址, 仍拒绝回环、链路本地、特殊保留地址和重定向。未知或损坏配置保留并拒绝覆盖。PBIP 创建时可选的 Git 初始化仍单独检测本机 Git。

“选择当前来源全部函数”从同一完整本地目录预览，确认后一次加入选择；搜索条件不限制全来源范围，超限、失败或取消不会部分加入。模型 UDF 页提供未使用函数 lint，按模型内 DAX 引用及传递依赖分析，预览完整删除集合并取得适用删除确认后，经共享模型编辑通道一次提交并回读；未知语法或过期基线禁止清理，远端只读。

CLI 使用 `udf catalog status` 查看完整缓存状态, `udf catalog refresh` 显式联网更新; `search/show` 只读本地。仅保留当前最新完整缓存, 不提供缓存历史或回滚命令; 当前缓存包含函数库支持的全部版本。目录命令可提供 `--source` 与 `--source-baseline`。第三方选择继续携带稳定 `source`、配置 `sourceBaseline` 和目录 `catalogBaseline`; 单选与多选均可保留同库精确历史 tag, 未固定版本时解析最新稳定版。精确选择的预发行包及其声明依赖自动按固定版本解析, 无需勾选允许开关; 版本冲突、来源/目录基线及依赖许可仍完整核验。已选区和全来源预览按包显示来源与版本, 每个函数独立编号并完整换行。配置、目录或选择变化后须重新预览, 创建继续使用已冻结内容。

更新来源显示整数进度与明确终态，获取时间按本机时区显示为 `YYYY-MM-DD HH:mm:ss`。缓存刷新绑定操作开始时的完整指针修订并保留固定提交追溯，失败或取消不切换。成功后清理无读者占用的旧代；在途读者保持原快照，占用代延后到后续成功刷新清理。目录指针仅接受当前 schema3，包摘要索引采用当前 schema5，包含精确版本的 Git 提交日期 `committedAt`；旧 schema2/3/4 索引不兼容读取或迁移，需用户显式更新来源后整体重建，防止将旧 API 发布日期混为 Git 版本更新日期。当前快照仍包含支持的全部包版本；缺失或损坏的当前缓存明确失败并保留原文件。
脚手架实现、验证范围与清理例外见 [261017-01 验收报告](.docs/261017-01/reports/acceptance.md)。

示例参数文件格式（生成与增量共用）：

```json
{"locale":"zh-cn","productCount":666,"storeCount":55,"inventoryCycle":22,"startDate":"2022-01-01","endDate":"2026-10-14"}
```

产品/门店数量 1–10000，入库周期 5–180，日期窗口至少 60 天。`seed` 可选，仅用于固定本项目随机序列；省略时使用随机种子。C# 与 Go 的随机序列不同，保持业务规则与分布，不声明同种子逐字节等价。桌面增量起点必须为已有事实截止日的次日；CLI 与上游核心一致, 允许其他与现有事实完全不重叠的窗口。产品、门店、客户复用已有记录，订单/明细/入库主键继续递增，重建仍存在的销售目标，日历年份只扩张。CLI 全量覆盖需 `--overwrite`，清空需 `--confirm`；清空删除固定 11 张 CSV 和整个 `pbip`，保留根目录及未知数据文件。生成在隔离目录进行，发布前复核原目录基线，取消与冲突保留原产物，提交后不再响应取消。它不修改当前连接的模型，也不调用 Desktop 或远程服务。

实现与分项证据见 [学习开发记录](.docs/learning-implementation.md) 和 [本轮原型差异验收](.docs/prototype05-acceptance.md)，第三方来源见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。未实测的 UDF 模型持久化及 Desktop 组合仍按实际证据标为 UNRUN 或 FAIL，不沿用历史 PASS。

本轮 P0-01 至 P0-05 和相关配套工程的变更、实际红绿测试、独立发布 smoke、UNRUN 与回退见 [可靠修改验收](.docs/reliable-modification-acceptance.md)。未做持久多文件崩溃事务或自动清算未知提交。


## 验证与设计

开发约定见 [AGENTS.md](AGENTS.md)，可复用开发流程与按模块规则见 [项目开发 skill](.agents/skills/pbi-utils-development/SKILL.md)。当前验收以本次改动实际执行的检查、测试与发布结果为准，使用 PASS、FAIL、UNRUN 区分；浏览器模拟、真实发布 WebView2、Desktop 模型执行/保存和真实远端验证分别记录。

### 历史验收记录

以下保留早期基线及人工验收事实。其中 Hello/mock 演示入口和命令现已移除，相关历史 PASS 不作为当前功能契约或本轮回归结果。

2026-10-09 第一阶段完成：清单 24/24 项 PASS。43 项自动测试、真实桌面 Hello/mock Agent/取消/关闭清理与本地发布验证通过；独立无开发工具机器与无外网运行由用户实测确认，证据来源和限制分别记录。详见 [验收记录](.docs/acceptance.md) 与 [人工验收记录](.docs/evidence/remote-acceptance.md)。这不代表生产发布、真实模型接入或旧项目功能迁移完成。

- [实际验收状态和证据](.docs/acceptance.md)
- [架构与依赖边界](.docs/architecture.md)
- [桥接协议](.docs/bridge-protocol.md)
- [历史 mock Agent 设计](.docs/agent.md)
- [发布、离线验证及后续安装方案](.docs/publishing.md)

品牌图标、主题色和本地资源来源见 [品牌说明](BRANDING.md)。

根 AGENTS.md 和项目开发 skill 是开发约定，不能作为产品内系统提示词或用户数据发送给模型。稳定规则与模块参考保持一致，开发日期流水账及当次验收证据留在相应任务文档中。

连接页和其他保留的二级页签统一使用共享 `UiTabs` 的强调样式；整组锁定期间阻止鼠标和键盘切换，保留 Tab 离开焦点。桌面示例页在下次打开时恢复上次结果目录和统计；固定产物的文件元数据一致时快速加载缓存，变化后重新读取实际文件。切换目录时先匹配当前宿主目录，跳过无关旧缓存目标；实际目录检查在后台执行。全量、增量和清空成功后更新缓存，恢复显示不重复生成或弹出完成提示。缓存只保存目录和统计，不复制 CSV 或 PBIP，也不代替覆盖、清空确认或发布前基线复核。CLI 继续按显式输出目录执行，不使用桌面结果缓存。

首次读取已有示例数据或选择目录后统计时，显示可取消的加载提示；请求结束后关闭，不将恢复已有数据提示为生成成功。

## 用户数据目录

所有活动用户数据统一位于 `%USERPROFILE%\.pbi-utils`，从操作系统获取实际用户主目录，不硬编码用户名、不依赖启动目录。

- `ui-settings.json`：统一中文/英文与最近文件显示偏好；默认中文并显示最近文件，保存成功后生效。仅接受当前版本 2 的完整偏好，不读取或迁移版本 1；损坏、重复字段或未知版本保留原文件并拒绝覆盖。
- `product-updates.json`：自动检查开关、所选发布来源及各来源最近检查结果，原子保存并独立互斥；不保存访问令牌或下载文件。
- `sample-data-result.json`：桌面示例页的最近结果目录、十一张表的行数/大小、事实截止日及固定产物元数据；损坏或未知版本保留原文件并回退实际文件读取。缓存故障不改变已生成、增量或清空的业务结果。
- `templates/pbip/`：用户 PBIP 模板目录，每个模板保留完整工程结构；新建页可打开目录并刷新选择。
- `udf/sources.json`、`udf/local/`、`udf/catalog/`：第三方 UDF 来源配置、按稳定 ID 保存的本地函数及按来源/配置隔离的完整不可变目录代、包内容和成功更新时间。`udf/cache/` 的历史零散缓存保留，不自动认作完整目录。来源和本地函数的更新、删除必须匹配当前基线；损坏或未知版本配置保留并拒绝覆盖。
- `scaffold/plans/`：新建预览与创建回执；提交状态保留实际发布事实，不通过删除记录重放未知提交。
- 模型及文件事务恢复材料：仅保留当前失败恢复或未知提交核验所需的材料；结果确定且恢复完成后清理临时材料，不创建首次定义或逐轮提交的长期模型历史备份。
- `mcp/executions/`：独立 MCP 执行回执，保存运行身份、请求摘要、提交状态和人工核验元数据，不保存聊天或参数正文。未知提交阻止新的 MCP 写入和用户数据重置。
- `recent-projects.json`：最近20个成功连接的 PBIP/PBIX 文件，按本机路径身份去重排序；连接页点击重开时通过 Desktop 连接，隐藏模块保留历史，文件不存在或读取失败会明确报错。
- `remote-connections.json`：远端连接表单最近20条连接尝试；保存的 Windows 密码为当前用户 DPAPI 密文，分别绑定目标、模型和账号。文件复制到另一台机器或用户后，密码可能无法解密，需要重新输入；可单独清除密码或删除连接记录。
- `service-connections.json`：最近20条已提交的 Service 登录邮箱及云环境，包含未成功的认证尝试；按邮箱与云去重，可删除或取消记住，不保存密码或令牌，复用共享文件锁及原子保存。
- `service-recent-models.json`：成功连接的 Service 模型记录，按真实认证账号与云环境隔离；登录后以最新可访问目录过滤，模型和工作区使用服务标识匹配，不保存令牌、服务器或连接串，损坏或未知版本保留原文件并明确提示。
- `WebView2/`：桌面应用界面的浏览器运行数据。
- `WebView2Learning/`：独立学习网站浏览器的数据，与应用界面和消息桥接隔离。
- `.lock`：跨进程存储锁文件。

业务数据只在当前用户 `.pbi-utils` 目录创建和读取。共享后台的启动/维护协调及用户数据会话租约统一使用用户 Profile 下独立的 `.pbi-utils-runtime` 目录；后台锁按用户数据根与安装身份隔离，用户数据租约按数据根隔离。该目录位于应用数据树之外，避免打包客户端对 AppData 的重定向分裂跨客户端锁。重置先取得独占维护租约，确认无其他客户端并排空后台、GUI 与日志后执行，期间阻止自动重连；不删除或替换持有中的协调锁。环境变量中的密钥不会自动写入磁盘。


设置页从上到下展示本地 MCP、常规（含业务日志）、软件更新、运行环境和本地数据，仅移除关于中的产品介绍。常规包含语言和 DAX 的 Long / Short 格式偏好，不提供“模型保存”分区或保存前确认开关；中文和英文覆盖全部前端页面、公共导航、日期控件和编辑器搜索。新建 / 示例使用同一偏好，移除独立语言按钮；生成使用当前语言，已有产物保持原有语言。名称和表达式原文保持不变，不联网翻译。实现与验证见 [设置迁移与多语言](.docs/settings-language.md)。

进入设置页自动读取运行环境，保留双语显示、加载和错误反馈，以及离开时取消未完成请求；也可使用 CLI `doctor` 读取诊断。

设置页“业务日志”可选择最低日志级别（默认 Information），保存后当前进程即时生效，后续启动的 CLI/桌面沿用保存值；“打开日志目录”在资源管理器打开当前用户 `.pbi-utils/logs`。日志记录业务开始、结束、耗时、失败与取消，使用关联 ID 串起嵌套操作；不记录凭据、聊天正文或模型定义。逐行 JSON 日志按天及 10 MiB 滚动，每类最多保留31个文件和14天。设置位于 `.pbi-utils/logging-settings.json`。规范、读取方法和验收见 [业务日志](.docs/logging.md)。

业务日志统一调用 `BusinessLog` 的强类型事件方法，日志等级判断、异常安全字段和诊断参数计算集中在封装中；固定事件由编译期生成器实现。关闭 Debug 时不采集 Windows 身份或提取 XMLA 错误码。模板、字段、等级及业务关联保持，事件增加稳定名称以便定位。实现与验证见 [日志统一封装](.docs/logging-encapsulation.md)。
