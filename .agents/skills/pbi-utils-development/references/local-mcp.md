# 本地 MCP、业务覆盖与客户端接入

修改本地 MCP、工具 schema、客户端配置、桌面本机通道或相关发布时读取. 产品不内嵌聊天、提供方目录、AI 密钥或会话管理; 不以恢复旧 Agent 项目实现外部 AI 接入.

## 入口与所有权

- 访问真实用户回执的验收须跨进程串行: CLI 测试的 `CLI User Data` 集合与独立 MCP smoke 共用 `scripts/tests/AcceptanceUserDataLease.cs` 测试租约. PowerShell 通过 `AcceptanceUserDataLease.ps1` 取得租约并在子进程排空/回执清理之后 finally 释放. 新的同类脚本沿用该入口; 子产品进程不重复申请, `mcp-write-positive.ps1` 由其 CLI 测试集合持有, 避免父子等待. 此锁只协调测试, 不替代产品并发保护, 不控制用户手动启动的程序; 超时明确失败, 不删除产品锁或清空回执.

- `src/PbiUtils.Cli/Mcp/` 以 `pbi-utils.exe mcp serve` 托管官方 C# MCP SDK 的 stdio 服务. 产品运行无需 Node 或开发 SDK, 不新增 HTTP 端口/系统常驻服务, 不自动修改第三方客户端配置.
- 普通 CLI JSON 仍为一个完整文档; MCP stdout 则只承载协议帧, 日志和诊断不得混入. 版本由中央配置与锁文件读取, 不手写旧协议实现或擅自扩大依赖升级.
- 协议使用官方 SDK 的默认协商能力, McpServerOptions.ProtocolVersion 不固定单一版本. 当前 Codex 的 initialize/notifications/initialized 与 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28) 的逐请求 _meta 入口由同一 SDK 处理, 不另写协议代理或业务分支. server/discover 仅广告逐请求协议版本; 新版请求须携带协议/能力/身份并验证 resultType、缓存字段与版本拒绝, 握手客户端按协商版本发送后续请求. 两种入口复用相同目标绑定、写授权、取消和回执, 不因协议会话存在或缺失放宽应用安全. SDK 包版本以中央配置和锁文件为准.
- PBIP、模板、新建、日期/期间、示例、UDF、副本及模型工具都在 Host 内复用 Application/Infrastructure/Windows 共享服务. CLI stdio 仅适配官方 MCP 协议, 不持有隐藏 GUI 或重复业务; 相关 CLI 共享资源操作同样进入 Host, 纯格式化/帮助可本地执行.
- `McpDesktopCatalog` 是明确的业务映射, 参数 schema 构建期从权威 `tests/contracts/bridge.schema.json` 嵌入. 产品运行不依赖测试目录. 新增业务同步映射、描述、权限与覆盖测试; 不允许运行时任意反射/桥接方法分发.
- MCP 对外输入 schema 在共享运行时统一注册入口经 `McpInputSchema` 等价适配, 不修改权威 bridge 契约或运行时输入校验. 多类型 type 使用 anyOf, schema 位置的 false 使用 not {}, 任意 JSON 使用完整类型域; 保留 required/null/数字字符串/条件禁止/引用及业务字面 JSON. 不为消除客户端诊断删除约束或收窄 JsonElement, 不把 schema 标注当授权. 变更后核对独立 JSON Schema 接受结果及 Inspector modern --strict 的错误与警告, 同时回归真实只读调用和权限拒绝.
- 外部 MCP 与网页输入不同: SSAS连接只接受server/database, 使用当前Windows身份, 不公开密码/用户名/表单身份. GUI密码表单仍独立存在. 显式Service登录只打开微软登录窗口, 不唤起pbi-utils前台.

## 权限、身份与事务

