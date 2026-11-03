# FilePath    : pbi-utils\scripts\tests\frontend-check-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离仓库验证同轮前端检查复用, 输入变化失效及独立命令自检.

<# .SYNOPSIS 执行真实脚本编排, 仅替换原生工具和输入快照边界, 不调用开发工具或真实用户数据. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-utils-frontend-check-' + [Guid]::NewGuid().ToString('N'))
$savedLocation = Get-Location
$savedOutputEncoding = [Console]::OutputEncoding

<# .SYNOPSIS 断言实际编排结果, 不符合时终止并保留错误原因. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }

try {
    # 与固定 C# 进程测试的 UTF-8 读取契约一致, 不依赖调用者控制台代码页.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts') -Force
    foreach ($file in @('CommandRegistry.psm1', 'check.ps1', 'test.ps1', 'build.ps1', 'PublishArtifacts.psm1', 'SharedPublish.psm1', 'PublishRuntime.psm1', 'TestResults.psm1')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot "scripts/$file") -Destination (Join-Path $sandbox 'scripts')
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LicenseBundleStub.psm1') -Destination (Join-Path $sandbox 'scripts/ThirdPartyLicenses.psm1')
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'web')
    Set-Content -LiteralPath (Join-Path $sandbox 'web/package.json') -Value '{"scripts":{"test":"pnpm lint && vitest run","build":"pnpm lint && vite build"}}'
    Set-Content -LiteralPath (Join-Path $sandbox 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $sandbox 'global.json') -Value '{"sdk":{"version":"10.0.403"}}'
    Import-Module (Join-Path $sandbox 'scripts/CommandRegistry.psm1') -Force
    $module = Get-Module CommandRegistry
    Set-Location -LiteralPath $sandbox
    & $module {
        foreach ($project in (Get-PbiProjects 'Core')) {
            $null = New-Item -ItemType Directory -Path (Join-Path $script:Root "$project/obj") -Force
            Set-Content -LiteralPath (Join-Path $script:Root "$project/obj/project.assets.json") -Value '{}'
        }
        $script:FrontendEvents = [Collections.Generic.List[string]]::new()
        $script:NativeFrontendCalls = [Collections.Generic.List[string]]::new()
        $script:InputRevision = 0
        $script:SnapshotCalls = 0
        $script:MutateSnapshot = 0
        $script:MutateStep = ''
        $script:FailureStep = ''
        $script:CancellationStep = ''
        $script:CanReuseInputs = $true
        $script:FrontendValidation = $null

        <# .SYNOPSIS 隔离工具版本查询; 参数及真实动作分发仍由模块校验. #>
        function Assert-PbiTools([string[]]$Tools) { }

        <# .SYNOPSIS 模拟输入修订, 可在检查中或检查后确定性变更, 无需时序等待. #>
        function Get-PbiFrontendInputFingerprint {
            $script:SnapshotCalls++
            if ($script:SnapshotCalls -eq $script:MutateSnapshot) { $script:InputRevision++ }
            if ($script:CanReuseInputs) { return "inputs-$($script:InputRevision)" }
            return $null
        }

        <# .SYNOPSIS 记录真实工具边界的业务阶段, 可注入修订变化、失败或取消. #>
        function Add-FrontendStep([string]$Step) {
            $script:FrontendEvents.Add($Step)
            if ($Step -eq $script:MutateStep) { $script:InputRevision++; $script:MutateStep = '' }
            if ($Step -eq $script:FailureStep) { Stop-Pbi 47 '合成前端失败' }
            if ($Step -eq $script:CancellationStep) { throw [OperationCanceledException]::new('合成取消') }
        }

        <# .SYNOPSIS 合成检查、测试和对应部署模式的最小产物; 不执行还原、安装或真实构建. #>
        function Invoke-PbiNative([string]$File, [string[]]$Arguments) {
            Assert ($Arguments -notcontains 'restore' -and $Arguments -notcontains 'install') 'all 和独立测试/构建不得执行 setup 或依赖还原'
            if ($File -eq 'pnpm') {
                $script:NativeFrontendCalls.Add($Arguments -join ' ')
                switch ($Arguments[2]) {
                    'lint' { Add-FrontendStep 'lint' }
                    'test' { Add-FrontendStep 'lint'; Add-FrontendStep 'vitest' }
                    'build' { Add-FrontendStep 'lint'; Add-FrontendStep 'vite' }
                    'exec' {
                        if ($Arguments[3] -eq 'vitest' -and $Arguments[4] -eq 'run') { Add-FrontendStep 'vitest' }
                        elseif ($Arguments[3] -eq 'vite' -and $Arguments[4] -eq 'build') { Add-FrontendStep 'vite' }
                        else { throw '未知前端原生命令' }
                    }
                    default { throw '不应启动其他前端命令' }
                }
                return
            }
            Assert ($File -eq 'dotnet' -and $Arguments -contains '--no-restore') 'C# 非 setup 动作必须显式禁止还原'
            if ($Arguments[0] -eq 'format') {
                $report = $Arguments[[Array]::IndexOf($Arguments, '--report') + 1]
                Set-Content -LiteralPath $report -Value '[]'
            } elseif ($Arguments[0] -eq 'publish') {
                $output = $Arguments[[Array]::IndexOf($Arguments, '-o') + 1]
                $null = New-Item -ItemType Directory -Path $output -Force
                Set-Content -LiteralPath (Join-Path $output 'pbi-utils.exe') -Value 'synthetic executable'
                foreach ($name in @('pbi-utils.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { Set-Content -LiteralPath (Join-Path $output $name) -Value "synthetic $name" }
                # Core 夹具保留真实自包含发布的基本 JSON 形状, 产品合并器无需测试特例.
                Set-Content -LiteralPath (Join-Path $output 'pbi-utils.runtimeconfig.json') -Value '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.13"}]}}'
                Set-Content -LiteralPath (Join-Path $output 'pbi-utils.deps.json') -Value '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64","signature":""},"compilationOptions":{},"targets":{".NETCoreApp,Version=v10.0":{},".NETCoreApp,Version=v10.0/win-x64":{"pbi-utils/1.2.3":{"dependencies":{"runtimepack.Microsoft.NETCore.App.Runtime.win-x64":"10.0.13"},"runtime":{"pbi-utils.dll":{}}},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"runtime":{"System.Private.CoreLib.dll":{}},"native":{"coreclr.dll":{},"hostfxr.dll":{},"hostpolicy.dll":{}}}}},"libraries":{"pbi-utils/1.2.3":{"type":"project","serviceable":false,"sha512":""},"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13":{"type":"runtimepack","serviceable":false,"sha512":""}}}'
                # 后台夹具使用相同框架文件, 但保留独立入口与完整 Windows 框架声明.
                if ($Arguments[1] -like '*Host') {
                    $depsPath = Join-Path $output 'pbi-utils.deps.json'
                    $deps = (Get-Content -LiteralPath $depsPath -Raw).Replace('pbi-utils', 'PbiUtils.Host') | ConvertFrom-Json -AsHashtable
                    $target = $deps.targets[$deps.runtimeTarget.name]
                    $frameworkId = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
                    $target['PbiUtils.Host/1.2.3'].dependencies[$frameworkId] = '10.0.13'
                    $target["$frameworkId/10.0.13"] = @{}
                    $deps.libraries["$frameworkId/10.0.13"] = @{ type = 'runtimepack'; serviceable = $false; sha512 = '' }
                    $deps | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $depsPath -Encoding utf8
                    $configPath = Join-Path $output 'pbi-utils.runtimeconfig.json'
                    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
                    $config.runtimeOptions.includedFrameworks += @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.13' }
                    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
                    foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
                        Move-Item -LiteralPath (Join-Path $output "pbi-utils.$suffix") -Destination (Join-Path $output "PbiUtils.Host.$suffix")
                    }
                }
                if ($Arguments[[Array]::IndexOf($Arguments, '--self-contained') + 1] -eq 'false') {
                    $entry = if ($Arguments[1] -like '*Host') { 'PbiUtils.Host' } else { 'pbi-utils' }
                    $depsPath = Join-Path $output "$entry.deps.json"
                    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
                    $target = $deps.targets[$deps.runtimeTarget.name]
                    $target["$entry/1.2.3"].dependencies = @{}
                    foreach ($name in @($target.Keys | Where-Object { $_ -like 'runtimepack.*' })) { $target.Remove($name); $deps.libraries.Remove($name) }
                    $deps | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $depsPath -Encoding utf8
                    $configPath = Join-Path $output "$entry.runtimeconfig.json"
                    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
                    $config.runtimeOptions.frameworks = $config.runtimeOptions.includedFrameworks
                    $config.runtimeOptions.Remove('includedFrameworks')
                    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
                    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) { Remove-Item -LiteralPath (Join-Path $output $name) }
                }
            } else {
                Assert ($Arguments[0] -eq 'test') '只允许检查、测试和发布'
                $directory = $Arguments[[Array]::IndexOf($Arguments, '--results-directory') + 1]
                $logger = $Arguments[[Array]::IndexOf($Arguments, '--logger') + 1]
                Assert ($logger -match '^trx;LogFileName=(.+\.trx)$') '每次 C# 测试须使用唯一 TRX 文件名'
                $reportName = $Matches[1]
                $time = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
                $null = New-Item -ItemType Directory -Path $directory -Force
                $xml = "<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Times start=`"$time`" finish=`"$time`"/><Results><UnitTestResult outcome=`"Passed`"/></Results><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" error=`"0`" timeout=`"0`" aborted=`"0`" inconclusive=`"0`" passedButRunAborted=`"0`" notRunnable=`"0`" notExecuted=`"0`" disconnected=`"0`" warning=`"0`" completed=`"0`" inProgress=`"0`" pending=`"0`"/></ResultSummary></TestRun>"
                Set-Content -LiteralPath (Join-Path $directory $reportName) -Value $xml -Encoding utf8
            }
        }

        <# .SYNOPSIS 清空单场景记录, 保留输入版本以验证跨轮不会沿用旧成功. #>
        function Reset-FrontendScenario {
            $script:FrontendEvents.Clear()
            $script:NativeFrontendCalls.Clear()
            $script:SnapshotCalls = 0
            $script:MutateSnapshot = 0
            $script:MutateStep = ''
            $script:FailureStep = ''
            $script:CancellationStep = ''
            $script:CanReuseInputs = $true
        }

        $outer = @{ Fingerprint = 'outer-context' }
        $script:FrontendValidation = $outer
        Invoke-PbiAll @{ Scope = 'Core'; Configuration = 'Release' }
        Assert (($script:FrontendEvents -join ',') -eq 'lint,vitest,vite') '同一次 all 必须仅执行一轮完整 lint, 然后测试、构建'
        Assert (Test-Path -LiteralPath (Join-Path $script:Root 'artifacts/build/release/win-x64/without-dotnet/manifest.json')) 'Release all 默认交付 without-dotnet'
        $licenseCalls = @(Get-Content -LiteralPath (Join-Path $script:Root 'license-stub-calls.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
        Assert ($licenseCalls.Count -eq 1 -and $licenseCalls[0].scope -ceq 'Core') 'all 必须将当前范围传入许可生成器且仅调用一次'
        $publishedRoot = Join-Path $script:Root 'artifacts/build/release/win-x64/without-dotnet'
        $publishedManifest = Get-Content -LiteralPath (Join-Path $publishedRoot 'manifest.json') -Raw | ConvertFrom-Json
        foreach ($license in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt', 'licenses/manifest.json')) {
            $entry = @($publishedManifest.files | Where-Object { $_.file -ceq $license })
            Assert ($entry.Count -eq 1 -and $entry[0].sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $publishedRoot $license)).Hash) '本轮许可生成结果必须进入真实发布清单及交付目录'
        }
        Assert (!(Test-Path -LiteralPath (Join-Path $script:Root 'artifacts/build/release/win-x64/with-dotnet'))) 'Release all 不得隐式生成另一模式'
        Assert ([object]::ReferenceEquals($script:FrontendValidation, $outer)) 'all 成功须恢复外层检查上下文'
        Reset-FrontendScenario
        Invoke-PbiAll @{ Scope = 'Core'; Configuration = 'Release' }
        Assert (($script:FrontendEvents -join ',') -eq 'lint,vitest,vite') '连续 all 每轮均重新检查, 不跨轮复用'
        $script:FrontendValidation = $null

        Reset-FrontendScenario
        Invoke-PbiAction 'testVue' @{}
        Invoke-PbiAction 'buildVue' @{}
        Assert (($script:FrontendEvents -join ',') -eq 'lint,vitest,lint,vite') '独立测试及构建必须各自完整自检'
        Assert ($script:SnapshotCalls -eq 0) '独立命令不承担聚合复用的快照开销'

        foreach ($scenario in @('duringLint', 'beforeTest', 'duringTest', 'untrustedInputs', 'customScript', 'pretest', 'posttest', 'prebuild', 'postbuild')) {
            Reset-FrontendScenario
            switch ($scenario) {
                'duringLint' { $script:MutateStep = 'lint' }
                'beforeTest' { $script:MutateSnapshot = 3 }
                'duringTest' { $script:MutateStep = 'vitest' }
                'untrustedInputs' { $script:CanReuseInputs = $false }
                'customScript' { Set-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Value '{"scripts":{"test":"pnpm lint && vitest run && node custom-hook.js","build":"pnpm lint && vite build"}}' }
                { $_ -in @('pretest', 'posttest', 'prebuild', 'postbuild') } {
                    $package = @{ scripts = @{ test = 'pnpm lint && vitest run'; build = 'pnpm lint && vite build' } }
                    $package.scripts[$scenario] = 'node lifecycle-hook.js'
                    $package | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:Root 'web/package.json')
                }
            }
            try {
                Invoke-PbiAll @{ Scope = 'Core'; Configuration = 'Release' }
                $expected = switch ($scenario) {
                    { $_ -in @('duringTest', 'prebuild', 'postbuild') } { 'lint,vitest,lint,vite' }
                    'untrustedInputs' { 'lint,lint,vitest,lint,vite' }
                    default { 'lint,lint,vitest,vite' }
                }
                Assert (($script:FrontendEvents -join ',') -eq $expected) "检查结果在 $scenario 时必须失效并执行完整包脚本"
                if ($scenario -eq 'customScript') { Assert ($script:NativeFrontendCalls -contains '--dir web test') '包脚本新增动作后不得直接绕过包脚本' }
                if ($scenario -in @('pretest', 'posttest')) { Assert ($script:NativeFrontendCalls -contains '--dir web test') '测试生命周期脚本不得被绕过' }
                if ($scenario -in @('prebuild', 'postbuild')) { Assert ($script:NativeFrontendCalls -contains '--dir web build') '构建生命周期脚本不得被绕过' }
                Assert ($null -eq $script:FrontendValidation) '每轮结束须释放内部上下文'
            } finally {
                Set-Content -LiteralPath (Join-Path $script:Root 'web/package.json') -Value '{"scripts":{"test":"pnpm lint && vitest run","build":"pnpm lint && vite build"}}'
            }
        }

        foreach ($step in @('lint', 'vitest', 'vite')) {
            foreach ($kind in @('failure', 'cancel')) {
                Reset-FrontendScenario
                if ($kind -eq 'failure') { $script:FailureStep = $step } else { $script:CancellationStep = $step }
                $manifestPath = Join-Path $script:Root 'artifacts/build/release/win-x64/without-dotnet/manifest.json'
                $beforeManifest = Get-Content -LiteralPath $manifestPath -Raw
                try { Invoke-PbiAll @{ Scope = 'Core'; Configuration = 'Release' }; throw '前端失败或取消应停止交付' }
                catch {
                    if ($kind -eq 'failure') { Assert ($_.Exception.Data['ExitCode'] -eq 47) '失败须保留原生退出码' }
                    else { Assert ($_.Exception -is [OperationCanceledException]) '取消不得转换为普通成功' }
                }
                $expected = switch ($step) { 'lint' { 'lint' }; 'vitest' { 'lint,vitest' }; 'vite' { 'lint,vitest,vite' } }
                Assert (($script:FrontendEvents -join ',') -eq $expected) '失败须首错停止, 不进入后续阶段'
                Assert ($null -eq $script:FrontendValidation) '失败或取消须清除本轮复用状态'
                Assert ((Get-Content -LiteralPath $manifestPath -Raw) -ceq $beforeManifest) '失败或取消必须保留选中模式的上一轮产物'
                Assert (!(Test-Path -LiteralPath (Join-Path $script:Root 'artifacts/build/release/win-x64/with-dotnet'))) '失败或取消不得构建未选模式'
            }
        }
    }
    Write-Output 'PASS 同轮前端检查复用与失效契约'
}
finally {
    [Console]::OutputEncoding = $savedOutputEncoding
    Set-Location -LiteralPath $savedLocation.Path
    $absoluteSandbox = [IO.Path]::GetFullPath($sandbox)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($absoluteSandbox.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absoluteSandbox) -match '^pbi-utils-frontend-check-[0-9a-f]{32}$' -and [IO.Directory]::Exists($absoluteSandbox)) {
        Remove-Item -LiteralPath $absoluteSandbox -Recurse -Force
    }
}
