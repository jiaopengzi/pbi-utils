// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Export\ModelTableFileWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在原生授权目标旁暂存多表导出, 完整文件或全新CSV目录一次发布并清理失败临时产物.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Application.Models.Export;
using System.Security.Cryptography;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Models.Export;

/// <summary>导出的文件系统适配, 仅创建本轮私有暂存及全新CSV输出目录, 不接受网页路径.</summary>
public sealed class ModelTableFileWriter : IModelTableFileWriter
{
    /// <summary>后台记录目标文件基线并准备同目录临时文件, 失败只返回安全错误.</summary>
    /// <param name="destination">原生保存选择器授权的完整路径.</param>
    /// <param name="request">已验证的格式和摘要选项.</param>
    /// <param name="token">建立临时文件前后的取消令牌.</param>
    /// <returns>必须总是释放的单次暂存文件.</returns>
    public Task<IModelTableExportFile> CreateAsync(string destination, ModelTableExportRequest request, CancellationToken token) => Task.Run<IModelTableExportFile>(() =>
    {
        token.ThrowIfCancellationRequested();
        try { return request.Format == "csv" && request.Tables.Count > 1 ? new ModelTableDirectoryExportFile(destination, request) : new ExportFile(destination, request, token); }
        catch (AppException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new AppException("EXPORT_FILE_FAILED", "无法准备导出文件, 请检查目标目录和文件权限", 4); }
    }, token);

    /// <summary>同目录暂存和发布的唯一 owner, 成功发布之后不再接受取消或执行可能失败的外部核验.</summary>
    private sealed class ExportFile : IModelTableExportFile
    {
        private readonly string destination;
        private readonly string temporary;
        private readonly ModelTableExportRequest request;
        private readonly string? original;
        private int written;
        private bool completed;
        private bool published;
        private bool disposed;
        private FileStream? output;
        private ModelTableSpreadsheetWriter? workbook;
        public string FileName { get; }

        /// <summary>核验文件扩展名、目录和链接, 不主动建立目录或覆盖现有文件.</summary>
        public ExportFile(string path, ModelTableExportRequest options, CancellationToken token)
        {
            destination = Path.GetFullPath(path);
            request = options;
            FileName = Path.GetFileName(destination);
            if (!Path.GetExtension(destination).Equals("." + request.Format, StringComparison.OrdinalIgnoreCase))
                throw new AppException("EXPORT_FORMAT_INVALID", "文件扩展名与所选导出格式不一致");
            UserDataStore.Check(destination);
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new AppException("EXPORT_FILE_FAILED", "导出目录不存在", 3);
            original = Fingerprint(destination, token);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".pbi-utils-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        }

        /// <summary>同步数据消费只写任务私有临时文件, 格式失败和取消不会触碰原目标.</summary>
        public long Write(string table, IModelTableRows rows, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed || written >= request.Tables.Count || request.Tables[written] != table) throw new AppException("EXPORT_FILE_STATE", "导出文件已经写入", 4);
            token.ThrowIfCancellationRequested();
            try
            {
                long count;
                if (request.Format == "csv")
                {
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                    count = ModelTableCsvWriter.Write(stream, rows, token);
                    stream.Flush(true);
                }
                else
                {
                    output ??= new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                    workbook ??= new ModelTableSpreadsheetWriter(output, request);
                    count = workbook.Write(table, rows, token);
                }
                token.ThrowIfCancellationRequested();
                written++;
                return count;
            }
            catch (AppException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException or OverflowException)
            { throw new AppException("EXPORT_FILE_FAILED", "无法写入完整导出文件, 请检查空间、权限和数据格式", 4); }
        }

        /// <summary>完成全部工作表及格式尾部并刷新到磁盘, 取消或任何写入错误仍保留原目标.</summary>
        public void Complete(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed || written != request.Tables.Count) throw new AppException("EXPORT_FILE_STATE", "导出文件尚未完成或已经发布", 4);
            token.ThrowIfCancellationRequested();
            try
            {
                workbook?.Complete(token);
                output?.Flush(true);
                workbook?.Dispose(); workbook = null;
                output?.Dispose(); output = null;
                token.ThrowIfCancellationRequested();
                completed = true;
            }
            catch (AppException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException or OverflowException)
            { throw new AppException("EXPORT_FILE_FAILED", "无法写入完整导出文件, 请检查空间、权限和数据格式", 4); }
        }

        /// <summary>后台复核原目标基线, 取消竞争裁决完成后受锁发布, 此后不检查取消.</summary>
        public Task CommitAsync(Action? beginCommit, CancellationToken token) => Task.Run(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!completed || published) throw new AppException("EXPORT_FILE_STATE", "导出文件尚未完成或已经发布", 4);
            try
            {
                token.ThrowIfCancellationRequested();
                UserDataStore.Check(destination); UserDataStore.Check(temporary);
                if (Fingerprint(destination, token) != original) throw new AppException("EXPORT_DESTINATION_CHANGED", "目标文件已被其他操作修改, 未覆盖; 请重新导出", 3);
                token.ThrowIfCancellationRequested();
                beginCommit?.Invoke();
                // beginCommit 与宿主取消共用临界区; 返回后必须完成发布且不再把取消误报为失败.
                ModelTableExportPublication.Publish(temporary, destination, original, stream => Fingerprint(stream, CancellationToken.None), () => published = true);
            }
            catch (AppException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { throw new AppException("EXPORT_FILE_FAILED", "导出文件发布失败, 请检查目标文件是否被占用", 4); }
        }, token);

        /// <summary>只清理本次独立命名的临时文件, 清理失败显式报告而不吞掉残留事实.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception? failure = null;
            try { workbook?.Dispose(); } catch (Exception error) { failure = error; }
            try { output?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { if (!published && File.Exists(temporary)) { UserDataStore.Check(temporary); File.Delete(temporary); } }
            catch (Exception error) { failure ??= error; }
            if (failure is not null) throw new AppException("EXPORT_CLEANUP_FAILED", "导出未完成且临时文件清理失败, 请检查目标目录", 4);
        }

        /// <summary>内容及元数据共同约束已有目标, 读取失败不得当作文件不存在.</summary>
        private static string? Fingerprint(string path, CancellationToken token)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Fingerprint(stream, token);
            }
            catch (FileNotFoundException) { return null; }
        }

        /// <summary>在拒绝外部写入/删除的句柄内读取元数据和全部内容, 不重新按路径打开文件.</summary>
        private static string Fingerprint(FileStream stream, CancellationToken token)
        {
            var metadata = FormattableString.Invariant($"{stream.Length}:{File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks}:{File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks}");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            return metadata + ":" + Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
}
