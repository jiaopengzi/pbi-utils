// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Editing\DesktopUdfCompatibility.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 按实际模型级别核验 Desktop UDF 环境, 并保护本轮冻结配置的独立升级授权.

using PbiUtils.Application.Models.Editing;
using PbiUtils.Application;
using PbiUtils.Core;

namespace PbiUtils.Windows.Models.Editing;

/// <summary>先核实际模型级别, 低级别再核 Desktop GA 版本和实际升级能力; 独立写入确认保持分离.</summary>
public static class DesktopUdfCompatibility
{
    /// <summary>解析受信原 Desktop 主模块的 ProductVersion, 不将固定数值资源字段或 FileVersion 当作替代来源.</summary>
    /// <param name="productVersion">宿主从已绑定真实 Desktop 进程取得的 ProductVersion, 不是网页或用户输入.</param>
    /// <returns>可证明的四段数值版本; 未知、超长、损坏或不识别的格式为 null, 不抛出解析错误.</returns>
    public static Version? ParseDesktopProductVersion(string? productVersion)
    {
        if (string.IsNullOrEmpty(productVersion) || productVersion.Length > 256) return null;
        var tokenEnd = 0;
        var dots = 0;
        while (tokenEnd < productVersion.Length && (char.IsAsciiDigit(productVersion[tokenEnd]) || productVersion[tokenEnd] == '.'))
        {
            if (productVersion[tokenEnd] == '.') dots++;
            tokenEnd++;
        }
        if (dots != 3 || !Version.TryParse(productVersion.AsSpan(0, tokenEnd), out var version) || version.Major == 0) return null;
        var suffix = productVersion.AsSpan(tokenEnd);
        if (suffix.IsEmpty) return version;
        // Store ProductVersion 可为四段数值加 " (YY.MM)+commit"; 固定数值 ProductParts 实际可能全为零.
        if (suffix.StartsWith(" (", StringComparison.Ordinal))
        {
            if (suffix.Length < 9 || !char.IsAsciiDigit(suffix[2]) || !char.IsAsciiDigit(suffix[3]) || suffix[4] != '.'
                || !(suffix[5] == '0' && suffix[6] is >= '1' and <= '9' || suffix[5] == '1' && suffix[6] is >= '0' and <= '2')
                || suffix[7] != ')') return null;
            suffix = suffix[8..];
        }
        if (suffix.Length is < 8 or > 65 || suffix[0] != '+') return null;
        foreach (var character in suffix[1..])
            if (!char.IsAsciiHexDigit(character)) return null;
        return version;
    }

    /// <summary>核验 UDF 保存环境, 版本和能力读取均由受信的原 Desktop 进程及连接提供.</summary>
    /// <param name="currentLevel">本次编辑前原模型实际兼容级别, 不使用本地暂存升级后的级别.</param>
    /// <param name="readDesktopVersion">仅在低级别模型时读取绑定 Desktop 进程的产品版本, 未知为 null.</param>
    /// <param name="readSupportedLevels">仅在低级别模型且版本满足时读取当前引擎实际支持集合.</param>
    /// <exception cref="AppException">低级别模型的 Desktop 版本不足、未知或引擎不支持实际升级时拒绝.</exception>
    public static void RequireSupportedEnvironment(int currentLevel, Func<Version?> readDesktopVersion,
        Func<IReadOnlyCollection<int>> readSupportedLevels)
    {
        if (currentLevel >= 1702) return;
        var version = readDesktopVersion()
            ?? throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "无法核实当前 Power BI Desktop 产品版本, 请重新连接后再试; 未修改模型", 3);
        // Microsoft Learn 的更新归档将 June 2026 GA 对应为 2.155.756.0; 不比较字符串或使用安装器版本.
        if (version < new Version(2, 155, 756, 0))
            throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "当前 Power BI Desktop 早于 2026 年 6 月 UDF 正式版, 请更新后再试; 未修改模型", 3);
        RequireSupportedLevel(readSupportedLevels(), 1702);
    }

    /// <summary>仅实际服务器声明包含精确目标级别时允许 UDF 写入, 不把更大的其他级别视为支持.</summary>
    /// <param name="supportedLevels">TOM Server.GetSupportedCompatibilityLevels 返回的实际支持集合.</param>
    /// <param name="compatibilityLevel">本次仅允许核验的 UDF 最低级别 1702.</param>
    /// <exception cref="AppException">目标不是 1702 或引擎未声明支持时拒绝, 不修改模型.</exception>
    public static void RequireSupportedLevel(IReadOnlyCollection<int> supportedLevels, int compatibilityLevel)
    {
        if (compatibilityLevel != 1702 || !supportedLevels.Contains(compatibilityLevel))
            throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "当前 Desktop 引擎未声明支持兼容级别 1702, 未修改模型", 3);
    }

    /// <summary>只允许本次冻结 UDF 配置已独立同意的低级别升至 1702, 无变化时不扩大授权.</summary>
    /// <param name="currentLevel">提交前权威基线的兼容级别.</param>
    /// <param name="candidateLevel">原连接对象本地暂存后的最终兼容级别.</param>
    /// <param name="edits">当前草稿实际持有的编辑和冻结配置计划, 不接受网页单独授权字段.</param>
    /// <returns>本次是否需要并获准进行兼容级别升级.</returns>
    /// <exception cref="AppException">降级、任意其他级别或没有实际 UDF 和独立批准时拒绝.</exception>
    public static bool GetUpgradeApproval(int currentLevel, int candidateLevel, IReadOnlyList<ModelEditRequest> edits)
    {
        if (currentLevel == candidateLevel) return false;
        if (currentLevel < 1702 && candidateLevel == 1702 && edits.Any(edit => edit.Operation == "configuration.apply"
            && edit.Configuration is { Request.AllowCompatibilityUpgrade: true, Udfs.Functions.Count: > 0 })) return true;
        throw new AppException("UDF_MODEL_WRITE_UNSUPPORTED", "本次兼容级别变更未获独立批准或超出 UDF 升级范围, 未修改模型", 3);
    }

    /// <summary>识别本次实际函数新增、修改、删除或配置安装, 由原模型级别决定保存前需要的环境核验.</summary>
    /// <param name="edits">当前私有草稿实际编辑集合.</param>
    /// <returns>是否包含需要按原模型级别选择 UDF 环境核验的保存操作.</returns>
    public static bool HasUdfWrites(IReadOnlyList<ModelEditRequest> edits) => edits.Any(edit => edit.Operation is "udf.set" or "udf.deleteUnused"
        || edit.Operation == "configuration.apply" && edit.Configuration is { Udfs.Functions.Count: > 0 });
}
