// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Mcp\McpDesktopCatalog.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将现有桌面业务契约映射为固定 MCP 工具, 区分读取与需要启动写权限的副作用.

using System.Text.Json;
using PbiUtils.Infrastructure.Execution;

namespace PbiUtils.Infrastructure.Mcp;

/// <summary>桌面工具的固定身份、权限及真实业务参数结构.</summary>
public sealed record McpDesktopOperation(string Method, string Description, bool ReadOnly, JsonElement InputSchema);

/// <summary>显式业务白名单, 不开放弹窗答复、凭据输入、页面几何或宿主销毁控制通道.</summary>
public static class McpDesktopCatalog
{
    private static readonly string[] RemoteAddressFields = ["server", "database"];
    private static readonly Dictionary<string, McpDesktopOperation> Operations = Load();
    public static IReadOnlyList<McpDesktopOperation> All { get; } = Operations.Values.OrderBy(item => item.Method, StringComparer.Ordinal).ToArray();

    /// <summary>查询固定方法; 未声明方法不能经 IPC 执行.</summary>
    public static bool TryGet(string method, out McpDesktopOperation operation) => Operations.TryGetValue(method, out operation!);

    /// <summary>从编译嵌入的权威机器契约读取结构, 缺少声明时启动失败而非放宽参数.</summary>
    private static Dictionary<string, McpDesktopOperation> Load()
    {
        using var stream = typeof(McpDesktopCatalog).Assembly.GetManifestResourceStream("PbiUtils.Mcp.DesktopBridgeSchema")
            ?? throw new InvalidOperationException("MCP desktop contract missing");
        using var document = JsonDocument.Parse(stream);
        var schemas = document.RootElement.GetProperty("allOf").EnumerateArray().ToDictionary(
            rule => rule.GetProperty("if").GetProperty("properties").GetProperty("method").GetProperty("const").GetString()!,
            rule => rule.GetProperty("then").GetProperty("properties").GetProperty("params").Clone(), StringComparer.Ordinal);
        Dictionary<string, McpDesktopOperation> result = new(StringComparer.Ordinal);
        void Add(string method, string description, JsonElement? inputSchema = null)
        {
            if (!BusinessOperationCatalog.TryGet(method, out var business)) throw new InvalidOperationException("Business operation metadata missing");
            result.Add(method, new(method, description, !business.HasSideEffects, inputSchema ?? schemas[method]));
        }

        Add("doctor", "Read the desktop runtime diagnostic.");
        Add("app.info", "Read product and platform information.");
        Add("mcp.configuration", "Read the installed local MCP client configuration.");
        Add("ui.settings", "Read language, editor and display preferences.");
        Add("ui.saveSettings", "Save application language, editor and display preferences.");
        Add("logging.settings", "Read business logging preferences.");
        Add("logging.save", "Save business logging level.");
        Add("logging.openDirectory", "Open the application log directory on this computer.");
        Add("learning.open", "Open the product learning website in the system browser.");
        Add("powerbi.instances", "Discover existing local Power BI Desktop instances; never launches or modifies Power BI.");
        Add("powerbi.serviceStatus", "Read Power BI Service sign-in status.");
        Add("powerbi.serviceSignIn", "Explicitly start Microsoft sign-in in the system browser; this is the only authentication interaction and does not open the pbi-utils window. Credentials stay in Microsoft login.");
        Add("powerbi.serviceDeleteLogin", "Delete a saved Service login record.");
        Add("powerbi.serviceSignOut", "Sign out of Service and disconnect its active model.");
        Add("powerbi.serviceModels", "List models available to the existing Service account.");
        Add("powerbi.serviceRecentModels", "Read account-scoped recent Service models.");
        Add("powerbi.serviceDeleteRecentModel", "Remove a recent Service model record, without deleting the remote model.");
        Add("remote.form", "Read the remote connection form identity. Enter passwords only in the pbi-utils application.");
        Add("remote.mutate", "Delete one saved remote connection record or clear its saved password. Never returns credentials or deletes the remote model.");
        // MCP 仅允许当前 Windows 身份直连 SSAS, 不公开凭据、表单身份或最近记录存储开关.
        var remoteConnection = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            required = RemoteAddressFields,
            properties = new { server = new { type = "string", minLength = 1, maxLength = 1024 }, database = new { type = "string", minLength = 1, maxLength = 200 } }
        });
        Add("project.connectRemote", "Connect directly to SSAS with server and database using only the current Windows identity. No credentials, local form or saved connection changes; remote models remain read-only. Startup write permission is required.", inputSchema: remoteConnection);
        var localConnection = System.Text.Json.Nodes.JsonNode.Parse(schemas["project.connectLocal"].GetRawText())!;
        localConnection["required"] = new System.Text.Json.Nodes.JsonArray("processId", "instanceId");
        Add("project.connectLocal", "Connect to the exact discovered Desktop instance using its processId and instanceId; rejects replaced instances. Startup write permission is required, without additional confirmation.",
            JsonSerializer.SerializeToElement(localConnection));
        Add("project.openPath", "Open or reuse an exact local PBIP/PBIX path through the verified Desktop file workflow. Rejects network, device and link paths; does not modify model contents. Startup write permission is required, without a file picker or additional confirmation.");
        Add("project.connectService", "Connect to an available Service model for read-only use.");
        Add("project.disconnect", "Disconnect the current application model with startup write permission and no additional confirmation.");
        Add("project.recent", "List recent local Power BI project records.");
        Add("project.openRecent", "Open a saved recent project using the normal Desktop connection workflow.");
        Add("project.deleteRecent", "Remove a recent project record without deleting its files.");
        Add("project.open", "Ask the user to select a PBIP/PBIX file and open/connect it through Power BI Desktop.");
        Add("project.inspect", "Read the current connected project and authoritative model snapshot.");
        Add("dax.format", "Format DAX locally with the existing formatter, preserving tokens and calculation semantics.");
        Add("model.udfLint", "Validate the supported UDF structure without changing the model.");
        Add("model.tableStatistics", "Read available storage and row statistics without refreshing data.");
        Add("model.tableRowCounts", "Execute explicit read-only row counts under the current connection and security rules.");
        Add("model.exportLocation", "Read the latest successfully used model export directory.");
        Add("model.exportTables", "Export selected tables to CSV/XLSX using the normal user-selected destination and cancellation flow.");
        Add("model.openExportDirectory", "Open the existing verified model export directory.");
        Add("model.edit", "Apply an exact model edit without an additional dialog. Startup write permission, source, exact grant, baseline and transaction checks remain mandatory.");
        Add("model.save", "Complete an existing supported pending save without replaying model modifications.");
        Add("model.discard", "Discard only uncommitted application edits after rereading the authoritative model.");
        Add("sample.status", "Read sample output configuration and generated table statistics.");
        Add("sample.chooseDirectory", "Ask the user to select a sample data directory.");
        Add("sample.generate", "Generate or append the existing sample dataset; the explicit mode selects overwrite and atomic publication is preserved.");
        Add("sample.clear", "Clear only the application-owned sample outputs using the explicit clear operation and verified output ownership.");
        Add("sample.openDirectory", "Open the current verified sample directory.");
        Add("scaffold.templates", "List built-in and user PBIP templates.");
        Add("scaffold.templateDetails", "Read selected template fields, measures and available configuration.");
        Add("scaffold.chooseOutput", "Ask the user to choose the new PBIP output directory.");
        Add("scaffold.openTemplates", "Open the user template directory.");
        Add("scaffold.openDirectory", "Open the successfully created PBIP directory.");
        Add("scaffold.openDesktop", "Open the created PBIP through the existing Power BI Desktop workflow.");
        Add("scaffold.updateDraft", "Update the new-project draft using the existing validated template/calendar/UDF configuration.");
        Add("scaffold.preview", "Validate the current new-project draft, save its selected preferences and freeze the exact plan.");
        Add("scaffold.create", "Create the previously previewed PBIP, preserving baseline, ownership, file transaction and exact-plan validation.");
        Add("scaffold.discard", "Discard the application new-project draft, without deleting created projects.");
        Add("configuration.context", "Read the current model configuration including templates, calendar, time intelligence and UDF context.");
        Add("configuration.apply", "Apply current-model configuration without a generic confirmation, preserving full baseline, generated ownership and compatibility-upgrade checks.");
        Add("configuration.complete", "Complete supported pending configuration save stages without replaying model changes.");
        Add("configuration.discard", "Discard uncommitted model configuration after authoritative reread.");
        Add("calendar.preferences", "Read the saved calendar configuration preferences.");
        Add("calendar.savePreferences", "Save calendar configuration preferences.");
        Add("calendar.parseWorkdays", "Parse and validate holiday/workday overrides locally.");
        Add("calendar.preview", "Generate calendar/date/time-intelligence code using the existing local compiler without committing a model.");
        Add("udf.catalog", "Query locally cached third-party UDF packages, versions, dependencies and documentation.");
        Add("udf.catalogRefresh", "Explicitly refresh the selected UDF source over the network; retain the current snapshot on failure.");
        Add("udf.sources", "Read configured third-party UDF sources and baselines.");
        Add("udf.sourceSave", "Create or update a UDF source with its exact configuration baseline.");
        Add("udf.sourceDelete", "Delete a UDF source after baseline validation.");
        Add("udf.localList", "List stored local UDF definitions.");
        Add("udf.localGet", "Read a local UDF and its exact baseline.");
        Add("udf.localSave", "Create or update a local UDF through the shared store and baseline checks.");
        Add("udf.localDelete", "Delete a local UDF after exact baseline validation.");
        return result;
    }
}
