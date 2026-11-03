// FilePath    : pbi-utils\src\PbiUtils.Application\Connections\RecentRecordDeletion.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义仅按宿主绑定身份删除最近记录的固定请求与结果.

using PbiUtils.Core;

namespace PbiUtils.Application.Connections;

/// <summary>删除最近记录的固定请求, 不接受文件路径、展示名称或模型句柄.</summary>
/// <param name="Id">宿主发布的最近记录身份.</param>
public sealed record RecentRecordDeletionRequest(string Id)
{
    /// <summary>校验本机历史使用的规范 SHA256 身份, 拒绝路径或任意文本.</summary>
    /// <param name="id">待校验的宿主记录身份.</param>
    /// <returns>通过校验的原身份.</returns>
    /// <exception cref="AppException">身份不是64位小写十六进制文本.</exception>
    public static string ValidateId(string id) => id is { Length: 64 } && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        ? id : throw new AppException("VALIDATION_ERROR", "最近记录标识无效, 请刷新列表后重试");
}

/// <summary>最近记录删除结果, 不存在或不属于当前账号分区时返回 false.</summary>
/// <param name="Deleted">是否已原子删除一条历史记录, 不代表删除实际模型文件.</param>
public sealed record RecentRecordDeletionResult(bool Deleted);
