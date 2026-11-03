# 连接、用户数据与安全日志

涉及 Desktop/Service/SSAS 连接、最近记录、凭据、持久化或日志时读取. 模型修改与保存继续使用既有模型安全规则, 不因增加连接入口而放宽写权限.

## 连接与身份

- 桌面连接页复用 Desktop、Service、SSAS/XMLA 三种来源. PBIP/PBIX 文件由官方 Desktop 打开并连接已就绪模型; 窗口启动不等于模型就绪. CLI 的 PBIP 离线读取及显式副本流程保持独立.
- Desktop 文件入口与最近文件重开共用 `ModelConnectionService.FileAsync`. 由官方 Bridge 复核完整文件状态、PBIP/PBIR 绑定、PID、启动时间和引擎; 同文件已开时复用. 不凭标题、文件同名、单个 PID 或随机数据库名认定身份.
- 当前 Desktop 模型只按宿主返回的完整非空 `instanceId` 匹配. 扫描仅关联当前会话 PBIDesktop 与 msmdsrv 父子进程和 IPv4/IPv6 回环端口, 连接前后复核身份. 歧义、多模型、缺失身份或失效快照不得合成成功连接.
- 成功切换才替换旧工作区. 失败或提交前取消保留旧工作区; 取消等待原请求终态, 不自动关闭用户 Desktop. 待保存/待核验与在途状态继续阻止冲突操作. 当前模型的“查看模型”只导航, 不重新连接.
- 同步 SDK 调用在线程池执行, 在调用边界检查取消并释放连接. 不宣称 CancellationToken 能立即中断 SDK 内部同步调用.

入口: `src/PbiUtils.Application/Connections/ModelConnectionService.cs`, `src/PbiUtils.Windows/Desktop/Files/DesktopModelFileOpener.cs`, `src/PbiUtils.Windows/Desktop/Files/DesktopModelFileIdentity.cs`, `src/PbiUtils.Windows/Desktop/Discovery/LocalPowerBiReader.cs`, `src/PbiUtils.Windows/Models/Connected/ConnectedModelReader.cs`.

## Service 与 SSAS/XMLA

- Service 全球/中国云复用同一用例和 `PowerBiCloudPolicy`, 由固定官方服务发现与认证流程给出配置. 同一认证、目录、集群、XMLA/pbiazure 请求绑定同一云, 不跨云发送令牌, 不接受网页任意认证端点或连接串.
- 首次邮箱发现: 单云自动继续、多云明确选择; 已保存邮箱/云按现有验证流程复用. 改账号/云时先处理旧认证状态; 失败或取消不伪装切换成功.
- Service 最近模型按真实认证账号与云隔离, 登录后用最新可访问目录过滤. 工作区与模型使用服务标识和宿主绑定句柄精确定位, 不凭名称、旧网页 ID 或保存路径授权. 重开先刷新目录并同步选择; 句柄缺失不以同名替代.
- XMLA 可用的工作区与共享/个人容量使用实际支持的不同架构读取路径. 受限架构的未知定义、兼容级别和计数保持未读取语义, 不推测为完整定义或零. 共同 UI 未知占位为 `-`; 未提供报表页数的 UI 例外显示 0, 后端仍保持 null.
- ADOMD 只读架构读取只对已明确 HTTP 链中的 Socket 10053/10054 重建会话并完整重读一次, 共用原取消/超时预算. 不重试认证、权限、TLS、超时或未知故障, 不拼接两轮部分结果.
- SSAS/XMLA 表单在连接页平铺. 空闲填写不持有连接请求或 busy 锁; 提交才消费宿主一次性表单身份. 读取表单元数据不解密密码, 目标/身份变化解除旧密码选择.
- SSAS 指定 Windows 身份由 Windows 适配层在完整同步连接、读取、释放范围内模拟; 不把账号密码加入连接串. XMLA/Service 不复用指定 Windows 凭据. 不能由凭据上下文创建或 TCP 可达宣称远端认证成功.
- Service/SSAS 始终只读. 阶段错误给可操作提示和稳定安全诊断; 不根据 10054 等单个码断言密码错误, 不代改防火墙或系统认证策略.

入口: `src/PbiUtils.Application/Connections/PowerBiServiceConnection.cs`, `src/PbiUtils.Infrastructure/Connections/PowerBiService/PowerBiCloudPolicy.cs`, `src/PbiUtils.Windows/Connections/PowerBiService/PowerBiSchemaReader.cs`, `src/PbiUtils.Windows/Connections/Ssas/SsasConnection.cs`, `src/PbiUtils.Host/Bridge/Connections/RemoteConnectionForm.cs`.

## 外部 AI 客户端

产品不再持有提供方、AI 密钥或聊天会话. 外部客户端只经本地 MCP 调用已有业务, 协议与授权见 [local-mcp.md](local-mcp.md). 不将旧 AI 配置、会话或凭据读取/迁移作为当前运行依赖.

## 持久化与凭据

