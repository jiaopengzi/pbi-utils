// FilePath    : pbi-utils\src\PbiUtils.Host\BusinessContext.Composition.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 为每个已认证客户端组合独立业务服务, 构造失败和完整排空均保持唯一资源所有者.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop.Discovery;
using PbiUtils.Windows.Connections.PowerBiService;
using PbiUtils.Infrastructure.Settings;
using PbiUtils.Infrastructure.Projects;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Infrastructure.Models.Export;
using PbiUtils.Infrastructure.Logging;
using PbiUtils.Infrastructure.Diagnostics;
using PbiUtils.Infrastructure.Connections.PowerBiService;
using PbiUtils.Infrastructure.Connections;
using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Scaffolding;
using PbiUtils.Host.Bridge.Connections;
using PbiUtils.Host.Bridge.Configuration;
using PbiUtils.Application.Settings;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Application.Logging;
using PbiUtils.Application.Diagnostics;
using PbiUtils.Application.Connections;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Gui;
using PbiUtils.Infrastructure.Hosting;
using PbiUtils.Infrastructure.Mcp;
using PbiUtils.Infrastructure.Execution;
using PbiUtils.Infrastructure.SampleData;
using PbiUtils.Infrastructure.Scaffolding;
using PbiUtils.Infrastructure.Updates;
using PbiUtils.Windows;

using PbiUtils.Infrastructure.Scaffolding.Templates;

namespace PbiUtils.Host;

internal sealed partial class BusinessContext
{
    private PowerBiCloudClient? cloud;
    private UiDialogBroker? dialogs;
    private BridgeSession? bridge;
    private McpBusinessRuntime? mcp;

    /// <summary>当前连接独占账号、目标、草稿、计划及编辑服务; 全局协调由宿主另行注入.</summary>
    private void Compose(string userData, LoggingRuntime logging, IBusinessHostMcpConnections? mcpConnections)
    {
        var logs = logging.Factory;
        ChangeWorkspace? pendingWorkspace = null;
        ModelEditService? pendingEdits = null;
        ScaffoldRuntime? pendingScaffold = null;
        try
        {
            cloud = new PowerBiCloudClient(logger: logs.CreateLogger<PowerBiCloudClient>());
            var workspace = pendingWorkspace = new ChangeWorkspace(logger: logs.CreateLogger<ChangeWorkspace>());
            var reader = new ConnectedModelReader(new ProjectReader(), cloud,
                new PowerBiSchemaReader(logger: logs.CreateLogger<PowerBiSchemaReader>()), logs.CreateLogger<ConnectedModelReader>(), workspace,
                gui is null ? null : gui.BeginFocusAsync);
            var edits = pendingEdits = new ModelEditService(reader, reader, logs.CreateLogger<ModelEditService>());
            var connections = new ModelConnectionService(reader, logs.CreateLogger<ModelConnectionService>(), new DesktopModelFileOpener(logs.CreateLogger<DesktopModelFileOpener>()));
            var scaffold = pendingScaffold = new ScaffoldRuntime(userData, logs);
            var inspector = new ScaffoldTemplateInspector(scaffold.Catalog, userData);
            dialogs = new UiDialogBroker(Send, () => gui is not null);
            bridge = new BridgeSession(new OriginPolicy(false, null), new DoctorService(new RuntimeEnvironment(powerBiSupported: true), logs.CreateLogger<DoctorService>()),
                Send, registration.Options.WebViewVersion ?? "-",
                powerBi: new PowerBiService(new LocalPowerBiReader(), TimeProvider.System, logs.CreateLogger<PowerBiService>()),
                projects: new ProjectService(edits, logs.CreateLogger<ProjectService>()), selectProject: gui is null ? null : gui.SelectProjectAsync,
                ownedWorkspace: workspace, recentProjects: new LocalRecentProjects(userData, logs.CreateLogger<LocalRecentProjects>()), connections: connections,
                remoteForm: registration.Options.Kind is "gui" or "mcp" ? new RemoteConnectionForm(new LocalRemoteConnectionStore(userData, logs.CreateLogger<LocalRemoteConnectionStore>()), logs.CreateLogger<RemoteConnectionForm>()) : null,
                cloudConnection: new PowerBiServiceConnection(cloud, logs.CreateLogger<PowerBiServiceConnection>(), new LocalPowerBiLoginStore(userData, logs.CreateLogger<LocalPowerBiLoginStore>()), new LocalPowerBiRecentModelStore(userData, logs.CreateLogger<LocalPowerBiRecentModelStore>())),
                logging: new LoggingService(logging, logs.CreateLogger<LoggingService>()), openLogDirectory: token => OpenDirectoryAsync(logging.DirectoryPath, token),
                logger: logs.CreateLogger<BridgeSession>(), edits: edits,
                samples: new SampleDataService(new SampleDataAdapter(logs.CreateLogger<SampleDataAdapter>()), logs.CreateLogger<SampleDataService>(), new LocalSampleDataCache(userData)),
                chooseSampleDirectory: gui is null ? null : gui.SelectSampleAsync, confirmSample: ConfirmSampleAsync, openSampleDirectory: OpenDirectoryAsync,
                uiSettings: new UiSettingsService(new LocalUiSettingsStore(userData), logs.CreateLogger<UiSettingsService>()), dialogs: dialogs,
                scaffold: new ScaffoldBridge(scaffold.Catalog, scaffold.Service, userData, SelectScaffoldAsync, OpenDirectoryAsync,
                    scaffold.Packages, scaffold.Local, inspector.InspectAsync, scaffold.Dispose, scaffold.Sources, inspector.PreviewCalendarAsync,
                    (path, identity, token) => OpenDirectoryAsync(HostPlatformActions.CreatedDirectory(path, identity), token),
                    (path, identity, token) => HostPlatformActions.OpenCreatedDesktopAsync(path, identity, connections, token)),
                configuration: new ModelConfigurationBridge(new ProjectReader(), workspace, scaffold.Configuration,
                    HostPlatformActions.VerifyCreatedConfigurationAsync, ConfirmUpgradeAsync, reader),
                tableExports: new ModelTableExportService(reader, reader, new ModelTableFileWriter()), chooseTableExportFile: gui is null ? null : gui.SelectExportAsync,
                tableStatistics: reader, exportLocations: new LocalModelExportLocationStore(userData), openExportDirectory: OpenDirectoryAsync, tableRowCounts: reader,
                updateLearningView: gui is null ? null : gui.UpdateLearningAsync, closeLearningView: gui is null ? null : gui.CloseLearningAsync, openLearningSite: gui is null ? HostPlatformActions.OpenLearningSiteAsync : gui.OpenLearningAsync,
                mcpJournal: new ExecutionJournal(userData), checkUserDataReset: ValidateResetAsync, scheduleUserDataReset: ScheduleReset,
                mcpConnections: mcpConnections, productUpdates: ProductUpdateFactory.Create(userData),
                openUpdateLink: gui is null ? null : HostPlatformActions.OpenUpdateLinkAsync,
                externalToolTarget: gui is null ? null : registration.Options.ExternalToolTarget);
            pendingWorkspace = null;
            pendingEdits = null;
            pendingScaffold = null;
            if (registration.Options.Kind == "mcp") mcp = new McpBusinessRuntime(registration.Options.AllowWrite, userData, logs, desktopContext: this);
        }
        catch
        {
            pendingScaffold?.Dispose();
            pendingEdits?.Dispose();
            pendingWorkspace?.Dispose();
            throw;
        }
    }

