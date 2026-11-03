// FilePath    : pbi-utils\src\PbiUtils.Desktop\Shell\MainForm.Interactions.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅适配所属 GUI 的系统选择器与后台已绑定目录打开, 不执行业务服务.

using PbiUtils.Windows.NativeUi;
using PbiUtils.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Windows;

namespace PbiUtils.Desktop.Shell;

public sealed partial class MainForm
{
    /// <summary>由原生选择器授权具体 PBIP/PBIX 文件, 后续统一通过 Desktop 打开, 网页不传路径.</summary>
    private Task<string?> SelectProjectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var dialog = new OpenFileDialog { Title = "通过 Power BI Desktop 打开", Filter = "Power BI 文件 (*.pbip;*.pbix)|*.pbip;*.pbix", CheckFileExists = true, Multiselect = false };
        var selected = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        token.ThrowIfCancellationRequested();
        return Task.FromResult(selected);
    }

    /// <summary>由原生选择器绑定示例数据根目录, 网页只显示选择结果.</summary>
    private Task<string?> ChooseSampleDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var dialog = new FolderBrowserDialog { Description = "选择示例数据目录（包含 data 和 pbip 子目录）", UseDescriptionForTitle = true };
        using var cancellation = new OwnedDialogCancellation(Handle, dialog.Description, token);
        var selected = dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        token.ThrowIfCancellationRequested();
        return Task.FromResult(selected);
    }

    /// <summary>系统目录选择器只授权新建项目的父目录, 网页不提供路径.</summary>
    private Task<string?> ChooseScaffoldDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var dialog = new FolderBrowserDialog { Description = "PBIP", UseDescriptionForTitle = true };
        using var cancellation = new OwnedDialogCancellation(Handle, dialog.Description, token);
        var selected = dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        token.ThrowIfCancellationRequested();
        return Task.FromResult(selected);
    }

    /// <summary>使用系统选择器绑定工作簿、单表CSV文件或多表CSV全新子目录, 网页不能直接提供目标路径.</summary>
    /// <param name="request">已核验的当前表及格式, 语言仅影响系统窗口固定文案.</param>
    /// <param name="token">关闭、超时和取消令牌, 仅关闭本宿主拥有的本次窗口.</param>
    /// <returns>用户明确选择的完整路径, 取消返回 null, 不在选择阶段写文件.</returns>
    private Task<string?> ChooseTableExportFileAsync(ModelTableExportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var english = request.Locale == "en-us";
        if (request.Format == "csv" && request.Tables.Count > 1)
        {
            using var folder = new FolderBrowserDialog { Description = english ? "Export table data" : "导出表数据", UseDescriptionForTitle = true };
            using var folderCancellation = new OwnedDialogCancellation(Handle, folder.Description, token);
            if (folder.ShowDialog(this) != DialogResult.OK) return Task.FromResult<string?>(null);
            token.ThrowIfCancellationRequested();
            // 一次目录选择只授权全新子目录, 不覆盖目录中既有文件; 批量 writer 完整成功后才发布该目录.
            return Task.FromResult<string?>(Path.Combine(folder.SelectedPath, "Export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]));
        }
        var stem = request.Tables.Count == 1 ? request.Tables[0] : "Tables";
        var safeName = string.Concat(stem.Take(100).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).TrimEnd('.', ' ');
        using var dialog = new SaveFileDialog
        {
            Title = english ? "Export table data" : "导出表数据",
            Filter = request.Format == "xlsx" ? (english ? "Excel Workbook (*.xlsx)|*.xlsx" : "Excel 工作簿 (*.xlsx)|*.xlsx") : "CSV (*.csv)|*.csv",
            DefaultExt = request.Format,
            FileName = "Export-" + safeName + "." + request.Format,
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        using var cancellation = new OwnedDialogCancellation(Handle, dialog.Title, token);
        var selected = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        token.ThrowIfCancellationRequested();
        return Task.FromResult(selected);
    }

    /// <summary>受限宿主目录共用系统资源管理器入口, 不使用命令行拼接.</summary>
    private static void OpenBoundDirectory(string directory, string missingCode, string missingMessage)
    {
        UserDataStore.Check(directory);
        if (!Directory.Exists(directory)) throw new PbiUtils.Core.AppException(missingCode, missingMessage, 3);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = directory, UseShellExecute = true, Verb = "open" });
    }
}
