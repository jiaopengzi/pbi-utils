# 新建、模型配置、日期与 UDF

涉及新建向导、模板、示例组合、模型页配置、日期/期间或 UDF 时读取. 本文件保留现有业务约束, 不新增通用审批步骤; 写入仍走共享模型策略和当前授权范围.

## 新建与模板

- 五步顺序为模板→模拟数据→模板度量值→日期与时间智能→UDF. 共享 `UiSteps` 封装 `el-steps`, 数字、文字和整步边缘可点; 键盘可达且不冒泡到外层页签, 在途/模态阻止切换但允许 Tab 离开.
- 基础项和宿主目录有效后可在任意步骤创建, 不要求看完五步, 不恢复上一步/下一步或仅末步创建限制. 操作栏稳定吸附在内容上方; 错误切到对应步骤/内部页签, 定位真实字段/行并关联 ARIA.
- 往返和关闭可选段保留编辑, 仅校验并提交本次开启的段; 关闭段提交 null. 不把可选步骤关闭、未访问或位置靠前当成业务失败/完成. 模型页复用相同字段、校验和生成器.
- 创建内部串行保存草稿、冻结校验与摘要发布; 网页不恢复整份工程代码/文件预览. CLI 显式 plan/create 与内部冻结仍共用同一业务契约.
- 模板步骤的“辅助表”提供固定 `Hour24`、`MQHY`、`RefreshTime`、`TypeDetails` 多选及 `PQ(M)` 只读代码预览/复制, 未勾选仍可复制. 固定目录为 `web/src/features/scaffold/templateQueries.json`, Application 嵌入同一资源, 网页只提交 ID 集合 `templateQueries` 及已选项分组 map `templateQueryGroups`, 不提交任意 M. 当前契约允许 ID 集合省略/null/空数组, 分组覆盖省略/null时沿用默认; 这是可选配置语义, 不据此新增历史兼容分支. 此选项仅属于新建, 不扩展当前模型配置.
- 默认 `Hour24`/`MQHY`/`RefreshTime` 分组为 `model`, `TypeDetails` 为 `config`; 每项分组独立编辑, 空串表示不分组, 非空名称最多200个UTF-16字符且不能包含控制字符或全为空白, 正常中文和内部空格保留, 不在输入时trim. 选择与覆盖map按桥接会话跨步骤/语言/重挂载保留, 不修改宿主目录或M原文; 只校验并提交选中项, 默认分组也显式进入创建冻结. 非法已选分组沿template步骤字段问题定位真实输入, 关联错误与ARIA. 组map变化使旧计划失效; 操作锁阻止选择、分组编辑及复制.
- 固定中文业务列名不随界面语言翻译. 选中的 `Hour24`/`MQHY`/`RefreshTime` 生成具有完整类型列和 Import M 分区的加载表, 分组绑定分区; `TypeDetails` 继续为 M NamedExpression. 四者标记 `PBI_ResultType=Table`, 元数据回读不代表引擎计算成功. 内置已有辅助查询保持; 选中 TypeDetails 的替换须核验内置来源, 用户同名表、M 分区或表达式保持冲突保护, 不按名称推定归属.
- 无示例数据时, `RefreshTime` 仅在辅助表选中时加载. 其文本列在实际刷新时由本机 `DateTime.LocalNow()` 生成, 预览和创建共用同一固定M. 启用示例数据时, 基础刷新度量所需的该表仍自动保留; 若辅助表已显式选择, 复用同一表并保留选定分组, 不重复生成或转CSV.
- 新建和模型页的显式模板度量配置均按全模型名称匹配原对象就地覆盖, 不存在时在 `Measure` 表创建; 不以旧生成归属或人工改动阻挡. 完整有效配置在创建准备阶段通过宿主用户目录记忆, 包含增删、禁用条目和空集, 关闭段不清空上次方案. 独占锁和完整内容基线保护其他窗口的新配置; 损坏或未知版本不覆盖.
- 模板度量分类仅接受14种标准值, 不接受 RefreshTime 应用扩展. `LocalTemplateMeasurePreferencesStore` 按当前 schema1 和共享 `TemplateMeasureValidation` 严格读取, 不将旧 RefreshTime 偏好转换为 Uncategorized. 无效分类、损坏或未知版本返回带警告的默认配置及 invalid 基线, 保留原文件并拒绝保存覆盖; 合法读取以完整原始字节计算基线. 不把模型中 Config/TMDL 模式切换的物化逻辑当作偏好迁移规则.
- 桌面目录由系统选择器绑定, 网页不能提交任意路径. 新建仅发布到尚不存在的本机子目录; 模板、参数、目标和基线进入冻结摘要, 计划15分钟有效, 提交前再次回核. 持久计划/回执保护同目标重放, 取消提交前生效、提交后排空并回读真实事实, 未知提交不删回执重做.
- Git 默认关闭, 载入和冻结前检测可用性, 只在最终工程的 `pbip/` 目录初始化. 无论开关状态都在冻结前生成 `pbip/.gitignore`, 包含 `**/.pbi/localSettings.json` 和 `**/.pbi/cache.abf`; 模板已有规则保留并只补缺失项, 不改源文件. 缺 Git 提示并跳过初始化, 不改冻结摘要或吞没已完成的创建事实. 发布清单仅豁免显式请求对应的 `pbip/.git` 普通目录, 部分初始化/恢复回执不重放. 完整完成只反馈一次, 部分保存/待核验保留恢复入口.
- 当前内置来源为 `src/PbiUtils.Infrastructure/Scaffolding/Assets/pbip/blank`. 名称与目录原名一致且不翻译; 用户模板与当前内置重名可见提示并由后端拒绝. 模板、报表/模型占位与许可证不能因 demo 字样删除.
- 用户模板位于 `.pbi-utils/templates/pbip/<独立目录>`, 每目录只含一个完整 PBIP, 入口可位于根目录或直接 `pbip/` 子目录, 报表相对绑定 PBIP 目录内本地模型. 管理界面用 Vue; 固定说明文件首次原子创建且不覆盖已有内容. 发现/预览不执行外部源.
- 保留模板来源快照、清单、原始字节和作者声明. 转换按真实表/列/sourceColumn/dataType/业务角色, 不全局字符串替换或仅删除路径参数. 没有模拟输入的零售业务表保留完整空结构, 不伪造业务数据.
- 新建入口与绑定报表/模型统一为 `pbip/<项目名>.pbip`、同层 `<项目名>.Report`/`<项目名>.SemanticModel`, 按真实绑定映射路径, 不做业务文本替换. 原始11表仅在开启模拟数据时由共享示例生成器写入根 `data/`; 模板适配写 `data/model/`, 页面/权限配置 CSV 写 `data/config/`. 两种模板度量模式均建立根 `pbi-utils/` 目录, Config 写 `pbi-utils/config.json` 的 `MeasureTemplates`, TMDL 不生成该 JSON; 不生成或迁移旧 `config/config.json`. 资产 hash、UDF 精确安装锁与逐包原始许可统一保存在模型注解, 不生成 scaffold/README/配置归属/UDF锁/许可旁文件. 不生成 `data/sample/`; 新建在本轮 staging 内替换同名模拟表及其固定 CSV, 保留源模板字节和已有成员引用, 已有模型仍须完整归属与资产核验. 文件数据源移动后更新指向工程根的 `PbiUtilsProjectRoot`, 后续整树事务及资产根保持该层级, 定义文件清单仍相对 PBIP 父目录.

