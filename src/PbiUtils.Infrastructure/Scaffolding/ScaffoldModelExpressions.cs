// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldModelExpressions.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 复用内置模板表达式本地化和工程根参数规则, 不改变用户模板的其他表达式.

using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.SampleData.Model;

using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.Models;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>预览与创建共用纯模型转换, 输入必须是调用方拥有的模型副本.</summary>
internal static class ScaffoldModelExpressions
{
    /// <summary>本地化受信内置表达式; 显式模板度量配置可直接修复本次工程根参数, 其他调用仍核验旧归属.</summary>
    /// <param name="model">本轮自有 staging 模型.</param>
    /// <param name="builtin">是否为受信内置资源.</param>
    /// <param name="output">本轮最终工程绝对目录.</param>
    /// <param name="generated">模板是否声明已有生成元数据.</param>
    /// <param name="templateMeasures">本轮是否明确启用模板度量配置, 授权重建该功能的根参数.</param>
    /// <exception cref="AppException">未明确启用模板度量且既有根参数归属或结构无效.</exception>
    internal static void LocalizeExpressions(TOM.Model model, bool builtin, string output, bool generated, bool templateMeasures = false)
    {
        if (builtin)
            LocalizeBuiltinExpressions(model);
        const string parameterMetadata = " meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]";
        if (model.Expressions.Find("PbiUtilsProjectRoot") is { } prior)
        {
            if (!templateMeasures && (!generated || prior.Kind != TOM.ExpressionKind.M || !prior.Expression.EndsWith(parameterMetadata, StringComparison.Ordinal)))
                throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "项目根参数名称与模板对象冲突", 2);
            if (!templateMeasures) ModelConfigurationOwnership.Require(prior, prior.Annotations);
            prior.Kind = TOM.ExpressionKind.M;
            prior.Expression = PowerQueryLiterals.Text(Path.GetFullPath(output)) + parameterMetadata;
            TomModelMetadata.TextParameterMetadata(prior);
            return;
        }
        var parameter = new TOM.NamedExpression
        {
            Name = "PbiUtilsProjectRoot",
            Kind = TOM.ExpressionKind.M,
            Expression = PowerQueryLiterals.Text(Path.GetFullPath(output)) + parameterMetadata
        };
        TomModelMetadata.TextParameterMetadata(parameter);
        model.Expressions.Add(parameter);
    }

    /// <summary>内置转换仅保留安全常量辅助查询, 不将原文件或百度函数带入产物; 预览复用同一规则.</summary>
    internal static void LocalizeBuiltinExpressions(TOM.Model model)
    {
        foreach (var expression in model.Expressions.ToArray())
        {
            if (expression.Name is "A01_MQHY" or "A02_Hour24" or "TypeDetails") continue;
            if (expression.Name is "Path" or "Path_Sample") expression.Expression = "PbiUtilsProjectRoot";
            else model.Expressions.Remove(expression);
        }
    }
}
