// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpBusinessRuntime.Core.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将现有 CLI/共享用例注册为类型化 MCP 工具, 不执行 shell 或构造临时请求文件.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Application.Projects;
using PbiUtils.Application.Models;
using PbiUtils.Application.Diagnostics;
using PbiUtils.Application.Code;
using PbiUtils.Application;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

using PbiUtils.Application.Calendar.TimeIntelligence;
using PbiUtils.Application.Calendar;
using PbiUtils.Infrastructure.Scaffolding.Templates;

namespace PbiUtils.Infrastructure.Mcp;

public sealed partial class McpBusinessRuntime
{
    /// <summary>固定业务白名单与写入标记; 预览不改项目, 联网更新必须显式调用.</summary>
    private void RegisterCoreTools()
    {
        Register<EmptyInput>("server_info", "Report pbi-utils version, startup write permission and transport. No credentials or AI provider are needed.", false,
            (_, _) => Task.FromResult<object>(new { version = Product.Version, allowWrite = AllowWrite, transport = "stdio", offlineConfiguration = "PBIP", maxInputBytes = 4 * 1024 * 1024 }));
        Register<EmptyInput>("doctor", "Inspect the local pbi-utils runtime without modifying models or opening Power BI.", false,
            (_, token) => Task.FromResult<object>(new DoctorService(new RuntimeEnvironment()).Execute(token)));
        Register<UpdateCheckInput>("updates_check", "Check stable desktop product releases from the fixed jiaopengzi/pbi-utils repository. source is github (default) or gitee. Returns plain-text release notes and verified full-installer links. Does not download, install, restart or open a browser. Network failure is a tool error, not an up-to-date result.", false,
            async (input, token) => await productUpdates.CheckAsync(input.Source, false, token), openWorld: true);
        Register<ProjectInput>("project_inspect", "Read a specific local PBIP project, full baseline, model metadata and report pages. Does not execute DAX/M or refresh data sources.", false,
            async (input, token) => await InspectAsync(input.Project, token));
        Register<ProjectInput>("model_inspect", "Read local PBIP model metadata and its complete baseline.", false,
            async (input, token) => { var value = await InspectAsync(input.Project, token); return new { value.ProjectId, value.BaselineId, value.Model, value.Warnings }; });
        Register<ProjectInput>("measure_list", "List measures from a local PBIP project, including expressions and formatting.", false,
            async (input, token) => { var value = await InspectAsync(input.Project, token); return new { value.ProjectId, value.BaselineId, value.Model.Measures, value.Warnings }; });
        Register<ProjectInput>("report_inspect", "Read report pages and visual metadata from a local PBIP project.", false,
            async (input, token) => { var value = await InspectAsync(input.Project, token); return new { value.ProjectId, value.BaselineId, value.ReportFormat, value.Pages, value.Warnings }; });
        Register<ProjectInput>("udf_list", "Read installed functions and compatibility level from a local PBIP project.", false,
            async (input, token) => { var value = await InspectAsync(input.Project, token); return new { value.ProjectId, value.BaselineId, value.Model.CompatibilityLevel, value.Model.Functions, value.Warnings }; });
        Register<FormatInput>("format_code", "Format DAX or Power Query M locally. language is dax or m; text is at most 16000 characters. Does not execute code.", false,
            (input, token) => Task.FromResult<object>(new { input.Language, text = CodeFormatting.Format(input.Language, input.Text, token), executed = false }));
        Register<EmptyInput>("scaffold_templates", "List built-in and user PBIP templates with the exact template IDs and baselines required by scaffold_plan.", false,
            async (_, token) => new { templates = await scaffold.Catalog.ListAsync(token) });
        Register<TemplateInput>("scaffold_template_inspect", "Read actual template tables, date capabilities and default roles using the template ID and baseline.", false,
            async (input, token) => await new ScaffoldTemplateInspector(scaffold.Catalog, userRoot).InspectAsync(input.TemplateId, input.TemplateBaseline, token));
        Register<EmptyInput>("configuration_catalog", "List date field IDs, time-intelligence templates and built-in auxiliary queries accepted by scaffold and configuration tools.", false,
            (_, _) => Task.FromResult<object>(new { calendarFields = ScaffoldCalendarFields.All, recommendedFields = ScaffoldCalendarFields.Recommended, simpleFields = ScaffoldCalendarFields.Simple, periodTemplates = ScaffoldTimeIntelligence.Templates, templateQueries = ScaffoldTemplateQueries.All }));
        Register<CalendarInput>("calendar_preview", "Compile an independent Power Query calendar function and invocation using the shared generator. Does not create or change a model.", false,
            (input, token) => Task.FromResult<object>(ScaffoldCalendarM.Compile(input.Options, ScaffoldCalendarFields.Resolve(input.Options), input.Locale, cancellationToken: token)));
        Register<PeriodInput>("period_preview", "Generate time-intelligence DAX with the shared Long formatter and explicit date/measure roles. Does not change a model.", false,
            (input, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<object>(new { measures = ScaffoldTimeIntelligence.Generate(input.BaseMeasure, input.Options, input.ReservedTableNames) }); });
        Register<ScaffoldInput>("scaffold_plan", "Preview a new PBIP project. Obtain templateId/templateBaseline with scaffold_templates. request supports calendar, time intelligence, measures, sample data, UDFs and auxiliary queries. Review returned digest before creating.", false,
            async (input, token) => await PrepareScaffoldAsync(input, token));
        Register<ScaffoldCreateInput>("scaffold_create", "Create the previously reviewed new project using the identical request/output and exact approve digest from scaffold_plan. Requires startup --allow-write. Never overwrites an existing target.", true,
            async (input, token) => await CreateScaffoldAsync(input, token));
        Register<DigestInput>("scaffold_discard", "Discard this server's uncommitted scaffold preview and its temporary preparation files. Never deletes a created project.", false,
            async (input, token) => new { discarded = await DiscardScaffoldAsync(input.Digest, token) });
        Register<ConfigurationInput>("configuration_plan", "Preview changes to an existing local PBIP: template measures, calendar, time intelligence, sample data or UDF installation. Requires the current baseline from project_inspect. No target writes occur. Review the returned planId and digest before configuration_apply.", false,
            async (input, token) => await PrepareConfigurationAsync(input, token));
        Register<PlanApplyInput>("configuration_apply", "Apply one exact configuration plan once to its bound local PBIP. Requires startup --allow-write and approve equal to its digest. Rejects changed sources/baselines. Does not refresh external data or save a live Desktop engine.", true,
            async (input, token) => await ApplyConfigurationAsync(input, token));
        Register<IdInput>("configuration_discard", "Release an uncommitted configuration plan without changing its PBIP project.", false,
            (input, _) => Task.FromResult<object>(new { discarded = DiscardConfiguration(input.Id) }));
        Register<ChangeRequest>("change_plan", "Prepare a new-directory copy with a whitelisted measure/table/UDF/visual/calendar/period change. In-place writes are not supported by this tool; use configuration_plan for local configuration.", false,
            async (input, token) => { if (input.InPlace) throw new AppException("INVALID_ARGUMENT", "change_plan 仅支持新目录副本"); RequirePath(input.ProjectPath); RequirePath(input.OutputPath); return await changes.PrepareAsync(input, token); });
        Register<PlanApplyInput>("change_apply", "Publish a reviewed new-directory copy using planId and exact approve digest. Requires startup --allow-write; consumes the plan once.", true,
            async (input, token) => await changes.ApplyAsync(input.PlanId, input.Approve, token, CommitLocalWrite));
        Register<IdInput>("change_discard", "Release an unpublished copy-change plan and its staging files; never deletes the original project.", false,
            (input, _) => { changes.Discard(input.Id); return Task.FromResult<object>(new { discarded = true }); });
        Register<OutputInput>("sample_inspect", "Inspect the existing eleven-table retail sample-data output without generating data.", false,
            async (input, token) => { RequirePath(input.Output); return await sample.GetAsync(input.Output, token); });
        Register<SampleInput>("sample_generate", "Generate the existing eleven-table sample dataset and PBIP. Requires --allow-write; overwrite defaults false and must be explicitly true to replace known generated output.", true,
            async (input, token) => { RequirePath(input.Output); return await sample.GenerateAsync(input.Output, input.Options, input.Overwrite, beginCommit: CommitLocalWrite, cancellationToken: token); });
        Register<SampleAppendInput>("sample_append", "Append a non-overlapping window to existing sample data using shared key and dimension rules. Requires --allow-write.", true,
            async (input, token) => { RequirePath(input.Output); return await sample.IncrementAsync(input.Output, input.Options, beginCommit: CommitLocalWrite, cancellationToken: token); });
        Register<SampleClearInput>("sample_clear", "Delete the fixed eleven generated CSV files and the whole pbip child directory from the selected sample output. Retains the root and unrelated files. Requires --allow-write and confirm=true.", true,
            async (input, token) => { RequireConfirmation(input.Confirm); RequirePath(input.Output); return await sample.ClearAsync(input.Output, CommitLocalWrite, token); });
        RegisterUdfTools();
    }

    /// <summary>仅允许绝对本地 PBIP 入口; ProjectReader 继续执行完整目录及链接边界检查.</summary>
    private static async Task<ProjectSnapshot> InspectAsync(string path, CancellationToken token)
    {
        RequirePath(path);
        if (!Path.GetExtension(path).Equals(".pbip", StringComparison.OrdinalIgnoreCase)) throw new AppException("PBIP_REQUIRED", "需要具体本地 PBIP 入口", 3);
        return await new ProjectService(new ProjectReader()).InspectAsync(path, token).ConfigureAwait(false);
    }

    /// <summary>拒绝相对、网络、设备和超限路径, 不依赖第三方客户端的工作目录.</summary>
    private static void RequirePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("model://", StringComparison.Ordinal))
            throw new AppException("INVALID_ARGUMENT", "MCP 路径必须是本机绝对路径");
        UserDataStore.Check(path);
    }

    /// <summary>独立删除风险参数不能由启动写权限替代.</summary>
    private static void RequireConfirmation(bool confirmed)
    {
        if (!confirmed) throw new AppException("CONFIRMATION_REQUIRED", "此删除操作需要明确 confirm=true", 3);
    }
}
