// FilePath    : pbi-utils\src\PbiUtils.Windows\Models\Connected\ModelConnectionDiagnostics.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 格式化受限 SDK 异常链, 隐藏凭据、连接属性、账号、地址与敏感路径.

using PbiUtils.Application.Connections;
using System.Text.RegularExpressions;
using PbiUtils.Application;

namespace PbiUtils.Windows.Models.Connected;

/// <summary>只为模型连接边界生成可展示详情, 不保存原始异常或连接字符串.</summary>
public static class ModelConnectionDiagnostics
{
    /// <summary>保留最多六层异常的类型、错误码和脱敏原因, 不输出堆栈或任意异常数据.</summary>
    public static string Describe(Exception error, PowerBiServiceTarget? target = null)
    {
        List<string> entries = [];
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        for (Exception? current = error; current is not null && entries.Count < 6 && visited.Add(current); current = current.InnerException)
        {
            var message = Sanitize(current.Message, target);
            entries.Add($"{current.GetType().Name} 0x{current.HResult:X8}: {message}");
        }
        return string.Join(" → ", entries);
    }
    /// <summary>先移除完整已知凭据再截断, 避免截断后剩余半个令牌; 超长原文直接省略.</summary>
    public static string Sanitize(string text, PowerBiServiceTarget? target = null)
    {
        if (text.Length > 16384) return "SDK 详情过长, 已省略";
        if (target is not null)
        {
            foreach (var secret in new[] { target.AccessToken, target.AccountId, target.DatabaseId, target.Database, target.Server, target.IdentityProvider })
            {
                if (string.IsNullOrEmpty(secret)) continue;
                text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
                text = text.Replace(Uri.EscapeDataString(secret), "[已隐藏]", StringComparison.OrdinalIgnoreCase);
            }
        }
        var duration = TimeSpan.FromMilliseconds(100);
        try
        {
            // 连接串和 JSON 凭据属性支持双引号、单引号及未加引号的值.
            text = Regex.Replace(text, "(?i)\\b(password|pwd|access[_ -]?token|refresh[_ -]?token|token|client[_ -]?secret|authorization|data\\s+source|initial\\s+catalog|identity\\s+provider|user\\s+id|uid)\\s*[\"']?\\s*[:=]\\s*(\"(?:[^\"]|\"\")*\"|'(?:[^']|'')*'|[^;\\r\\n]+)", "$1=[已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s;,'""<>]+", "Bearer [已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(?:\.[A-Za-z0-9_-]*)?", "[JWT 已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"(?i)\b(?:https?|powerbi|pbiazure|asazure)://[^\s<>""']+", "[地址已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""'<>;]*", "[路径已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"\b[^\s<>""'@]+@[^\s<>""'@]+\.[A-Za-z]{2,}\b", "[账号已隐藏]", RegexOptions.CultureInvariant, duration);
            text = Regex.Replace(text, @"[\r\n\t\p{Cc}]+", " ", RegexOptions.CultureInvariant, duration).Trim();
            return text.Length > 1200 ? text[..1200] + "…" : text;
        }
        catch (RegexMatchTimeoutException) { return "SDK 详情格式复杂, 已省略"; }
    }
}
