// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Hosting\BusinessHostPeer.Receive.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 共享连接的唯一读取循环与严格请求相关, 业务等待不阻塞取消或回调答复.

using System.Text.Json;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Hosting;

public sealed partial class BusinessHostPeer
{
    /// <summary>只读取和投递, 不等待业务处理; 控制答复可穿过正在等待用户的业务请求.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await BusinessHostProtocol.ReadAsync<BusinessHostFrame>(stream, lifetime.Token).ConfigureAwait(false);
                if (frame.ContextId != ContextId || frame.HostGeneration != HostGeneration) throw BusinessHostProtocol.Invalid();
                switch (frame.Type)
                {
                    case "call":
                        ValidateCall(frame);
                        await ReceiveCallAsync(frame).ConfigureAwait(false);
                        break;
                    case "result":
                        if (!BusinessHostProtocol.ValidId(frame.Id) || frame.Method is not null || (frame.Data is null) == (frame.Error is null)) throw BusinessHostProtocol.Invalid();
                        if (!pending.TryRemove(frame.Id!, out var completion)) throw BusinessHostProtocol.Invalid();
                        if (frame.Error is { } error) completion.TrySetException(new AppException(error.Code, error.Message, error.ExitCode));
                        else completion.TrySetResult(frame.Data!.Value.Clone());
                        break;
                    case "cancel":
                        if (!BusinessHostProtocol.ValidId(frame.Id) || frame.Method is not null || frame.Data is not null || frame.Error is not null) throw BusinessHostProtocol.Invalid();
                        if (incoming.TryGetValue(frame.Id!, out var target)) target.Cancel();
                        break;
                    case "event":
                        if (frame.Id is not null || frame.Error is not null || frame.Data is null || frame.Method is null) throw BusinessHostProtocol.Invalid();
                        RequireMethod(frame.Method, frame.Data.Value);
                        ReceiveEvent(frame);
                        break;
                    case "detach":
                        RequireEmpty(frame);
                        lock (lifecycle) { if (receivedDetach) throw BusinessHostProtocol.Invalid(); receivedDetach = true; }
                        RequestShutdown();
                        break;
                    case "detached":
                        if (frame.Id is not null || frame.Method is not null || frame.Data is not null) throw BusinessHostProtocol.Invalid();
                        lock (lifecycle) { receivedDetachResult = true; accepting = false; }
                        if (frame.Error is { } failure) detached.TrySetException(new AppException(failure.Code, failure.Message, failure.ExitCode));
                        else detached.TrySetResult();
                        // 该控制帧是对端排空后的最后终态, 无需再发送 detach 或等下一次 EOF 才结束 reader.
                        return;
                    default: throw BusinessHostProtocol.Invalid();
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or AppException or JsonException)
        { /* 传输失败统一完成待调用者, 不把故障当成业务未提交或重放许可. */ }
        finally { FailTransport(); }
    }

    /// <summary>固定请求字段及完整相关身份, 不允许数据帧携带额外响应或错误信息.</summary>
    private static void ValidateCall(BusinessHostFrame frame)
    {
        if (!BusinessHostProtocol.ValidId(frame.Id) || frame.Method is null || frame.Data is null || frame.Error is not null) throw BusinessHostProtocol.Invalid();
        RequireMethod(frame.Method, frame.Data.Value);
    }

    /// <summary>注册后异步执行, 在任何业务代码开始前固定取消和完成所有权.</summary>
    private async Task ReceiveCallAsync(BusinessHostFrame frame)
    {
        var request = new Incoming();
        bool rejected;
        lock (lifecycle)
        {
            rejected = !accepting;
            if (!rejected && (incoming.Count >= MaximumPending || !incoming.TryAdd(frame.Id!, request)))
            {
                request.Dispose();
                throw BusinessHostProtocol.Invalid();
            }
        }
        if (rejected)
        {
            request.Dispose();
            await SendAsync(new("result", HostGeneration, ContextId, frame.Id,
                Error: new("BUSINESS_HOST_STOPPING", "当前连接正在排空, 不再接受新请求", 3)), CancellationToken.None).ConfigureAwait(false);
            return;
        }
        _ = HandleCallAsync(frame, request);
    }

    /// <summary>业务错误转为稳定结果; 结果发送失败不能改变已经完成的业务事实.</summary>
    private async Task HandleCallAsync(BusinessHostFrame frame, Incoming request)
    {
        try
        {
            // 强制让 reader 先继续读取, 防止同步业务等待反向回调阻塞同一循环.
            await Task.Yield();
            BusinessHostFrame response;
            try
            {
                var handler = requestHandler ?? throw new AppException("BUSINESS_HOST_METHOD_NOT_ALLOWED", "此连接不接受业务回调", 3);
                var result = await handler(frame.Method!, frame.Data!.Value.Clone(), request.Cancellation.Token).ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.Undefined) throw new AppException("BUSINESS_HOST_RESULT_INVALID", "后台业务返回了未定义结果", 4);
                response = new("result", HostGeneration, ContextId, frame.Id, Data: result);
            }
            catch (Exception error)
            {
                var failure = error switch
                {
                    AppException app => new BusinessHostFailure(app.Code, app.Message, app.ExitCode),
                    OperationCanceledException => new("CANCELLED", "操作已取消", 130),
                    _ => new("BUSINESS_HOST_EXECUTION_FAILED", "后台业务执行失败, 请查看业务日志", 4)
                };
                response = new("result", HostGeneration, ContextId, frame.Id, Error: failure);
            }
            try { await SendAsync(response, CancellationToken.None).ConfigureAwait(false); }
            catch (AppException error) when (error.Code == "MESSAGE_TOO_LARGE")
            {
                await SendAsync(new("result", HostGeneration, ContextId, frame.Id,
                    Error: new(error.Code, "后台结果超过传输上限; 写入结果须核验, 不要重放", 4)), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is AppException or IOException or OperationCanceledException or ObjectDisposedException) { FailTransport(); }
        finally
        {
            incoming.TryRemove(frame.Id!, out _);
            request.Dispose();
            request.Completion.TrySetResult();
        }
    }

    /// <summary>事件回调独立执行并计入排空, 失败结束传输以免静默丢弃界面状态.</summary>
    private void ReceiveEvent(BusinessHostFrame frame)
    {
        if (eventHandler is null) return;
        var id = Guid.NewGuid().ToString("N");
        var request = new Incoming();
        if (incoming.Count >= MaximumPending || !incoming.TryAdd(id, request)) { request.Dispose(); throw BusinessHostProtocol.Invalid(); }
        // 事件自身保持接收顺序, 但 reader 不等待该链, 请求结果和取消仍能继续读取.
        eventTail = HandleEventAsync(eventTail, id, frame, request);
    }

    private async Task HandleEventAsync(Task previous, string id, BusinessHostFrame frame, Incoming request)
    {
        try
        {
            await Task.Yield();
            await previous.ConfigureAwait(false);
            await eventHandler!(frame.Method!, frame.Data!.Value.Clone(), request.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception) { FailTransport(); }
        finally { incoming.TryRemove(id, out _); request.Dispose(); request.Completion.TrySetResult(); }
    }

    private static void RequireEmpty(BusinessHostFrame frame)
    {
        if (frame.Id is not null || frame.Method is not null || frame.Data is not null || frame.Error is not null) throw BusinessHostProtocol.Invalid();
    }
}
