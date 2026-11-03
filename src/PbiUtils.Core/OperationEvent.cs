// FilePath    : pbi-utils\src\PbiUtils.Core\OperationEvent.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 定义宿主业务操作共用的有序进度和唯一终态事件契约.

namespace PbiUtils.Core;

/// <summary>绑定请求身份、连续序号和时间戳的业务执行事件, 不改变桥接消息结构.</summary>
/// <param name="RequestId">宿主当前请求身份.</param>
/// <param name="Sequence">当前请求内递增的事件序号.</param>
/// <param name="Type">开始、进度或唯一完成/失败/取消终态.</param>
/// <param name="Timestamp">实际事件时间的 ISO 8601 文本.</param>
/// <param name="Payload">调用方已经校验并允许向界面展示的业务数据.</param>
public sealed record OperationEvent(string RequestId, int Sequence, string Type, string Timestamp, object Payload);