    /// <summary>目录归属在后台核验, GUI 只执行已验证目标的系统打开.</summary>
    private Task OpenDirectoryAsync(string path, CancellationToken token) => HostPlatformActions.OpenDirectoryAsync(path, gui, token);

    /// <summary>没有所属 GUI 的请求不能唤起任意其他窗口作为选择器.</summary>
    private Task<string?> SelectScaffoldAsync(CancellationToken token) => gui is not null ? gui.SelectScaffoldAsync(token)
        : Task.FromException<string?>(new AppException("MCP_INTERACTION_REQUIRED", "此操作需要在桌面端选择目录", 3));

    /// <summary>保留样例覆盖/清空的具体确认, 不新增通用写入弹窗.</summary>
    private async Task<bool> ConfirmSampleAsync(string action, string directory, CancellationToken token)
    {
        var prefix = action == "clear" ? "sampleClear" : "sampleOverwrite";
        var answer = await dialogs!.ShowAsync(new("confirm", "hostDialog." + prefix + "Title", "hostDialog." + prefix + "Message", new { directory }), token);
        token.ThrowIfCancellationRequested();
        return answer.Action == "confirm";
    }

    /// <summary>GUI 保留兼容级别升级的独立确认, 精确目标仍由原配置用例校验.</summary>
    private async Task<bool> ConfirmUpgradeAsync(ProjectSnapshot project, int compatibility, CancellationToken token)
    {
        var answer = await dialogs!.ShowAsync(new("confirm", "configuration.upgradeTitle", "configuration.upgradeMessage",
            new { from = project.Model.CompatibilityLevel, to = compatibility }), token);
        token.ThrowIfCancellationRequested();
        return answer.Action == "confirm";
    }
}