- 默认只读, `--allow-write` 只能由用户客户端配置在进程启动时开启. SDK tool annotations 用于说明, 不代替 `McpBusinessRuntime` 的实际服务端检查. 参数中的批准、布尔值或提示词不能打开写能力.
- 选择、切换或断开宿主连接归为写工具, 需要启动--allow-write; 所有MCP通用确认移除, 默认只读可读取已连接目标, Service/SSAS模型始终只读.
- 本机管道要求 CurrentUserOnly 及同安装 PID/启动时间核验. GUI、多个 MCP 与相关 CLI 同时运行; 每个客户端拥有独立上下文, 注册时固定 kind/allowWrite. desktop_sessions 只返回本客户端身份, 错误/其他客户端 sessionId 明确拒绝; 不枚举或采用其他窗口的模型.
- MCP后台模型操作无通用弹窗, ToolWritePolicy/Proposal/Grant及真实before/after、基线、一次消费、可靠回执不变. 配置兼容升级由MCP精确请求allowCompatibilityUpgrade显式表达, GUI仍保留独立升级确认. 示例覆盖/清空MCP按明确工具意图执行, 共享归属与文件事务保持.
- 按需启动的 PbiUtils.Host.exe 不加载 WebView2. 后台不依附首个客户端, EOF 仅取消并排空本连接, 其他客户端继续运行; 最后连接及在途操作排空后退出. 写入统一协调, 冲突明确反馈; 旧观察基线、未知提交阻断及可靠回执不能换入口绕过. 故障使连接/观察票据失效, 不自动重放. 全量重置先持用户数据树之外的独占维护租约, 确认无其他客户端, 排空 Host/GUI/日志后执行, 防止自动重连抢占.
- 维护状态探测使用相互兼容的只读句柄, 不把另一探测误判为真实维护; 真正维护继续排他. 后台启动不继承启动客户端的 stdio 句柄; 启动客户端先结束时, stdout/stderr 均须有界到达 EOF, 其余客户端仍可调用, 不等待共享后台一起退出.
- 新建/离线配置按精确预览计划与摘要应用. 摘要是参数和计划绑定, 不是人类确认凭证; 不把模型自己读到摘要解释成用户批准. 已配置写权限的文件工具仍保留各用例的覆盖/删除确认、目标安全、基线及原子文件事务.
- 只有业务方法映射为工具; `ui.dialogRespond`、密码写入、页面浏览器几何、请求内部取消及全量用户数据重置不公开. 系统选择器、登录、秘密输入和确认由用户在可信本地 UI 完成. 不允许第三方 AI 伪造宿主答复.
- 调用取消经专用通道进入既有门闩, 提交前取消准备, 提交后排空实际提交、权威回读与回执. 客户端断开不能宣称未提交或触发自动重放. 未知提交可靠保留, 阻止新写和数据重置; 详细约束见 [model-safety.md](model-safety.md).
- 非抛异常不等于成功. 根据类型化业务结果区分完整成功、已知部分失败与未知提交/回读/回执, MCP 的 isError 和可靠回执保持一致, 保留工程已创建等实际事实. 已知 Git 初始化失败不能虚报创建失败或未知提交, 回读/回执未确定则仍阻止新写; 本机 IPC 不丢弃这种结果分类.
- 回执 `completed` 表示完整业务成功, `failed` 表示已知部分失败且保留已完成事实, 后者不要求未知结果核销; `needs_verification`/`commit_started` 继续阻止新写和重置. 工具错误仍返回原始分项结果, 已知 Git 失败不能通过重建已创建工程重试.
- `mcp executions` 和 `mcp resolve` 是人工 CLI 入口, 不注册为 MCP 工具. 核销绑定完整回执摘要、人工结论和固定句柄读取的证据 SHA256, 保留原始未知状态, 仅解除 MCP 阻塞; 不代替现有模型权威恢复, 不自动宣称已提交、计算或文件保存, 不重放原调用.

## 设置说明与依赖

