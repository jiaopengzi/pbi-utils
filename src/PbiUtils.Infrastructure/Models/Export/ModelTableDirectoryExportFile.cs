// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableDirectoryExportFile.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 将多表CSV流式写入同父目录私有暂存, 完整核验后只发布到全新子目录且永不覆盖.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Models.Export;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>多表CSV目录发布的唯一 owner, 不创建ZIP, 不合并或覆盖任何既有目录内容.</summary>
internal sealed class ModelTableDirectoryExportFile : IModelTableExportFile
{
    private readonly string destination;
    private readonly string temporary;
    private readonly ModelTableExportRequest request;
    private readonly string[] files;
    private readonly List<string> created = [];
    private bool completed;
    private bool published;
    private bool disposed;
    public string FileName { get; }

    /// <summary>核对宿主指定的全新子目录, 临时目录只会在第一张表开始消费时创建.</summary>
    /// <param name="path">目录选择器之后由宿主生成的全新输出子目录完整路径.</param>
    /// <param name="options">已验证的多表CSV选择, 不携带网页提供的路径.</param>
    /// <exception cref="AppException">目标已存在、父目录不可用或路径包含链接.</exception>
    internal ModelTableDirectoryExportFile(string path, ModelTableExportRequest options)
    {
        destination = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        request = options;
        FileName = Path.GetFileName(destination);
        var parent = Path.GetDirectoryName(destination);
        UserDataStore.Check(destination);
        if (string.IsNullOrEmpty(FileName) || string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new AppException("EXPORT_FILE_FAILED", "导出目录不存在", 3);
        EnsureDestinationAbsent();
        temporary = Path.Combine(parent, ".pbi-utils-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        files = ModelTableExportNames.CsvFiles(options.Tables);
    }

    /// <summary>逐表写一个单层CSV文件, 只触及本轮创建的暂存目录, 原始数据不缓存或去重.</summary>
    /// <param name="table">当前已选表名, 必须与固定选择顺序相同.</param>
    /// <param name="rows">当前后台数据会话持有的实际游标.</param>
    /// <param name="token">逐行及同步写入前后检查的取消令牌.</param>
    /// <returns>完整写入的实际数据行数.</returns>
    /// <exception cref="AppException">状态、路径或磁盘写入失败, 不发布任何部分目录.</exception>
    public long Write(string table, IModelTableRows rows, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || created.Count >= request.Tables.Count || request.Tables[created.Count] != table)
            throw new AppException("EXPORT_FILE_STATE", "导出文件已经写入", 4);
        token.ThrowIfCancellationRequested();
        try
        {
            UserDataStore.Check(temporary);
            if (created.Count == 0)
            {
                if (Directory.Exists(temporary) || File.Exists(temporary)) throw new AppException("EXPORT_DESTINATION_CHANGED", "目标文件已被其他操作修改, 未覆盖; 请重新导出", 3);
                Directory.CreateDirectory(temporary);
            }
            var path = Path.Combine(temporary, files[created.Count]);
            UserDataStore.Check(path);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            created.Add(path);
            var count = ModelTableCsvWriter.Write(output, rows, token);
            token.ThrowIfCancellationRequested();
            output.Flush(true);
            return count;
        }
        catch (AppException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        { throw new AppException("EXPORT_FILE_FAILED", "无法写入完整导出文件, 请检查空间、权限和数据格式", 4); }
    }

    /// <summary>确认所有已选CSV均已关闭并且暂存只含本轮已知文件, 发布前仍受理取消.</summary>
    /// <param name="token">完成检查前的取消令牌.</param>
    /// <exception cref="AppException">缺少表或暂存内容被外部更改.</exception>
    public void Complete(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || created.Count != request.Tables.Count) throw new AppException("EXPORT_FILE_STATE", "导出文件尚未完成或已经发布", 4);
        token.ThrowIfCancellationRequested();
        try { VerifyOwnedContents(); completed = true; }
        catch (AppException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new AppException("EXPORT_FILE_FAILED", "无法写入完整导出文件, 请检查空间、权限和数据格式", 4); }
    }

    /// <summary>再次核验目标不存在, 提交裁决后一次移动全目录, 竞争创建的任何路径都保持不变.</summary>
    /// <param name="beginCommit">宿主取消与提交的原子裁决, 返回后不再受理本轮取消.</param>
    /// <param name="token">提交裁决之前的取消令牌.</param>
    /// <returns>完整目录发布任务, 不覆盖既有目录或文件.</returns>
    /// <exception cref="AppException">目标冲突或目录发布失败.</exception>
    public Task CommitAsync(Action? beginCommit, CancellationToken token) => Task.Run(() =>
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!completed || published) throw new AppException("EXPORT_FILE_STATE", "导出文件尚未完成或已经发布", 4);
        try
        {
            token.ThrowIfCancellationRequested();
            UserDataStore.Check(destination); VerifyOwnedContents(); EnsureDestinationAbsent();
            token.ThrowIfCancellationRequested(); beginCommit?.Invoke();
            EnsureDestinationAbsent();
            Directory.Move(temporary, destination);
            published = true;
        }
        catch (AppException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
        { throw new AppException("EXPORT_DESTINATION_CHANGED", "目标文件已被其他操作修改, 未覆盖; 请重新导出", 3); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new AppException("EXPORT_FILE_FAILED", "导出文件发布失败, 请检查目标文件是否被占用", 4); }
    }, token);

    /// <summary>只删除预先完整核验的自有CSV及空暂存目录, 不递归处理外部插入或链接目录.</summary>
    /// <exception cref="AppException">不能安全释放本轮临时目录, 原始业务首错由共享服务保留.</exception>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (published || !Directory.Exists(temporary)) return;
        try
        {
            VerifyOwnedContents();
            foreach (var file in created) { UserDataStore.Check(file); File.Delete(file); }
            Directory.Delete(temporary, recursive: false);
        }
        catch (Exception) { throw new AppException("EXPORT_CLEANUP_FAILED", "导出未完成且临时文件清理失败, 请检查目标目录", 4); }
    }

    /// <summary>已有文件和目录同样视作冲突, 不将其他类型路径当作可覆盖目标.</summary>
    private void EnsureDestinationAbsent()
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new AppException("EXPORT_DESTINATION_CHANGED", "目标文件已被其他操作修改, 未覆盖; 请重新导出", 3);
    }

    /// <summary>枚举全部已创建目录项后才允许发布或清理, 未知项和reparse明确失败而不递归删除.</summary>
    private void VerifyOwnedContents()
    {
        UserDataStore.Check(temporary);
        var actual = Directory.GetFileSystemEntries(temporary);
        if (actual.Length != created.Count || actual.Any(path => !created.Contains(path, StringComparer.OrdinalIgnoreCase) || !File.Exists(path) || Directory.Exists(path)))
            throw new AppException("EXPORT_FILE_FAILED", "无法写入完整导出文件, 请检查空间、权限和数据格式", 4);
        foreach (var path in actual) UserDataStore.Check(path);
    }
}
