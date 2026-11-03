// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopSyncCallOrderTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 约束真实保存异步状态机的焦点、待应用查询、完整备份与保存阶段事实顺序.

using PbiUtils.Windows.Models.Connected;
using PbiUtils.Windows.Desktop.Files;
using PbiUtils.Windows.Desktop;
using PbiUtils.Application.Models.Editing;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using PbiUtils.Application;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>检查实际编译调用顺序, 防止单独的窗口协议测试通过但保存链路仍在提交后才建立保护.</summary>
public sealed class DesktopSyncCallOrderTests
{
    private static readonly Dictionary<ushort, OpCode> Instructions = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    /// <summary>TOM 提交在线程池运行之前必须建立作用域, 避免 Desktop 已取得前台后保护被跳过.</summary>
    [Fact]
    public void DesktopSubmissionBeginsFocusBeforeSchedulingTomCommit() =>
        AssertBefore("SaveDesktopAsync", "BeginDesktopSyncAsync", typeof(Task), "Run");

    /// <summary>离线文件提交与其后重载共用同一提前建立的作用域, 不仅保护已提交后的尾部.</summary>
    [Fact]
    public void PbipSubmissionBeginsFocusBeforeSavingDraft() =>
        AssertBefore("SavePbipAsync", "BeginDesktopSyncAsync", typeof(IModelDraft), "SaveAsync");

    /// <summary>只补保存复用原票据, 从首次权威回读直到尾部完成均处于同一保护周期.</summary>
    [Fact]
    public void PendingSaveBeginsFocusBeforeReadback() =>
        AssertBefore("CompleteSaveAsync", "BeginDesktopSyncAsync", typeof(ConnectedModelReader), "ReadAsync");

    /// <summary>已打开 PBIP 的准备在返回同步票据前核挂起查询, 不绕过文件入口的既有保护.</summary>
    [Fact]
    public void PbipPreparationChecksPendingBeforeReturningTicket()
    {
        var calls = AsyncCalls(typeof(DesktopFileSync), "PreparePbipAsync");
        var guard = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopPendingQueries) && call.Name == "RequireNoneAsync");
        var ticket = Array.FindLastIndex(calls, call => call.DeclaringType == typeof(DesktopSyncTicket) && call.IsConstructor);
        Assert.True(guard >= 0 && ticket > guard);
    }

    /// <summary>共享文件草稿成功返回才记录文件已保存, 再进入可能失败的 Desktop 同步尾部.</summary>
    [Fact]
    public void PbipSubmissionRecordsFileSaveBeforeDesktopTail()
    {
        var calls = AsyncCalls(typeof(ConnectedModelReader), "SavePbipAsync");
        var save = Array.FindIndex(calls, call => call.DeclaringType == typeof(IModelDraft) && call.Name == "SaveAsync");
        var fact = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopSyncTicket) && call.Name == "set_FileSaved");
        var tail = Array.FindIndex(calls, call => call.Name == "FinishFileSaveAsync");
        Assert.True(save >= 0 && fact > save && tail > fact);
    }

    /// <summary>尾部在保存返回后记录已证明阶段, 再进行最终挂起检查; 后来的挂起不能抹去文件保存事实.</summary>
    [Fact]
    public void TailChecksPendingBeforeReloadAndAfterProvenFileSave()
    {
        var calls = AsyncCalls(typeof(DesktopFileSync), "CompleteAsync");
        var first = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopPendingQueries) && call.Name == "RequireNoneAsync");
        var reload = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopBridgeClient) && call.Name == "ReloadAsync");
        var save = Array.FindIndex(calls, call => call.Name == "SaveFileAsync");
        var fact = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopSyncTicket) && call.Name == "set_FileSaved");
        var last = Array.FindLastIndex(calls, call => call.DeclaringType == typeof(DesktopPendingQueries) && call.Name == "RequireNoneAsync");
        Assert.True(first >= 0 && reload > first && save > reload && fact > save && last > fact);
    }

    /// <summary>每次控件保存前再次核待应用文件, 不是在弹窗或查询冲突后继续盲目点击.</summary>
    [Fact]
    public void SaveInvocationRequiresPendingCheck()
    {
        var calls = AsyncCalls(typeof(DesktopFileSync), "SaveFileAsync");
        var guard = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopPendingQueries) && call.Name == "RequireNoneAsync");
        var invoke = Array.FindIndex(calls, call => call.DeclaringType == typeof(DesktopSaveControlLookup) && call.Name == "InvokeOnceAsync");
        Assert.True(guard >= 0 && invoke > guard);
    }

    /// <summary>比较真实异步状态机中的调用位置, 未找到调用同样失败.</summary>
    /// <param name="method">实际业务方法名.</param>
    /// <param name="before">必须先执行的作用域建立方法.</param>
    /// <param name="afterType">可能导致 Desktop 激活的实际调用类型.</param>
    /// <param name="after">需要被覆盖的实际调用名.</param>
    private static void AssertBefore(string method, string before, Type afterType, string after)
    {
        var calls = AsyncCalls(typeof(ConnectedModelReader), method);
        var start = Array.FindIndex(calls, call => call.DeclaringType == typeof(ConnectedModelReader) && call.Name == before);
        var work = Array.FindIndex(calls, call => call.DeclaringType == afterType && call.Name == after);
        Assert.True(start >= 0 && work > start, $"{method} 必须先建立焦点作用域再执行 {afterType.Name}.{after}");
    }

    /// <summary>读取指定真实异步业务方法的调用, 检查其保存前置和阶段记录顺序.</summary>
    /// <param name="type">拥有实际保存方法的产品类型.</param>
    /// <param name="method">要读取的产品异步方法名.</param>
    /// <returns>真实状态机中按 IL 顺序出现的方法引用.</returns>
    private static MethodBase[] AsyncCalls(Type type, string method)
    {
        var source = type.GetMethod(method, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var state = source.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var move = state.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return [.. Calls(move)];
    }

    /// <summary>按 IL 操作码边界读取真实调用, 不把常量或分支数据误识别为方法令牌.</summary>
    /// <param name="method">要读取的编译后状态机方法.</param>
    /// <returns>按实际调用顺序解析的方法引用.</returns>
    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var body = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var position = 0; position < body.Length;)
        {
            ushort code = body[position++];
            if (code == 0xfe) code = (ushort)(0xfe00 | body[position++]);
            var instruction = Instructions[code];
            var size = instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(body, position),
                _ => 4
            };
            if (instruction.OperandType == OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(body, position), method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments())!;
            position += size;
        }
    }
}
