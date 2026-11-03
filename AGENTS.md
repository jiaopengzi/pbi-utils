# AGENTS.md

本文件是 pbi-utils 的开发约定, 供编码 AI 与开发者使用. 不得把它作为用户数据或产品内 Agent 的运行时提示词发送给模型.

## 1. 范围与事实来源

- 产品面向 Windows, 由共享 C# 业务服务、独立 CLI、WinForms/WebView2 宿主及 Vue 界面组成. 当前业务包括模型连接/读取/编辑/保存、示例数据、新建 PBIP、日期与时间智能、UDF 和本地 MCP.
- 当前用户需求决定任务范围和授权. 开始前阅读需求、本文、`README.md` 及相关模块说明, 核对工作目录、Git 状态、用户已有改动和实际平台.
- 项目当前未发布, 默认处于变化较大的快速开发迭代阶段. 除非用户明确要求兼容某项 `legacy`, 否则以最新开发迭代、当前需求与实际效果为准, 不默认承担历史接口、配置、数据格式、行为或流程的保留、兼容及迁移义务. 明确要求的兼容仅覆盖指定范围, 不推定其他历史实现也需保留.
- 以当前源码、配置与机器契约核验现状. `.docs` 保存设计、进度与历史证据; 历史实现及 PASS 不自动代表当前规则或本次验证结果.
- 执行开发/排障/源码整理/验收时, 使用仓库级 [pbi-utils-development](.agents/skills/pbi-utils-development/SKILL.md), 按任务选择相关参考文件, 不默认加载全部资料.

## 2. 结构与依赖

| 位置 | 职责 |
| --- | --- |
| `src/PbiUtils.Core` | 稳定输入/输出、错误与产品契约, 不依赖文件系统、UI 或 SDK |
| `src/PbiUtils.Application` | 共享业务用例与策略, 仅项目引用 Core |
| `src/PbiUtils.Infrastructure` | 文件/模型、存储、身份认证、提供方、日志和资源适配, 引用 Application |
| `src/PbiUtils.Windows` | Windows 进程、Desktop Bridge、TOM/ADOMD 与桌面自动化适配, 引用 Application/Infrastructure |
| `src/PbiUtils.Cli` | 独立控制台 composition root, 引用 Infrastructure |
| `src/PbiUtils.Host` | 无 UI 的共享业务后台、独立客户端上下文、写入协调与排空, 引用 Windows/Infrastructure |
| `src/PbiUtils.Desktop` | WinForms/WebView2 外壳、生命周期与受限本机 UI 适配, 引用 Windows/Infrastructure |
| `web/src` | Vue 页面、类型化 bridge、共享 UI、双语和编辑器 |
| `web/src/composables/<领域>` | 按原业务领域组织的 `use*` 组合式函数 |
| `web/tests/<领域>` | 与原源码领域对应的前端行为测试, 工具测试集中在 `tooling` |
| `scripts`、`tests` | 开发命令、发布检查、共享/CLI/桌面测试及机器契约 |

- CLI、Desktop、MCP 复用共享用例, 不复制业务逻辑. Core/Application 不引用桌面类型; GUI 与 CLI 不互相引用. 以架构测试约束实际项目和程序集依赖.
- 宿主拥有依赖注入与平台资源生命周期. Vue 只管理状态/显示并调用类型化 bridge, 不直接获得文件系统、shell 或模型连接能力.
- 目标框架/产品版本见 `Directory.Build.props`; SDK 见 `global.json`; NuGet 见 `Directory.Packages.props`; Node/pnpm/前端包见 `web/package.json` 及锁文件. 不在说明中复制当前版本, 不顺带升级依赖.

### 依赖与许可证同步维护

- 新增、升级或替换第三方依赖, 以及引入第三方源码、字体、图标、主题、模板和安装器组件时, 必须在同一变更中核对对应精确版本的上游许可, 补齐完整 LICENSE、版权声明及适用的 NOTICE/COPYING/EULA. 不以许可证名称、网页链接或旧版本文本代替当前版本全文, 不将第三方内容统一视为项目 MIT 许可.
- 依赖声明与锁文件变更时, 按 [许可维护说明](licenses/README.md) 同步维护所需的 `licenses/catalog.json`、`licenses/source/`、`THIRD-PARTY-NOTICES.md` 及资源目录内的许可文件, 保留来源、精确版本和校验哈希. 优先复用包内原文及现有生成机制, 不重复手工维护自动生成的分发清单; 仅开发期使用的依赖与实际随包分发的依赖分别核对.
- 移除依赖或第三方资源时, 同步移除不再使用的许可清单条目、专属许可文件和来源说明, 并更新生成结果. 删除前核对全部直接/传递依赖、内嵌资源与构建/发布引用; 仍被其他组件使用的共享许可及版权声明必须保留, 不因删除一条直接依赖就删除仍被间接引用组件的许可. 源码与文件删除继续遵守本文件的确认和保护规则.
- 依赖或许可发生变化后, 运行许可生成与校验相关测试, 并按交付规则完成门禁; 核对两种 .NET 发布模式的实际分发内容与许可证一致, 新增内容无遗漏、已移除内容无残留. 缺失全文、来源不明、版本或哈希不一致时必须停止发布并明确报告, 不跳过校验或沿用旧产物掩盖问题.

## 3. 变更纪律与注释

