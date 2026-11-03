# DAX Formatter 官方基准

此目录保存任务 `.feat/261030-03.md` 的全部 92 个生产度量、`logo_image2` 示例及 24 个独立边界用例, 共 117 个输入. 每个输入都已实际调用 SQLBI 服务取得 Long/Short 两种响应, 合计 234 份成功格式化结果. 常规测试仅读取程序集内的快照, 不联网.

- `production.tmdl`: 任务中的完整 TMDL 原文, 仅统一文件行尾为 LF. 不属于产品模板, 不装入真实模型.
- `inputs.json`: 从 TMDL 提取的正文、原度量名称、任务文件 SHA-256 与边界输入. 三个 fenced 表达式也完整提取, 注释和字面量内部空白不变.
- `response-long.json` / `response-short.json`: 实际发送的请求和完整服务响应. 响应 `formatted` 保留服务的 CRLF/LF, `errors` 全为空数组.
- `oracle.json`: 完整请求/响应与可直接断言的 `expectedLong` / `expectedShort`. `expected` 只统一 CRLF, 剥离固定测试定义头及一个服务末尾换行, 不重新排版.

请求端点为 `https://api.daxformatter.com/api/daxtextformatmulti`; `maxLineLength=0` 为 Long, `1` 为 Short. 另外固定 `skipSpaceAfterFunctionName=false`, `listSeparator=","`, `decimalSeparator="."`. 规则来自 [SQLBI 官方文档](https://docs.sqlbi.com/dax-formatter/) 和 [官方客户端的固定提交](https://github.com/sql-bi/DaxFormatter/tree/2979aeb6abaaf799f69fb403fda67dee6bcf4428/mcp/src/daxformatter). 服务未返回 formatter 版本, 快照不虚构服务版本.

2026-10-30 的采集有两个可见差异, 不应进入产品实现:

1. 0802/0803/0804 三个库存度量的首部注释很长且含等号, 服务返回的定义头从 `OracleMeasure =` 变成 ` =`. 快照完整保留该响应; 基准只剥离这个异常定义头, 并核验正文全部词元与输入逐项一致. 使用裸名 `:=` 仍出现同类问题, 方括号定义头探针返回语法错误, 没有改写原始生产 DAX 来规避问题.
2. `boundary-preserved-lowercase` 的服务响应把 `var/sum/return/divide` 转成大写. 原始期望保持服务原样; 测试用独立词元扫描器保留每个官方空白字符并恢复输入裸词大小写. 任何字符串、注释、引用、词元数量或顺序变化都会失败. 其余全部用例的所有词元与输入完全相同.

本地 `FormatMeasure` 仅增加前导 LF, 正文顶级行顶格, 直接与官方正文布局比较. 不额外叠加宿主缩进; 内部语法缩进保持. 多行字符串、转义标识符和块注释作为完整词元保留, 不逐行缩进其内部内容.

离线检查快照:

```powershell
python scripts/tests/update-dax-format-oracle.py
```

只有显式更新才发送此目录固定测试 DAX 到 SQLBI, 不读取连接模型或其他用户工程:

```powershell
python scripts/tests/update-dax-format-oracle.py --refresh
```

从新的、已授权任务文件重新提取输入并更新:

```powershell
python scripts/tests/update-dax-format-oracle.py --source .feat/261030-03.md --refresh
```

`--rebuild` 仅使用已保存的完整响应重新构造 `oracle.json`, 不联网. 数据缺失、输入/请求不匹配、服务报错或非大小写词元变化都会失败, 不生成空成功. 更新后须审阅快照差异并运行对应测试; API 格式化成功不代表真实模型绑定或计算验证通过.
