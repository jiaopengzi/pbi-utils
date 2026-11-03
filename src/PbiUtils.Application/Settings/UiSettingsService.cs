// FilePath    : pbi-utils\src\PbiUtils.Application\Settings\UiSettingsService.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 统一界面语言、最近文件显示及 DAX 格式偏好, 仅保存独立的界面设置.

using PbiUtils.Application.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PbiUtils.Core;

namespace PbiUtils.Application.Settings;

/// <summary>宿主权威界面偏好, 默认中文、显示最近文件及 Long 布局, 模拟状态不代表实际保存.</summary>
public sealed record UiSettings(string Locale = "zh-cn", bool ShowRecentFiles = true, string DaxFormatStyle = "long", string? Warning = null, bool IsSimulation = false);

/// <summary>本次用户实际修改的界面偏好, null 表示保留锁内最新值; 至少包含一项修改.</summary>
public sealed record UiSettingsPatch(string? Locale = null, bool? ShowRecentFiles = null, string? DaxFormatStyle = null);

/// <summary>界面偏好存储边界, 不接受模型权限、文件路径或网络地址.</summary>
public interface IUiSettingsStore
{
    /// <summary>返回已保存界面偏好或带警告的默认值; 不自动改写无法识别的配置.</summary>
    UiSettings Read();

    /// <summary>锁内合并实际修改字段与最新偏好, 提交前取消保留原值, 提交后返回实际保存结果.</summary>
    /// <param name="patch">至少包含一个白名单字段的修改, 未提供字段保持最新保存值.</param>
    /// <param name="token">提交前取消令牌.</param>
    /// <param name="committing">宿主进入不可取消提交阶段的回调.</param>
    /// <returns>已持久化的界面偏好.</returns>
    UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null);
}

/// <summary>界面及示例共用的界面设置, 不改变模型写入或 工具 写授权.</summary>
public sealed class UiSettingsService(IUiSettingsStore store, ILogger<UiSettingsService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<UiSettingsService>.Instance;
    public static IReadOnlyList<string> Locales { get; } = Array.AsReadOnly(["zh-cn", "en-us"]);

    /// <summary>读取宿主偏好, 保留存储返回的警告和模拟标识.</summary>
    /// <returns>当前语言偏好.</returns>
    public UiSettings Read() => BusinessLog.Run(log, "Ui.Settings", store.Read);

    /// <summary>先校验再保存, 非法语言、DAX 布局或提交前取消不改变已有偏好.</summary>
    /// <param name="patch">本次实际修改字段, 不携带其他字段的旧快照.</param>
    /// <param name="token">提交前取消令牌.</param>
    /// <param name="committing">宿主进入不可取消提交阶段的回调.</param>
    /// <returns>实际保存后的偏好.</returns>
    /// <exception cref="AppException">语言或 DAX 布局不受支持, 或存储无法安全更新.</exception>
    public UiSettings Save(UiSettingsPatch patch, CancellationToken token, Action? committing = null) => BusinessLog.Run(log, "Ui.SettingsSave", () =>
    {
        ValidatePatch(patch);
        token.ThrowIfCancellationRequested();
        return store.Save(patch, token, committing);
    });

    /// <summary>校验非空修改集合及其提供的字段, 未提供字段由存储在锁内合并.</summary>
    /// <param name="patch">类型化界面偏好修改.</param>
    /// <exception cref="AppException">修改为空, 或语言和 DAX 布局不受支持.</exception>
    public static void ValidatePatch(UiSettingsPatch patch)
    {
        if (patch.Locale is null && patch.ShowRecentFiles is null && patch.DaxFormatStyle is null)
            throw new AppException("VALIDATION_ERROR", "界面设置至少修改一个有效字段");
        if (patch.Locale is not null) ValidateLocale(patch.Locale);
        if (patch.DaxFormatStyle is not null) ValidateDaxFormatStyle(patch.DaxFormatStyle);
    }

    /// <summary>拒绝未知语言、大小写别名和空白, 与桥接白名单一致.</summary>
    /// <param name="locale">待保存或读取的语言标识.</param>
    /// <exception cref="AppException">标识不在支持列表中.</exception>
    public static void ValidateLocale(string locale)
    {
        if (!Locales.Contains(locale, StringComparer.Ordinal)) throw new AppException("INVALID_UI_LOCALE", "请选择中文或英文");
    }

    /// <summary>拒绝未知 DAX 布局, 不以默认值掩盖非法参数.</summary>
    /// <param name="daxFormatStyle">精确为 long 或 short 的布局标识.</param>
    /// <exception cref="AppException">布局不受支持.</exception>
    public static void ValidateDaxFormatStyle(string daxFormatStyle)
    {
        if (daxFormatStyle is not ("long" or "short")) throw new AppException("INVALID_DAX_FORMAT_STYLE", "DAX 格式必须为 short 或 long");
    }
}