入口: `src/PbiUtils.Application/Scaffolding/ScaffoldService.cs`, `src/PbiUtils.Application/Scaffolding/ScaffoldInputJson.cs`, `src/PbiUtils.Infrastructure/Scaffolding/ScaffoldWorkspace.cs`, `src/PbiUtils.Infrastructure/Scaffolding/ScaffoldPlanStore.cs`, `src/PbiUtils.Infrastructure/Scaffolding/Assets/README.md`, `web/src/features/scaffold/ScaffoldPanel.vue`.

## 同目标模型配置与生成归属

- 创建前五步配置随创建一次提交; 创建后新建页只保留结果与固定打开/新建入口, 不再读取 created 配置上下文或提供追加配置/重新核验 UI. 后续模拟数据、模板度量、日期/期间和 UDF 在打开连接后的模型页使用共享配置能力, 不为追加配置重新创建工程. 有可信 PBIP 目录才支持文件资产, 远端不开放配置写入; MCP 仅复用同一受限用例.
- 目标绑定宿主模型身份与文件 ID, 当前工程与新建目标是同一文件时同步原保存服务. 复用完整基线、必要事务恢复材料、提交和保存排空; 普通保存直接执行, MCP 精确一次授权及兼容升级等独立风险确认保留, 不创建长期模型历史备份; 连接 Desktop 在原引擎专属 TOM 会话原对象暂存并一次提交, 离线 PBIP 使用文件 staging, 不建立旁路写入通道.
- 模板显式覆盖之外的生成归属由内容、角色、资产 hash 和完整 SDK 可写定义核验. 这些功能的未知对象、人工修改对象/资产或过期基线拒绝覆盖, 不以名称或旧 stamp 存在重新背书. 摘要与候选回核必须涵盖最终/保留/新增字段映射和函数事实.
- 模板度量 Config 使用宿主已核实的本机 PBIP/PBIX 资产根下 `pbi-utils/config.json` / `MeasureTemplates`. 普通 PBIP/PBIX 以入口父目录为根, 只有 PBIP 位于 `pbip/` 时提升到其父目录; PBIX 不解析内容. 缺目录或文件时返回可创建的 missing 修订并允许 Config, 读取无文件副作用, 首次应用时才创建. 无可信文件绑定不授予文件权限. 此公共 JSON 允许用户编辑, 不以历史生成 hash 拒绝当前有效配置; 从实际文件读取 revision, 冻结、确认和提交逐次复核, 保留其他配置段. 路径类型冲突、链接或 JSON 无效时仅回退 TMDL 展示并保留文件, 不在读取时修改模型. 显式模板度量及其参数对象按当前请求直接创建或覆盖, 不以旧生成归属或人工修改阻止切换; 同名度量原位置更新, 其他对象保留. 切 TMDL 时参数表有引用则保留隐藏内嵌快照, 模式切换中有引用的旧项保留而不阻断; 同模式明确删除仍核对存活引用. 目标/完整基线/必要事务恢复材料/一次提交/回读及文件并发规则保持, 不将对象覆盖例外扩大到日期、样例或 UDF.
- 新生成对象在打 stamp 前固定确定性 lineage 和实际类型注解, 只接受当前 v2 完整归属; 不保留旧 v1 指纹、lineage 补全或磁盘升级例外. 未知、旧格式或人工变化对象不得自动认领或覆盖.
- Desktop 以引擎/文件完整可写定义指纹及官方身份/保存状态证明持久化, 不以表列形状、展示 DTO 或模型总结宣布成功. 三段保存事实分别保留; 继续保存只补未完成阶段, 未知 Invoke/提交不重放.

