<!--
 * FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\README.md
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 示例资源来源, 完整清单与目录事务边界.
-->

# 示例资源

资源迁自用户本地上游 `Power-BI-custom-sample-data`, 固定提交 `197aaf4b6c3fac28990814851640c21796d49263`。PBIP 表名在此来源上统一为 `Measure`、`Calendar`、`Hour24`、`MQHY` 和 `RefreshTime`, 同步文件名、分区、关系、度量、Q&A、报表与图表引用, 保留原有 GUID、BOM、换行和其他业务内容。`manifest.json` 记录当前全部 56 个文件的相对路径、实际字节数与 SHA-256, 其中 5 个是地理/姓名数据, 51 个是完整语义模型和报表模板。上游地址为 https://github.com/jiaopengzi/Power-BI-custom-sample-data 。没有迁入 Fyne、Go 工具链、上游界面字体或原生可执行程序。

模板保留 `.platform` 与 `.pbi/editorSettings.json` 等必要隐藏资源。`cache.abf`、`localSettings.json`、`.mimosa` 不在清单, 也被资源声明明确排除。JSON 和来源模板不添加源文件注释, 避免破坏格式。测试对照当前清单与实际程序集资源验证每个文件的哈希、大小和必要隐藏条目。

全量模板只动态替换 `expressions.tmdl` 的 `Path` 参数及 `Calendar.tmdl` 中日期表达式的起止年份, 保留原始 BOM、CRLF、缩进、月日和其他内容。增量只扩张当前 `Calendar.tmdl` 的年份, 不读取或迁移旧表名/缺自动编号的事实 CSV, 旧输出目录不自动升级, 请使用新目录全量生成; 不补回用户已删除的模板或目标 CSV。上游虽然接受 `locale`, 当前生成器没有依此改变 CSV 内容; 两种语言继续保留相同中文字段与数据。路径中的 `#` 在 M 字符串中转义为 `#(#)`, 使目录名得到正确的字面值。

固定产物为 `data` 内 11 张 BOM UTF-8 CSV 和 `pbip` 内完整模板。全量覆盖同名资源, 保留两个子目录中不属于固定产物的额外文件; 清空删除 11 张固定 CSV 和**整个 `pbip` 目录**, 因而宿主确认必须明确包括用户放入其中的额外文件。`data` 额外条目及输出根内其他文件保留。只有 PBIP、部分 CSV 或损坏 CSV 也能选中并经明确覆盖后修复; 统计读取严格报损坏, 增量仍要求四张基础表。

两个产物子树先在同盘随机隔离目录生成, 完成模板、统计和内容基线复核后发布。准备失败或取消清理隔离目录; 发布开始后排空不可取消, 两目录移动失败按反向顺序恢复。目标及其祖先、产物子树拒绝重解析节点; 发布复核基线变化时保留外部内容。该流程不是跨进程、断电或崩溃后的持久多目录事务。若自动恢复也失败, 保留隔离现场并返回 `SAMPLE_RECOVERY_REQUIRED`; 发布成功后的隔离清理失败写安全业务日志, 不改写已提交成功事实。
