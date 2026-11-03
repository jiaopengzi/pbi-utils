// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\UdfCleanupNotice.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 在协议边界为 UDF 临时目录清理失败附加固定安全提示, 不泄露异常正文或路径.

namespace PbiUtils.Infrastructure.Udf;

/// <summary>只传递清理未完成这一事实, 不改变首因、异常类型、错误码或退出码.</summary>
public static class UdfCleanupNotice
{
    private const string Marker = "UdfTemporaryCleanupFailed";
    private const string Notice = "本轮临时目录清理失败, 可能仍有临时文件.";

    /// <summary>业务所有者确认次要清理失败后标记原异常, 由 CLI/桥接的安全错误消息决定展示.</summary>
    internal static void Mark(Exception failure, bool messageIncludesNotice = false) => failure.Data[Marker] = new MarkerValue(messageIncludesNotice);

    /// <summary>用稳定超时异常替换主异常时转移清理事实, 不复制原始 Data 或异常正文.</summary>
    internal static void Transfer(Exception source, Exception target)
    {
        for (var current = source; current is not null; current = current.InnerException)
            if (current.Data[Marker] is MarkerValue)
            { Mark(target); return; }
    }

    /// <summary>对已经去敏的主错误消息附加固定提示; 不读取任何原始异常正文或自定义 Data 值.</summary>
    public static string Decorate(Exception? failure, string safeMessage)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current.Data[Marker] is MarkerValue marker)
            {
                // 已经包含安全清理说明的 AppException 直接保留; 取消/超时改写主文案时仍重新附加提示.
                if (marker.MessageIncludesNotice && current == failure && failure is PbiUtils.Core.AppException app && safeMessage == app.Message) return safeMessage;
                return safeMessage.EndsWith(Notice, StringComparison.Ordinal) ? safeMessage : safeMessage + "; " + Notice;
            }
        }
        return safeMessage;
    }

    /// <summary>类型化内部标记不包含异常正文、路径或其他业务值.</summary>
    private sealed record MarkerValue(bool MessageIncludesNotice);
}