入口: `src/PbiUtils.Application/Models/Configuration/ModelConfiguration.cs`, `src/PbiUtils.Infrastructure/Models/Configuration/ModelConfigurationCompiler.cs`, `src/PbiUtils.Infrastructure/Models/Configuration/ModelConfigurationOwnership.cs`, `src/PbiUtils.Infrastructure/Models/Configuration/ModelConfigurationReadback.cs`, `src/PbiUtils.Host/Bridge/Configuration/ModelConfigurationBridge.cs`, `web/src/features/configuration/`.

## 日期函数、字段和预览

- 新建/模型页共用日期表与时间智能两个内部 `UiTabs`, 63字段平铺、推荐15/简单5、固定01–63编号可选及独立上次方案记忆. 目标必需字段不污染用户偏好; 编号切换覆盖已有标准生成列的名称和 SourceColumn, 保留原列对象、类型、业务别名和可核实引用. DAX 仅改写真实列 token; 歧义和未知 M 下游依赖拒绝整批. 同批期间绑定通过原日期列对象同步最终名称.
- 按最终实际映射求字段依赖闭包. 日期由独立 M `NamedExpression` 函数承载, 分区仅调用, 函数和表同一候选提交; 不恢复内嵌整段分区或日期 CSV 生成.
- 日期函数固定名 `PbiUtilsCalendar`, 切换字段/编号/年份/语言不增生函数. 只管理当前固定名及完整归属有效的函数; 旧哈希名称不自动迁移. 未知同名对象和第二张独立日期表拒绝, 不按前缀清理、不恢复 inline 旁路.
- schema3 同时核表/函数双归属、唯一绑定、`Kind=M`、Function 类型注解、生成/资源版本、locale、定义 hash 和调用 hash. 摘要、预览、候选及权威回读包含函数事实, 公共查询名称冲突须拒绝.
- 函数使用完整 `Value.ReplaceType` 包装、四个实际显式参数的类型/文档元数据和有效示例, 归入 `functions` 查询组. 当前仅保留 v5 生成和验证, 以显式最终步骤直接返回类型包装函数; 不保留 v2 内嵌生成器或 v3/v4 重建/迁移, 不以版本名或重打 stamp 证明可覆盖.
- 纯公历支持1900–2100年; 含农历字段覆盖公历2000–2099年, 仅需要时携带315字节固定年规则. 结果由显式参数、实际映射、工作周与覆盖决定, 不读网络、文件、系统日期、区域设置或 .NET 农历 API, 不调用百度 API. 传统节日标签不代表官方调休.
- 日历底部使用可聚焦、搜索、选择、复制的只读 CodeEditor 分别展示完整函数/调用. 生成代码关键注释和参数说明按当前界面语言; 用户原始 DAX/M/TMDL 内容不翻译. 源码注释规则与生成代码语言分别处理.
- 无效、过期、目标/基线/语言改变时立即禁用旧代码复制. 自动预览使用独立只读队列, 从私有完整 TOM 快照核目标/基线, 不长期占用模型写门闩. 偏好与纯解析也独立排队, 关闭统一取消/排空.
- M/DAX 格式化全在本地 C#, 保留词元、名称、字符串、注释、大小写和运算符内容. 不依赖在线格式化、生产 JS 引擎或 Desktop 私有分发文件, 不把排版当语义执行.
- 工作周与 CSV/TSV `date,isWorkday,name` 覆盖先预览后合并, 默认保留草稿. 原文最多2 MiB, 最终最多10000个唯一日期且规范化 UTF-8 JSON最多512 KiB; 专用桥接信封绝对上限4 MiB, 不放宽其他请求预算. 重复、错误或超限整批拒绝.
- 日期只接受当前 schema3/function-m/v5, 不读取或迁移 v1/v2、旧 CSV 或 inline 归属. 当前无文件模型无需引入旧资产证明. 测试 Fixtures 的逐日资源不恢复生产嵌入, 315字节规则与来源许可保留.
- 时间智能复用51项期间定义与其真实依赖, 不将生成文本/解析成功当作引擎数值验证.

