// FilePath    : pbi-utils\src\PbiUtils.Windows\Desktop\Files\DesktopFileSync.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 精确绑定 Desktop 文件, 重载 PBIP 并通过目标窗口保存, 区分提交和同步结果.

using PbiUtils.Windows.Desktop;
using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Udf;
using PbiUtils.Application.Models.Editing;
using PbiUtils.Application.Models;
using PbiUtils.Infrastructure.Models.Configuration;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure;
using PbiUtils.Infrastructure.Scaffolding;

namespace PbiUtils.Windows.Desktop.Files;

/// <summary>一次保存的后端身份票据, 不向网页或 AI 返回文件路径及窗口句柄.</summary>
public sealed class DesktopSyncTicket(DesktopFileTarget? target, bool reload)
{
    public DesktopFileTarget? Target { get; } = target;
    public bool Reload { get; } = reload;
    public bool Reloaded { get; set; }
    /// <summary>仅共享文件提交成功或完整自动保存读回成功后记录, 尾部失败不能抹去已证明的文件保存.</summary>
    public bool FileSaved { get; set; }
    public IReadOnlyList<ModelEditRequest> Edits { get; init; } = [];
    /// <summary>本轮模型提交后的真实 TOM 安全指纹, 不保存分区/来源原文.</summary>
    public string? ConfigurationDefinitionFingerprint { get; set; }
}

/// <summary>只同步定义和界面, 不请求数据处理, 不关闭或重启用户的 Desktop.</summary>
public static partial class DesktopFileSync
{

