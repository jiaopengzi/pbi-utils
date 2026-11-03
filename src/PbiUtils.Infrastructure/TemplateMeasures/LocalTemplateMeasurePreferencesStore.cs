// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\TemplateMeasures\LocalTemplateMeasurePreferencesStore.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 严格保存新建模板度量的完整配置, 保留空集、损坏文件与并发修改.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Input;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;

using PbiUtils.Application.TemplateMeasures;

namespace PbiUtils.Infrastructure.TemplateMeasures;

/// <summary>用户的完整六字段配置及其文件基线, 不绑定任何模型写授权.</summary>
public sealed record TemplateMeasurePreferences(TemplateMeasureDefinition[] Measures, string Baseline, string? Warning = null);

/// <summary>宿主固定用户目录中的偏好存储, 锁内按完整内容基线更新且原子发布.</summary>
public sealed class LocalTemplateMeasurePreferencesStore(string root)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string path = Path.Combine(root, "template-measure-preferences.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    /// <summary>只读有界解析并严格验证当前类别, 损坏或未知版本保留原件并附警告.</summary>
    /// <returns>合法配置或受保护的默认回退; 读取不写文件, 基线对应原始完整字节.</returns>
    public TemplateMeasurePreferences Read()
    {
        UserDataStore.Check(path);
        if (!File.Exists(path)) return new(TemplateMeasureValidation.DefaultMeasures(), "missing");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumBytes + 1];
            var count = stream.ReadAtLeast(bytes, bytes.Length, false);
            if (count > MaximumBytes) throw new JsonException();
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            JsonInputValidation.RejectDuplicates(document.RootElement);
            var value = document.RootElement.Deserialize<Payload>(Json);
            if (value is null || value.SchemaVersion != 1 || value.Measures is null) throw new JsonException();
            Validate(value.Measures);
            return new(value.Measures, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count))));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or AppException)
        {
            return new(TemplateMeasureValidation.DefaultMeasures(), "invalid", "模板度量配置无法读取, 已使用默认配置; 原文件已保留且未覆盖。");
        }
    }

    /// <summary>保存完整列表, 空集表示用户删除全部项; 旧基线、损坏原文件及提交前取消拒绝写入.</summary>
    /// <param name="measures">严格验证的六字段列表, 不含目标或网页路径.</param>
    /// <param name="baseline">读取时的完整文件指纹.</param>
    /// <param name="token">原子发布前有效的取消令牌.</param>
    /// <param name="committing">原子写入前由宿主复核请求身份, 不代表模型提交.</param>
    /// <returns>已保存配置及新基线; 输入、并发及存储错误使用稳定应用错误.</returns>
    public TemplateMeasurePreferences Save(TemplateMeasureDefinition[] measures, string baseline, CancellationToken token, Action? committing = null)
    {
        Validate(measures);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, measures), Json);
        if (bytes.Length > MaximumBytes) throw new AppException("INVALID_TEMPLATE_MEASURE_PREFERENCES", "模板度量配置超出存储预算", 2);
        token.ThrowIfCancellationRequested();
        UserDataStore.Check(root);
        Directory.CreateDirectory(root);
        using var lease = UserDataStore.Lock(Path.Combine(root, "template-measure-preferences.lock"));
        var current = Read();
        if (current.Warning is not null) throw new AppException("INVALID_TEMPLATE_MEASURE_PREFERENCES", "模板度量配置无法安全更新, 请保留并检查原配置", 3);
        if (current.Baseline != baseline) throw new AppException("TEMPLATE_MEASURE_PREFERENCES_CHANGED", "模板度量配置已由其他窗口更新, 请关闭当前应用窗口后重试", 3);
        token.ThrowIfCancellationRequested();
        committing?.Invoke();
        UserDataStore.Write(path, bytes);
        return new([.. measures], Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>复用共享业务输入校验, 不为偏好另建较宽松的度量规则.</summary>
    private static void Validate(TemplateMeasureDefinition[] measures)
        => TemplateMeasureValidation.Validate(measures);

    private sealed record Payload(int SchemaVersion, TemplateMeasureDefinition[] Measures);
}
