// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Execution\BusinessOperationCatalog.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 声明全客户端业务副作用与既有回执边界, 不决定 MCP 可发现范围或传输权限.

namespace PbiUtils.Infrastructure.Execution;

/// <summary>业务方法的副作用事实和现行可靠回执策略; 独立宿主控制沿用其自身协调边界.</summary>
public sealed record BusinessOperationMetadata(string Method, bool HasSideEffects, bool RequiresExecutionReceipt);

/// <summary>中性业务元数据覆盖网页业务全集; MCP 显式选择其公开子集, 不能据此扩大协议白名单.</summary>
public static class BusinessOperationCatalog
{
    // 宿主几何/弹窗/取消/维护、更新和外部工具连接保持现行独立边界, 本轮不改变其并发策略.
    private static readonly BusinessOperationMetadata[] Operations =
    [
        new("ui.settings", false, false),
        new("ui.saveSettings", true, true),
        new("ui.dialogRespond", true, false),
        new("logging.settings", false, false),
        new("logging.save", true, true),
        new("logging.openDirectory", true, true),
        new("app.info", false, false),
        new("mcp.configuration", false, false),
        new("app.resetUserData", true, false),
        new("doctor", false, false),
        new("request.cancel", true, false),
        new("mcp.connections", false, false),
        new("mcp.disconnect", true, false),
        new("mcp.setAdmission", true, false),
        new("updates.status", false, false),
        new("updates.save", true, false),
        new("updates.check", true, false),
        new("updates.open", true, false),
        new("learning.update", true, false),
        new("learning.close", true, false),
        new("learning.open", true, true),
        new("powerbi.instances", false, false),
        new("powerbi.serviceStatus", false, false),
        new("powerbi.serviceSignIn", true, true),
        new("powerbi.serviceDeleteLogin", true, true),
        new("powerbi.serviceSignOut", true, true),
        new("powerbi.serviceModels", false, false),
        new("powerbi.serviceRecentModels", false, false),
        new("powerbi.serviceDeleteRecentModel", true, true),
        new("remote.form", false, false),
        new("remote.mutate", true, true),
        new("project.disconnect", true, true),
        new("project.connectLocal", true, true),
        new("project.connectExternalTool", true, false),
        new("project.connectRemote", true, true),
        new("project.connectService", true, true),
        new("dax.format", false, false),
        new("model.udfLint", false, false),
        new("model.exportTables", true, true),
        new("model.tableStatistics", false, false),
        new("model.tableRowCounts", false, false),
        new("model.exportLocation", false, false),
        new("model.openExportDirectory", true, true),
        new("model.edit", true, true),
        new("model.save", true, true),
        new("model.discard", true, true),
        new("project.recent", false, false),
        new("project.openRecent", true, true),
        new("project.deleteRecent", true, true),
        new("project.openPath", true, true),
        new("project.open", true, true),
        new("project.inspect", false, false),
        new("sample.status", false, false),
        new("sample.chooseDirectory", true, true),
        new("sample.generate", true, true),
        new("sample.clear", true, true),
        new("sample.openDirectory", true, true),
        new("scaffold.templates", false, false),
        new("scaffold.templateDetails", false, false),
        new("scaffold.chooseOutput", true, true),
        new("scaffold.openTemplates", true, true),
        new("scaffold.openDirectory", true, true),
        new("scaffold.openDesktop", true, true),
        new("scaffold.updateDraft", true, true),
        new("scaffold.preview", true, true),
        new("scaffold.create", true, true),
        new("scaffold.discard", true, true),
        new("configuration.context", false, false),
        new("configuration.apply", true, true),
        new("configuration.complete", true, true),
        new("configuration.discard", true, true),
        new("calendar.preferences", false, false),
        new("calendar.savePreferences", true, true),
        new("calendar.parseWorkdays", false, false),
        new("calendar.preview", false, false),
        new("udf.catalog", false, false),
        new("udf.catalogRefresh", true, true),
        new("udf.sources", false, false),
        new("udf.sourceSave", true, true),
        new("udf.sourceDelete", true, true),
        new("udf.localList", false, false),
        new("udf.localGet", false, false),
        new("udf.localSave", true, true),
        new("udf.localDelete", true, true),
    ];
    private static readonly Dictionary<string, BusinessOperationMetadata> ByMethod = Operations.ToDictionary(item => item.Method, StringComparer.Ordinal);
    // 原回执验证接受的读取方法仍显式声明, 内部控制不会因业务目录全集而获得回执入口.
    private static readonly HashSet<string> RecordedReadMethods = new(StringComparer.Ordinal)
    {
        "doctor", "app.info", "mcp.configuration",
        "ui.settings", "logging.settings", "powerbi.instances",
        "powerbi.serviceStatus", "powerbi.serviceModels", "powerbi.serviceRecentModels",
        "remote.form", "project.recent", "project.inspect",
        "dax.format", "model.udfLint", "model.tableStatistics",
        "model.tableRowCounts", "model.exportLocation", "sample.status",
        "scaffold.templates", "scaffold.templateDetails", "configuration.context",
        "calendar.preferences", "calendar.parseWorkdays", "calendar.preview",
        "udf.catalog", "udf.sources", "udf.localList",
        "udf.localGet",
    };
    private static readonly HashSet<string> FileExecutionMethods = new(StringComparer.Ordinal)
    {
        "configuration_apply", "change_apply", "scaffold_create", "sample_generate", "sample_append", "sample_clear",
        "udf_catalog_refresh", "udf_source_save", "udf_source_delete", "udf_local_create", "udf_local_update", "udf_local_delete"
    };

    /// <summary>按固定方法查询真实副作用策略; 未知方法返回 false, 由入口继续严格拒绝.</summary>
    /// <param name="method">宿主已验证的业务方法名.</param>
    /// <param name="operation">找到时返回现行元数据, 不携带 schema 或客户端授权.</param>
    /// <returns>方法是否在显式业务目录中.</returns>
    public static bool TryGet(string method, out BusinessOperationMetadata operation) => ByMethod.TryGetValue(method, out operation!);

    /// <summary>验证现有 GUI/本机文件回执名称, 不接受任意工具名或路径作为账本方法.</summary>
    /// <param name="method">当前执行或磁盘回执声明的方法名.</param>
    /// <returns>仅显式业务方法或固定文件用例回执名返回 true.</returns>
    public static bool IsRecordedMethod(string method) => ByMethod.TryGetValue(method, out var operation) && operation.RequiresExecutionReceipt
        || RecordedReadMethods.Contains(method) || FileExecutionMethods.Contains(method);
}
