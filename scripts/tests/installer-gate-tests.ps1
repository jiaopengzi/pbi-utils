# FilePath    : pbi-utils\scripts\tests\installer-gate-tests.ps1
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 验证安装器前置门禁顺序及静默安装只读约束.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../installer/installer.nsi') -Raw
$section = $source.Substring($source.IndexOf('Section "pbi-utils" Main'))
$gate = $section.IndexOf('Call CheckPrerequisites')
$payload = $section.IndexOf('File /r "${PBI_PAYLOAD}\*"')
if ($gate -lt 0 -or $payload -lt 0 -or $gate -ge $payload) { throw '完整载荷释放前必须执行权威只读门禁' }
if ($section -match 'Ensure-Prerequisites\.ps1[^\r\n]*ManifestPath[^\r\n]*[^-]$') { throw '安装阶段不能隐式安装前置组件' }
if ($source -match '(?i)UpdatePageCreate|Update-Worker\.ps1|Updates\.psm1|api\.github\.com|gitee\.com') { throw '安装向导不得检查产品更新' }
Write-Host 'PASS 安装阶段在完整载荷释放前执行只读门禁'
