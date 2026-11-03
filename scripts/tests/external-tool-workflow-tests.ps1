# FilePath    : pbi-utils\scripts\tests\external-tool-workflow-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 隔离验证 all 开发外部工具注册顺序、参数与首错边界, 不操作系统登记.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../CommandRegistry.psm1') -Force
$module = Get-Module CommandRegistry

& $module {
    $script:WorkflowEvents = [Collections.Generic.List[string]]::new()
    $script:WorkflowFailure = ''
    $script:RegistrationOptions = $null
    $script:Delivered = $false

    <# .SYNOPSIS 断言真实编排结果, 条件不满足时抛错. #>
    function Assert-Workflow([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }

    <# .SYNOPSIS 仅替换工具动作边界, 保留真实 all/release 编排; 参数为动作、选项和编译后回调, 指定阶段失败返回 47. #>
    function Invoke-PbiAction([string]$Name, [hashtable]$Options, [scriptblock]$AfterCompile) {
        $script:WorkflowEvents.Add($Name)
        if ($Name -ceq $script:WorkflowFailure) { Stop-Pbi 47 'isolated upstream failure' }
        if ($AfterCompile) { & $AfterCompile }
        if ($Name -ceq 'buildC#') { $script:Delivered = $true }
    }

    <# .SYNOPSIS 模拟系统注册边界并核验必须已交付, 返回空值; 指定失败返回 3, 不改真实注册目录. #>
    function Invoke-PbiDevelopmentRegistration([hashtable]$Options) {
        Assert-Workflow $script:Delivered '必须在发布和临时清理完成后注册'
        $script:WorkflowEvents.Add('register')
        $script:RegistrationOptions = $Options.Clone()
        if ($script:WorkflowFailure -ceq 'register') { Stop-Pbi 3 'isolated registration permission failure' }
    }

    <# .SYNOPSIS 清除本测试模块内记录, 无磁盘或系统写入. #>
    function Reset-Workflow {
        $script:WorkflowEvents.Clear()
        $script:RegistrationOptions = $null
        $script:Delivered = $false
        $script:WorkflowFailure = ''
    }

    foreach ($configuration in @('Debug', 'Release')) {
        foreach ($mode in @('with-dotnet', 'without-dotnet')) {
            Reset-Workflow
            $options = @{ Scope = 'All'; Configuration = $configuration; DotNetMode = $mode; NonInteractive = $true }
            Invoke-PbiAll $options
            Assert-Workflow (($script:WorkflowEvents -join ',') -ceq 'checkC#,testC#,buildC#,checkVue,testVue,buildVue,register') '完整 all 在最后注册且只注册一次'
            Assert-Workflow ($script:RegistrationOptions.Configuration -ceq $configuration -and $script:RegistrationOptions.DotNetMode -ceq $mode -and $script:RegistrationOptions.NonInteractive) '注册继承配置、模式和非交互选择'
            Assert-Workflow ($options.Count -eq 4) '编排不能污染调用者选项'
        }
    }
    Reset-Workflow
    Invoke-PbiAll @{ Scope = 'Core'; Configuration = 'Release'; DotNetMode = 'without-dotnet' }
    Assert-Workflow ($null -eq $script:RegistrationOptions) 'Core 没有桌面入口, 不注册'
    Reset-Workflow
    Invoke-PbiRelease @{ DotNetMode = 'without-dotnet' }
    Assert-Workflow (($script:WorkflowEvents -join ',') -ceq 'checkC#,testC#,buildC#,checkVue,testVue,buildVue,package') 'release 只验证和打包, 不改开发登记'

    $stages = @('checkC#', 'testC#', 'buildC#', 'checkVue', 'testVue', 'buildVue')
    foreach ($stage in $stages) {
        Reset-Workflow
        $script:WorkflowFailure = $stage
        $caught = $null
        try { Invoke-PbiAll @{ Scope = 'All'; Configuration = 'Release'; DotNetMode = 'without-dotnet' } }
        catch { $caught = $_ }
        Assert-Workflow ($null -ne $caught -and $caught.Exception.Data['ExitCode'] -eq 47) '上游错误保留原始退出码'
        Assert-Workflow ($null -eq $script:RegistrationOptions -and !$script:Delivered) '上游失败不注册'
    }
    Reset-Workflow
    $script:WorkflowFailure = 'register'
    $caught = $null
    try { Invoke-PbiAll @{ Scope = 'All'; Configuration = 'Release'; DotNetMode = 'without-dotnet' } }
    catch { $caught = $_ }
    Assert-Workflow ($null -ne $caught -and $caught.Exception.Data['ExitCode'] -eq 3 -and $caught.Exception.Data['Action'] -ceq 'registerExternalTool') '注册失败须保留退出码并指出失败阶段'
    Assert-Workflow $script:Delivered '注册失败不能谎称发布产物未完成'
}

& $module {
    $script:CapturedRegistration = $null
    $script:ExpectedConfiguration = 'Release'
    $script:ExpectedDeployment = 'framework-dependent'
    $script:ManifestMismatch = $false

    <# .SYNOPSIS 隔离模块加载, 下方适配仅记录输入, 不加载系统注册或真实产物. #>
    function Import-Module { }
    <# .SYNOPSIS 提供已验证产物的测试快照, 参数为实际传入路径, 返回版本、范围、配置及模式. #>
    function Assert-PbiPublishedArtifacts([string]$Root) {
        $script:ValidatedRoot = $Root
        return @{ scope = $(if ($script:ManifestMismatch) { 'Core' } else { 'All' }); version = '7.8.9'; configuration = $script:ExpectedConfiguration; deploymentMode = $script:ExpectedDeployment }
    }
    <# .SYNOPSIS 记录开发注册请求, 参数与生产 API 相同, 返回虚构登记位置而不执行写入. #>
    function Register-PbiDevelopmentExternalTool([string]$PublishRoot, [string]$Version, [switch]$NonInteractive) {
        $script:CapturedRegistration = @{ Root = $PublishRoot; Version = $Version; NonInteractive = $NonInteractive.IsPresent }
        return @{ Path = 'isolated-registration' }
    }
    foreach ($scenario in @(
        @{ Configuration = 'Debug'; DotNetMode = 'with-dotnet'; Deployment = 'self-contained'; Relative = 'artifacts/build/debug/win-x64' },
        @{ Configuration = 'Release'; DotNetMode = 'without-dotnet'; Deployment = 'framework-dependent'; Relative = 'artifacts/build/release/win-x64/without-dotnet' },
        @{ Configuration = 'Release'; DotNetMode = 'with-dotnet'; Deployment = 'self-contained'; Relative = 'artifacts/build/release/win-x64/with-dotnet' }
    )) {
        $script:ExpectedConfiguration = $scenario.Configuration
        $script:ExpectedDeployment = $scenario.Deployment
        Invoke-PbiDevelopmentRegistration @{ Configuration = $scenario.Configuration; DotNetMode = $scenario.DotNetMode; NonInteractive = $true }
        $expected = [IO.Path]::GetFullPath((Join-Path $script:Root $scenario.Relative))
        if ($script:CapturedRegistration.Root -cne $expected -or $script:ValidatedRoot -cne $expected -or $script:CapturedRegistration.Version -cne '7.8.9' -or !$script:CapturedRegistration.NonInteractive) { throw '注册必须使用本轮已校验产物、清单版本与非交互约束' }
    }
    $script:CapturedRegistration = $null
    $script:ManifestMismatch = $true
    $caught = $null
    try { Invoke-PbiDevelopmentRegistration @{ Configuration = 'Release'; DotNetMode = 'with-dotnet' } } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Data['ExitCode'] -ne 3 -or $null -ne $script:CapturedRegistration) { throw '产物不匹配必须在系统注册前停止' }
}
Write-Host 'PASS external tool workflow ordering, scope and failure boundaries'
