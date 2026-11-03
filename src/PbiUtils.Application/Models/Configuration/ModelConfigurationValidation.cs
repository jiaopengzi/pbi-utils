// FilePath    : pbi-utils\src\PbiUtils.Application\Models\Configuration\ModelConfigurationValidation.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 持续模型配置直接组合同一组领域校验, 不依赖占位新建请求.

using PbiUtils.Application.Calendar;
using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.TemplateMeasures;
using PbiUtils.Application.Udf;
using PbiUtils.Core;

namespace PbiUtils.Application.Models.Configuration;

/// <summary>冻结配置的共享输入边界, 资产、模型及升级授权仍由原宿主所有者管理.</summary>
public static class ModelConfigurationValidation
{
    /// <summary>校验配置入口语言、存储模式及每个明确选择的领域段, 不接触环境.</summary>
    /// <param name="request">宿主已冻结的完整配置请求.</param>
    /// <exception cref="AppException">配置入口或任一领域输入无效.</exception>
    public static void Validate(ModelConfigurationRequest request)
    {
        if (request is null || request.Locale is not ("zh-cn" or "en-us"))
            throw new AppException("INVALID_SCAFFOLD_REQUEST", "脚手架参数无效, 请检查模板、名称、日期及选择内容");
        TemplateMeasureValidation.ValidateMode(request.MeasuresMode);
        TemplateMeasureValidation.Validate(request.Measures);
        request.Sample?.Validate();
        if (request.Calendar is { } calendar) CalendarValidation.Validate(calendar);
        if (request.Periods is { } periods) PeriodValidation.Validate(periods);
        UdfSelectionValidation.Validate(request.Udfs);
    }
}