- 保留用户源码、工作文件、已有改动和功能占位; 应用用户数据按下述快速迭代规则处理. 不擅自清空或强制覆盖上述文件及任务范围外文件, 不擅自 reset/clean、提交、推送、改版本/tag、注册外部工具、发布安装器或更改系统设置. 不修改用户既有忽略规则; `.docs` 保持忽略.
- 源码清理先核查运行入口、DI、项目/资源、契约、测试和发布引用, 区分废弃演示、正式示例数据、开发 mock、测试替身、历史兼容及功能占位. 名称含 demo/mock/fake/Prototype 不构成删除依据; 按用户要求先提供准确清单并等待删除确认.
- 未明确要求兼容的 `legacy`, 不新增或保留仅服务历史行为的兼容分支、适配层、回退路径、双轨实现或旧路径转发壳. 按当前用途核验, 在任务授权范围内同步清理失效实现、调用方、契约、测试与说明; 保持功能与契约是指当前需求仍需要的能力, 不为旧方案延续历史包袱. 本规则不豁免用户源码/工作文件保护、模型安全、授权边界及源码删除确认要求, 当前仍使用的功能、功能占位及真实外部协议/SDK 适配不因历史命名被视为可删.
- 只增加当前任务真实使用的依赖和抽象. 不创建空目录/空接口大礼包, 不用 TODO、吞异常或空成功替代实现. 改动共享逻辑时检查相邻场景和全部调用方.
- 新建/修改源码保留规范文件头: `FilePath`、`Author`、`Blog`、`Copyright`、`Description`. 保留已有署名、年份及第三方许可; 使用合法注释语法, 不给 JSON 强加注释.
- 新增/修改函数、方法和组件写明用途、主要参数、返回值及错误. 源码注释使用中文和英文标点; 抽离逻辑时等价迁移原注释, 不删除关键约束. 具体格式见 skill 的 `code-and-comments.md`.
- C# 启用 nullable, 异步 I/O 传播 CancellationToken, UI 不使用 `.Result/.Wait`. SDK 同步调用放在线程池, 如实说明取消边界, 不宣称可立即中断.

- C# 中固定 DAX/M 模板优先使用原始字符串: 无插值用 `"""..."""`, 带变量用 `$"""..."""`, 含字面花括号时按实际需要使用 `$$"""..."""`. 动态行/列/依赖集合继续使用 `string.Join`、`StringBuilder` 等现有构造方式; 插入值继续复用 DAX/M 标识符与文本转义及文化不变数字规则. 改写时保持生成内容、空白、行尾与既有格式化入口, 细则见项目 skill 的 `code-and-comments.md`.

### DAX 格式与命名

