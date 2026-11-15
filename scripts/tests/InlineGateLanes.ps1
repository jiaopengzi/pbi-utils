# FilePath    : pbi-utils\scripts\tests\InlineGateLanes.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在命令注册模块作用域按启动顺序同步执行并行门禁通道, 让编排测试沿用原有动作替身.

<#
.SYNOPSIS 固定为并行门禁, 编排断言不随测试机逻辑处理器数量变化; 逐个执行模式由测试显式覆盖本函数.
.OUTPUTS 始终返回 $true.
#>
function Test-PbiParallelGate { return $true }

<#
.SYNOPSIS 替换通道子进程启动边界, 以全新前端上下文同步执行真实通道主体并记录退出码和失败阶段.
.PARAMETER Gate 生产门禁上下文.
.PARAMETER Name 允许列表中的通道名称.
.OUTPUTS 已完成的通道记录. 前序通道已取消时后续通道直接记为取消, 模拟控制台取消同时到达全部子进程.
.NOTES 必须在 & $module { } 内点源加载, 使生产编排调用到本替身; 不启动进程或写入门禁结果文件.
#>
function Start-PbiGateLane([pscustomobject]$Gate, [string]$Name) {
    $lane = [pscustomobject]@{ Name = $Name; Process = $null; Result = ''; Completed = $true; ExitCode = 0; Action = $Name; Message = ''; Elapsed = [TimeSpan]::Zero }
    $cancelled = $false
    foreach ($previous in $Gate.Lanes) { if ($previous.ExitCode -eq 130) { $cancelled = $true } }
    if ($cancelled) {
        $lane.ExitCode = 130
        $lane.Message = '并行通道随同轮取消结束'
    } else {
        # 子进程不继承调用方的前端复用上下文, 同步替身也从空上下文开始并在结束后恢复.
        $savedFrontend = $script:FrontendValidation
        $script:FrontendValidation = $null
        try { Invoke-PbiGateLaneBody $Name @{ Scope = $Gate.Options.Scope; Configuration = $Gate.Options.Configuration } }
        catch {
            $lane.ExitCode = Get-PbiExitCode $_.Exception
            if ($_.Exception.Data.Contains('Action')) { $lane.Action = [string]$_.Exception.Data['Action'] }
            $lane.Message = $_.Exception.Message
        }
        finally { $script:FrontendValidation = $savedFrontend }
    }
    $Gate.Lanes.Add($lane)
    return $lane
}
