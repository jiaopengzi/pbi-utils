// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldModelFiles.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 对模型及附属文本执行受预算的隔离写入, 保持目录边界与取消.

using System.Text;
using PbiUtils.Core;

using PbiUtils.Infrastructure.SampleData.Model;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>只操作 owner 传入的 staging 和字节预算, 不决定归属或最终提交.</summary>
internal static class ScaffoldModelFiles
{
    private static readonly string[] DesktopGitIgnoreRules = ["**/.pbi/localSettings.json", "**/.pbi/cache.abf"];

    /// <summary>在输出 PBIP 目录补齐 Desktop 缓存忽略规则, 保留模板原字节和既有规则.</summary>
    /// <param name="staging">调用方拥有的隔离工程根, 不访问模板源文件.</param>
    /// <param name="budget">与模板复制和模型文档共用的写入预算.</param>
    /// <param name="token">追加前检查的取消令牌.</param>
    /// <exception cref="AppException">路径或总写入预算无效; 文件访问错误原样传播.</exception>
    internal static void WriteGitIgnore(string staging, ScaffoldWriteBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Inside(staging, "pbip/.gitignore");
        var existing = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var lines = Encoding.UTF8.GetString(existing).TrimStart('\uFEFF').Split('\n')
            .Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var missing = DesktopGitIgnoreRules.Where(rule => !lines.Contains(rule)).ToArray();
        if (missing.Length == 0) return;
        var separator = existing.Length > 0 && existing[^1] != (byte)'\n' ? "\n" : "";
        var addition = Encoding.UTF8.GetBytes(separator + string.Join('\n', missing) + "\n");
        token.ThrowIfCancellationRequested();
        budget.Reserve(addition.LongLength);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        stream.Write(addition);
    }

    /// <summary>内部相对路径仍验证根内, 拒绝 manifest 或表名逃逸.</summary>
    internal static string Inside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new AppException("SCAFFOLD_PATH_UNSAFE", "输出相对路径不安全", 2);
        return path;
    }
    /// <summary>附属文本计入同一输出预算, 不覆盖模板同名用户文件.</summary>
    internal static void WriteText(string root, string relative, string text, ScaffoldWriteBudget budget)
    {
        var path = Inside(root, relative); var bytes = Encoding.UTF8.GetBytes(text); budget.Reserve(bytes.LongLength);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes);
    }
    /// <summary>官方序列化文档通过有界写入流输出, 元数据也不能绕过 staging 总预算.</summary>
    internal static void WriteModelDocument(string path, Action<Stream> write, ScaffoldWriteBudget budget, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) budget.ReleaseReplaced(new FileInfo(path).Length);
        using var stream = new BudgetOutputStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), budget, token);
        write(stream);
    }
    /// <summary>逐块验证取消和字节预算, 保留真实磁盘写入错误.</summary>
    private sealed class BudgetOutputStream(Stream inner, ScaffoldWriteBudget budget, CancellationToken token) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        /// <summary>刷新由真实文件流负责.</summary>
        public override void Flush() => inner.Flush();
        /// <summary>只写流拒绝读取.</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <summary>只顺序写入, 不允许绕过预算定位.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>不允许预分配或截断绕过预算.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>每次真正写入前计量并检查取消.</summary>
        public override void Write(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); budget.Reserve(count); inner.Write(buffer, offset, count); }
        /// <summary>Span 写入使用相同预算边界.</summary>
        public override void Write(ReadOnlySpan<byte> buffer) { token.ThrowIfCancellationRequested(); budget.Reserve(buffer.Length); inner.Write(buffer); }
        /// <summary>无论成功失败均关闭任务拥有的文件句柄.</summary>
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
