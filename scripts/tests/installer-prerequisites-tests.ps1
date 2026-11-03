# FilePath    : pbi-utils\scripts\tests\installer-prerequisites-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 在隔离目录验证安装前提的版本、校验、失败和元数据行为, 不安装本机依赖.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$modulePath = Join-Path $repositoryRoot 'scripts/installer/Prerequisites.psm1'
$metadataPath = Join-Path $repositoryRoot 'scripts/InstallerMetadata.psm1'
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pbi-installer-tests-' + [Guid]::NewGuid().ToString('N'))
$script:passed = 0

<# .SYNOPSIS 断言实际行为; 条件不满足时终止并保留失败原因. #>
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
<# .SYNOPSIS 执行一个具名行为场景, 成功后累计数量; 错误直接传播. #>
function Test([string]$Name, [scriptblock]$Action) { & $Action; $script:passed++; Write-Host "PASS $Name" }
<# .SYNOPSIS 断言操作拒绝非法输入或失败结果, 不把未抛错当作成功. #>
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert $threw $Message
}

Test '前置条件模块存在' { Assert (Test-Path -LiteralPath $modulePath) '尚未实现前置条件模块' }
$null = New-Item -ItemType Directory -Path $sandbox
try {
    $module = Import-Module $modulePath -Force -PassThru
    $installer = Join-Path $sandbox 'official.exe'
    [IO.File]::WriteAllText($installer, 'installer fixture')
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash
    $manifest = [pscustomobject]@{
        schemaVersion = 1; dotNetMode = 'without-dotnet'
        dotnet = [pscustomobject]@{
            sdkVersion = '10.0.403'; runtimeVersion = '10.0.14'; minimumVersion = '10.0.0'
            architecture = 'x64'; frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')
            url = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
            sha512 = $hash
        }
        webView2 = [pscustomobject]@{ url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' }
    }
    $manifestPath = Join-Path $sandbox 'prerequisites.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Test '两个框架允许同系列更新补丁' {
        $actual = & $module {
            Test-PbiDotNetRuntimeInventory @('Microsoft.NETCore.App 10.0.15 [C:\dotnet\shared\Microsoft.NETCore.App]', 'Microsoft.WindowsDesktop.App 10.0.14 [C:\dotnet\shared\Microsoft.WindowsDesktop.App]') '10.0.0' 'C:\dotnet'
        }
        Assert $actual '兼容补丁应满足实际框架最低版本'
    }
    Test '缺桌面框架、旧版、其他系列和预览版均不满足' {
        foreach ($version in @('9.0.20', '10.1.0', '11.0.0', '10.0.15-preview.1')) {
            $actual = & $module { param($v) Test-PbiDotNetRuntimeInventory @("Microsoft.NETCore.App $v [C:\dotnet\shared\Microsoft.NETCore.App]", "Microsoft.WindowsDesktop.App $v [C:\dotnet\shared\Microsoft.WindowsDesktop.App]") '10.0.0' 'C:\dotnet' } $version
            Assert (!$actual) "不得接受 $version"
        }
        Assert (!(& $module { Test-PbiDotNetRuntimeInventory @('Microsoft.NETCore.App 10.0.14 [C:\dotnet\shared\Microsoft.NETCore.App]') '10.0.0' 'C:\dotnet' })) '缺少桌面框架不得成功'
        Assert (!(& $module { Test-PbiDotNetRuntimeInventory @('Microsoft.NETCore.App 10.0.0 [C:\dotnet\shared\Microsoft.NETCore.App]', 'Microsoft.WindowsDesktop.App 10.0.0 [C:\dotnet\shared\Microsoft.WindowsDesktop.App]') '10.0.1' 'C:\dotnet' })) '低于实际最低补丁不得成功'
    }
    Test '另一个安装根的输出不能冒充已核验 x64 根' {
        Assert (!(& $module { Test-PbiDotNetRuntimeInventory @('Microsoft.NETCore.App 10.0.14 [C:\other\shared\Microsoft.NETCore.App]', 'Microsoft.WindowsDesktop.App 10.0.14 [C:\other\shared\Microsoft.WindowsDesktop.App]') '10.0.0' 'C:\dotnet' })) '必须约束已核验安装根'
    }
    Test '哈希与微软签名必须同时满足' {
        & $module {
            function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation, C=US' } } }
        }
        & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash
        Assert-Throws { & $module { param($p) Assert-PbiTrustedInstaller $p ('0' * 128) 'dotnet' } $installer } '错误哈希必须拒绝'
        & $module { function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null } } }
        Assert-Throws { & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash } '未签名文件必须拒绝'
        & $module { function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Other, O=Other, C=US' } } } }
        Assert-Throws { & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash } '其他可信发布者必须拒绝'
    }
    Test '.NET 产品证书须结合微软组织、有效签名与清单哈希, 不用于 WebView2' {
        & $module {
            $script:SignatureStatus = 'Valid'
            $script:SignerSubject = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
            <# .SYNOPSIS 返回实际官方 .NET 证书主体结构的隔离签名结果, 不修改系统信任或安装文件. #>
            function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = $script:SignatureStatus; SignerCertificate = [pscustomobject]@{ Subject = $script:SignerSubject } } }
        }
        & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash
        Assert-Throws { & $module { param($p) Assert-PbiTrustedInstaller $p ('0' * 128) 'dotnet' } $installer } '.NET 产品证书不能跳过哈希匹配'
        Assert-Throws { & $module { param($p) Assert-PbiTrustedInstaller $p '' 'dotnet' } $installer } '.NET 必须提供构建固化的官方 SHA512'
        Assert-Throws { & $module { param($p) Assert-PbiTrustedInstaller $p '' 'webview2' } $installer } '.NET 产品证书不得作为 WebView2 引导程序签名'
        & $module { $script:SignatureStatus = 'NotTrusted' }
        Assert-Throws { & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash } '证书主体匹配也必须通过 Windows 信任检查'
        & $module { $script:SignatureStatus = 'Valid'; $script:SignerSubject = 'CN=.NET, O=Microsoft Corporation Other, C=US' }
        Assert-Throws { & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash } '组织名称相似不得冒充微软'
        & $module { $script:SignerSubject = 'CN=xNET, O=Microsoft Corporation, C=US' }
        Assert-Throws { & $module { param($p, $h) Assert-PbiTrustedInstaller $p $h 'dotnet' } $installer $hash } '.NET 通用名必须精确匹配, 点号不能按正则通配符处理'
    }
    Test '官方安装适配器验签后仅启动指定 EXE, 并在执行期间禁止替换文件' {
        & $module {
            $script:Started = @()
            function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation, C=US' } } }
            function script:Start-Process {
                param($FilePath, $ArgumentList, [switch]$PassThru, [switch]$Wait, $WindowStyle, $Verb)
                $locked = $false
                try { [IO.File]::WriteAllText($FilePath, 'tampered') } catch { $locked = $true }
                if (!$locked) { throw '下载文件在执行边界未锁定' }
                $script:Started += [pscustomobject]@{ Path = $FilePath; Arguments = $ArgumentList; Verb = $Verb; Wait = $Wait; WindowStyle = $WindowStyle }
                $result = [pscustomobject]@{ ExitCode = 0 }
                $result | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
                return $result
            }
        }
        Assert ((& $module { param($p, $h) Invoke-PbiOfficialInstaller $p $h 'dotnet' } $installer $hash) -eq 0) '.NET 适配器应保留安装器成功码'
        Assert ((& $module { param($p) Invoke-PbiOfficialInstaller $p '' 'webview2' } $installer) -eq 0) 'WebView2 适配器应保留安装器成功码'
        & $module { function script:Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' } } } }
        Assert ((& $module { param($p, $h) Invoke-PbiOfficialInstaller $p $h 'dotnet' } $installer $hash) -eq 0) '.NET 产品证书必须通过真实安装编排校验'
        $started = & $module { $script:Started }
        Assert ($started[0].Path -eq $installer -and $started[0].Arguments -eq '/install /passive /norestart' -and $started[0].Wait) '只能等待官方 .NET EXE 并禁止自动重启'
        Assert ($started[1].Arguments -eq '/silent /install' -and !$started[1].Verb) 'WebView2 使用支持按用户安装的官方参数'
        Assert-Throws { & $module { param($p) Invoke-PbiOfficialInstaller $p ('0' * 128) 'dotnet' } $installer } '错误哈希不能进入 Start-Process'
        Assert ((& $module { $script:Started.Count }) -eq 3) '校验失败不能再启动安装'
    }
    Test 'WebView2 检测允许 HKCU, 拒绝缺失和零版本' {
        & $module {
            $script:UserWebView = '120.0.1.2'
            function script:Get-PbiRegistryValue { param($Hive, $View, $SubKey, $Name) if ([string]$Hive -eq 'CurrentUser') { return $script:UserWebView }; return '0.0.0.0' }
        }
        Assert (& $module { Test-PbiInstalledWebView2 }) '按用户安装的 Evergreen Runtime 应满足要求'
        & $module { $script:UserWebView = '0.0.0.0' }
        Assert (!(& $module { Test-PbiInstalledWebView2 })) '零版本不得代表已安装'
        & $module { $script:UserWebView = '' }
        Assert (!(& $module { Test-PbiInstalledWebView2 })) '缺失版本不得代表已安装'
    }
    Test '下载适配器拒绝 HTTP, 不创建部分文件' {
        $target = Join-Path $sandbox 'http-download.exe'
        Assert-Throws { & $module { param($p) Receive-PbiInstaller 'http://example.invalid/test.exe' $p } $target } '非 HTTPS 不能发出下载请求'
        Assert (!(Test-Path -LiteralPath $target)) '被拒绝的下载不能生成文件'
    }
    # 只替换系统适配边界, 继续执行真实 manifest 解析、缺失判断、分支和安装后复核.
    & $module {
        $script:InstalledDotNet = $false; $script:InstalledWebView = $true
        $script:Downloads = 0; $script:Installs = 0; $script:InstallerCode = 0
        $script:DownloadFails = $false; $script:InstallMakesAvailable = $true
        $script:PrerequisiteCalls = @()
        <# .SYNOPSIS 记录真实编排传入的最低版本并返回隔离的 .NET 状态, 不读取系统环境. #>
        function script:Test-PbiInstalledDotNet {
            param($MinimumVersion)
            $script:PrerequisiteCalls += [pscustomobject]@{ Operation = 'detect'; Kind = 'dotnet'; MinimumVersion = $MinimumVersion; Ready = $script:InstalledDotNet }
            return $script:InstalledDotNet
        }
        <# .SYNOPSIS 记录真实编排的 WebView2 检测时机并返回隔离状态. #>
        function script:Test-PbiInstalledWebView2 {
            $script:PrerequisiteCalls += [pscustomobject]@{ Operation = 'detect'; Kind = 'webview2'; Ready = $script:InstalledWebView }
            return $script:InstalledWebView
        }
        <# .SYNOPSIS 记录真实下载 URL 与目标文件; 按场景抛错或写入隔离夹具, 不访问网络. #>
        function script:Receive-PbiInstaller {
            param($Url, $Destination)
            $script:PrerequisiteCalls += [pscustomobject]@{ Operation = 'download'; Kind = [IO.Path]::GetFileNameWithoutExtension($Destination); Url = $Url; Path = $Destination }
            $script:Downloads++
            if ($script:DownloadFails) { throw 'TEST_DOWNLOAD_TIMEOUT' }
            [IO.File]::WriteAllText($Destination, 'installer fixture')
        }
        <# .SYNOPSIS 记录真实安装路径、哈希与组件选择, 更新隔离状态并返回场景退出码, 不启动安装器. #>
        function script:Invoke-PbiOfficialInstaller {
            param($Path, $Sha512, $Kind)
            $script:PrerequisiteCalls += [pscustomobject]@{ Operation = 'install'; Kind = $Kind; Path = $Path; Sha512 = $Sha512 }
            $script:Installs++
            if ($script:InstallMakesAvailable) { if ($Kind -eq 'dotnet') { $script:InstalledDotNet = $true } else { $script:InstalledWebView = $true } }
            return $script:InstallerCode
        }
    }
    Test 'CheckOnly 缺失返回 3 且不下载或安装' {
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath -CheckOnly) -eq 3) '缺失必须返回 3'
        Assert ((& $module { $script:Downloads + $script:Installs }) -eq 0) '只读检查不能下载或安装'
    }
    Test '满足要求时离线跳过全部安装' {
        & $module { $script:InstalledDotNet = $true }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 0) '满足要求必须成功'
        Assert ((& $module { $script:Downloads + $script:Installs }) -eq 0) '已满足要求不能联网'
    }
    Test 'with-dotnet 仍检测 WebView2 且不要求系统 .NET' {
        $manifest.dotNetMode = 'with-dotnet'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        & $module { $script:InstalledDotNet = $false }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath -CheckOnly) -eq 0) '自包含应跳过系统 .NET'
        & $module { $script:InstalledWebView = $false }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath -CheckOnly) -eq 3) '自包含也需要 WebView2'
        $manifest.dotNetMode = 'without-dotnet'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        & $module { $script:InstalledWebView = $true }
    }
    Test '下载失败不能安装或成功' {
        & $module { $script:DownloadFails = $true }
        Assert-Throws { Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath } '下载失败必须传播'
        Assert ((& $module { $script:Installs }) -eq 0) '失败下载不能执行'
        & $module { $script:DownloadFails = $false }
    }
    Test '安装器失败保留退出码' {
        & $module { $script:InstallerCode = 1603; $script:InstallMakesAvailable = $false }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 1603) '不得吞官方安装器失败'
    }
    Test '安装器返回成功但仍缺失必须失败' {
        & $module { $script:InstallerCode = 0 }
        Assert-Throws { Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath } '安装后真实检测必须执行'
    }
    Test '补齐缺失依赖后保留 3010 重启要求' {
        & $module { $script:InstallerCode = 3010; $script:InstallMakesAvailable = $true }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 3010) '重启要求不能改成普通成功'
    }
    Test '两个依赖缺失时依次补齐并逐项复检' {
        & $module {
            $script:InstalledDotNet = $false; $script:InstalledWebView = $false
            $script:InstallerCode = 0; $script:PrerequisiteCalls = @()
        }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 0) '两个缺失依赖补齐后应成功'
        $calls = @(& $module { $script:PrerequisiteCalls })
        $order = ($calls | ForEach-Object { $_.Operation + ':' + $_.Kind }) -join ','
        Assert ($order -ceq 'detect:dotnet,detect:webview2,download:dotnet,install:dotnet,detect:dotnet,download:webview2,install:webview2,detect:webview2') '必须先检测, 再依次下载、安装并复检每个缺失组件'
        Assert ($calls[0].MinimumVersion -ceq '10.0.0' -and $calls[4].MinimumVersion -ceq '10.0.0') '.NET 初检与复检必须使用实际最低版本'
        Assert (!$calls[0].Ready -and !$calls[1].Ready -and $calls[4].Ready -and $calls[7].Ready) '每个组件必须从缺失变成复检满足'
        Assert ($calls[2].Url -ceq 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe') '.NET 必须使用清单指定的官方安装地址'
        Assert ($calls[3].Path -ceq $calls[2].Path -and $calls[3].Sha512 -ceq $hash) '.NET 安装必须接收本轮下载文件及清单哈希'
        Assert ($calls[5].Url -ceq 'https://go.microsoft.com/fwlink/p/?LinkId=2124703') 'WebView2 必须使用官方引导程序地址'
        Assert ($calls[6].Path -ceq $calls[5].Path -and $calls[6].Sha512 -ceq '') 'WebView2 安装必须接收本轮下载文件, 不套用 .NET 哈希'
    }
    Test '仅 WebView2 缺失时绝不下载或安装 .NET' {
        & $module {
            $script:InstalledDotNet = $true; $script:InstalledWebView = $false
            $script:InstallerCode = 0; $script:PrerequisiteCalls = @()
        }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 0) '仅补齐 WebView2 后应成功'
        $calls = @(& $module { $script:PrerequisiteCalls })
        $order = ($calls | ForEach-Object { $_.Operation + ':' + $_.Kind }) -join ','
        Assert ($order -ceq 'detect:dotnet,detect:webview2,download:webview2,install:webview2,detect:webview2') '已满足的 .NET 只能检测, 不得下载、安装或多余复检'
        Assert ($calls[0].MinimumVersion -ceq '10.0.0' -and $calls[0].Ready -and !$calls[1].Ready -and $calls[4].Ready) '应保留 .NET 已满足状态, 并复检 WebView2 从缺失变为满足'
        Assert ($calls[2].Url -ceq 'https://go.microsoft.com/fwlink/p/?LinkId=2124703') '仅缺 WebView2 时必须选择其官方下载地址'
        Assert ($calls[3].Path -ceq $calls[2].Path -and $calls[3].Sha512 -ceq '') '安装必须接收本轮 WebView2 下载文件与空浮动哈希'
    }
    Test 'with-dotnet 忽略系统 .NET 缺失且仅补齐 WebView2' {
        $manifest.dotNetMode = 'with-dotnet'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        & $module {
            $script:InstalledDotNet = $false; $script:InstalledWebView = $false
            $script:InstallerCode = 0; $script:PrerequisiteCalls = @()
        }
        Assert ((Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath) -eq 0) '自包含包应在系统 .NET 仍缺失时成功补齐 WebView2'
        $calls = @(& $module { $script:PrerequisiteCalls })
        $order = ($calls | ForEach-Object { $_.Operation + ':' + $_.Kind }) -join ','
        Assert ($order -ceq 'detect:webview2,download:webview2,install:webview2,detect:webview2') '自包含包不得探测、下载或安装系统 .NET'
        Assert (!$calls[0].Ready -and $calls[3].Ready) '自包含包仍须复检 WebView2 从缺失变为满足'
        Assert ($calls[1].Url -ceq 'https://go.microsoft.com/fwlink/p/?LinkId=2124703') '自包含包必须使用 WebView2 官方下载地址'
        Assert ($calls[2].Path -ceq $calls[1].Path -and $calls[2].Sha512 -ceq '') '自包含包安装的必须是本轮 WebView2 文件'
        $manifest.dotNetMode = 'without-dotnet'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    }
    Test '非法 manifest 或 HTTP 下载源在安装前拒绝' {
        $manifest.dotnet.url = 'http://builds.dotnet.microsoft.com/runtime.exe'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Assert-Throws { Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath -CheckOnly } 'HTTP manifest 必须拒绝'
        $manifest.dotnet.url = 'https://builds.dotnet.microsoft.com/runtime.exe'
        $manifest.dotnet.frameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Assert-Throws { Invoke-PbiInstallerPrerequisites -ManifestPath $manifestPath -CheckOnly } 'ASP.NET 不能代替桌面框架'
    }
    Test '构建元数据从 SDK 对应的 WindowsDesktop 官方记录取值' {
        $metadataModule = Import-Module $metadataPath -Force -PassThru
        $repo = Join-Path $sandbox 'repository'; $publish = Join-Path $sandbox 'publish'
        $null = New-Item -ItemType Directory -Path $repo, $publish
        '{"sdk":{"version":"10.0.403"}}' | Set-Content -LiteralPath (Join-Path $repo 'global.json')
        @{ schemaVersion = 3; configuration = 'Release'; scope = 'All'; runtime = 'win-x64'; layout = 'shared'; deploymentMode = 'framework-dependent' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'manifest.json')
        foreach ($entry in @('pbi-utils', 'PbiUtils.Host', 'PbiUtils.Desktop')) {
            @{ runtimeOptions = @{ frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.0' }, @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' }) } } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish ($entry + '.runtimeconfig.json'))
        }
        & $metadataModule {
            function script:Invoke-RestMethod {
                [pscustomobject]@{ releases = @([pscustomobject]@{
                    sdks = @([pscustomobject]@{ version = '10.0.403'; 'runtime-version' = '10.0.14' })
                    windowsdesktop = [pscustomobject]@{ version = '10.0.14'; files = @([pscustomobject]@{ name = 'windowsdesktop-runtime-win-x64.exe'; rid = 'win-x64'; url = 'https://builds.dotnet.microsoft.com/official-runtime.exe'; hash = ('A' * 128) }) }
                }) }
            }
        }
        $output = Join-Path $sandbox 'generated.json'
        $actual = Export-PbiInstallerPrerequisites -RepositoryRoot $repo -PublishRoot $publish -OutputPath $output
        Assert ($actual.dotnet.sdkVersion -eq '10.0.403' -and $actual.dotnet.runtimeVersion -eq '10.0.14' -and $actual.dotnet.minimumVersion -eq '10.0.0') 'SDK 推荐版本和实际最低版本必须区分'
        Assert ($actual.dotnet.url -eq 'https://builds.dotnet.microsoft.com/official-runtime.exe' -and $actual.dotnet.sha512 -eq ('A' * 128)) '必须使用元数据原 URL 和哈希'
        Assert ((Get-Content -LiteralPath $output -Raw | ConvertFrom-Json).dotNetMode -eq 'without-dotnet') '构建输出必须映射包名'
        & $metadataModule { function script:Invoke-RestMethod { [pscustomobject]@{ releases = @() } } }
        Assert-Throws { Export-PbiInstallerPrerequisites -RepositoryRoot $repo -PublishRoot $publish -OutputPath $output } '官方缺少 SDK 记录不得猜测版本或 URL'
    }
    Write-Host "Installer prerequisites tests passed: $script:passed"
} finally {
    if (Get-Module Prerequisites) { Remove-Module Prerequisites }
    if (Get-Module InstallerMetadata) { Remove-Module InstallerMetadata }
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    Assert ($resolvedSandbox.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) '仅能清理测试临时目录'
    if (Test-Path -LiteralPath $resolvedSandbox) { Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force }
}