- `mcp.configuration` 由宿主生成真实安装绝对路径和显式写入参数, 返回 executablePath/readWrite/isSimulation; 前端只展示/复制. 模拟环境明确标注, 路径不可从网页输入反向授予宿主执行能力. 请求方法与空参数契约保持, 已移除选项不保留 readOnly 输出; CLI 不带 --allow-write 的默认只读仍由服务端强制执行.
- 设置将接入和允许修改说明合并为介绍段, 不单列权限标题; 保留单一接入提示词, 内含宿主真实安装路径及 --allow-write JSON, 无权限 tab 或第二个 JSON 复制入口. 提示词先检查当前 AI 客户端已有配置: 正确则复用, 缺失才添加, 不匹配仅修正该服务必要项, 保留其他服务. 所有分支最后统一执行当前客户端实际发现, 再调用 server_info 核验 version/transport/allowWrite、desktop_sessions 读取 sessions[0].id、desktop_app_info 带本客户端 sessionId 核验后台; 只读诊断不连接或修改用户模型. 分别记录配置、发现、调用和权限, 未执行为 UNRUN, 不以 shell 另起进程替代目标客户端证据. 失败/冲突不无限重试或自动重放写入. 不要求关闭桌面或配置 LLM 密钥.
- MCP 连接管理只供真实 GUI 使用, 不加入工具目录或允许 MCP/普通 CLI 调用. Host 拥有同用户/同安装的认证连接清单, 不依网页 PID 或客户端自报名授权. 断开以准确上下文为目标, 停止新调用后取消准备、排空提交/回读/回执, 再让 CLI 自行结束; 接受请求、排空和实际进程退出分别展示, 不强杀或将 IPC 关闭冒充文件占用已释放. 未知提交与原回执保留, 管理操作不重放业务.
- 暂停接入只阻止 MCP, 先持久保存同安装准入状态并封闭注册, 再关闭现有 MCP; GUI 与普通 CLI 仍可用. 暂停跨 GUI/Host 退出保持, 明确恢复后才接纳新 MCP, 不复用要求唯一 GUI 的用户数据重置维护租约. 启动、握手和上下文组合后的登记均须防暂停竞态; 状态损坏/读取失败不能当默认允许. 更新产物前仍需正常关闭 GUI 及等待 Host 退出, 不把 MCP 空列表当全部文件已释放.
- 本机 MCP 传输不代表外部 AI 客户端不会把模型内容发送给其提供方; 向用户说明这一事实, 不宣称所有后续处理离线.
- 移除内嵌 AI 的 MAF/提供方/目录依赖, 保留仍用于 UDF 文档的 Markdown/净化/编辑组件. 官方 MCP SDK 所需 AI 抽象包按真实依赖保留, 不是内嵌模型功能. 同步 NuGet/pnpm 锁文件, 不升级无关基线.

## 验证

覆盖官方 SDK 真实 stdio 发现、无需发现直接调用、逐请求元数据与版本拒绝、真实 initialize 协商、两种入口工具列表/调用、结构化失败、默认只读、参数不能提权、计划过期/重复消费、取消/EOF 及提交排空. 共享后台检查 GUI+两个 MCP 共存、独立上下文/权限/目标/取消、错会话拒绝、旧观察基线、未知结果跨入口阻断、最后连接排空、重置和重连竞争. 核心业务行为回归继续运行, 不用工具数量断言替代业务验证.

完整门禁和发布 smoke 验证 Release 两种部署的正式入口、精确产物及实际运行时来源. `scripts/tests/mcp-background-smoke.ps1` 独立验证共享后台、双客户端共存/错会话拒绝、真实 IPC、单客户端 EOF、空闲客户端异常退出与最后排空, 不关闭用户窗口或强杀后台. 浏览器模拟、真实 WebView2、真实 Desktop 提交/计算/保存重开、联网 UDF/Service 和第三方 AI 客户端分别记录 PASS/FAIL/UNRUN. 不用原始 JSON 协议测试冒充某第三方客户端已经配置成功.