    /// <summary>PBIP 只匹配官方接口返回的完整文件路径, 拒绝同名猜测、多窗口和未保存覆盖.</summary>
    /// <param name="snapshot">共享读取器已经核实的当前工程与报告绑定.</param>
    /// <param name="token">发现和只读前置检查的取消令牌.</param>
    /// <returns>对应唯一 Desktop 或离线工程的保存票据, 尚未提交文件.</returns>
    /// <exception cref="AppException">身份歧义、未保存内容或待应用查询阻止进入文件写入.</exception>
    public static async Task<DesktopSyncTicket> PreparePbipAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        List<DesktopFileTarget> matches = [];
        var paths = snapshot.Files.Where(f => Path.GetFileName(f.Path).Equals("definition.pbir", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(snapshot.ProjectPath)!, f.Path))).Append(Path.GetFullPath(snapshot.ProjectPath)).ToArray();
        foreach (var process in Process.GetProcessesByName("PBIDesktop"))
        {
            using (process)
            {
                using var self = Process.GetCurrentProcess();
                if (process.SessionId != self.SessionId) continue;
                DesktopFileTarget state;
                try { state = await DesktopBridgeClient.InspectAsync(process.Id, token); }
                // 未命名窗口没有任何已保存文件, 不可能与当前 PBIP 匹配.
                catch (AppException e) when (e.Code == "DESKTOP_FILE_REQUIRED") { continue; }
                if (paths.Contains(state.Path, StringComparer.OrdinalIgnoreCase)) matches.Add(state);
            }
        }
        if (matches.Count > 1) throw new AppException("DESKTOP_FILE_AMBIGUOUS", "同一工程在多个 Desktop 窗口打开, 请保留一个窗口后保存", 3);
        var target = matches.SingleOrDefault();
        if (target?.HasUnsavedChanges == true) throw new AppException("DESKTOP_UNSAVED_CHANGES", "该工程在 Desktop 有未保存的修改, 请先保存并在此刷新模型, 避免相互覆盖", 3);
        if (target is not null) await DesktopPendingQueries.RequireNoneAsync(target, token);
        return new(target, reload: true);
    }

    /// <summary>写入后独立完成同步; 失败由上层保留已提交状态, 重试不会重复修改模型.</summary>
    /// <param name="ticket">包含目标身份和已证明阶段的原保存票据.</param>
    /// <param name="saved">已提交模型的权威快照, 不在此重新应用编辑.</param>
    /// <param name="token">提交后独立完成期限令牌, 不使用已经取消的准备令牌.</param>
    /// <returns>已经证明的模型、文件与 Desktop 三阶段结果.</returns>
    /// <exception cref="AppException">待应用查询或同步/保存无法核实; 已证明的 FileSaved 仍保留在票据.</exception>
    public static async Task<ModelSaveResult> CompleteAsync(DesktopSyncTicket ticket, ProjectSnapshot saved, CancellationToken token)
    {
        if (ticket.Target is not { } target) return new(true, true, false, "PBIP 文件已保存；当前工程未在 Power BI Desktop 打开。");
        await DesktopBridgeClient.VerifyAsync(target, token);
        await DesktopPendingQueries.RequireNoneAsync(target, token);
        if (ticket.Reload && !ticket.Reloaded)
        {
            await DesktopBridgeClient.ReloadAsync(target, token);
            ticket.Reloaded = true;
        }
        var state = await DesktopBridgeClient.VerifyAsync(target, token);
        // PBIP 文件已经由原子写入保存; 重载没有产生新修改时无需再次覆盖相同内容.
        if (!ticket.Reload || state.HasUnsavedChanges)
        {
            await SaveFileAsync(target, saved, ticket.Edits, ticket.ConfigurationDefinitionFingerprint, token);
            ticket.FileSaved = true;
        }
        await DesktopPendingQueries.RequireNoneAsync(target, token);
        return new(true, true, true, "模型和文件已保存，Power BI Desktop 已同步；未重新查询数据源。");
    }

    /// <summary>调用目标窗口的保存按钮, 同时观察文件变更与宿主未保存标志, 不把发出按键当作完成.</summary>
    /// <param name="target">已核实的真实 Desktop 文件身份.</param>
    /// <param name="saved">本轮模型提交后的预期快照.</param>
    /// <param name="edits">只读核验落盘结果的实际编辑集合.</param>
    /// <param name="configurationFingerprint">整批配置的完整可写定义指纹, 不能以展示形状替代.</param>
    /// <param name="token">保存尾部完成期限, 不用于撤销已经提交的模型.</param>
    /// <returns>仅在保存与完整文件读回已证明时完成.</returns>
    /// <exception cref="AppException">查询待应用、控件不可用或文件保存无法确认.</exception>
    private static async Task SaveFileAsync(DesktopFileTarget target, ProjectSnapshot saved, IReadOnlyList<ModelEditRequest> edits, string? configurationFingerprint, CancellationToken token)
    {
        // 用户可在尾部失败后手动保存; 先只读证明已完成, 避免disabled保存按钮让恢复永远卡住.
        if (await DesktopFileSaveRecovery.TryConfirmAsync(target, configurationFingerprint, ct => DesktopBridgeClient.VerifyAsync(target, ct),
            async ct => await ModelConfigurationReadback.FingerprintFileAsync(await ReadSavedProjectAsync(target, ct), ct), TimeSpan.FromSeconds(2), token)) return;
        // TOM 的变更事件异步传到 Desktop; 每轮只重试文件保存, 不再次提交模型.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(750, token);
            await DesktopBridgeClient.VerifyAsync(target, token);
            var before = Stamp(target.Path);
            var pattern = await FindSaveControlAsync(target, true, button =>
            {
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) throw new AppException("DESKTOP_SAVE_UNAVAILABLE", "Desktop 保存控件不支持自动调用, 请在 Desktop 保存文件后重试", 3);
                return (InvokePattern)pattern;
            }, token);
            await DesktopPendingQueries.RequireNoneAsync(target, token);
            if (await DesktopSaveControlLookup.InvokeOnceAsync(() => Task.Run(() => pattern.Invoke(), token), ct => ObserveSaveAsync(before, ct), token)) return;
        }
        throw new AppException("DESKTOP_FILE_SAVE_UNCONFIRMED", "模型已提交, 但未确认文件保存完成; 请处理 Desktop 中的弹窗后重试", 4);

        async Task<bool> ObserveSaveAsync(string before, CancellationToken cancellation)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(250, cancellation);
                var state = await DesktopBridgeClient.VerifyAsync(target, cancellation);
                if (!state.HasUnsavedChanges && Stamp(target.Path) != before)
                {
                    if (!await HasExpectedFileEditsAsync(target, saved, edits, configurationFingerprint, cancellation)) return false;
                    // 继续观察迟到的 Desktop 脏标记, 避免将上一版本的保存误报成本轮完成.
                    await Task.Delay(2000, cancellation);
                    return !(await DesktopBridgeClient.VerifyAsync(target, cancellation)).HasUnsavedChanges
                        && await HasExpectedFileEditsAsync(target, saved, edits, configurationFingerprint, cancellation);
                }
            }
            return false;
        }
    }

    /// <summary>PBIP 用实际文件回读校验本轮属性, PBIX 由 Desktop 文件写入和状态联合确认, 不解析二进制.</summary>
    private static async Task<bool> HasExpectedFileEditsAsync(DesktopFileTarget target, ProjectSnapshot expected, IReadOnlyList<ModelEditRequest> edits, string? configurationFingerprint, CancellationToken token)
    {
        if (Path.GetExtension(target.Path).Equals(".pbix", StringComparison.OrdinalIgnoreCase)) return true;
        var actual = await ReadSavedProjectAsync(target, token);
        // 配置、函数与表删除必须核验完整定义; 可见对象缺失不能证明关系和删除历史均已准确落盘.
        if (edits.Any(edit => edit.Operation is "configuration.apply" or "udf.deleteUnused" or "table.deleteBatch"))
            return EditsPersistedWithDefinition(expected.Model, actual.Model, edits, configurationFingerprint, await ModelConfigurationReadback.FingerprintFileAsync(actual, token));
        return EditsPersisted(expected.Model, actual.Model, edits);
    }

    /// <summary>只读取官方身份对应的唯一PBIP, 不用同名入口猜测文件保存结果.</summary>
    internal static async Task<ProjectSnapshot> ReadSavedProjectAsync(DesktopFileTarget target, CancellationToken token)
    {
        var path = target.Path;
        if (Path.GetExtension(path).Equals(".pbir", StringComparison.OrdinalIgnoreCase))
        {
            path = DesktopPendingQueries.BoundProject(path, token);
        }
        return await new ProjectReader().ReadAsync(path, token);
    }

    /// <summary>按实际操作比较持久化属性, 只规范化换行, 不以文件时间替代模型内容验证.</summary>
    public static bool EditsPersistedWithDefinition(ModelInfo expected, ModelInfo actual, IReadOnlyList<ModelEditRequest> edits, string? expectedFingerprint, string actualFingerprint)
        => expectedFingerprint is { Length: 64 } && expectedFingerprint.All(char.IsAsciiHexDigit) && expectedFingerprint == actualFingerprint
            && EditsPersisted(expected, actual, edits.Where(edit => edit.Operation != "configuration.apply").ToArray());

    public static bool EditsPersisted(ModelInfo expected, ModelInfo actual, IReadOnlyList<ModelEditRequest> edits)
    {
        static string Normalize(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        foreach (var edit in edits)
        {
            if (edit.Operation == "configuration.apply")
            {
                if (expected.CompatibilityLevel != actual.CompatibilityLevel || expected.Tables.Count != actual.Tables.Count || expected.Measures.Count != actual.Measures.Count || expected.Functions.Count != actual.Functions.Count) return false;
                if (expected.Tables.Any(table => actual.Tables.SingleOrDefault(item => item.Name == table.Name) is not { } persisted
                    || table.Columns.Count != persisted.Columns.Count || table.Columns.Any(column => !persisted.Columns.Any(item => item.Name == column.Name && item.Kind == column.Kind && item.DataType == column.DataType)))) return false;
                if (expected.Measures.Any(measure => !actual.Measures.Any(item => item.Table == measure.Table && item.Name == measure.Name
                    && Normalize(item.Expression) == Normalize(measure.Expression) && Normalize(item.Description) == Normalize(measure.Description)
                    && Normalize(item.DisplayFolder) == Normalize(measure.DisplayFolder) && Normalize(item.FormatString) == Normalize(measure.FormatString) && item.IsHidden == measure.IsHidden))) return false;
                if (expected.Functions.Any(function => !actual.Functions.Any(item => item.Name == function.Name && Normalize(item.Expression) == Normalize(function.Expression) && Normalize(item.Description) == Normalize(function.Description)))) return false;
            }
            if (edit.Operation == "measure.batch")
            {
                foreach (var item in MeasureBatch.Parse(edit.Value).Items)
                {
                    var measure = actual.Measures.SingleOrDefault(m => m.Table == item.Table && m.Name == item.Name);
                    if (item.Action == "delete") { if (measure is not null) return false; continue; }
                    if (measure is null || item.Expression is not null && Normalize(measure.Expression) != Normalize(item.Expression)
                        || item.Description is not null && Normalize(measure.Description) != Normalize(item.Description)
                        || item.FormatString is not null && Normalize(measure.FormatString) != Normalize(item.FormatString)
                        || item.DisplayFolder is not null && Normalize(measure.DisplayFolder) != Normalize(item.DisplayFolder)
                        || item.IsHidden is not null && measure.IsHidden != item.IsHidden) return false;
                }
            }
            else if (edit.Operation == "measure.set")
            {
                var measure = actual.Measures.SingleOrDefault(m => m.Table == edit.Table && m.Name == edit.ObjectName);
                if (measure is null) return false;
                var value = edit.Property switch { "description" => measure.Description, "displayFolder" => measure.DisplayFolder, "formatString" => measure.FormatString, "expression" => measure.Expression, _ => null };
                if (Normalize(value) != Normalize(edit.Value)) return false;
            }
            else if (edit.Operation == "table.set" && (actual.Tables.SingleOrDefault(t => t.Name == edit.ObjectName) is not { } table || Normalize(table.Description) != Normalize(edit.Value))) return false;
            else if (edit.Operation == "table.deleteBatch" && TableDeleteBatch.Parse(edit.Value).Tables.Any(name => actual.Tables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)))) return false;
            else if (edit.Operation == "udf.deleteUnused" && UdfDeleteBatch.Parse(edit.Value).Names.Any(name => actual.Functions.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))) return false;
            else if (edit.Operation == "udf.set" && (actual.Functions.SingleOrDefault(f => f.Name == edit.ObjectName) is not { } function || Normalize(function.Expression) != Normalize(edit.Value))) return false;
            else if (edit.Operation == "calendar.create" && !actual.Tables.Any(t => t.Name == edit.ObjectName && t.Columns.Any(c => c.Name == "Date"))) return false;
            else if (edit.Operation == "period.generate" && expected.Measures.Any(e => !actual.Measures.Any(a => a.Table == e.Table && a.Name == e.Name && Normalize(a.Expression) == Normalize(e.Expression)))) return false;
        }
        return true;
    }

    /// <summary>通过 UI Automation 精确调用已绑定 PID 的保存按钮, 不发送全局快捷键或坐标点击.</summary>
    private static Task<T> FindSaveControlAsync<T>(DesktopFileTarget target, bool requireEnabled, Func<AutomationElement, T> read, CancellationToken token)
        => DesktopSaveControlLookup.FindAsync(async ct => { await DesktopBridgeClient.VerifyAsync(target, ct); },
            ct => Task.Run(() => read(FindSaveButton(target, requireEnabled)), ct), TimeSpan.FromMilliseconds(150), token);

    private static AutomationElement FindSaveButton(DesktopFileTarget target, bool requireEnabled)
    {
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.ProcessName != "PBIDesktop" || process.StartTime.ToUniversalTime() != target.Started || process.MainWindowHandle == IntPtr.Zero)
            throw new AppException("DESKTOP_INSTANCE_CHANGED", "Desktop 窗口身份已变化, 未执行自动保存", 3);
        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        var candidates = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))).Cast<AutomationElement>().ToArray();
        var buttons = DesktopSaveControlLookup.Available(candidates, e => IsSaveCommand(e.Current.Name) && e.TryGetCurrentPattern(InvokePattern.Pattern, out _) && !e.Current.IsOffscreen);
        // 重开文件后 Desktop 可保留多个完全重叠的 WebView 保存控件; 仅对同一控件身份按原生层级选择最上层.
        if (buttons.Length > 1 && buttons.All(e => e.Current.AutomationId == "save")
            && buttons.Select(e => e.Current.BoundingRectangle).Distinct().Count() == 1)
        {
            var ranked = buttons.Select(e => (Element: e, Rank: NativeZOrder(e, process.MainWindowHandle))).Where(e => e.Rank is not null).OrderBy(e => e.Rank, StringComparer.Ordinal).ToArray();
            buttons = ranked.Length > 0 && (ranked.Length == 1 || ranked[0].Rank != ranked[1].Rank) ? [ranked[0].Element] : [];
        }
        if (buttons.Length != 1 || (requireEnabled && !buttons[0].Current.IsEnabled))
            throw new AppException("DESKTOP_SAVE_UNAVAILABLE", "未找到唯一可用的 Desktop 保存按钮, 请关闭弹窗并展开主窗口后重试", 3);
        return buttons[0];
    }
    /// <summary>快捷键的空格和换行随 Desktop 布局变化, 保留严格保存命令白名单.</summary>
    public static bool IsSaveCommand(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c))) is "保存" or "Save" or "保存(Ctrl+S)" or "Save(Ctrl+S)";
    /// <summary>依据本窗口内部子窗口的实际 Z 序区分重叠表面, 不依赖坐标点击或前台焦点.</summary>
    private static string? NativeZOrder(AutomationElement element, IntPtr main)
    {
        IntPtr handle = IntPtr.Zero;
        for (var depth = 0; depth < 64 && element is not null; depth++)
        {
            handle = new IntPtr(element.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero) break;
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || !IsChild(main, handle)) return null;
        List<string> ranks = [];
        for (var depth = 0; depth < 64 && handle != main; depth++)
        {
            if (handle == IntPtr.Zero) return null;
            var rank = 0;
            for (var previous = GetWindow(handle, 3); previous != IntPtr.Zero; previous = GetWindow(previous, 3))
                if (++rank >= 4096) return null;
            ranks.Add(rank.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            handle = GetParent(handle);
        }
        if (handle != main) return null;
        ranks.Reverse(); return string.Join('.', ranks);
    }
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr handle);
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr handle, uint command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr handle);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(IntPtr parent, IntPtr child);

    /// <summary>观察 PBIX 本体或 PBIP 定义文件的写入时间与大小, 不读取业务数据或缓存.</summary>
    private static string Stamp(string path)
    {
        UserDataStore.Check(path);
        var files = new List<string> { path };
        if (!Path.GetExtension(path).Equals(".pbix", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(path)!;
            if (Path.GetExtension(path).Equals(".pbir", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root)!;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                UserDataStore.Check(folder);
                var definition = Path.Combine(folder, "definition");
                if (Directory.Exists(definition)) AddDefinitionFiles(definition, files);
                var bim = Path.Combine(folder, "model.bim"); if (File.Exists(bim)) files.Add(bim);
            }
        }
        return string.Join("|", files.Order(StringComparer.OrdinalIgnoreCase).Select(f => { UserDataStore.Check(f); var info = new FileInfo(f); return info.FullName + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks; }));
    }
    /// <summary>递归前逐级拒绝链接, 不进入用户数据或缓存文件夹.</summary>
    private static void AddDefinitionFiles(string directory, List<string> files)
    {
        UserDataStore.Check(directory);
        files.AddRange(Directory.EnumerateFiles(directory));
        foreach (var child in Directory.EnumerateDirectories(directory)) AddDefinitionFiles(child, files);
    }
}
