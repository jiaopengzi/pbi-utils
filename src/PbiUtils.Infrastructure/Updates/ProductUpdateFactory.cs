// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Updates\ProductUpdateFactory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 组合共享更新服务与进程生命周期匿名 HTTP 资源.

using PbiUtils.Application.Updates;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Updates;

/// <summary>复用唯一进程生命周期客户端, 服务本身无需释放, 不逐次创建连接池.</summary>
public static class ProductUpdateFactory
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 32,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>在宿主提供的用户数据根目录创建独立服务; 实际 HTTP 有20秒总预算且无自动重定向.</summary>
    public static ProductUpdateService Create(string userData) => new(new LocalProductUpdateStore(userData), new ProductReleaseHttpSource(Client), Product.Version);
}
