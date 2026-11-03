// FilePath    : pbi-utils\src\PbiUtils.Host\Bridge\Protocol\BridgeMethods.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 集中声明实际桥接方法, 契约测试核对分发与 schema 不漂移.

using PbiUtils.Infrastructure.Gui;

namespace PbiUtils.Host.Bridge.Protocol;

/// <summary>第一方方法名称注册, 不提供动态反射或第三方 handler 加载.</summary>
public static class BridgeMethods
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        "ui.settings", "ui.saveSettings", "ui.dialogRespond", "logging.settings", "logging.save", "logging.openDirectory", "app.info", "mcp.configuration", "app.resetUserData", "doctor", "request.cancel",
        "mcp.connections", "mcp.disconnect", "mcp.setAdmission",
        "updates.status", "updates.save", "updates.check", "updates.open",
        "learning.update", "learning.close", "learning.open",
        "powerbi.instances", "powerbi.serviceStatus", "powerbi.serviceSignIn", "powerbi.serviceDeleteLogin", "powerbi.serviceSignOut", "powerbi.serviceModels", "powerbi.serviceRecentModels", "powerbi.serviceDeleteRecentModel",
        "remote.form", "remote.mutate", "project.disconnect", "project.connectLocal", "project.connectExternalTool", "project.connectRemote", "project.connectService",
        "dax.format", "model.udfLint", "model.exportTables", "model.tableStatistics", "model.tableRowCounts", "model.exportLocation", "model.openExportDirectory", "model.edit", "model.save", "model.discard", "project.recent", "project.openRecent", "project.deleteRecent", "project.openPath", "project.open", "project.inspect",
        "sample.status", "sample.chooseDirectory", "sample.generate", "sample.clear", "sample.openDirectory",
        "scaffold.templates", "scaffold.templateDetails", "scaffold.chooseOutput", "scaffold.openTemplates", "scaffold.openDirectory", "scaffold.openDesktop", "scaffold.updateDraft", "scaffold.preview", "scaffold.create", "scaffold.discard",
        "configuration.context", "configuration.apply", "configuration.complete", "configuration.discard",
        "calendar.preferences", "calendar.savePreferences", "calendar.parseWorkdays", "calendar.preview",
        "udf.catalog", "udf.catalogRefresh", "udf.sources", "udf.sourceSave", "udf.sourceDelete", "udf.localList", "udf.localGet", "udf.localSave", "udf.localDelete"
    ]);
}
