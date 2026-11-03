// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\TableDeleteBatch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 绑定当前模型身份和完整基线的严格所选表删除批次.

using PbiUtils.Core;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Models.Editing;

/// <summary>整批删除准确表集合, 关系由共享内存修改器在同一操作上下文清理.</summary>
public sealed record TableDeleteBatch(string ProjectId, string BaselineId, IReadOnlyList<string> Tables)
{
    public const int MaxTables = 2000;

    /// <summary>解析有界身份和表集合, 拒绝空目标、重复、未知字段及过大输入.</summary>
    /// <param name="value">当前桥接请求的嵌入 JSON, 不接受路径或连接地址.</param>
    /// <returns>只含用户明确选择目标的严格批次.</returns>
    /// <exception cref="AppException">输入无效时在创建模型操作上下文前失败.</exception>
    public static TableDeleteBatch Parse(string value)
    {
        var batch = StructuredInput.Parse<TableDeleteBatch>(value);
        if (string.IsNullOrWhiteSpace(batch.ProjectId) || batch.ProjectId.Length > 200
            || string.IsNullOrWhiteSpace(batch.BaselineId) || batch.BaselineId.Length > 200
            || batch.Tables is not { Count: > 0 and <= MaxTables })
            throw new AppException("VALIDATION_ERROR", "请选择 1–2000 张表并重新读取模型");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in batch.Tables)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
                throw new AppException("VALIDATION_ERROR", "表名称无效");
            if (!names.Add(name)) throw new AppException("DUPLICATE_TARGET", "同一批次不能重复删除同一张表");
        }
        return batch;
    }
}