- 用户数据统一位于操作系统用户主目录下 `.pbi-utils`, 不硬编码用户名, 不写安装目录. 设置、模型草稿、最近记录、日志、事务恢复材料与 WebView2 按职责隔离; 不创建长期模型历史备份, 不添加浏览器 localStorage 作为第二真源.
- 持久存储复用原有修订检查、跨实例锁、原子替换、取消和损坏保护. 按各格式的严格 schema 处理重复字段、未知版本与未知字段; 只在格式明确允许时保留未知字段, 不把单一存储例外推广到全部 JSON. 不能为修复读取而覆盖损坏原文件或恢复覆盖用户并发修改.
- 本地最近文件和 Service 最近模型各自隔离. 只用宿主绑定记录 ID 删除历史, 不删除工程/远端模型或断开当前工作区. 确认期间锁来源/导航; 取消、目标改变或卸载不得删除. SSAS/XMLA 不新增连接页最近模型模块.
- 远端连接密码由 C# CurrentUser DPAPI 保护, 按目标/账号隔离, 损坏不得降级为明文或静默改变认证身份; 不保存云令牌. 产品不再管理 LLM API 密钥.
- 新 SSAS 密码只通过可信本地 Vue 限定字段和宿主签发的一次性表单身份进入后端. 不接受 MCP 密码参数, 不回传既存明文/密文/令牌; 清空状态和真实 DOM, 禁用浏览器密码保存和自动填充.
- SSAS 保存密码按服务器、模型、认证模式和账号共同绑定 CurrentUser DPAPI; 最多20条最近连接. 临时明文缓冲区清理, 不进入网页存储、草稿、历史、日志、错误正文、测试快照或仓库. 其他实例清除已存密码后不得静默改用当前身份.
- UI 语言/显示偏好、日志级别和 MCP 一次写授权独立. 显示与日志偏好不能授予模型写权限; 不提供“模型保存”设置或普通保存确认偏好. MCP 精确提案仍须独立的一次授权. 当前语言持久保存成功后才切换显示.
- 示例结果缓存仅保存目录和统计. 命中先核当前规范化目录及固定产物元数据; 真实目录/链接探测在后台门闩内执行. 缓存不复制产物、不授予网页路径权限、不代替覆盖/清空确认; 缓存失败不抹除已提交业务事实.

入口: `src/PbiUtils.Infrastructure/Connections/LocalRemoteConnectionStore.cs`, `src/PbiUtils.Infrastructure/Connections/LocalRecentProjects.cs`, `src/PbiUtils.Infrastructure/Connections/PowerBiService/LocalPowerBiRecentModelStore.cs`, `src/PbiUtils.Infrastructure/Gui/UiDialogBroker.cs`, `README.md` 的用户数据目录说明.

## 日志与调试

- CLI/Desktop composition root 创建并拥有 `LoggingRuntime`. Serilog 及文件 sink 只在 Infrastructure; Application 注入 `ILogger`, Core 不依赖日志库. 不另建全局 Logger 或旁路日志文件.
- 业务入口共用 `BusinessLog.Run/RunAsync` 或 `BusinessOperation`: Started 与唯一 Completed/Failed/Cancelled、ElapsedMs、宿主 RequestId、嵌套 OperationId/ParentOperationId. 工具调用前校验失败也属于业务边界; 纯校验/格式化/映射/哈希及流式片段不逐项记常规日志.
- 固定结构化事件在 `BusinessLog.Events.cs` 封装, 使用强类型方法和 LoggerMessage. 昂贵诊断先在封装内判等级, 不拼接文本、解构 DTO 或在调用点重复通用日志包装. 终态先裁决再写日志, 仍遵守最低级别过滤.
- 禁止日志包含账号/邮箱、服务器、用户路径、模型/对象名、DAX/M/TMDL、聊天/业务值、请求响应正文、完整配置、密码/令牌/密钥或凭据连接串. 不传原始 Exception, 不输出 Message/ToString/Data. 仅记录最多六层类型/HRESULT/稳定码及无源码路径的有界代码调用栈; SDK 原异常先转安全摘要.
- 输出为用户日志目录内 Compact JSON `.clef`, desktop/cli/host 分组, UTC、Host/Version/ProcessId/SourceContext; 按天及10 MiB滚动, 每组最多31文件和14天, 同步无缓冲 shared sink, 不污染 stdout、不上传、不加入 HTTP/SDK 正文日志中间件. SelfLog 原文不落盘或回网页.
- 级别白名单为 Verbose/Debug/Information/Warning/Error/Fatal, 默认 Information. 先原子保存偏好再切当前进程 level switch; 失败/取消保留旧级别. 不宣称其他运行实例自动同步.
- 正常关闭先取消并排空业务, 再释放日志设施和刷盘; UI 不使用 `.Result/.Wait`. AI 调试只读取当前相关范围, 不自动上传整份日志或发送给产品模型.

入口: `src/PbiUtils.Application/Logging/BusinessLog.cs`, `src/PbiUtils.Application/Logging/BusinessLog.Events.cs`, `src/PbiUtils.Infrastructure/Logging/LoggingRuntime.cs`.
