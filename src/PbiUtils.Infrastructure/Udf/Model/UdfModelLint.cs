// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Model\UdfModelLint.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从完整 TOM 可写定义分析 DAX 根和 UDF 依赖闭包, 保守拒绝未知表达式.

using PbiUtils.Application.Udf;
using PbiUtils.Application.Models;
using PbiUtils.Application.Code.Dax;
using System.Text.Json;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Udf.Model;

namespace PbiUtils.Infrastructure.Udf.Model;

/// <summary>只读取调用方拥有的 TOM; 无法证明完整时不提供可删除函数.</summary>
public static class UdfModelLint
{
    /// <summary>分析全部 DAX 表达式根及函数传递闭包, 未被根使用的递归环也属于未使用集合.</summary>
    /// <param name="database">完整私有模型, 包含列、分区、角色、计算组和格式表达式.</param>
    /// <param name="baseline">宿主绑定目标和完整基线, 不使用网页展示 DTO 代替 TOM.</param>
    /// <param name="token">分析取消令牌, 不包含模型写入.</param>
    /// <returns>完整结果或禁止清理的保守结果; 取消继续传播.</returns>
    public static UdfLintResult Analyze(TOM.Database database, ProjectSnapshot baseline, CancellationToken token = default)
        => Analyze(database, baseline.ProjectId, baseline.BaselineId, token);

    /// <summary>内部分析只需要宿主目标身份, 不从展示 DTO 推断表达式事实.</summary>
    private static UdfLintResult Analyze(TOM.Database database, string projectId, string baselineId, CancellationToken token)
    {
        var functions = database.Model.Functions.ToArray();
        UdfLintResult Blocked() => new(projectId, baselineId, [], functions.Length, false);
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in functions)
        {
            token.ThrowIfCancellationRequested();
            // 未识别的函数命名语法不能被当成无人使用而删除.
            if (!DaxFormatter.TryReadFunctionCalls(function.Name + "()", out var identity, token)
                || identity.Count != 1 || !identity.Contains(function.Name)
                || string.IsNullOrWhiteSpace(function.Expression)
                || !DaxFormatter.TryReadFunctionCalls(function.Expression, out var calls, token)
                || !dependencies.TryAdd(function.Name, calls)) return Blocked();
        }
        using var definition = JsonDocument.Parse(TOM.JsonSerializer.SerializeDatabase(database));
        // 仅真实 SDK 表对象的 BasicRefreshPolicy 承载 M 查询, 同名扩展容器不能据字段名获得豁免.
        var tableDefinitions = definition.RootElement.GetProperty("model").TryGetProperty("tables", out var serializedTables)
            ? serializedTables.EnumerateArray().ToHashSet() : [];
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ReadRoots(definition.RootElement, roots, tableDefinitions, refreshPolicy: false, token)) return Blocked();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Where(dependencies.ContainsKey));
        while (pending.TryPop(out var name))
        {
            token.ThrowIfCancellationRequested();
            if (!used.Add(name)) continue;
            foreach (var dependency in dependencies[name].Where(dependencies.ContainsKey)) pending.Push(dependency);
        }
        return new(projectId, baselineId, functions.Select(f => f.Name).Where(name => !used.Contains(name)).Order(StringComparer.OrdinalIgnoreCase).ToArray(), functions.Length, true);
    }

    /// <summary>重新分析后原子删除批准集合; 有使用引用或未知表达式时整批拒绝, 调用方仍负责基线与保存.</summary>
    public static void Delete(TOM.Database database, UdfDeleteBatch batch)
    {
        var result = Analyze(database, batch.ProjectId, batch.BaselineId, default);
        // 必须删除完整未使用闭包, 避免仅删其中依赖而留下另一未用函数的悬空调用.
        if (!result.Complete || !new HashSet<string>(result.Unused, StringComparer.OrdinalIgnoreCase).SetEquals(batch.Names))
            throw new AppException("UDF_LINT_UNSAFE", "函数使用情况已变化或包含未知表达式, 未删除; 请重新分析", 3);
        ModelConfigurationUdfLocks.MarkLintRemoved(database.Model, batch.Names);
        foreach (var name in batch.Names) database.Model.Functions.Remove(database.Model.Functions.Find(name)!);
    }

    /// <summary>遍历 SDK 完整 JSON, 只排除函数自身及明确的 M/SQL 来源, 未知表达式或策略形状阻止清理.</summary>
    /// <param name="node">来自同一 SDK 序列化文档的当前节点.</param>
    /// <param name="roots">累积真实 DAX 调用根, 非模型写入.</param>
    /// <param name="tables">实际 model.tables 对象身份, 不按容器名称猜测策略来源.</param>
    /// <param name="refreshPolicy">仅当前节点为实际表的刷新策略时启用严格 M 字段分类.</param>
    /// <param name="token">读取与词法分析的取消令牌.</param>
    /// <returns>所有表达式根均已安全分类时为 true.</returns>
    private static bool ReadRoots(JsonElement node, HashSet<string> roots, HashSet<JsonElement> tables, bool refreshPolicy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (refreshPolicy && (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("policyType", out var policy)
            || policy.ValueKind != JsonValueKind.String || policy.GetString() != "basic")) return false;
        if (node.ValueKind == JsonValueKind.Array)
            return node.EnumerateArray().All(item => ReadRoots(item, roots, tables, refreshPolicy: false, token));
        if (node.ValueKind != JsonValueKind.Object) return true;
        // M 命名表达式和 M/原生查询分区不执行 DAX, 不把其中相同名称误当模型函数调用.
        var nonDax = node.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "m" or "M"
            || node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "m" or "M" or "query";
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == "functions") continue;
            if (refreshPolicy && property.Name is "sourceExpression" or "pollingExpression") continue;
            if (property.Name == "refreshPolicy" && tables.Contains(node))
            {
                if (!ReadRoots(property.Value, roots, tables, refreshPolicy: true, token)) return false;
                continue;
            }
            if (property.Name.EndsWith("expression", StringComparison.OrdinalIgnoreCase))
            {
                if (nonDax) continue;
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                // 当前 SDK 的计算组选择表达式是包含 DAX 和动态格式的定义容器, 仅精确放行这两个已知字段.
                if (property.Value.ValueKind == JsonValueKind.Object && property.Name is "noSelectionExpression" or "multipleOrEmptySelectionExpression")
                {
                    if (!ReadRoots(property.Value, roots, tables, refreshPolicy: false, token)) return false;
                    continue;
                }
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)) return false;
                var expression = ReadExpression(property.Value);
                if (expression is null || !DaxFormatter.TryReadFunctionCalls(expression, out var calls, token)) return false;
                roots.UnionWith(calls);
            }
            else if (!ReadRoots(property.Value, roots, tables, refreshPolicy: false, token)) return false;
        }
        return true;
    }

    /// <summary>SDK 表达式允许字符串或字符串行数组, 其他数组类型按未知语法拒绝.</summary>
    private static string? ReadExpression(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()
        : value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) ? string.Join('\n', value.EnumerateArray().Select(item => item.GetString())) : null;
}