`tests/contracts/mcp-tool-cases.json` 是显式全工具回归清单, 新增/移除/权限变化须同步用例, 与运行时发现集合逐项比较. `McpToolCoverageTests` 纳入正常 C# 门禁, 执行核心只读、桌面包装拒绝、全部写入口默认拒绝及参数提权拒绝; 另用隔离目录验证副本编辑、样例和 UDF 的真实文件流程. 发布后执行 `scripts/tests/mcp-background-smoke.ps1 -AllTools`, 在自有无模型共享后台上下文运行同一清单并输出逐项结果. 必须区分 success、input-rejected、prerequisite-rejected、write-denied, 不把权限拦截当作写入正向成功. 协议中断后余项标 UNRUN; 夹具哈希和清理失败也使验收失败. 清单和脚本不依赖 `.docs` 历史快照.

`scripts/tests/mcp-inspector-smoke.ps1` 使用固定第三方 Inspector CLI, 分别核验 modern 与 legacy 握手入口的完整工具集合及零 schema 错误/警告, 并执行后台信息、纯格式化和日期预览. `scripts/tests/mcp-codex-smoke.ps1` 使用本机已安装 Codex 的原生 app-server 验证真实握手和完整工具发现, 仅覆盖本次进程配置, 不注册客户端、不创建聊天或调用模型; 缺客户端或集合不符明确失败. 两脚本默认只读, 用 -AllowWrite 独立验证设置推荐的启动配置, 仍不执行写工具. Codex 发现结果不证明业务调用或运行时权限已核验; Inspector 在两种协议下检查 server_info.allowWrite 与后台版本, 每条命令的独立上下文不可混用. 开发机工具不纳入产品依赖, 证据保存唯一本轮目录.

写入正向清单 `tests/contracts/mcp-write-cases.json` 必须与实际可写工具集合完全相同, 每项绑定实际测试方法、理论展开及副作用断言. `scripts/tests/mcp-write-positive.ps1` 顺序运行隔离用例并解析本轮 TRX, 缺失/失败/跳过均不算通过; 完成同配置构建后可显式 `-NoBuild`. 核心运行时与宿主 MCP 分发均执行真实业务服务、文件事务及回执, 云认证、外部连接/打开/选择器和 HTTP 使用受控适配器时必须注明, 不以此声称真实云或引擎已经验收.

`scripts/tests/mcp-model-write-smoke.ps1 -Prepare` 在没有现有 Desktop/产品宿主时创建独占模板夹具, 输出下一阶段完整命令. 执行阶段核验固定证据目录、文件、原进程启动时间、准确引擎和唯一模型标记, 只对该新模型执行 MCP 修改与独立回读; 发送写请求后失败保留 unknown, 不误记零写入或重放. 通过 EOF 排空自有宿主, 保留测试 Desktop 供显式计算、保存与重开; 文件保存和真实计算需另取证, 不从 modelSaved 推定成功.

写入后执行 `scripts/tests/mcp-model-verify-smoke.ps1 -FixtureRecord <fixture.json> -Stage AfterWrite`, 通过独立 ADOMD 连接核验固定探针计算. 操作者在该测试 Desktop 保存并关闭后执行 `-Stage AfterReopen`, 要求原进程已退出, 由 MCP 打开同一夹具, 核验新引擎、唯一标记、完整基线、表达式及再次计算. 两阶段均保存独立证据, 不修改/刷新模型、不重放写入, 不接管用户产品宿主; 不能把手动保存操作或旧进程回读当作重开通过.

## 有界读取与精确文件入口

- Desktop 的连接和模型操作成功结果默认投影为摘要, 保留目标、基线、保存事实与分类; 失败/待核验保持原结果。project.inspect/configuration.context 的 read 选项只在共享 MCP 运行时校验与投影, 不发送给宿主授权层。后续分页需项目与基线同时匹配, 表/列/度量/函数按需读取, 不截断定义。
- project.openPath 只允许已核验 MCP, 复用 FileAsync 与完整文件身份核验, 不开放网页任意路径。connectLocal 要求发现的 instanceId, 不能只凭 PID。实例文件路径来自官方 Bridge, 未保存/不可用保持明确状态。路径显示状态不代替稳定引擎身份。
