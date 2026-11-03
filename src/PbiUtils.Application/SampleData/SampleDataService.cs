// FilePath    : pbi-utils\src\PbiUtils.Application\SampleData\SampleDataService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享示例数据生成, 增量, 统计与清空用例, 不接受模型传入路径.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Serialization;
using PbiUtils.Application.SampleData;
using PbiUtils.Core;

namespace PbiUtils.Application.SampleData;

/// <summary>一张固定 CSV 的名称, 数据行数与实际文件字节数.</summary>
public sealed record SampleTableStat(string Name, long Rows, long Size);

/// <summary>宿主绑定或 CLI 明确选择的输出目录状态; 截止日期来自既有订单表.</summary>
public sealed record SampleDataState(string OutputPath, bool HasData, string? LastFactDate, IReadOnlyList<SampleTableStat> Tables)
{
    /// <summary>统计前后均一致的固定文件元数据, 仅用于缓存一致性, 不发送到网页.</summary>
    [JsonIgnore]
    public IReadOnlyList<SampleDataFileStamp>? FileStamps { get; init; }
}

/// <summary>一项固定产物的存在状态、字节数和最后写入 UTC 时间刻度, 不包含文件内容.</summary>
public sealed record SampleDataFileStamp(string Name, bool Exists, long Size, long LastWriteUtcTicks);

/// <summary>提交后的权威文件状态及本次生成行数, 不包含模拟标识.</summary>
public sealed record SampleDataResponse(SampleDataState State, SampleGenerationResult Result);

/// <summary>已保存的目录结果及固定产物元数据是否仍与当前文件一致.</summary>
public sealed record SampleDataCacheEntry(SampleDataState State, bool IsCurrent);

/// <summary>桌面可选的用户级结果缓存, 不授予路径访问或生成覆盖权限.</summary>
public interface ISampleDataCache
{
    /// <summary>仅读取用户缓存内的目录字符串, 不探测目录、网络位置或产物子树.</summary>
    string? RestoreOutputPath();
    /// <summary>仅为当前宿主绑定目录比较实际文件元数据; 其他目录或不存在时返回 null, 损坏时拒绝读取.</summary>
    /// <param name="expectedOutputPath">经过适配器规范化和验证的当前输出目录.</param>
    SampleDataCacheEntry? Read(string expectedOutputPath);
    /// <summary>原子记录权威状态及固定产物元数据, 不覆盖损坏或未知版本缓存.</summary>
    void Save(SampleDataState state);
}

/// <summary>实际 CSV, 内嵌模板和目录发布适配, 业务入口由共享服务统一调用.</summary>
public interface ISampleDataAdapter
{
    /// <summary>只复核安全路径和固定表是否存在, 用于损坏 CSV 仍可选中后全量修复.</summary>
    Task<SampleDataState> SelectAsync(string outputPath, CancellationToken cancellationToken);
    /// <summary>验证目录并读取实际统计; 文件错误不能伪装成空目录成功.</summary>
    Task<SampleDataState> InspectAsync(string outputPath, CancellationToken cancellationToken);
    /// <summary>在隔离目录生成并复核基线后发布; 提交开始后排空且不再响应取消.</summary>
    /// <param name="beforeCommit">可选提交前可靠回执钩子; 失败或取消不发布文件.</param>
    Task<SampleDataResponse> GenerateAsync(string outputPath, SampleDataOptions options, bool incremental, bool overwrite,
        IProgress<SampleDataProgress>? progress, Action? beginCommit, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null);
    /// <summary>删除固定 CSV 与整个 pbip; 保留输出根和其他数据文件, 提交前可取消.</summary>
    Task<SampleDataState> ClearAsync(string outputPath, Action? beginCommit, CancellationToken cancellationToken);
}

/// <summary>CLI 与桌面共用上游配置和生成管理流程; 原生宿主负责目录选择与明确确认.</summary>
public sealed class SampleDataService(ISampleDataAdapter adapter, ILogger<SampleDataService>? logger = null, ISampleDataCache? cache = null)
{
    private readonly ILogger logger = logger ?? NullLogger<SampleDataService>.Instance;

    /// <summary>绑定原生选择并读取轻量存在状态, 不将损坏 CSV 误报为空; 表统计另行严格读取.</summary>
    public Task<SampleDataState> GetSelectionAsync(string outputPath, CancellationToken cancellationToken = default) =>
        BusinessLog.RunAsync(logger, "SampleData.Select", () => adapter.SelectAsync(outputPath, cancellationToken));

    /// <summary>恢复上次由宿主保存的目录; 实际操作仍由适配器重新校验, 缓存故障不阻止启动.</summary>
    /// <returns>上次结果目录, 未缓存或缓存无法识别时返回 null.</returns>
    public string? RestoreOutputPath()
    {
        if (cache is null) return null;
        try { return cache.RestoreOutputPath(); }
        catch (Exception error)
        {
            using var operation = BusinessLog.Begin(logger, "SampleData.CacheRead");
            operation.Fail(error);
            return null;
        }
    }

