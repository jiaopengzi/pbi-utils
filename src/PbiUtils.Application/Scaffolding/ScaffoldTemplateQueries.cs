// FilePath    : pbi-utils\src\PbiUtils.Application\Scaffolding\ScaffoldTemplateQueries.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 提供宿主预览、显式模拟及创建共用的固定 M 查询目录, 不读取用户路径.

using System.Text.Json;
using System.Collections.ObjectModel;

namespace PbiUtils.Application.Scaffolding;

/// <summary>固定查询的稳定 ID、Power Query 分组与完整 M 原文; 不接受网页提交表达式.</summary>
public sealed record ScaffoldTemplateQuery(string Id, string QueryGroup, string Expression);

/// <summary>只从编译资源加载版本化目录, 返回不可变集合供预览和创建共用.</summary>
public static class ScaffoldTemplateQueries
{
    /// <summary>固定四个自包含辅助表/查询; 资源缺失或损坏时启动失败, 不回退空成功.</summary>
    public static IReadOnlyList<ScaffoldTemplateQuery> All { get; } = Load();

    /// <summary>读取同源 JSON 编译资源, 保持预览原文与生成原文一致.</summary>
    private static ReadOnlyCollection<ScaffoldTemplateQuery> Load()
    {
        using var resource = typeof(ScaffoldTemplateQueries).Assembly.GetManifestResourceStream("PbiUtils.Scaffolding.TemplateQueries.v1")
            ?? throw new InvalidOperationException("固定 M 查询资源缺失");
        return Array.AsReadOnly(JsonSerializer.Deserialize<ScaffoldTemplateQuery[]>(resource, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("固定 M 查询资源无效"));
    }
}
