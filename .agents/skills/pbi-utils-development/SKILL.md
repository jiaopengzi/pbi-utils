---
name: pbi-utils-development
description: Use when developing, debugging, reviewing, cleaning source files, validating, or maintaining documentation in the pbi-utils Windows C#/Vue repository, including model operations, PBIP scaffolding, calendar, UDF, local MCP, host bridge, installer, and release work.
---

# pbi-utils 开发与自检

用于当前仓库的工程开发, 不作为产品内 Agent 提示词. 先读根 `AGENTS.md` 和当前需求; 本 skill 不增加用户授权, 不把历史验收结论视为本轮事实.

## 按影响选择参考

只读取当前任务相关部分. 路径从仓库根定位, 不依赖特定用户名、全局安装或私有 `.docs` 文件存在.

| 当前工作 | 读取 |
| --- | --- |
| 定位模块、判断依赖或配置所有权 | [architecture.md](references/architecture.md) |
| 新增/修改/抽离源码、DAX/M 模板与注释 | [code-and-comments.md](references/code-and-comments.md) |
| 模型修改、保存、MCP 精确授权、恢复 | [model-safety.md](references/model-safety.md) |
| Vue/宿主、桥接、弹窗、交互与双语 | [ui-and-bridge.md](references/ui-and-bridge.md) |
| 连接、身份、用户存储、凭据与日志 | [connections-and-storage.md](references/connections-and-storage.md) |
| 新建、示例、日期函数或 UDF | [scaffolding-calendar-udf.md](references/scaffolding-calendar-udf.md) |
| 本地 MCP、第三方客户端接入、工具覆盖与权限 | [local-mcp.md](references/local-mcp.md) |
| 构建、发布、真实验收与收尾 | [validation-and-delivery.md](references/validation-and-delivery.md) |
| 安装器、软件更新、发布标签及 GitHub/Gitee 分发 | [release-and-updates.md](references/release-and-updates.md) |

根 README 是用户指南, 不承担开发操作手册和历史验收索引. 维护文档时从对应功能入口、共享实现和机器契约核对事实; 涉及多个领域才按范围读取多份参考, 不将旧说明互相引用当作代码证据.

## 执行流程

1. 确认需求、现有授权、Git/cwd、用户改动和相关模块. 在 `.docs/<任务>/` 保存非平凡任务的计划、发现与进度, 使用唯一任务目录; 不恢复其他任务或擅改共享指针.
2. 用当前入口/调用方/契约/测试核实行为, 再决定最小改动. 保留当前仍使用的功能占位、真实示例数据、开发 mock 和测试夹具; 未明确要求兼容的 legacy 分支按授权范围清理, 不因历史存在而保留, 也不因名称像 demo 判断可删.
3. 复用已有服务和组件, 保持完整身份、基线、MCP 一次写授权及提交边界. 普通保存直接执行, 不创建长期模型历史备份; 保留必要的事务临时回滚、未知提交核验材料与业务回执. 修改共享逻辑时核查所有调用方, 不用 UI 结果替代后端事实.
4. 以行为验证需求和相邻边界: 空/上限/重复/过期输入、取消/超时/关闭、并发/目标变更/迟到结果、失败保留/部分保存/未知提交. 按实际影响选取, 不为任务无关场景重放真实写入、登录或付费请求.
5. 执行受影响检查. 涉及产品代码的完整门禁按 C# check→test→build, 再 Vue check→test→build 及发布目录交付, 首错停止; NSIS 安装包由独立 package/release 动作生成. 文档任务验证事实、引用、路径与 skill 格式, 不虚报产品重测.
6. 验收按模拟/发布宿主/真实引擎/真实外部服务分别取证; 仅将本轮实际执行结果写为 PASS/FAIL, 无证据为 UNRUN. 失败后保留原始记录, 修复后另记复验.
7. 正常关闭本任务资源并确认端口/句柄释放, 保留用户与其他任务进程及并发配置. 报告实际改动、命令/退出码、证据与限制; 无明确指令不提交/推送.

## C# 固定模板

固定 DAX/M 模板优先使用原始字符串, 动态集合构造及目标语言转义保留现有方式. 修改这类生成代码前读取 [code-and-comments.md](references/code-and-comments.md) 的模板选择与输出保持规则.

## 源码整理的额外检查

- 为每候选记录具体路径、用途、运行/DI/资源引用、契约/测试/发布引用、共享类型及删除后行为.
- 区分整文件删除与混合文件定点编辑; 独立共享契约先保留, 通用安全回归迁移后才能移除演示夹具.
- 需求要求先确认时, 先提交准确白名单、保护项和可审阅方案, 等确认再删; 批准只覆盖该清单. 未列入或身份不明的文件保留.
- 正式 SampleData/Scaffolding 的 CSV/PBIP/资源是业务功能. `mock`、`fake` 和旧数据标识可能仍用于当前开发验收; 以实际用途决定去留, 不按关键词批量清理或默认保留历史兼容.
- `.docs` 历史证据和用户忽略目录不混入源码删除. 不改忽略规则、不进行全盘或用户配置清理.

## 维护这个 skill

规则来自当前代码和明确约定; 修订时保留授权范围及领域例外. 更新长期规则与相关参考, 不在根 AGENTS 或 skill 追加开发日期流水账、历史计数或未经当前核验的 PASS.
