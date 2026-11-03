// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\NativeDiscoveryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在本机验证生成的 Windows 进程与 TCP 表导入, 不访问或修改 Power BI 模型.

using PbiUtils.Windows.Desktop.Discovery;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>直接验证原生结构布局和返回值, 避免没有 Power BI 时的空结果掩盖问题.</summary>
public sealed class NativeDiscoveryTests
{
    /// <summary>系统进程快照包含本测试进程的完整名称, TCP 表包含本测试的回环监听.</summary>
    [Fact]
    public void GeneratedImportsReadOwnProcessAndLoopbackListener()
    {
        using var process = Process.GetCurrentProcess();
        var processes = Read("Processes");
        var own = Assert.Single(processes, item => Value<int>(item, "Id") == process.Id);
        Assert.Equal(process.ProcessName, Path.GetFileNameWithoutExtension(Value<string>(own, "Name")), ignoreCase: true);
        Assert.DoesNotContain('\0', Value<string>(own, "Name"));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(Read("Ports"), item => Value<int>(item, "ProcessId") == process.Id && Value<int>(item, "Port") == port);
    }

    /// <summary>反射调用内部只读原生入口, 返回系统快照以验证真实导入.</summary>
    /// <param name="method">固定的 Processes 或 Ports 方法名.</param>
    private static object[] Read(string method)
    {
        var type = typeof(LocalPowerBiReader).Assembly.GetType("PbiUtils.Windows.Desktop.Discovery.NativeDiscovery", throwOnError: true)!;
        return [.. ((IEnumerable)type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).Cast<object>()];
    }

    /// <summary>读取内部快照的强类型属性, 不将系统进程信息写入日志.</summary>
    /// <param name="item">原生快照中的一条记录.</param>
    /// <param name="name">固定属性名.</param>
    private static T Value<T>(object item, string name) => (T)item.GetType().GetProperty(name)!.GetValue(item)!;
}
