// FilePath    : pbi-utils\src\PbiUtils.Core\ModelTableExport.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义绑定当前模型的多表导出请求、真实计数进度与不含路径的逐表结果.

namespace PbiUtils.Core;

/// <summary>仅指定已连接模型中的精确表集合和输出格式, 不接受路径、连接串或 DAX.</summary>
/// <param name="ProjectId">宿主当前模型完整身份.</param>
/// <param name="BaselineId">用户看到的完整结构基线.</param>
/// <param name="Tables">当前模型实际表名, 非空且按精确名称去重, 顺序决定输出页或文件顺序.</param>
/// <param name="Format">固定格式 xlsx 或 csv.</param>
/// <param name="IncludeLog">是否在 Excel 添加导出摘要工作表, CSV 必须为 false.</param>
/// <param name="Locale">摘要语言 zh-cn 或 en-us.</param>
/// <param name="RowLimit">每表模型侧抽样行数上限, 必须为正整数; null 表示全部行, 不指定排序.</param>
public sealed record ModelTableExportRequest(string ProjectId, string BaselineId, IReadOnlyList<string> Tables, string Format, bool IncludeLog = false, string Locale = "zh-cn", int? RowLimit = null);

/// <summary>单张表完整写入后的实际结果, 不包括标题或摘要.</summary>
/// <param name="Table">已核验的实际表名, 不因输出文件或工作表清理命名而改写.</param>
/// <param name="RowCount">该表实际成功写入的数据行数.</param>
public sealed record ModelTableExportItem(string Table, long RowCount);

/// <summary>仅含实际读取和逐表复核计数的导出进度, 不包含表名、路径、业务值或伪造终态百分比.</summary>
/// <param name="Stage">固定阶段reading、verifying、finalizing或publishing, 发布成功以原请求终态为准.</param>
/// <param name="CompletedTables">已经完成数据读取和权威身份复核的表数.</param>
/// <param name="TotalTables">本轮冻结的所选表总数.</param>
/// <param name="TableIndex">当前表在请求中的1起始索引, 格式完成和发布阶段仍指向最后一张表.</param>
/// <param name="RowsRead">当前表实际成功读取的行数, 不推断源总数或已发布行数.</param>
public sealed record ModelTableExportProgress(string Stage, int CompletedTables, int TotalTables, int TableIndex, long RowsRead);

/// <summary>完整文件发布后的导出事实, 不向网页返回目录或业务数据.</summary>
/// <param name="FileName">原生选择器授权的文件名或全新CSV输出子目录名, 不含父目录.</param>
/// <param name="RowCount">所有表实际成功导出的数据行数总和, 不包括标题.</param>
/// <param name="Format">用户选择的格式, 多表CSV发布为目录而非压缩包.</param>
/// <param name="Tables">按请求顺序返回的逐表实际行数.</param>
/// <param name="IsSimulation">是否为显式浏览器模拟.</param>
/// <param name="RowLimit">本轮请求的每表抽样上限; null 表示全部行, 不推断源表总行数或是否截断.</param>
/// <param name="LocationWarning">文件已发布但目录记录未持久化等安全警告, 不改变导出成功事实.</param>
public sealed record ModelTableExportResult(string FileName, long RowCount, string Format, IReadOnlyList<ModelTableExportItem> Tables, bool IsSimulation = false, int? RowLimit = null, string? LocationWarning = null);