- 项目自有 DAX 参考 SQLBI [DAX Format](https://docs.sqlbi.com/dax-style/dax-format) 和 [DAX Naming Conventions](https://docs.sqlbi.com/dax-style/dax-naming-conventions). 共享格式化支持 short/long, 默认 long, 前端使用 switch 显式选择. Long 允许80字符内的短调用/变量块内联, short 展开多参数/嵌套调用; 成对参数及 CALCULATE/CALCULATETABLE 关系筛选按实际 API 基准保留上下文布局, 不机械逐逗号换行. 采用4空格缩进, 逗号留在前一参数末尾, 多行右括号与调用名对齐, 空参数写为 `BLANK ()`. 函数名和左括号之间、非空括号内部及运算符两侧留空格, 多行二元运算符置于续行行首, 表名与列引用之间不留空格.
- 自有表达式的变量使用 PascalCase, 最后定义 `Result` 并 `RETURN Result`; 与模型表名冲突时选择确定的无冲突 PascalCase 数字后缀. 完整度量定义的 `名称 =` 独立成行; VAR 标量赋值可内联, 函数表达式与较长变量块按所选样式换行, 不额外插入变量块空行. 格式化布局以已采集的 SQLBI API 测试基准核验, 不把开发期 API 接入产品运行时.
- 度量值正文使用共享 `DaxFormatter.FormatMeasure`/`FormatMeasures` 上下文, 在实际保存的 `Measure.Expression` 中保留一个前导 LF, 顶级行顶格且仅保留内部语法缩进, 确保 Desktop 自行拼接的 `名称 =` 与首个变量分行; 查询及其他 DAX 槽位使用通用上下文. 不靠给整串文本逐行添加空格处理多行字符串. 已转义单常量的生成路径可直接按同一布局包装, 保持其原有生成容量; 交互格式化与回填须满足输出及真实保存请求上限, 超限保留原草稿.
- 业务可见名称清楚表达结果, 多词名称使用空格, 时间智能缩写作为后缀; 隐藏技术对象可使用 PascalCase. UDF 函数使用 PascalCase 与明确的库/本地命名空间, 参数使用 camelCase, 表达式引用参数按 SQLBI 规则使用类型后缀. 不为样式自动重命名已有公开对象、用户代码或第三方函数, 不破坏报表引用和精确依赖.
- 自有 DAX 生成器复用共享本地 Long 格式化策略, 不继承编辑器中显式选择的 Short; 导出和统计等固定后台查询使用 `DaxFormatter.FormatGeneratedQuery` 保留大模型容量, 交互入口仍执行原字符与传输预算. 格式化器保留原始词元、字符串、注释和标识符绑定, 不自动重命名变量或改写计算. 列引用限定表名、度量引用不限定表名、仅必要时使用表名单引号等规范须结合模型元数据确认, 不让纯词法格式化器猜测对象类型或删除安全转义. 对自有模型的引用调整须验证绑定与计算语义不变.

### DAX 变量与保留字

- 微软官方 [VAR 规则](https://learn.microsoft.com/en-us/dax/var-dax) 禁止保留字、已有表名、空格及数字开头的变量名, 也不支持用单引号或方括号转义变量名. PascalCase 或仅改大小写不能避开保留字; 生成变量继续使用有业务含义的 PascalCase, 如 `SvgMatchPosition`, 再核查当前和同批新增表名并确定数字后缀. 不自动重命名用户或第三方 DAX.
- 官方 [MDX Reserved Words](https://learn.microsoft.com/en-us/sql/mdx/mdx-reserved-words) 提供 Analysis Services 的公开参考名单, [Reserved Keywords](https://learn.microsoft.com/en-us/sql/mdx/reserved-keywords-mdx-syntax) 说明关键字和带分隔符对象名的区别. 该 MDX 页面不是完整的 DAX 变量禁用表, 不得据其缺词判定变量合法, 也不得将所有函数名一概宣称为 DAX 变量保留字.
- `Status` 是必须避开的名称; 本机服务端关键词包含 `STATUS`, 且独立 DAX 查询 `VAR Status = 1 RETURN Status` 已报语法错误. 容易误用的服务端关键词还包括 `NAME`, `VALUE`, `CURRENT`, `MEMBER`, `LEVEL`, `PARENT`, `CHILDREN`, `TYPE`, `DESCRIPTION`, `DATE`, `COLUMN`, `MEASURE`, `TABLE`, `DEFINE`, `EVALUATE`, `RETURN`, `VAR`, `FUNCTION`, `ORDER`, `BY`, `START`, `AT`, `ASC`, `DESC`, `TRUE`, `FALSE`, `NULL`.
- 以官方 [DISCOVER_KEYWORDS 协议](https://learn.microsoft.com/en-us/openspecs/sql_server_protocols/ms-ssas/6bb30c01-71de-4c6a-96ce-a2628d988304) 定义的目标服务端返回值核对当前关键词, [DMV 文档](https://learn.microsoft.com/en-us/analysis-services/instances/use-dynamic-management-views-dmvs-to-monitor-analysis-services) 说明其只读查询方式: `SELECT * FROM $SYSTEM.DISCOVER_KEYWORDS`. 该 SELECT 是 SQL 风格的 DMV 查询, 解析器和 SELECT 语法基于 DMX, 可在支持 MDX/DMX 的查询工具运行; 它不是 DAX `EVALUATE` 查询, 不直接粘贴到仅接受 DAX 的编辑器. 权限不足、目标不可用或查询失败须如实报告, 不以空名单代替成功结果.
- 下列为 2026-10-30 经官方接口取得的本机服务端 695 个去重关键词快照, 包含不同查询语言的关键词及函数名, 不宣称每个词都在所有引擎版本中禁止作为 DAX 变量. 项目自有生成变量保守避开此集合; 引擎更新或新基名引入时重新查询并用独立只读 DAX 最小式核验. 快照不能替代真实引擎计算验收, 也不用于认定既有用户表达式非法.

<details>
<summary>服务端关键词完整快照 (695 项, 按名称排序)</summary>

```text
$ADJUSTEDPROBABILITY $CLUSTER $DISTANCE $NODECAPTION $NODEID $NODENAME $PROBABILITY $SEQUENCE
$STDEV $SUPPORT $TIME $VARIANCE ABSOLUTE ACTION ACTION_TYPE ADD
ADDCALCULATEDMEMBERS ADDCOLUMNS ADDRESS ADDRESS_BUILDING ADDRESS_CITY ADDRESS_COUNTRY ADDRESS_FAX ADDRESS_FLOOR
ADDRESS_HOUSE ADDRESS_PHONE ADDRESS_QUARTER ADDRESS_ROOM ADDRESS_STATE_OR_PROVINCE ADDRESS_STREET ADDRESS_ZIP AFTER
AGGREGATE ALL ALLEXCEPT ALLMEMBERS ALLNOBLANKROW ALLSELECTED ALTER ANCESTOR
ANCESTORS AND APPEND APPLICATION AS AS_FORMULA AS_VALUE ASC
ASCENDANTS ASSOCIATED_MEASURE_GROUP AT AUTOMATIC AUTOSUGGESTEDTOKEN AVERAGE AVERAGEA AVERAGEX
AVG AXIS BACK_COLOR BASC BATCH BDESC BEFORE BEFORE_AND_AFTER
BEGIN BINARY BLANK BLOB BOOLEAN BOTTOMCOUNT BOTTOMPERCENT BOTTOMSUM
BY CACHE CALCULATE CALCULATE.CONDITIONAL CALCULATED CALCULATEDMEASURE CALCULATETABLE CALCULATION
CALCULATION_PASS_DEPTH CALCULATION_PASS_NUMBER CALCULATIONCURRENTPASS CALCULATIONPASSVALUE CALCULATIONS CALL CAPTION CAPTION_ABBREVIATION
CAPTION_DESCRIPTION CAPTION_SHORT CASE CASES CATALOG CATALOG_NAME CELL CELL_BY_ALLOCATION
CELL_EVALUATION_LIST CELL_ORDINAL CELLS CHAPTERS CHILDREN CHILDREN_CARDINALITY CLEAR CLOSE
CLOSINGBALANCEMONTH CLOSINGBALANCEQUARTER CLOSINGBALANCEYEAR CLOSINGPERIOD CLUSTER CLUSTERDISTANCE CLUSTERPROBABILITY CLUSTERS
COALESCEEMPTY COLUMN COLUMN_VALUES COLUMNCOMPLEXMATCH COLUMNS COMMANDLINE COMMIT CONDITION
CONSTEVAL CONSTRAINED CONTAINS CONTENT CONTINUOUS CORRELATION COUNT COUNTA
COUNTAX COUNTBLANK COUNTROWS COUNTX COUSIN COVARIANCE COVARIANCEN CREATE
CROSSJOIN CUBE CUBE_NAME CURRENT CURRENT_TIME_MEMBER CURRENTCUBE CURRENTMEMBER CURRENTORDINAL
CUSTOM CUSTOM_ROLLUP CUSTOM_ROLLUP_PROPERTIES CUSTOMDATA CUSTOMROLLUP CUSTOMROLLUPPROPERTIES CYCLICAL DATAID
DATAMEMBER DATASET DATASOURCEVARIABLES DATATABLE DATE DATE_CANCELED DATE_DURATION DATE_ENDED
DATE_MODIFIED DATE_START DATESBETWEEN DATESINPERIOD DATESMTD DATESQTD DATESYTD DAX
DBTYPE_CY DBTYPE_DATE DBTYPE_DECIMAL DBTYPE_I1 DBTYPE_I2 DBTYPE_I4 DBTYPE_I8 DBTYPE_NUMERIC
DBTYPE_R4 DBTYPE_R8 DBTYPE_UI1 DBTYPE_UI2 DBTYPE_UI4 DBTYPE_UI8 DEFAULT_MEMBER DEFAULTMEMBER
DEFINE DELETE DENSE DENSIFY DEPENDENCIES DESC DESCENDANTS DESCRIPTION
DIMENSION DIMENSION_CONTENT DIMENSION_UNIQUE_NAME DIMENSIONS DISABLED DISCRETE DISCRETIZED DISPLAY_FOLDER
DISTANCENORMALIZED DISTINCT DISTINCTCOUNT DIVIDE DOUBLE DRILLDOWNLEVEL DRILLDOWNLEVELBOTTOM DRILLDOWNLEVELTOP
DRILLDOWNMEMBER DRILLDOWNMEMBERBOTTOM DRILLDOWNMEMBERTOP DRILLTHROUGH DRILLUPLEVEL DRILLUPMEMBER DROP DYNAMIC
EAGER EARLIER EARLIEST ELSE EMPTY END ENDOFMONTH ENDOFQUARTER
ENDOFYEAR EQUAL_AREAS ERROR EVALUATE EXCEPT EXCLUDE EXCLUDE_NULL EXCLUDEEMPTY
EXCLUSIVE EXCLUSIVE_ASSOCIATION EXISTING EXISTS EXPORT EXTEND_MODEL_CASES EXTRACT FALSE
FILTER FIRSTCHILD FIRSTDATE FIRSTNONBLANK FIRSTROWSET FIRSTSIBLING FLATTENED FN
FOLDING FONT_FLAGS FONT_NAME FONT_SIZE FOR FORE_COLOR FORMAT FORMAT_STRING
FORMATTED_VALUE FORMATTING_COLOR FORMATTING_FONT FORMATTING_FONT_EFFECTS FORMATTING_FONT_SIZE FORMATTING_ORDER FORMATTING_SUB_TOTAL FREEZE
FROM FUNCTION GENERATE GENERATEALL GEO_BOUNDARY_BOTTOM GEO_BOUNDARY_FRONT GEO_BOUNDARY_LEFT GEO_BOUNDARY_POLIGON
GEO_BOUNDARY_READ GEO_BOUNDARY_RIGHT GEO_BOUNDARY_TOP GEO_CENTROID_X GEO_CENTROID_Y GEO_CENTROID_Z GLOBAL GOAL
GROUP GROUPING HASONEFILTER HASONEVALUE HAVING HEAD HIDDEN HIERARCHIZE
HIERARCHY HIERARCHY_UNIQUE_NAME HINT HOLDOUT HTML ID IF IF.CONSTEVAL
IF.EAGER IF.STATIC IIF IMPORT IN INCLUDE_CALC_MEMBERS INCLUDE_NODE_ID INCLUDE_NULL
INCLUDE_STATISTICS INCLUDEEMPTY INCLUSIVE INPUT_ONLY INSERT INTERACTIVE INTERSECT INTO
INVOCATION IS IS_DATAMEMBER IS_PLACEHOLDERMEMBER ISANCESTOR ISDESCENDANT ISEMPTY ISFILTERED
ISGENERATION ISINNODE ISLEAF ISSIBLING ISSUBTOTAL ISTESTCASE ISTRAININGCASE ITEM
JOIN KEEPFILTERS KEY KEYWORDSMATCHPREFIX KPI KPICURRENTTIMEMEMBER KPIGOAL KPISTATUS
KPITREND KPIVALUE KPIWEIGHT LAG LANGUAGE LASTCHILD LASTDATE LASTNONBLANK
LASTPERIODS LASTSIBLING LAZY LCID LEAD LEAVES LEVEL LEVEL_NUMBER
LEVEL_UNIQUE_NAME LEVELS LIBRARY LINKMEMBER LINREGINTERCEPT LINREGPOINT LINREGR2 LINREGSLOPE
LINREGVARIANCE LOG LONG LOOKUPCUBE LOOKUPDECISIONTABLE LOOKUPVALUE MARGINALNORMALIZED MATCHESRULE
MAX MAXA MAXROWS MAXX MDXFILTER MEASURE MEASUREGROUPMEASURES MEDIAN
MEMBER MEMBER_CAPTION MEMBER_KEY MEMBER_NAME MEMBER_ORDINAL MEMBER_TYPE MEMBER_UNIQUE_NAME MEMBER_VALUE
MEMBERS MEMBERTOSTR MEMBERVALUE MICROSOFT_ASSOCIATION_RULES MICROSOFT_CLUSTERING MICROSOFT_DECISION_TREES MICROSOFT_LINEAR_REGRESSION MICROSOFT_LOGISTIC_REGRESSION
MICROSOFT_NAIVE_BAYES MICROSOFT_NEURAL_NETWORK MICROSOFT_SEQUENCE_CLUSTERING MICROSOFT_TIME_SERIES MIN MINA MINING MINX
MODEL MODEL_EXISTENCE_ONLY MODELS MOVE MPARAMETER MTD NAME NAMETOSET
NATURAL NEST NEXTDAY NEXTMEMBER NEXTMONTH NEXTQUARTER NEXTYEAR NO_PROPERTIES
NON NON_EMPTY_BEHAVIOR NONEMPTY NONEMPTYCROSSJOIN NONLEAVES NONNORMALIZED NORMAL NORMALIZED
NOT NOT_RELATED_TO_FACTS NULL OF OLAP ON ON_OPEN OPEN
OPENINGBALANCEMONTH OPENINGBALANCEQUARTER OPENINGBALANCEYEAR OPENINGPERIOD OPENQUERY OPENROWSET OR ORDER
ORDERED ORDINAL ORG_TITLE PAGES PARALLELPERIOD PARENT PARENT_COUNT PARENT_KPI
PARENT_LEVEL PARENT_UNIQUE_NAME PASS PASSWORD PATH PATHCONTAINS PATHITEM PATHITEMREVERSE
PATHLENGTH PERCENT PERIODSTODATE PERSON_CONTACT PERSON_DEMOGRAPHIC PERSON_FIRST_NAME PERSON_FULL_NAME PERSON_LAST_NAME
PERSON_MIDDLE_NAME PHYSICAL_COLOR PHYSICAL_DENSITY PHYSICAL_DEPTH PHYSICAL_HEIGHT PHYSICAL_SIZE PHYSICAL_VOLUME PHYSICAL_WEIGHT
PHYSICAL_WIDTH PMML POST PREDICT PREDICT_ONLY PREDICTADJUSTEDPROBABILITY PREDICTASSOCIATION PREDICTCASELIKELIHOOD
PREDICTHISTOGRAM PREDICTION PREDICTIONSCORE PREDICTNODEID PREDICTPROBABILITY PREDICTSEQUENCE PREDICTSTDDEV PREDICTSTDEV
PREDICTSUPPORT PREDICTTIMESERIES PREDICTVARIANCE PREVIOUSDAY PREVIOUSMONTH PREVIOUSQUARTER PREVIOUSYEAR PREVMEMBER
PROBABILITY PROBABILITY_STDDEV PROBABILITY_STDEV PROBABILITY_VARIANCE PROPERTIES PROPERTY PROPRIETARY QTD
QTY_RANGE_HIGH QTY_RANGE_LOW RANGEMAX RANGEMID RANGEMIN RANK RANK.EQ RANKX
READ RECURSIVE REFRESH REGRESSOR REGULAR RELATE RELATED RELATEDTABLE
RELATION_TO_PARENT RELATIVE RENAME REPEATABLE REPLACE_MODEL_CASES RETURN ROLLBACK ROLLUP
ROLLUPCHILDREN ROLLUPCUBE ROLLUPGROUP ROOT ROW ROWPAGES ROWS ROWSET
SAMEPERIODLASTYEAR SAMPLE_CASES SCOPE SCOPE_ISOLATION SECTIONS SELECT SELF SELF_AND_AFTER
SELF_AND_BEFORE SELF_BEFORE_AFTER SEQUENCE SEQUENCE_TIME SERVER SESSION SET SETTOARRAY
SETTOSTR SHAPE SIBLINGS SKIP SKIPPED_LEVELS SKIPPEDLEVELS SLICE_EXPRESSION SOLVE_ORDER
SOLVEORDER START STARTOFMONTH STARTOFQUARTER STARTOFYEAR STATEMENT STATIC STATUS
STATUS_GRAPHIC STDDEV STDDEVP STDEV STDEV.P STDEV.S STDEVA STDEVP
STDEVPA STDEVX STDEVX.P STDEVX.S STORAGE STRICT STRIPCALCULATEDMEMBERS STRTOMEMBER
STRTOSET STRTOTUPLE STRTOVAL STRTOVALUE STRUCTURE STRUCTURECOLUMN SUBCUBE SUBCUBESET
SUBSET SUM SUMMARIZE SUMX SUPPORT SWITCH SYSTEMGETACCURACYRESULTS SYSTEMGETALLOWEDFOLDERS
SYSTEMGETCLASSIFICATIONMATRIX SYSTEMGETCLUSTERACCURACYRESULTS SYSTEMGETCLUSTERCROSSVALIDATIONRESULTS SYSTEMGETCROSSVALIDATIONRESULTS SYSTEMGETFILEEXISTS SYSTEMGETFILES SYSTEMGETLIFTTABLE SYSTEMGETLOGICALDRIVES
SYSTEMGETSCATTERTABLE SYSTEMGETSTRUCTURECLASSIFICATIONMATRIX SYSTEMGETSTRUCTURELIFTTABLE SYSTEMGETSTRUCTURESCATTERTABLE SYSTEMGETSUBDIRS SYSTEMOPENQUERY SYSTEMOPENSCHEMA SYSTEMRESTRICTSCHEMA
TABLE TAIL TEXT THEN THIS THRESHOLDS TO TOGGLEDRILLSTATE
TOP TOPCOUNT TOPN TOPPERCENT TOPSUM TOTALMTD TOTALQTD TOTALS
TOTALYTD TRAN TRANSACTION TREND TREND_GRAPHIC TRUE TUPLETOSTR TYPE
TYPED UNARY_OPERATOR UNARYOPERATOR UNDER UNIFORM UNION UNIQUE UNIQUE_NAME
UNIQUENAME UNKNOWNMEMBER UNORDER UPDATE UPDATEABLE UPDATES URL URLESCAPEFRAGMENT
USE USE_EQUAL_ALLOCATION USE_EQUAL_INCREMENT USE_WEIGHTED_ALLOCATION USE_WEIGHTED_INCREMENT USERELATIONSHIP USERNAME USING
VALIDMEASURE VALUE VALUES VAR VAR.P VAR.S VARA VARIANCE
VARIANCEP VARP VARPA VARX VARX.P VARX.S VERSION VISIBLE
VISUAL VISUALTOTALS WEB_HTML WEB_MAIL_ALIAS WEB_URL WEB_XML_OR_XSL WEIGHT WHEN
WHERE WITH WORK WRITE WTD XOR YTD
```

</details>
### 文件规模与职责拆分

- Vue 组件原则上控制在约 300 行, C# 类及业务 TypeScript 模块参考相同的可读性目标. 行数是检查职责的信号, 不是硬门禁; 内聚的解析器、声明表、生成代码及确有必要的状态机允许弹性, 在模块说明或任务记录中说明理由, 不压缩排版凑数.
- Vue 按功能拆分展示组件与组合式函数: 页面负责组装, 子组件通过明确且有类型的 props/emits/model/slots 接收配置和回传用户意图, 复杂业务及共享状态放入对应领域的组合式函数. 不把父组件原样搬入巨大 composable、无类型状态袋或依赖父级内部变量的碎片; 不为单次使用建立通用框架.
- 前端 `use*` 组合式函数统一放入 `web/src/composables/<原 src 领域>/`, 保留原领域与子目录归属, 不平铺或混入视图、字典及纯工具. `useI18n` 从 `composables/i18n/useI18n.ts` 导入; 反馈与操作消息复用 `composables/ui/useFeedback.ts` 中的 `useFeedback` 和 `useOperationMessage`. Vue API 与组合式函数保留显式导入, 仅类型依赖使用 `import type`, 不依赖隐式全局或无类型透传.
- 前端 `*.test.ts` 统一放入 `web/tests/<原 src 领域>/`, 镜像原领域及嵌套层级; 开发工具测试放入 `web/tests/tooling`, 全局测试初始化为 `web/tests/setup.ts`, 测试夹具与辅助模块随对应测试领域组织. 测试支持文件不放回产品 `src`, 产品代码不依赖测试目录. 测试引用产品源码使用既有 `@/` 别名, 测试内部支持文件使用正确的相对路径.
- 移动源码或测试时同步显式导入、文件头 `FilePath`、动态导入、测试读取路径、Vitest include/setupFiles、TypeScript include 及相关检查脚本与机器契约. 保留 `@` 指向 `web/src` 和严格检查项, 不通过旧路径转发文件、放宽类型或扩大扫描范围掩盖遗漏.
- 组件命名体现职责, 同类能力优先复用现有 UI 和业务组件. 保留响应式依赖、草稿和选择、watch 时序、挂载/卸载清理及性能边界, 避免重复监听、请求和无意义重渲染. 保留双语文案、ARIA/焦点/键盘行为、真实 disabled/guard 和原有样式作用域; 可配置性通过实际需要的参数表达.
- C# 按用例、策略、验证、存储和平台适配拆分内聚类型, 通过明确输入/返回值协作, 保持项目依赖方向及现有公开契约. partial 仅用于共享同一私有状态与生命周期的类型并按职责命名, 不以机械分文件代替职责治理. 宿主仍拥有资源, 写入/授权/锁/取消/事务/提交后排空边界保持同一权威所有者.
- 抽离时等价迁移原注释与约束, 为新组件、组合函数和协作者说明用途、主要参数、返回值及失败行为. 保持原导出或同步全部调用方与机器契约, 不顺带修改业务规则、界面流程、依赖和权限.
- 重构前确认现有行为测试与关键不变量, 重构后验证共享调用方及相邻失败/取消/并发场景. 优先运行原行为测试, 仅为未覆盖的真实边界补测试, 不用文件长度断言或实现镜像替代正确性; 静态检查、浏览器模拟和真实 Windows 验收分别取证.

## 4. 模型与授权边界

- 本地 PBIP 与已核验本机 Desktop 可经共享安全通道编辑; Power BI Service、SSAS/XMLA 保持只读. 不直接解析或改写 PBIX 二进制.
- 写入经 ModelEditService、宿主权威身份、完整基线复核及提交后权威回读, 用户发起的普通保存直接执行, 不提供“模型保存”设置或额外保存确认. 已连接 Desktop 在绑定原引擎的专属 TOM 会话原对象上校验和本地暂存, 不建立私有校验模型克隆或复制完整候选. 提交前由独立只读连接复核原权威定义, 对原对象一次提交; 失败或取消结束本次事务与会话, 不把未提交暂存留进引擎. 不创建首次或逐轮的长期模型历史备份; 文件事务临时回滚材料、未知提交核验材料与业务回执按实际恢复需要保留, 结果确定且恢复完成后清理临时材料, 不覆盖用户并发更改. 不先写 PBIP 文件再重载, PBIP/PBIX 文件由用户在 Desktop 保存. 模型提交、文件保存、Desktop 同步分别记录, 不以表列形状或模型回复宣布完整成功, 不自动刷新业务数据源. 离线 PBIP/TMDL 直接更新当前工程, 与新建工程按各自文件事务使用 staging, 不与连接模型操作混用.
- MCP 默认只读; 仅本地客户端启动参数 `--allow-write` 开启写能力, 工具参数不能授权. 桌面模型读写与本地连接/断开无需二次弹窗, 已核验 MCP 写请求由宿主自动签发精确授权. ToolWriteGrant 由应用签发, 一次绑定运行/工作区/来源/基线/精确参数, 不因外部 AI 调用或移除普通保存确认而省略, 网页/模型不能构造权限. 删除、覆盖或兼容级别升级等独立风险确认按具体用例保留, 不恢复通用保存确认.
- prepared/commit_started/业务回执可靠保存后才总结. 未知提交进入待核验并阻止新写入, 不重放; 离线文件部分保存仅补未完成阶段. 已连接模型提交前失败结束原对象操作上下文, 不提供重试保存/放弃修改流程; 核对未知提交后重新读取权威模型. 提交前可取消, 提交后排空.
- 日期、UDF 和脚手架须复用上述边界及完整生成归属核验; 不能以自有对象名、旧 stamp 或可见形状推定可覆盖. 细则按 skill 领域参考执行.

### 本地 MCP

- 产品不内嵌聊天、提供方目录、AI 密钥或会话管理. 新业务同时评估 CLI、Desktop 和 MCP 可用性; MCP 复用已有共享用例, 不复制官方 Power BI MCP 的通用能力.
- 本地 `pbi-utils.exe mcp serve` 以官方 C# SDK 提供 stdio 服务, 随包含和不包含 .NET 运行时的发布包分发. 不自动注册客户端、开放网络端口或安装常驻服务; stdout 仅协议, 诊断走 stderr 与既有业务日志.
- 所有 MCP 文件与模型工具及相关 CLI 共享资源操作经同用户、同安装副本的受限本机通道进入专用 Host, 核验真实 PID/启动时间. 每条连接固定 kind/allowWrite 并拥有独立目标、账号、草稿、计划与观察基线; 工具参数或 GUI 权限不能提升 MCP 权限. 不用通用反射、shell 或任意桥接代理扩大能力.
- 所有现有业务能力保持可发现的 MCP 映射. UI 几何、弹窗回复、凭据输入、全量数据重置等内部控制不暴露; 选择器、登录和确认由用户在可信本地 UI 完成. 只读标记是说明, 实际授权必须在服务端强制执行.
- GUI、多个 MCP 与相关 CLI 可同时连接按需启动的无 UI Host, 不共享当前模型或权限. Host 统一协调写入、可靠回执与未知结果阻断, 原文件锁、引擎事务、完整基线及回读继续执行. 客户端 EOF 仅排空其上下文, 最后连接与操作排空后 Host 退出; 旧连接及票据失效时不自动重放. 不按陈旧元数据或进程名授权, 不强杀在途提交.
- MCP 启用启动写权限后静默执行, 目标身份、连接代际、精确授权、旧观察基线、取消和回执保持. SSAS 通过无秘密 server/database 参数使用当前 Windows 身份, 不复用 GUI 密码契约. 显式 Service 登录可打开微软系统浏览器, 不打开 pbi-utils 前台. GUI 普通草稿保护保持.
- MCP 副作用保留独立可靠回执, 未知提交阻止新写和重置, 取消后排空提交. 本机通信不代表第三方 AI 不会外传工具结果; 设置说明须清楚表达边界.
- 按真实业务结果判定 MCP 成功, 不把所有正常返回对象当作成功. 已知部分失败保留已完成事实并返回工具错误, 未确定回读/回执保持待核验; Desktop 本机通道须保留相同分类.
- 更改 MCP 时同步工具 schema、覆盖/权限/取消测试、客户端配置说明、发布 smoke、依赖声明和锁文件. SDK 必需的 AI 抽象依赖不等于恢复内嵌 LLM, 不因包名包含 AI 盲目删除共享依赖.

## 5. UI、桥接、数据和日志

- 应用自有页面、表单、消息、进度、确认、授权和关闭提示全部使用 Vue 与共享 UI. WinForms 仅保留外壳、WebView2、生命周期和受限系统适配. 系统文件/目录选择器、微软登录和 Power BI Desktop 自身 UI 为系统/外部例外.
- 复用 UiTabs、UiSteps、CodeEditor、HostDialog、useOperationMessage 和 operation-lock; 不重复造组件或用视觉稳定绕过业务 disabled/guard. 连接页自动核验静默仍执行真实请求; 用户主动操作保留在途与取消入口.
- 前端固定文案集中在 `web/src/i18n`, 支持 zh-cn/en-us, 默认中文. 偏好持久化成功后才切换; 不用 localStorage 或系统区域另建来源. 切语言保留草稿/选区/历史, 不改写业务名称或已有源码. 未知值共用 `-`; 未读取报表页数仅 UI 显示 0, 后台仍为 null.
- 桌面最小内容区 1422×800, 保留侧栏折叠和并排工作区, 验证较宽窗口及双语布局. 所有模态双轴居中、正文可滚动、操作可达, 确认按钮间距 12px. 样式使用 SCSS、共享品牌变量及本地资源, 不加载远程字体/图标/脚本.
- 生产仅接受打包资源固定虚拟 HTTPS origin. Debug 开发 origin 必须显式启用并精确匹配, Release 不启用开发入口. 校验真实来源/导航、请求身份、参数/长度、超时和取消; 不提供通用文件、shell、eval 或反射桥接.
- 桥接变更同步 `BridgeMethods.cs`、`BridgeSession.cs`、TypeScript Methods 及 `tests/contracts/bridge.schema.json`/样例; 以一致性测试为准, 不将旧固定方法数量或 `.docs` 副本当机器契约. 生产缺桥接明确失败, 不降级模拟成功.
- 当前版本应用用户数据统一在 UserProfile 下 `.pbi-utils`, 不硬编码用户名或写安装目录. 原子存储、独占锁、修订核验、损坏检测及账号/云/目标隔离保持; 不覆盖用户并发改动. 共享后台协调锁位于用户数据树之外; 全量重置必须持独占维护租约并确认其他客户端已退出, 排空业务与日志后执行, 阻止自动重连抢占.
- 快速迭代期间, 当前及旧版应用用户数据(包括设置、缓存、会话及历史记录)均可不保留, 不受支持的数据格式可按当前任务需要清理或重建, 不作为延续旧实现的约束. 除非用户明确要求, 不为这些数据增加或保留兼容读取、迁移、恢复及多版本共存逻辑. 当前版本正常运行所需的原子存储、并发保护、凭据安全与目标隔离仍须满足; 此数据重置约定不授权删除用户源码、Power BI 模型、报表或工程文件, 不扩大模型/报表写入授权.
- 新密码/密钥仅可经可信本地 Vue 一次性受限字段单向进入宿主; 清空状态和真实 DOM, 禁止自动保存/填充. 既存凭据、密文、令牌不回网页; CurrentUser DPAPI, 不明文降级. 不写 VITE_*、草稿、历史、日志或测试快照.
- 业务日志复用 LoggingRuntime/BusinessLog/集中 Events, 记录安全阶段、关联和唯一终态; 不记录账号、地址、路径、聊天/模型/DAX/M正文、业务值、凭据或异常原文. 不另起日志体系或自动上传.

## 6. 自检、验证与交付

- 非平凡任务在 `.docs/<任务>/` 保留计划、发现、进度和报告. 恢复前核对当前需求、Git/cwd 和任务归属; 不串用其他活动任务. 交接记录已完成/待办、证据和阻塞, 不依赖缺失的个人 skill、未知 hook 或未提供的精确 token 数据.
- 修改前确定受影响路径与验证边界; 修改后检查需求覆盖、相邻输入、失败/取消/并发/迟到结果、共享复用、双语、契约、权限和资源释放. 测试关注真实行为与不变量, 不为低影响文档修改增加重复实现测试.
- 开发入口为 PowerShell 7 `run.ps1`, 动作/参数以注册表和 `-List/-Help` 为准. C# 与 Vue 均按 check → test → build, C# 先于 Vue, 首错停止. 正式总门禁:
- **注意：** 由于`.\run.ps1 -Choice all` 大约需要3分钟的执行时间，仅在AI单轮开发完毕后（不要在开发过程中频繁使用），交付给人类的时候，需要执行校验，保证全量通过。

  ```powershell
  pwsh -NoProfile -File ./run.ps1 -Choice all -Scope All -Configuration Release -NonInteractive
  ```

- clean 后先 `setupC# -Scope All`; check 只读, format 是显式写源码动作. SDK 不匹配报告配置/期望/可见/实际版本并返回前置条件错误, 不自动安装、改绑定或滚动版本.
- 门禁逐层保留工具非零退出码, 以本轮结构化结果核验测试实际执行; C# 编译器警告按错误处理, 零用例/全部跳过/无效 TRX 失败, 部分跳过明确为 UNRUN. 前端未预期 console.warn/error 和 Vue 诊断必须使测试失败; 负向用例仅在具体动作中精确声明预期参数与次数, 不全局过滤或吞诊断.
- 发布模块独立设置终止错误策略, 枚举/读取失败不得生成部分清单. 清理单独失败也返回非零; 与主操作同时失败时保留最先阶段和退出码, 并报告清理原因及产物实际状态.
- 前端测试覆盖模块导入与窗口 error/unhandledrejection, 全部 skip/todo 失败, 部分跳过单列 UNRUN. 正式构建均为独立Vite CLI, 进入日志器的warn/warnOnce/error立即标记进程失败且保留既有非零码, 晚期回调吞异常也不能成功交付; error日志不另抛通用横幅覆盖解析/写盘首因. 不把程序内build() promise完成当作成功判据. 保留500kB阈值, 写盘前按所有压缩后chunk的UTF-8字节硬校验. 分包按共享依赖归属和纯数据边界, 核验真实chunk字节、循环与重复模块, 不改页面挂载/草稿生命周期或提高阈值.
- `all/buildAll/buildC#` 接受 `-DotNetMode with-dotnet|without-dotnet`, Release 默认 without-dotnet, Debug 默认 with-dotnet, 一次只构建选中模式. Debug 发布到 `artifacts/build/debug/win-x64`; Release 发布到 `artifacts/build/release/win-x64/<模式>`. 编译、前端与清单校验后再交付, 固定目标持互斥锁并逐文件覆盖、删除过期文件、manifest 最后写, 失败恢复本轮文件. 清单使用 schemaVersion=3、layout=shared 和 deploymentMode=self-contained/framework-dependent, 按真实 runtimeconfig/deps 与文件核验模式. 不重命名根/子目录, 不通过更换输出目录或结束用户进程逃避真实文件锁. 影响发布时运行 `scripts/tests/published-smoke.ps1` 验证所选模式, 默认为 without-dotnet.
- `release` 执行完整 Release/All 和 NSIS 打包; `package` 仅打包同版本已验证 Release/All 产物, 均接受 DotNetMode, 默认 without-dotnet. 使用本机 makensis, 输出 artifacts/release/win-x64/<模式>, 不自动签名/安装/上传. 用户运行安装器时由 Windows PowerShell 5.1 检测并按需安装官方 .NET Desktop Runtime/WebView2, 仅对必要前置组件及固定的 Power BI 外部工具注册步骤请求管理员授权. all -Scope All 完成交付后注册开发入口, 安装器维护独立安装入口; Core/buildC#/buildAll/release/package 不修改开发登记. 注册失败明确返回非零, 卸载与回滚仅处理自有且未被并发修改的登记. 当前用户固定目录升级, 精确文件归属/占用/回滚, 不强杀进程或删除用户数据与共享运行时; 真实安装验收与编译/隔离测试分别报告.
- 验收分别记录自动化、浏览器显式模拟、真实发布 WebView2、真实 Desktop 提交/计算/保存重开、真实云/网络和物理异机. 浏览器不能替代 Windows 实测; 文档起始采集 window.error/unhandledrejection, 同时检查 console/pageerror. 未实际执行的项目为 UNRUN, 保留原始 FAIL 及后续解释.
- CLI JSON stdout 恰好一个完整文档, 进度/诊断到 stderr; 退出码 0/2/3/4/130 分别为成功/参数/前置/执行失败/取消. 元命令与取消规则保持, 不恢复已移除的开发包装动作. 保留 doctor 正式诊断, 不恢复已获准移除的 Hello/mock 演示命令和桥接.
- 交付前正常关闭并排空本任务窗口、服务、watch/测试宿主和自动化, 核实自有 PID/启动时间/命令行/路径、端口和文件锁. 不按进程名批量结束 node/dotnet/WebView2/Explorer/用户 Desktop, 不关闭归属不明句柄; 终端不得停留在发布目录.
- 报告改动、实际命令及退出码、PASS/FAIL/UNRUN、证据、未完成原因和资源清理. 不宣称未取证的生产可用、完整替代或 Windows 验收通过; 不提交或推送, 除非用户明确要求.
