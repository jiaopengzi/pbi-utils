// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\BridgeContractConsistencyTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 自动检测方法注册、C# 分发、TypeScript 映射与机器 schema 漂移.

using PbiUtils.Host.Bridge.Session;
using PbiUtils.Host.Bridge.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbiUtils.Host.Bridge;
using PbiUtils.Infrastructure.Gui;
using Xunit;

namespace PbiUtils.UnitTests;

public sealed partial class BridgeContractConsistencyTests
{
    /// <summary>新增或删除任意实际 handler 时, 其他三处契约必须同步且不能有重复注册.</summary>
    [Fact]
    public void RegistryDispatcherTypesAndSchemaAgree()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "pbi-utils.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "tests/contracts/bridge.schema.json")));
        // 可提交的 tests/contracts/bridge.schema.json 是权威契约, 用户忽略的私有文档副本不参与构建校验.
        var declared = BridgeMethods.All.Order().ToArray();
        Assert.Equal(declared.Length, declared.Distinct().Count());
        Assert.Equal(declared, schema.RootElement.GetProperty("properties").GetProperty("method").GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).Order().ToArray());
        var bridge = Path.Combine(root.FullName, "src/PbiUtils.Host/Bridge/Session");
        var dispatcherPath = Path.Combine(bridge, "BridgeSession.Dispatch.cs");
        var dispatcher = File.ReadAllText(dispatcherPath);
        AssertMethods(declared, DispatcherCases().Matches(dispatcher).Select(m => m.Groups[1].Value));
        // 中央路由与真正业务分发分别核验. 不能把两组 case 合并去重, 否则路由会掩盖 handler 遗漏.
        var handlers = Directory.EnumerateFiles(bridge, "BridgeSession.*.cs")
            .Where(path => path != dispatcherPath)
            .SelectMany(path => DispatcherCases().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .Append("request.cancel"); // 取消的真正实现仅在中央控制通道中.
        AssertMethods(declared, handlers);
        var types = File.ReadAllText(Path.Combine(root.FullName, "web/src/bridge/methods.ts"));
        var mapping = Interface(types, "Methods");
        Assert.Contains("extends ScaffoldMethods", mapping);
        // 新建类型化子契约仍逐项纳入, 不读取 re-export 或 DTO 中的其他字段.
        var scaffold = File.ReadAllText(Path.Combine(root.FullName, "web/src/features/scaffold/contracts.ts"));
        mapping += Interface(scaffold, "ScaffoldMethods");
        AssertMethods(declared, TypeScriptMethods().Matches(mapping).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        var rules = schema.RootElement.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(declared, rules.Select(r => r.GetProperty("if").GetProperty("properties").GetProperty("method").GetProperty("const").GetString()!).Order().ToArray());
        Assert.All(rules, r => Assert.False(r.GetProperty("then").GetProperty("properties").GetProperty("params").GetProperty("additionalProperties").GetBoolean()));
    }

    /// <summary>每一层实际声明必须完整且唯一, 不使用去重隐藏重复或缺失.</summary>
    /// <param name="declared">已排序的白名单全集.</param>
    /// <param name="methods">当前路由、业务分发或类型映射的实际声明.</param>
    private static void AssertMethods(string[] declared, IEnumerable<string> methods)
    {
        var actual = methods.Order().ToArray();
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.Equal(declared, actual);
    }

    /// <summary>只读取顶层接口本体, 不依赖下一个 DTO 名称或兼容导出的文件位置.</summary>
    /// <param name="source">使用仓库统一缩进的 TypeScript 源码.</param>
    /// <param name="name">固定接口名称.</param>
    /// <returns>包含声明和本体的接口文本; 声明或边界缺失即验收失败.</returns>
    private static string Interface(string source, string name)
    {
        var start = source.IndexOf("export interface " + name, StringComparison.Ordinal);
        Assert.True(start >= 0, $"缺少接口 {name}");
        var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"接口 {name} 没有完整本体");
        return source[start..(end + 2)];
    }
    /// <summary>提取实际 switch 的固定 handler 名称.</summary>
    [GeneratedRegex("case \"([^\"]+)\":")]
    private static partial Regex DispatcherCases();
    /// <summary>提取 TypeScript 方法映射的声明, 不匹配其他类型字段.</summary>
    [GeneratedRegex("^    (?:\"([^\"]+)\"|(doctor)): ", RegexOptions.Multiline)]
    private static partial Regex TypeScriptMethods();
}
