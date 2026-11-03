// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\FrontendUiBoundaryTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 从实际程序集检查 Vue 界面与薄 WinForms 宿主边界, 阻止新增原生业务控件和弹窗.

using PbiUtils.Desktop.Shell;
using System.Reflection;
using System.Reflection.Emit;
using PbiUtils.Desktop;
using Xunit;

namespace PbiUtils.Desktop.Tests;

/// <summary>检查编译后的类型与实际调用, 不依赖源码排版, 类型别名或目标类型推断写法.</summary>
public sealed class FrontendUiBoundaryTests
{
    private static readonly Dictionary<ushort, OpCode> instructions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    /// <summary>桌面程序集仅允许主窗口 WebView 外壳, 不允许原生业务控件.</summary>
    [Fact]
    public void OnlyMainShellIsANativeControl()
    {
        var controls = typeof(MainForm).Assembly.GetTypes()
            .Where(type => DerivesFrom(type, "System.Windows.Forms.Control"))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([typeof(MainForm)], controls);
    }

    /// <summary>实际 IL 只允许主外壳, WebView2 和系统文件或目录选择, 不启动任何窗口.</summary>
    [Fact]
    public void HostDoesNotConstructNativeBusinessUiOrShowNativeMessages()
    {
        var failures = new List<string>();
        foreach (var type in typeof(MainForm).Assembly.GetTypes())
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            foreach (var caller in methods)
                foreach (var (instruction, target) in Calls(caller))
                    if (IsNativeBusinessUi(caller, instruction, target))
                        failures.Add($"{type.FullName}.{caller.Name} -> {target.DeclaringType?.FullName}.{target.Name}");
        }
        Assert.True(failures.Count == 0, "应用交互必须使用 Vue UI; 检测到原生业务 UI 调用:\n" + string.Join('\n', failures.Distinct().Order(StringComparer.Ordinal)));
    }

    /// <summary>只放行主外壳基类构造和宿主操作, 系统选择器仍由宿主绑定文件或目录权限.</summary>
    /// <param name="caller">执行调用的实际方法, 含编译器生成状态机.</param>
    /// <param name="instruction">区分基类构造调用与额外创建 Form.</param>
    /// <param name="target">IL 引用的实际方法或构造函数.</param>
    /// <returns>若会引入原生业务界面则返回 true.</returns>
    private static bool IsNativeBusinessUi(MethodBase caller, OpCode instruction, MethodBase target)
    {
        var owner = target.DeclaringType;
        if (owner?.FullName is "System.Windows.Forms.MessageBox" or "System.Windows.Forms.TaskDialog") return true;
        if (DerivesFrom(owner, "System.Windows.Forms.CommonDialog"))
            return owner?.FullName is not ("System.Windows.Forms.OpenFileDialog" or "System.Windows.Forms.SaveFileDialog" or "System.Windows.Forms.FolderBrowserDialog"
                or "System.Windows.Forms.FileDialog" or "System.Windows.Forms.CommonDialog");
        if (!DerivesFrom(owner, "System.Windows.Forms.Control") && !DerivesFrom(owner, "System.Windows.Forms.ToolStripItem")) return false;
        if (owner?.FullName == "Microsoft.Web.WebView2.WinForms.WebView2") return false;
        if (owner?.FullName == "System.Windows.Forms.Form" && target.IsConstructor)
            return instruction != OpCodes.Call || !IsShell(caller.DeclaringType) || !caller.IsConstructor;
        if (owner?.FullName is "System.Windows.Forms.Form" or "System.Windows.Forms.Control")
        {
            for (var scope = caller.DeclaringType; scope is not null; scope = scope.DeclaringType)
                if (IsShell(scope)) return false;
            return true;
        }
        return !IsShell(owner);
    }

    /// <summary>精确允许仅承载 WebView 的主外壳, 不以 Form 派生关系放宽其他控件或消息框.</summary>
    private static bool IsShell(Type? type) => type == typeof(MainForm);

    /// <summary>沿真实继承链比较框架类型, 无需为门禁额外声明 Windows Forms 框架引用.</summary>
    /// <param name="type">要检查的类型, 允许没有声明类型的元数据.</param>
    /// <param name="name">框架类型的完整名称.</param>
    /// <returns>当前类型或其基类匹配时返回 true.</returns>
    private static bool DerivesFrom(Type? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.FullName == name) return true;
        return false;
    }

    /// <summary>按操作码边界读取实际调用, 跳过常量和分支数据, 泛型上下文交由模块解析.</summary>
    /// <param name="caller">桌面程序集内的方法或构造函数.</param>
    /// <returns>实际引用的方法与相应操作码; 无方法体的声明不产生调用.</returns>
    private static IEnumerable<(OpCode Instruction, MethodBase Target)> Calls(MethodBase caller)
    {
        var body = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var position = 0; position < body.Length;)
        {
            ushort code = body[position++];
            if (code == 0xfe) code = (ushort)(0xfe00 | body[position++]);
            Assert.True(instructions.TryGetValue(code, out var instruction), $"未知 IL 操作码: {code}");
            var size = instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(body, position),
                _ => 4
            };
            Assert.True(position + size <= body.Length, "IL 参数越过方法体边界");
            if (instruction.OperandType == OperandType.InlineMethod)
            {
                var target = caller.Module.ResolveMethod(BitConverter.ToInt32(body, position), caller.DeclaringType?.GetGenericArguments(),
                    caller.IsGenericMethod ? caller.GetGenericArguments() : null);
                Assert.NotNull(target);
                yield return (instruction, target);
            }
            position += size;
        }
    }
}