入口: `src/PbiUtils.Application/Calendar/ScaffoldCalendarFields.cs`, `src/PbiUtils.Application/Calendar/ScaffoldCalendarProgramCompiler.cs`, `src/PbiUtils.Application/Calendar/ScaffoldCalendar.cs`, `src/PbiUtils.Application/Calendar/WorkdayImport.cs`, `src/PbiUtils.Infrastructure/Calendar/ScaffoldCalendarFunction.cs`, `src/PbiUtils.Infrastructure/Calendar/CalendarProgramPlanner.cs`, `src/PbiUtils.Host/Bridge/Calendar/CalendarCodePreview.cs`, `src/PbiUtils.Host/Bridge/Calendar/CalendarBridgeBudget.cs`, `web/src/features/scaffold/CalendarCodePreview.vue`.

## UDF 来源、版本与许可

- UI 按 source/package/tag 组织, 主列表按完整 package 轻量分页、搜索与选择, 精确详情按需读取完整 Markdown 与包内函数. 本地同名函数作为一个包显示不同 tag, 内部 GUID/基线仍用于安全读写和依赖核验. 默认最新稳定版, 单选和多选均可固定同源同库的一个精确历史tag; 加入其他远端或本地函数不得清空已有版本/hash或静默升级. 精确选择的预发行包及声明的预发行依赖自动按固定版本解析, 不提供人工允许开关; 目录的预发行显示筛选不改变版本解析与许可核验. 当前页选择与全来源选择范围必须明确, 跨页/搜索保留已选项; 已选及全来源预览中的函数使用逐项编号列表, 保留包级来源/精确版本与移除动作.
- 第三方来源使用宿主稳定 ID、配置基线和严格原子存储, 支持符合 DAXLib packages 布局的 HTTP(S) Git 仓库, 包括 Gitee、GitLab、多层项目和无需登录的内网仓库. 官方固定 daxlib/daxlib, ID 为 daxlib, 注册和默认提供者共用 UdfSourceDefaults, 页面默认选择官方源并即时列出新增源. 协议仅 git, Git ref 最多256字符并纳入实际配置身份, kind 表示所有权; 保存不联网、不保存凭据. GitHub 保留 HTTPS 固定提交/完整树/归档验证; 其他地址使用托管 Git smart HTTP 并验证 PACK 与 commit/tree/blob, 不依赖系统 Git、不 checkout/执行脚本/子模块或继承凭据. 允许私网但拒绝回环、链路本地、特殊保留地址和重定向, URL 不带凭据/查询/片段. 保持包体/路径/解压/内容预算及失败取消保旧. 配置未知或损坏严格保留. PBIP 可选 Git 初始化仍独立检测系统 Git. 来源间依赖/许可锁隔离, 冻结/创建前核验全部来源.
- 所有来源日期仅来自 Git 仓库, 不使用 DAXLib 目录 API 或托管平台日期接口. 版本更新日期为该精确版本目录最后修改提交的 committer 时间, UTC 缓存并按本机时区显示, 不代表正式发布日期. 标准 smart HTTP 历史固定快照 commit 并有界获取; 沿首个目录内容相同的父提交追溯, 所有父目录不同才算当前修改, 不按时间戳最大值或 tagger 时间猜测. 浅边界无法证明则 null 显示 -, 不拿 HEAD/获取时间/文件时间补造. 历史请求错误和取消保持原缓存, 浏览/换版不联网.
- 生产浏览、版本/精确包、依赖与冻结验证只读完整本地快照, 网络仅发生于显式 catalog refresh. 首次无完整目录需引导手动更新; 历史零散请求缓存不作为初始化证明. 更新完整取得全部支持版本和许可后原子切换, 失败/取消保留旧目录与时间; 来源漂移、损坏、未知版本/超限拒绝部分发布. 读者绑定不可变代, 更新不占模型/草稿门闩, 发布后排空. 多函数选择/冻结锁绑定 catalogBaseline, 来源更新改变解析事实后须显式重新核验, 不静默改最新版本.
- 具体包摘要在有界本地索引分页查询, 列表版本标签只展示精确版本号. 防抖查询期间搜索框保持可编辑, 输入法组合不发中间查询; 旧请求排空后执行最新查询, 来源/目录/查询漂移丢弃迟到结果. 本地函数同时纳入当前仓库列表和搜索, 本地管理仍独立, 不清空已选项. 不在主列表传整库 README/全部正文或重复图标; 详情按需验证精确包. 当前目录必须有完整来源/ref/固定commit追溯, 缺失或损坏拒绝. 图标仅受限包内PNG或本地默认. 详情 Markdown 通过独立 Marked 和显式白名单 DOMPurify, 禁原始HTML/远程媒体/危险协议, 点击不触发任意导航, 不放宽 CSP 或增加通用 openURL 桥接. 时间仅显示层本机时区格式化.
- 缓存只登记最新完整快照, 每个快照保留全部支持包版本, 不提供 history/rollback CLI、桥接或界面. 已发布指针只接受当前schema3且仅登记current; 不读取或迁移旧schema2历史指针, 当前 manifest 索引仅 schema5, 精确版本 Git 提交时间为 committedAt; 不读取或迁移旧 schema2/3/4 索引, 必须显式更新整体重建. Revision仍绑定完整指针字节, 新发布身份防ABA; 刷新绑定开始时revision并持同源配置/发布锁. 提交前失败/取消保旧, 提交后回读失败只读核验且不重放. 成功回读后才退休旧指针可证明的代, reader持有可释放只读lease, 取得manifest独占权限后才能删除已退休代; 占用代延至后续成功刷新清理. 不扫描删除未知代或用户函数, 不触已安装模型/安装回执. 所有固定快照调用方必须释放reader.
- 新建与模型共用平铺库/单一搜索/已选区; 高亮只用文本插值, 筛选不清隐含选择. 全来源选择先完整预览再一次修改草稿, 不受搜索范围影响, 与模型最终导入动作分开. 模型导入位于已选区末尾并复用原提交通道; 新建随创建导入, 关闭可选段仍提交 null.
- 精确版本切换覆盖该包全部已装子集并回核依赖/许可. 既有 local 函数必须同批重新解析, 不用旧锁复活用户已删除函数. 保留逐包原始许可、版本和内容指纹.
- 继续使用包体预算、路径/内容/媒体安全限制. 仓库唯一字节与全部包解析逻辑字节分别受总预算约束, 祖先许可按每个包/版本的复制计入, 完整预检后才解析包; 包内文件与祖先许可的内部路径冲突明确拒绝, 不覆盖许可. 官方 PNG 的通用二进制 MIME 兼容只在签名实际为 PNG 时允许, 不推广到任意二进制.
- 新工程安装 UDF 时兼容级别至少1702. Desktop 先核实际模型级别, 已达1702直接通过, 不读产品版本或支持集合; 低模型才读取已绑定原进程的真实 ProductVersion, 核实至少June2026 GA, 再通过原连接证明本次升级支持. Store版本资源的Product数值字段可能全0, 必须严格解析可信产品版本文本的四段前缀, 不用网页版本、默认安装或查询成功猜测. 已有低级别 PBIP 和已核实 Desktop 走独立升级确认; Desktop 保存时按原模型级别再次核实环境和精确配置, 将兼容级别与完整模型放在同一服务器事务, 完整基线、必要事务恢复材料及权威回读不放宽. 不由网页布尔值或包声明授权升级. Service/SSAS 保持只读. 元数据解析/保存不等于真实 Desktop 执行或文件持久化成功.

入口: `src/PbiUtils.Application/Udf/UdfDependencyResolver.cs`, `src/PbiUtils.Application/Udf/UdfValidation.cs`, `src/PbiUtils.Infrastructure/Udf/`, `src/PbiUtils.Infrastructure/Udf/Model/ModelConfigurationUdfLocks.cs`, `src/PbiUtils.Infrastructure/Scaffolding/ScaffoldModelWriter.cs`, `web/src/features/scaffold/UdfStep.vue`.
