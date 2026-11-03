# 新建资源目录

当前只收纳实际使用的 PBIP 模板素材, 不提前创建未使用的资源类型或目录:

```text
Assets/
  README.md
  pbip/
    blank/
```

每个模板目录是一个完整来源快照, 包含 PBIP 入口、Report、SemanticModel 和 `pbi-utils.template.json`.
模板目录原名 `blank` 是展示名称与内置身份, 不翻译或重命名.
后续修改某个占位模板时, 在该目录内维护原工程及对应清单, 不混放生成产物.
`Infrastructure.csproj` 仅嵌入 `Assets/pbip/**/*`, 逻辑资源名继续为 `PbiUtils.Scaffold/{模板目录}/...`,
目录整理不改变模板相对文件名、内容或基线. 原 `Infrastructure/Templates/Blank` 仅剩空目录, 已移除;
后续有效模板统一放在上述 `pbip` 分类, 不保留第二套来源或空占位目录.

## PBIP 内置模板来源

`pbip` 目录保留用户提供的 PBIP 素材快照。blank 于 2026-10-22 替换为 blank.pbip，包含空白报表与空语义模型。
原素材未附独立许可证，不将其推定为第三方 MIT 授权；保留原作者注释、主题和报表内容。

每个模板的 `pbi-utils.template.json` schemaVersion 为 1，固定记录源文件大小/SHA256、
官方 TOM 解析的真实表/列/分区/关系/表达式，以及显式业务角色。
列的 sourceColumn 和 dataType 来自 TOM，不按文件名或正则推断；calculatedTableColumn 中的 Unknown 保持原始含义。
分区表达式仅作为来源映射材料保存，从未执行 M、DAX、Access 或网络源。

本目录保留 PBIP/PBIR/PBISM、报表布局/主题、TMDL 和必要的 `.pbi/editorSettings.json`；
排除 `.git`、`.mimosa`、`.pbi/cache.abf`、`.pbi/localSettings.json`、日志和临时文件。
blank 兼容级别为 1606, 不含日期表、命名表达式或关系.

**这些资源是转换输入，不是可以直接刷新独立数据的最终项目。**
脚手架组合器必须按清单替换机器路径、Access、配置和百度日历来源，生成项目内数据和配置，
将 PbiUtilsProjectRoot 参数设为最终输出根目录，保留计算表和报表引用。
不允许只删除 Path 参数或按全局文本替换改名；
城市不能冒充区县；零售组织/租赁/装修/员工/人工成本没有模拟输入时保留完整空结构。

用户模板放在用户根 `.pbi-utils/templates/pbip/<独立目录>`，每个目录根只能有一个 PBIP，
PBIP 报表必须通过相对路径绑定本目录内的本地模型。无描述文件也可发现，外部源不执行。
可选描述文件 `pbi-utils.template.json` 至少包含 `{"schemaVersion":1}`，可提供 name/description；
未知 schemaVersion、重复字段和未知顶层字段均拒绝读取，不回退覆盖。

## 新建产物的数据位置

- `pbip/<项目名>.pbip`、`pbip/<项目名>.Report/`、`pbip/<项目名>.SemanticModel/`: 新建时按真实绑定映射路径, 不修改本目录模板原名或业务表达式.
- `pbip/.gitignore`: 无论是否初始化 Git 都生成 `**/.pbi/localSettings.json` 和 `**/.pbi/cache.abf` 两条规则. Git 可选初始化仅位于 `pbip/`; 模板已有忽略内容保留在输出并补齐缺失规则, 源字节不改写.
- `data/<固定示例表名>.csv`: 只有选择模拟数据时才生成, 直接复用“示例”的共享生成器、基础数据与十一表 schema. 无转换的独立模拟表直接引用这些文件.
- `data/model/<模板表名>.csv`: 模板业务列、门店区县或销售目标月转日等适配后的 CSV; 未选择模拟数据时保留模板所需空表结构, 不生成原始十一表.
- `data/config/<配置表名>.csv`: 页面和安全配置表的数据; 模板度量 Config 模式直接读取业务 JSON, 不再生成参数 CSV.
- 日期表由自包含 Power Query M 生成, 内嵌315字节固定农历年规则, 不生成日期CSV或依赖项目目录参数. 已有日期CSV须核验完整归属后迁移, 原文件保留且停用.
- `pbi-utils/`: 两种模式的新建工程均建立此目录; TMDL 直接生成文本常量且不生成 JSON. Config 将六字段配置写入 `pbi-utils/config.json` 的 `MeasureTemplates`, 由隐藏参数表加载并由度量按名称查值. 该 JSON 是可手工编辑的权威配置, 读取修订和本轮前后 hash 控制并发, 不要求等于历史生成摘要. 模型页支持宿主已核实的本机 PBIP/PBIX, 不要求预建配置目录; 普通 PBIP/PBIX 使用入口父目录, PBIP 位于 `pbip/` 时使用上一级根. 读取缺目录或文件只取得 missing 修订, 首次应用 Config 才创建. 路径类型冲突、链接或 JSON 损坏时回退 TMDL 并保留原文件; 无可信文件身份或远端只读目标不发放文件写入能力. 显式模板对象按本次配置不存在创建、存在原位置覆盖, 不要求旧生成标记, 人工修改不阻挡切换. 切回 TMDL 物化保留项并移除有效文件的本功能段; 无引用参数表退休, 有引用时保留隐藏的内嵌值快照. 其他业务 JSON 段及页面/权限配置表保留. 模式切换中仍被引用的旧项保留而不阻断切换; 同模式显式删除/改名仍核对存活引用, 禁用仅隐藏. 不搜索、迁移或删除旧 `config/config.json`. 资产归属和 UDF 精确安装锁、原始许可保存在模型注解; 不生成 scaffold/配置归属/UDF锁/许可旁文件.

`pbip/`、`data/`、`pbi-utils/` 同级, `PbiUtilsProjectRoot` 始终指向最终项目根. 用户模板可使用根目录或直接 `pbip/` 子目录中的唯一入口, 新建产物仍统一使用上述布局.

旧 `data/sample` 已不再生成. 原始示例文件与模板适配文件分开, 避免同名但不同 schema 的 CSV 覆盖;
用户模板已有任何同名原始示例文件时拒绝生成, 不改写模板源工程. 所有新文件仍只进入隔离 staging,
共用总字节预算、取消检查和发布前回读, 最终目录由原有新建发布边界控制.
