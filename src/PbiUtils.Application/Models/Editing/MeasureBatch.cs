// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Editing\MeasureBatch.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 度量值批量增删改契约, 严格校验字段和目标身份.

using PbiUtils.Core;
using PbiUtils.Application.Models;
using PbiUtils.Application.Input;

namespace PbiUtils.Application.Models.Editing;

/// <summary>空属性表示不修改, 空字符串表示明确清空; 名称与表共同定位现有对象.</summary>
public sealed record MeasureChange(string Action, string Table, string Name, string? Expression = null,
    string? Description = null, string? FormatString = null, string? DisplayFolder = null, bool? IsHidden = null);

/// <summary>绑定用户当前看到的快照, 整批作为一次内存修改和保存.</summary>
public sealed record MeasureBatch(string BaselineId, IReadOnlyList<MeasureChange> Items)
{
    public const int MaxItems = 128;

    /// <summary>解析有界批次, 拒绝重复目标、空更新和删除夹带属性.</summary>
    public static MeasureBatch Parse(string value)
    {
        var batch = StructuredInput.Parse<MeasureBatch>(value);
        if (string.IsNullOrWhiteSpace(batch.BaselineId) || batch.BaselineId.Length > 200 || batch.Items is not { Count: > 0 and <= MaxItems })
            throw new AppException("VALIDATION_ERROR", "请选择 1–128 个度量值并重新读取模型");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch.Items)
        {
            if (item is null) throw new AppException("VALIDATION_ERROR", "度量值操作不可为空");
            ModelNames.Validate(item.Table); ModelNames.Validate(item.Name);
            if (!targets.Add(item.Table + "\0" + item.Name)) throw new AppException("DUPLICATE_TARGET", "同一批次不能重复操作同一个度量值");
            if (item.Action is not ("create" or "update" or "delete")) throw new AppException("UNSUPPORTED_OPERATION", "不支持此度量值操作");
            var hasFields = item.Expression is not null || item.Description is not null || item.FormatString is not null || item.DisplayFolder is not null || item.IsHidden is not null;
            if (item.Action == "delete" && hasFields || item.Action == "update" && !hasFields)
                throw new AppException("VALIDATION_ERROR", "删除不能包含修改属性, 更新必须选择至少一个属性");
            if ((item.Action == "create" || item.Expression is not null) && string.IsNullOrWhiteSpace(item.Expression))
                throw new AppException("VALIDATION_ERROR", "度量值 DAX 不可为空");
        }
        return batch;
    }
}