    /// <summary>先验证宿主目录, 元数据一致时快速读取缓存, 变化时重新统计实际文件.</summary>
    public Task<SampleDataState> GetAsync(string outputPath, CancellationToken cancellationToken = default) =>
        BusinessLog.RunAsync(logger, "SampleData.Inspect", async () =>
        {
            if (cache is not null)
            {
                var selection = await adapter.SelectAsync(outputPath, cancellationToken).ConfigureAwait(false);
                var entry = await Task.Run(() => ReadCache(selection.OutputPath), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is { IsCurrent: true } && string.Equals(entry.State.OutputPath, selection.OutputPath, StringComparison.OrdinalIgnoreCase)
                    && entry.State.HasData == selection.HasData) return entry.State;
            }
            var state = await adapter.InspectAsync(outputPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await SaveCacheAsync(state).ConfigureAwait(false);
            return state;
        });

    /// <summary>全量生成十一表与 PBIP; overwrite 必须由 CLI 参数或原生确认明确授权.</summary>
    /// <param name="beforeCommit">实际发布前等待的可靠回执钩子; 失败或取消保留原目录.</param>
    public Task<SampleDataResponse> GenerateAsync(string outputPath, SampleDataOptions options, bool overwrite = false,
        IProgress<SampleDataProgress>? progress = null, Action? beginCommit = null, Func<CancellationToken, Task>? beforeCommit = null,
        CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(outputPath, options, false, overwrite, progress, beginCommit, cancellationToken, beforeCommit);

    /// <summary>复用维度追加事实, 重建已存在目标表并扩张日历; 冲突失败不修改目录.</summary>
    public Task<SampleDataResponse> IncrementAsync(string outputPath, SampleDataOptions options,
        IProgress<SampleDataProgress>? progress = null, Action? beginCommit = null, CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(outputPath, options, true, false, progress, beginCommit, cancellationToken);

    /// <summary>清空固定十一表与整个 PBIP 子目录, 调用前必须在宿主明确确认删除范围.</summary>
    public Task<SampleDataState> ClearAsync(string outputPath, Action? beginCommit = null, CancellationToken cancellationToken = default) =>
        BusinessLog.RunAsync(logger, "SampleData.Clear", async () =>
        {
            var state = await adapter.ClearAsync(outputPath, beginCommit, cancellationToken).ConfigureAwait(false);
            await SaveCacheAsync(state).ConfigureAwait(false);
            return state;
        });

    /// <summary>在业务边界校验配置, 透传提交前可等待钩子, 并将上游日期冲突转换为稳定错误.</summary>
    private Task<SampleDataResponse> GenerateCoreAsync(string outputPath, SampleDataOptions options, bool incremental, bool overwrite,
        IProgress<SampleDataProgress>? progress, Action? beginCommit, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) =>
        BusinessLog.RunAsync(logger, incremental ? "SampleData.Increment" : "SampleData.Generate", async () =>
        {
            if (options is null) throw new AppException("INPUT_INVALID", "缺少示例数据配置.");
            options.Validate();
            try
            {
                var response = await adapter.GenerateAsync(outputPath, options, incremental, overwrite, progress, beginCommit, cancellationToken, beforeCommit).ConfigureAwait(false);
                // 缓存属于派生显示结果, 不能把已发布文件或已清空目录改报失败.
                await SaveCacheAsync(response.State).ConfigureAwait(false);
                return response;
            }
            catch (SampleDateConflictException error)
            {
                throw new AppException("SAMPLE_DATE_CONFLICT", error.Message);
            }
        });

    /// <summary>安全读取当前目录的可选缓存, 不探测其他旧目录, 只记录脱敏故障并回退实际文件读取.</summary>
    /// <param name="outputPath">当前宿主经适配器规范化的输出目录.</param>
    /// <returns>可识别缓存, 未配置、缺失或故障时返回 null.</returns>
    private SampleDataCacheEntry? ReadCache(string outputPath)
    {
        if (cache is null) return null;
        try { return cache.Read(outputPath); }
        catch (Exception error)
        {
            using var operation = BusinessLog.Begin(logger, "SampleData.CacheRead");
            operation.Fail(error);
            return null;
        }
    }

    /// <summary>缓存文件探测和原子保存在线程池执行, 不占用宿主 UI 上下文.</summary>
    /// <param name="state">已经取得的权威统计.</param>
    /// <returns>完成最佳努力缓存保存的任务.</returns>
    private Task SaveCacheAsync(SampleDataState state) => cache is null ? Task.CompletedTask : Task.Run(() => SaveCache(state));

    /// <summary>最佳努力保存权威结果, 提交后忽略缓存故障且不复用业务取消令牌.</summary>
    /// <param name="state">已经完成实际文件读取或提交的结果.</param>
    private void SaveCache(SampleDataState state)
    {
        if (cache is null) return;
        try { cache.Save(state); }
        catch (Exception error)
        {
            using var operation = BusinessLog.Begin(logger, "SampleData.CacheSave");
            operation.Fail(error);
        }
    }
}
