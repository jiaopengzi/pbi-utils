// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\ScaffoldRuntime.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 组合桌面和 CLI 共用的脚手架环境, 构造阶段不访问磁盘或网络.

using Microsoft.Extensions.Logging;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Application.Udf;
using PbiUtils.Infrastructure.Udf;

using PbiUtils.Infrastructure.Models.Configuration;
using PbiUtils.Infrastructure.Scaffolding.Templates;
using PbiUtils.Infrastructure.Udf.Sources;

namespace PbiUtils.Infrastructure.Scaffolding;

/// <summary>拥有 HTTP 客户端与共享用例, 宿主必须先排空调用再释放.</summary>
public sealed class ScaffoldRuntime : IDisposable
{
    public ScaffoldTemplateCatalog Catalog { get; }
    public IUdfCatalogProvider Packages { get; }
    public UdfSourceProvider Sources { get; }
    public LocalUdfStore Local { get; }
    public UdfDependencyResolver Resolver { get; }
    public ModelConfigurationCompiler Configuration { get; }
    public ScaffoldService Service { get; }

    /// <summary>使用固定用户根组合受限适配器, 测试可传独占临时根及受控 HTTP 传输; 默认仍由来源提供方构造安全网络客户端.</summary>
    /// <param name="userRoot">宿主指定的应用存储根, 不来自工具参数.</param>
    /// <param name="logs">宿主日志工厂.</param>
    /// <param name="clientFactory">可选来源传输工厂, 每次刷新拥有并释放其返回的客户端; 不替代目录解析、校验或提交.</param>
    public ScaffoldRuntime(string userRoot, ILoggerFactory? logs = null, Func<UdfSourceEntry, HttpClient>? clientFactory = null)
    {
        Catalog = new(userRoot);
        Sources = new(userRoot, clientFactory);
        Packages = Sources.DefaultProvider;
        Local = new(userRoot);
        Resolver = new(Packages, Local, Sources);
        Configuration = new(Resolver);
        Service = new(new ScaffoldWorkspace(userRoot, Catalog, new ScaffoldModelWriter(Resolver), localUdfs: Local, udfSources: Sources.Sources), logs?.CreateLogger<ScaffoldService>());
    }

    /// <summary>业务结束后释放拥有的客户端, 不丢弃供 CLI 下个进程消费的计划.</summary>
    public void Dispose() { Service.Dispose(); Configuration.Dispose(); Sources.Dispose(); }
}
