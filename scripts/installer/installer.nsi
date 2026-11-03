# -*- coding: utf-8 -*-
; FilePath    : pbi-utils\scripts\installer\installer.nsi
; Author      : jiaopengzi
; Blog        : https://jiaopengzi.com
; Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
; Description : 当前用户 NSIS 安装向导, 检查运行环境并调用文件事务维护脚本, 不依赖产品 .NET 启动.

Unicode true
!if "${NSIS_VERSION}" != "v${PBI_NSIS_VERSION}"
    !error "NSIS version differs from its bundled COPYING; review the license catalog before packaging."
!endif
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"

Name "pbi-utils ${PBI_VERSION}"
OutFile "${PBI_OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\pbi-utils"
InstallDirRegKey HKCU "Software\pbi-utils\Desktop" "InstallRoot"
VIProductVersion "${PBI_FILE_VERSION}"
VIAddVersionKey "ProductName" "pbi-utils"
VIAddVersionKey "ProductVersion" "${PBI_VERSION}"
VIAddVersionKey "FileVersion" "${PBI_VERSION}"
VIAddVersionKey "CompanyName" "jiaopengzi"
VIAddVersionKey "FileDescription" "pbi-utils Setup"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 jiaopengzi"
BrandingText "pbi-utils"
ShowInstDetails show
ShowUninstDetails show

Var PowerShell
Var RequiresReboot
Var CreateDesktopShortcut
Var CreateStartMenuShortcut
Var DesktopShortcutCheckbox
Var StartMenuShortcutCheckbox
Var PrerequisiteStatus
Var RegistrationNonInteractive
Var PrerequisiteDotnetStatus
Var PrerequisiteWebviewStatus
Var PrerequisiteInstallButton
Var PrerequisiteRetryButton
Var PrerequisiteCancelButton
Var WorkRunning
Var WorkHandle
Var PrerequisiteBack
Var JobCount
Var JobRoot
Var JobWait
; 状态颜色始终配合本地化文字, 不依赖颜色单独表达通过、缺失或失败.
!define PBI_STATUS_READY 16794A
!define PBI_STATUS_MISSING 9A5B00
!define PBI_STATUS_FAILED B42318
!define PBI_STATUS_NEUTRAL 515A6B
!define MUI_ICON "${PBI_ICON}"
!define MUI_UNICON "${PBI_ICON}"
; 仓库内高分辨率 BMP 供安装欢迎/完成页和卸载完成页共用, 由 MUI2 按控件尺寸缩放.
!define MUI_WELCOMEFINISHPAGE_BITMAP "${__FILEDIR__}\assets\logo_w550_h1054.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${__FILEDIR__}\assets\logo_w550_h1054.bmp"
!define MUI_ABORTWARNING
!define MUI_CUSTOMFUNCTION_ABORT OnAbortCheck
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeDescription)"
!insertmacro MUI_PAGE_WELCOME
; MIT 阅读不附加点击同意条件; AMO/ADOMD 的分发条款在下一页单独明确接受.
!define MUI_PAGE_HEADER_TEXT "$(LicenseTitle)"
!define MUI_PAGE_HEADER_SUBTEXT "$(LicenseSubtitle)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(LicenseIntroduction)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "$(LicenseContinue)"
!define MUI_LICENSEPAGE_BUTTON "$(^NextBtn)"
!insertmacro MUI_PAGE_LICENSE "${PBI_LICENSE_TEXT}"
!define MUI_PAGE_HEADER_TEXT "$(NoticesTitle)"
!define MUI_PAGE_HEADER_SUBTEXT "$(NoticesSubtitle)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(NoticesIntroduction)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "$(NoticesContinue)"
!define MUI_LICENSEPAGE_BUTTON "$(^NextBtn)"
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_CHECKBOX_TEXT "$(MicrosoftTermsAccept)"
!insertmacro MUI_PAGE_LICENSE "${PBI_PAYLOAD}\app\THIRD-PARTY-NOTICES.txt"
Page custom PrerequisitePageCreate PrerequisitePageLeave
!insertmacro MUI_PAGE_DIRECTORY
Page custom ShortcutOptionsCreate ShortcutOptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApplication
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(OpenNoticesText)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION OpenLicenseNotices
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(UninstallDescription)"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"
; 编译期按语言设置字体, 让对话框单位与控件布局随字体一起调整, 安装和卸载共用.
SetFont /LANG=${LANG_SIMPCHINESE} "Microsoft YaHei UI" 10
SetFont /LANG=${LANG_ENGLISH} "Segoe UI" 9

LangString WelcomeDescription ${LANG_SIMPCHINESE} "此向导将安装 pbi-utils ${PBI_VERSION}。$\r$\n$\r$\n继续前，请关闭正在运行的 pbi-utils 窗口，并断开其 MCP 客户端。$\r$\n$\r$\n安装程序会检查所需运行环境，并创建你选择的快捷方式。$\r$\n$\r$\n点击“下一步”继续。"
LangString WelcomeDescription ${LANG_ENGLISH} "This wizard will install pbi-utils ${PBI_VERSION}.$\r$\n$\r$\nBefore continuing, close running pbi-utils windows and disconnect its MCP clients.$\r$\n$\r$\nSetup will check the required runtimes and create the shortcuts you select.$\r$\n$\r$\nClick Next to continue."
LangString LicenseTitle ${LANG_SIMPCHINESE} "pbi-utils 开源许可"
LangString LicenseTitle ${LANG_ENGLISH} "pbi-utils license"
LangString LicenseSubtitle ${LANG_SIMPCHINESE} "本项目采用 MIT 许可，原文如下。"
LangString LicenseSubtitle ${LANG_ENGLISH} "This project is licensed under the MIT License."
LangString LicenseIntroduction ${LANG_SIMPCHINESE} "请阅读许可原文。第三方组件适用各自条款，详见下一页。"
LangString LicenseIntroduction ${LANG_ENGLISH} "Read the license below. Third-party terms are on the next page."
LangString LicenseContinue ${LANG_SIMPCHINESE} "点击“下一步”继续。安装后可在 app 目录查看 LICENSE。"
LangString LicenseContinue ${LANG_ENGLISH} "Click Next to continue. LICENSE is kept in the installed app folder."
LangString NoticesTitle ${LANG_SIMPCHINESE} "第三方许可与声明"
LangString NoticesTitle ${LANG_ENGLISH} "Third-party licenses and notices"
LangString NoticesSubtitle ${LANG_SIMPCHINESE} "查看随本安装包分发的依赖和资源许可。"
LangString NoticesSubtitle ${LANG_ENGLISH} "Licenses for dependencies and resources distributed with this package."
LangString NoticesIntroduction ${LANG_SIMPCHINESE} "请阅读声明。随包分发的 Microsoft AMO/ADOMD 组件要求接受其许可条款。"
LangString NoticesIntroduction ${LANG_ENGLISH} "Read the notices, including the Microsoft AMO/ADOMD license terms."
LangString NoticesContinue ${LANG_SIMPCHINESE} "接受微软组件条款后继续。完整声明保存在安装目录的 app 文件夹。"
LangString NoticesContinue ${LANG_ENGLISH} "Accept the Microsoft component terms to continue. Full notices are kept in the installed app folder."
LangString MicrosoftTermsAccept ${LANG_SIMPCHINESE} "我接受 Microsoft AMO/ADOMD 组件许可条款(&A)"
LangString MicrosoftTermsAccept ${LANG_ENGLISH} "I &accept the Microsoft AMO/ADOMD component license terms"
LangString SilentTermsRequired ${LANG_SIMPCHINESE} "静默安装前须阅读随包许可，并用 /ACCEPTMICROSOFTTERMS=1 明确接受 Microsoft AMO/ADOMD 组件条款。"
LangString SilentTermsRequired ${LANG_ENGLISH} "Before silent setup, read the bundled licenses and pass /ACCEPTMICROSOFTTERMS=1 to accept the Microsoft AMO/ADOMD component terms."
LangString OpenNoticesText ${LANG_SIMPCHINESE} "查看许可和第三方声明"
LangString OpenNoticesText ${LANG_ENGLISH} "View licenses and third-party notices"
LangString OpenNoticesFailed ${LANG_SIMPCHINESE} "无法打开声明。请在安装目录的 app 文件夹查看 THIRD-PARTY-NOTICES.txt。"
LangString OpenNoticesFailed ${LANG_ENGLISH} "Could not open the notices. Open THIRD-PARTY-NOTICES.txt in the installed app folder."
LangString ShortcutTitle ${LANG_SIMPCHINESE} "快捷方式"
LangString ShortcutTitle ${LANG_ENGLISH} "Shortcuts"
LangString ShortcutSubtitle ${LANG_SIMPCHINESE} "选择要创建的桌面和开始菜单入口。"
LangString ShortcutSubtitle ${LANG_ENGLISH} "Choose the desktop and Start menu shortcuts to create."
LangString ShortcutDescription ${LANG_SIMPCHINESE} "仅为当前 Windows 用户创建所选入口。"
LangString ShortcutDescription ${LANG_ENGLISH} "Create the selected shortcuts for the current Windows user."
LangString DesktopShortcutText ${LANG_SIMPCHINESE} "创建桌面快捷方式(&D)"
LangString DesktopShortcutText ${LANG_ENGLISH} "Create a &desktop shortcut"
LangString StartMenuShortcutText ${LANG_SIMPCHINESE} "创建开始菜单快捷方式(&M)"
LangString StartMenuShortcutText ${LANG_ENGLISH} "Create a Start &menu shortcut"
LangString ShortcutNotes ${LANG_SIMPCHINESE} "取消勾选会移除本次安装已有的对应入口。$\r$\n$\r$\n卸载时会检查桌面和开始菜单，仅清理属于本次安装的快捷方式。"
LangString ShortcutNotes ${LANG_ENGLISH} "Clearing an option removes this installation's existing shortcut.$\r$\n$\r$\nUninstall checks both locations and keeps shortcuts owned by other apps."
LangString ShortcutPageFailed ${LANG_SIMPCHINESE} "无法显示快捷方式选项，请重新运行安装程序。"
LangString ShortcutPageFailed ${LANG_ENGLISH} "The shortcut options could not be displayed. Please run Setup again."
LangString UninstallDescription ${LANG_SIMPCHINESE} "卸载 pbi-utils 时会清理属于本安装的快捷方式和 Power BI 外部工具登记；必要时请求管理员权限。用户数据、Power BI 文件和其他安装的入口将保留。"
LangString UninstallDescription ${LANG_ENGLISH} "Remove this installation's shortcuts and Power BI external tool registration, requesting administrator permission when needed. User data, Power BI files and other installations' entries will be kept."
LangString PlatformRequired ${LANG_SIMPCHINESE} "此安装包需要 64 位 Windows 和系统 Windows PowerShell."
LangString PlatformRequired ${LANG_ENGLISH} "This package requires 64-bit Windows and Windows PowerShell."
LangString Preparing ${LANG_SIMPCHINESE} "正在检查 .NET 桌面运行时和 WebView2; 缺少时将从微软下载并安装, 可能需要管理员确认."
LangString Preparing ${LANG_ENGLISH} "Checking .NET Desktop Runtime and WebView2. Missing prerequisites will be downloaded from Microsoft and may request administrator approval."
LangString PrerequisiteTitle ${LANG_SIMPCHINESE} "运行环境"
LangString PrerequisiteTitle ${LANG_ENGLISH} "Prerequisites"
LangString PrerequisiteSubtitle ${LANG_SIMPCHINESE} "安装前请检查所需运行环境。"
LangString PrerequisiteSubtitle ${LANG_ENGLISH} "Check the required runtimes before installation."
LangString PrerequisiteInstall ${LANG_SIMPCHINESE} "安装缺失组件"
LangString PrerequisiteInstall ${LANG_ENGLISH} "Install missing components"
LangString PrerequisiteCancel ${LANG_SIMPCHINESE} "取消下载"
LangString PrerequisiteCancel ${LANG_ENGLISH} "Cancel download"
LangString ComponentReady ${LANG_SIMPCHINESE} "已满足"
LangString ComponentReady ${LANG_ENGLISH} "Ready"
LangString ComponentBundled ${LANG_SIMPCHINESE} "随包提供"
LangString ComponentBundled ${LANG_ENGLISH} "Bundled"
LangString ComponentMissing ${LANG_SIMPCHINESE} "需要安装"
LangString ComponentMissing ${LANG_ENGLISH} "Installation required"
LangString ComponentFailed ${LANG_SIMPCHINESE} "检测失败"
LangString ComponentFailed ${LANG_ENGLISH} "Check failed"
LangString ComponentChecking ${LANG_SIMPCHINESE} "正在检查"
LangString ComponentChecking ${LANG_ENGLISH} "Checking"
LangString WorkChecking ${LANG_SIMPCHINESE} "正在检查运行环境..."
LangString WorkChecking ${LANG_ENGLISH} "Checking prerequisites..."
LangString WorkDownloading ${LANG_SIMPCHINESE} "正在下载, 可以取消。"
LangString WorkDownloading ${LANG_ENGLISH} "Downloading; cancellation is available."
LangString WorkInstalling ${LANG_SIMPCHINESE} "微软安装程序正在运行, 请等待其结束并复检。"
LangString WorkInstalling ${LANG_ENGLISH} "Microsoft setup is running. Wait for it to finish and be checked."
LangString WorkCancelled ${LANG_SIMPCHINESE} "操作已取消, 可以重新检查。"
LangString WorkCancelled ${LANG_ENGLISH} "Cancelled. You can check again."
LangString WorkNetworkError ${LANG_SIMPCHINESE} "下载或网络失败。请重试或使用官方链接。"
LangString WorkNetworkError ${LANG_ENGLISH} "Download or network failed. Retry or use the official links."
LangString WorkSignatureError ${LANG_SIMPCHINESE} "下载文件签名或校验失败。请使用官方链接。"
LangString WorkSignatureError ${LANG_ENGLISH} "The downloaded file failed signature or hash verification. Use the official links."
LangString WorkInstallError ${LANG_SIMPCHINESE} "安装或安装后复检失败。请检查组件后重新检查。"
LangString WorkInstallError ${LANG_ENGLISH} "Installation or the post-install check failed. Check the components and retry."
LangString WorkProbeError ${LANG_SIMPCHINESE} "检测失败。请重试或手动检查运行环境。"
LangString WorkProbeError ${LANG_ENGLISH} "Detection failed. Retry or check the runtimes manually."
LangString WorkAbortWait ${LANG_SIMPCHINESE} "已请求取消下载; 如果微软安装程序已启动, 请等待其结束和复检。"
LangString WorkAbortWait ${LANG_ENGLISH} "Cancellation requested. If Microsoft setup has started, wait for it to finish and be checked."
LangString PrerequisiteReady ${LANG_SIMPCHINESE} "运行环境已就绪，点击“下一步”继续。"
LangString PrerequisiteReady ${LANG_ENGLISH} "The required runtimes are ready. Click Next to continue."
LangString PrerequisiteMissing ${LANG_SIMPCHINESE} "点击“安装缺失组件”补齐所需环境，完成后将自动检查。"
LangString PrerequisiteMissing ${LANG_ENGLISH} "Click Install missing components. Setup will check again when installation finishes."
LangString PrerequisiteRetry ${LANG_SIMPCHINESE} "重新检查"
LangString PrerequisiteRetry ${LANG_ENGLISH} "Check again"
LangString PrerequisiteDotnet ${LANG_SIMPCHINESE} ".NET Desktop Runtime 官方下载"
LangString PrerequisiteDotnet ${LANG_ENGLISH} ".NET Desktop Runtime download"
LangString PrerequisiteWebview ${LANG_SIMPCHINESE} "WebView2 官方下载"
LangString PrerequisiteWebview ${LANG_ENGLISH} "WebView2 download"
LangString Installing ${LANG_SIMPCHINESE} "正在安装程序文件、快捷方式和 Power BI 外部工具；登记时可能请求管理员权限..."
LangString Installing ${LANG_ENGLISH} "Installing files, shortcuts and the Power BI external tool. Administrator permission may be requested for registration..."
LangString Failed ${LANG_SIMPCHINESE} "安装未完成. 请查看上方详情; 如提示程序占用, 请正常关闭本软件及其 MCP 连接后重试."
LangString Failed ${LANG_ENGLISH} "Installation did not complete. Review the details above; if files are in use, close pbi-utils and its MCP connections normally, then retry."
LangString UninstallFailed ${LANG_SIMPCHINESE} "卸载未完成. 请查看详情并正常关闭本软件及其 MCP 连接后重试."
LangString UninstallFailed ${LANG_ENGLISH} "Uninstall did not complete. Review the details and close pbi-utils and its MCP connections normally before retrying."
LangString RestartRequired ${LANG_SIMPCHINESE} "运行环境安装需要重启 Windows. 请重启后使用 pbi-utils."
LangString RestartRequired ${LANG_ENGLISH} "A prerequisite requires a Windows restart. Restart before using pbi-utils."

; 初始化系统自带的 64 位 PowerShell, 不要求终端安装开发工具.
Function .onInit
    ; 从 PowerShell 7/开发宿主启动时不继承其模块目录, 让系统 PS5.1 使用自己的默认模块.
    System::Call 'kernel32::SetEnvironmentVariableW(w "PSModulePath", p 0)'
    SetRegView 64
    SetShellVarContext current
    ; 静默安装不会显示许可页, 必须由调用者明确传入同意选项; 缺失或其他值在解包/安装前返回 2.
    IfSilent 0 silent_terms_done
        ${GetParameters} $0
        ClearErrors
        ${GetOptions} $0 "/ACCEPTMICROSOFTTERMS=" $1
        ${If} ${Errors}
        ${OrIf} $1 != "1"
            DetailPrint "$(SilentTermsRequired)"
            SetErrorLevel 2
            Abort
        ${EndIf}
    silent_terms_done:
    !insertmacro MUI_LANGDLL_DISPLAY
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP "$(PlatformRequired)" /SD IDOK
        SetErrorLevel 3
        Abort
    ${EndIf}
    StrCpy $PowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
    IfFileExists "$PowerShell" +4 0
        MessageBox MB_ICONSTOP "$(PlatformRequired)" /SD IDOK
        SetErrorLevel 3
        Abort
    ; 引导文件在固实归档中排在应用载荷前, 仅用于只读检查.
    InitPluginsDir
    SetOutPath "$PLUGINSDIR\bootstrap"
    File /oname=Ensure-Prerequisites.ps1 "${PBI_PAYLOAD}\maintenance\Ensure-Prerequisites.ps1"
    File /oname=Prerequisite-Worker.ps1 "${PBI_PAYLOAD}\maintenance\Prerequisite-Worker.ps1"
    File /oname=Prerequisites.psm1 "${PBI_PAYLOAD}\maintenance\Prerequisites.psm1"
    File /oname=prerequisites.json "${PBI_PAYLOAD}\maintenance\prerequisites.json"
    StrCpy $WorkRunning 0
    StrCpy $JobCount 0
    StrCpy $RequiresReboot 0
    StrCpy $CreateDesktopShortcut ${BST_CHECKED}
    StrCpy $CreateStartMenuShortcut ${BST_CHECKED}
FunctionEnd

 ; 仅调用随包引导文件的只读检查; 返回码留在 $0, 缺失与探测错误都不得继续.
Function CheckPrerequisites
    nsExec::ExecToLog '"$PowerShell" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\bootstrap\Ensure-Prerequisites.ps1" -ManifestPath "$PLUGINSDIR\bootstrap\prerequisites.json" -CheckOnly'
    Pop $0
FunctionEnd

; 创建页面前执行当前只读检测, 已满足时跳过该页; 其他结果继续后台检查, 不放宽最终安装门禁.
Function PrerequisitePageCreate
    Call CheckPrerequisites
    ${If} $0 == 0
        Abort
    ${EndIf}
    !insertmacro MUI_HEADER_TEXT "$(PrerequisiteTitle)" "$(PrerequisiteSubtitle)"
    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
        SetErrorLevel 4
        Quit
    ${EndIf}
    StrCpy $PrerequisiteBack 0
    GetFunctionAddress $0 PrerequisiteOnBack
    nsDialogs::OnBack $0
    ${NSD_CreateLabel} 0 0 100% 15u ""
    Pop $PrerequisiteDotnetStatus
    ${NSD_CreateLabel} 0 20u 100% 15u ""
    Pop $PrerequisiteWebviewStatus
    ${NSD_CreateLabel} 0 40u 100% 25u "$(WorkChecking)"
    Pop $PrerequisiteStatus
    ${NSD_CreateButton} 0 70u 30% 18u "$(PrerequisiteInstall)"
    Pop $PrerequisiteInstallButton
    ${NSD_OnClick} $PrerequisiteInstallButton PrerequisiteInstallClick
    ${NSD_CreateButton} 34% 70u 30% 18u "$(PrerequisiteRetry)"
    Pop $PrerequisiteRetryButton
    ${NSD_OnClick} $PrerequisiteRetryButton PrerequisiteRetryClick
    ${NSD_CreateButton} 68% 70u 30% 18u "$(PrerequisiteCancel)"
    Pop $PrerequisiteCancelButton
    ${NSD_OnClick} $PrerequisiteCancelButton PrerequisiteCancelClick
    ${NSD_CreateLink} 0 100u 100% 15u "$(PrerequisiteDotnet)"
    Pop $0
    ${NSD_OnClick} $0 PrerequisiteDotnetClick
    ${NSD_CreateLink} 0 120u 100% 15u "$(PrerequisiteWebview)"
    Pop $0
    ${NSD_OnClick} $0 PrerequisiteWebviewClick
    ${NSD_CreateTimer} PrerequisiteTimer 500
    StrCpy $2 0
    Call StartPrerequisiteWork
    nsDialogs::Show
    ${NSD_KillTimer} PrerequisiteTimer
FunctionEnd

; $2 为 0 检查或 1 安装缺失项; 每轮独立目录防止迟到状态覆盖新任务.
Function StartPrerequisiteWork
    ${If} $WorkRunning == 1
        Return
    ${EndIf}
    IntOp $JobCount $JobCount + 1
    StrCpy $JobRoot "$PLUGINSDIR\prerequisite-$JobCount"
    CreateDirectory "$JobRoot"
    StrCpy $JobWait 0
    StrCpy $WorkRunning 1
    SetCtlColors $PrerequisiteStatus ${PBI_STATUS_NEUTRAL} SYSCLR:15
    ${NSD_SetText} $PrerequisiteStatus "$(WorkChecking)"
    GetDlgItem $1 $HWNDPARENT 1
    EnableWindow $1 0
    GetDlgItem $1 $HWNDPARENT 3
    EnableWindow $1 0
    EnableWindow $PrerequisiteRetryButton 0
    EnableWindow $PrerequisiteInstallButton 0
    EnableWindow $PrerequisiteCancelButton 1
    ; 保留真实进程句柄直至退出, 防止启动延迟或 PID 复用导致安装仍在运行时放行.
    System::Call 'kernel32::GlobalAlloc(i 0x40, i 68) p .r6'
    System::Call 'kernel32::GlobalAlloc(i 0x40, i 16) p .r7'
    StrCpy $8 0
    ${If} $6 != 0
    ${AndIf} $7 != 0
        System::Call '*$6(i 68)'
        System::Call 'kernel32::CreateProcessW(p 0, w `"$PowerShell" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "$PLUGINSDIR\bootstrap\Prerequisite-Worker.ps1" -ManifestPath "$PLUGINSDIR\bootstrap\prerequisites.json" -WorkRoot "$JobRoot" -InstallMissing $2`, p 0, p 0, i 0, i 0x08000000, p 0, p 0, p r6, p r7) i .r8'
        ${If} $8 != 0
            System::Call '*$7(p .r9, p .r5)'
            StrCpy $WorkHandle $9
            System::Call 'kernel32::CloseHandle(p r5)'
        ${EndIf}
    ${EndIf}
    ${If} $6 != 0
        System::Call 'kernel32::GlobalFree(p r6)'
    ${EndIf}
    ${If} $7 != 0
        System::Call 'kernel32::GlobalFree(p r7)'
    ${EndIf}
    ${If} $8 == 0
        StrCpy $WorkRunning 0
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_FAILED} SYSCLR:15
        ${NSD_SetText} $PrerequisiteStatus "$(WorkProbeError)"
        EnableWindow $PrerequisiteRetryButton 1
        GetDlgItem $1 $HWNDPARENT 3
        EnableWindow $1 1
    ${EndIf}
FunctionEnd

; 输入状态 $2 和标签句柄 $R0, 输出文字 $3 并先设置颜色; SetCtlColors 颜色必须为编译期常量.
; 使用系统不透明背景清除旧文字, 未知状态保持检查中, 调用方随后更新文本触发重绘.
Function PrerequisiteStatusText
    StrCpy $3 "$(ComponentChecking)"
    SetCtlColors $R0 ${PBI_STATUS_NEUTRAL} SYSCLR:15
    ${If} $2 == "ready"
        StrCpy $3 "$(ComponentReady)"
        SetCtlColors $R0 ${PBI_STATUS_READY} SYSCLR:15
    ${ElseIf} $2 == "bundled"
        StrCpy $3 "$(ComponentBundled)"
        SetCtlColors $R0 ${PBI_STATUS_READY} SYSCLR:15
    ${ElseIf} $2 == "missing"
        StrCpy $3 "$(ComponentMissing)"
        SetCtlColors $R0 ${PBI_STATUS_MISSING} SYSCLR:15
    ${ElseIf} $2 == "failed"
        StrCpy $3 "$(ComponentFailed)"
        SetCtlColors $R0 ${PBI_STATUS_FAILED} SYSCLR:15
    ${EndIf}
FunctionEnd

; 检查进程是否仍存在并刷新状态; 缺失终态时 fail-closed, 不放行 Next.
Function PrerequisiteTimer
    ${If} $WorkRunning != 1
        Return
    ${EndIf}
    ; 先观察持有的进程句柄, 再读取状态; 已退出时一定读取最终原子状态.
    System::Call 'kernel32::GetExitCodeProcess(p $WorkHandle, *i .r9) i .r8'
    ${If} $8 == 0
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_FAILED} SYSCLR:15
        ${NSD_SetText} $PrerequisiteStatus "$(WorkProbeError)"
        Return
    ${EndIf}
    ReadINIStr $4 "$JobRoot\state.ini" "state" "phase"
    ReadINIStr $2 "$JobRoot\state.ini" "state" "dotnet"
    StrCpy $R0 $PrerequisiteDotnetStatus
    Call PrerequisiteStatusText
    ${NSD_SetText} $PrerequisiteDotnetStatus ".NET Desktop Runtime: $3"
    ReadINIStr $2 "$JobRoot\state.ini" "state" "webview2"
    StrCpy $R0 $PrerequisiteWebviewStatus
    Call PrerequisiteStatusText
    ${NSD_SetText} $PrerequisiteWebviewStatus "WebView2: $3"
    SetCtlColors $PrerequisiteStatus ${PBI_STATUS_NEUTRAL} SYSCLR:15
    ${If} $4 == "downloading"
        ${NSD_SetText} $PrerequisiteStatus "$(WorkDownloading)"
    ${ElseIf} $4 == "installing"
        ${NSD_SetText} $PrerequisiteStatus "$(WorkInstalling)"
    ${ElseIf} $4 == "checking"
        ${NSD_SetText} $PrerequisiteStatus "$(WorkChecking)"
    ${ElseIf} $4 == "ready"
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_READY} SYSCLR:15
        ${NSD_SetText} $PrerequisiteStatus "$(PrerequisiteReady)"
    ${ElseIf} $4 == "missing"
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_MISSING} SYSCLR:15
        ${NSD_SetText} $PrerequisiteStatus "$(PrerequisiteMissing)"
    ${ElseIf} $4 == "cancelled"
        ${NSD_SetText} $PrerequisiteStatus "$(WorkCancelled)"
    ${ElseIf} $4 == "failed"
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_FAILED} SYSCLR:15
        ReadINIStr $5 "$JobRoot\state.ini" "state" "error"
        ${If} $5 == "signature"
            ${NSD_SetText} $PrerequisiteStatus "$(WorkSignatureError)"
        ${ElseIf} $5 == "probe"
            ${NSD_SetText} $PrerequisiteStatus "$(WorkProbeError)"
        ${ElseIf} $5 == "install"
        ${OrIf} $5 == "postcheck"
            ${NSD_SetText} $PrerequisiteStatus "$(WorkInstallError)"
        ${Else}
            ${NSD_SetText} $PrerequisiteStatus "$(WorkNetworkError)"
        ${EndIf}
    ${EndIf}
    ; 等待保留的真实 worker 句柄退出, 确保安装后的复检及清理已经排空.
    ${If} $9 == 259
        Return
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p $WorkHandle)'
    StrCpy $WorkHandle 0
    StrCpy $WorkRunning 0
    ReadINIStr $5 "$JobRoot\state.ini" "state" "reboot"
    ${If} $5 == 1
        StrCpy $RequiresReboot 1
    ${EndIf}
    EnableWindow $PrerequisiteCancelButton 0
    EnableWindow $PrerequisiteRetryButton 1
    GetDlgItem $1 $HWNDPARENT 3
    EnableWindow $1 1
    ${If} $4 == "ready"
        GetDlgItem $1 $HWNDPARENT 1
        EnableWindow $1 1
    ${ElseIf} $4 == "missing"
        EnableWindow $PrerequisiteInstallButton 1
    ${ElseIf} $4 != "failed"
    ${AndIf} $4 != "cancelled"
        SetCtlColors $PrerequisiteStatus ${PBI_STATUS_FAILED} SYSCLR:15
        ${NSD_SetText} $PrerequisiteStatus "$(WorkProbeError)"
    ${EndIf}
FunctionEnd

Function PrerequisiteRetryClick
    Pop $0
    StrCpy $2 0
    Call StartPrerequisiteWork
FunctionEnd
Function PrerequisiteInstallClick
    Pop $0
    StrCpy $2 1
    Call StartPrerequisiteWork
FunctionEnd
; 取消请求只创建标记, worker 不会强杀已启动的微软安装器.
Function RequestPrerequisiteCancel
    FileOpen $0 "$JobRoot\cancel" w
    FileWrite $0 "1"
    FileClose $0
    SetCtlColors $PrerequisiteStatus ${PBI_STATUS_NEUTRAL} SYSCLR:15
    ${NSD_SetText} $PrerequisiteStatus "$(WorkAbortWait)"
FunctionEnd
Function PrerequisiteCancelClick
    Pop $0
    Call RequestPrerequisiteCancel
FunctionEnd
Function OnAbortCheck
    ${If} $WorkRunning == 1
        Call RequestPrerequisiteCancel
        MessageBox MB_ICONINFORMATION "$(WorkAbortWait)"
        Abort
    ${EndIf}
FunctionEnd
Function PrerequisiteDotnetClick
    Pop $0
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet"
FunctionEnd
Function PrerequisiteWebviewClick
    Pop $0
    ExecShell "open" "https://developer.microsoft.com/microsoft-edge/webview2/"
FunctionEnd
; 离开时重新读取权威状态, 并对后台未排空或失败一律拒绝.
Function PrerequisitePageLeave
    ${If} $WorkRunning == 1
        Abort
    ${EndIf}
    ${If} $PrerequisiteBack == 1
        Return
    ${EndIf}
    Call CheckPrerequisites
    ${If} $0 != 0
        Abort
    ${EndIf}
FunctionEnd

; 仅将明确的后退动作标记为后退; 后退不要求运行环境已满足.
Function PrerequisiteOnBack
    ${If} $WorkRunning == 1
        Abort
    ${EndIf}
    StrCpy $PrerequisiteBack 1
FunctionEnd

; 显示两个默认选中的快捷方式选项, 从本轮状态恢复回退选择; 控件创建失败时终止安装并返回 4.
Function ShortcutOptionsCreate
    !insertmacro MUI_HEADER_TEXT "$(ShortcutTitle)" "$(ShortcutSubtitle)"
    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
        Call ShortcutOptionsFailed
    ${EndIf}
    ${NSD_CreateLabel} 0 0 100% 24u "$(ShortcutDescription)"
    Pop $0
    ${NSD_CreateCheckbox} 0 34u 100% 16u "$(DesktopShortcutText)"
    Pop $DesktopShortcutCheckbox
    ${If} $DesktopShortcutCheckbox == error
        Call ShortcutOptionsFailed
    ${EndIf}
    ${NSD_CreateCheckbox} 0 62u 100% 16u "$(StartMenuShortcutText)"
    Pop $StartMenuShortcutCheckbox
    ${If} $StartMenuShortcutCheckbox == error
        Call ShortcutOptionsFailed
    ${EndIf}
    ${NSD_SetState} $DesktopShortcutCheckbox $CreateDesktopShortcut
    ${NSD_SetState} $StartMenuShortcutCheckbox $CreateStartMenuShortcut
    ${NSD_OnClick} $DesktopShortcutCheckbox ShortcutOptionsChanged
    ${NSD_OnClick} $StartMenuShortcutCheckbox ShortcutOptionsChanged
    ${NSD_CreateLabel} 0 88u 100% 48u "$(ShortcutNotes)"
    Pop $0
    nsDialogs::Show
FunctionEnd

; 接收 nsDialogs 栈上的控件句柄, 立即保存鼠标/空格键切换后的两项状态, 保证返回上页再进入时不丢选择.
Function ShortcutOptionsChanged
    Pop $0
    Call ShortcutOptionsLeave
FunctionEnd

; 读取本页复选框, 将确定的 0/1 留在安装变量中供维护脚本使用, 不执行任何快捷方式写入.
Function ShortcutOptionsLeave
    ${NSD_GetState} $DesktopShortcutCheckbox $CreateDesktopShortcut
    ${NSD_GetState} $StartMenuShortcutCheckbox $CreateStartMenuShortcut
FunctionEnd

; 页面或必选控件无法创建时报告错误并退出, 不静默跳过选项页继续安装.
Function ShortcutOptionsFailed
    MessageBox MB_ICONSTOP "$(ShortcutPageFailed)" /SD IDOK
    SetErrorLevel 4
    Quit
FunctionEnd

Section "pbi-utils" Main
    ; 静默和交互路径均在释放应用载荷前独立复检, 不自动安装依赖.
    Call CheckPrerequisites
    ${If} $0 != 0
        ${If} $0 == "error"
        ${OrIf} $0 == "timeout"
            StrCpy $0 4
        ${EndIf}
        SetErrorLevel $0
        Abort
    ${EndIf}
    SetOutPath "$PLUGINSDIR\payload"
    File /r "${PBI_PAYLOAD}\*"
    ; 卸载器先生成在 staging, 跟随程序文件一起由维护事务交付.
    WriteUninstaller "$PLUGINSDIR\payload\uninstall.exe"
    DetailPrint "$(Installing)"
    StrCpy $RegistrationNonInteractive 0
    IfSilent 0 +2
        StrCpy $RegistrationNonInteractive 1
    nsExec::ExecToLog '"$PowerShell" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\payload\maintenance\Install-Application.ps1" -Operation Install -PayloadRoot "$PLUGINSDIR\payload" -InstallRoot "$INSTDIR" -CreateDesktopShortcut $CreateDesktopShortcut -CreateStartMenuShortcut $CreateStartMenuShortcut -RegistrationNonInteractive $RegistrationNonInteractive'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_ICONSTOP "$(Failed)" /SD IDOK
        ${If} $0 == "error"
        ${OrIf} $0 == "timeout"
            StrCpy $0 4
        ${EndIf}
        SetErrorLevel $0
        Abort
    ${EndIf}
    ${If} $RequiresReboot == 1
        SetRebootFlag true
        SetErrorLevel 3010
        MessageBox MB_ICONINFORMATION "$(RestartRequired)" /SD IDOK
    ${EndIf}
SectionEnd

; 用户在完成页显式选择后启动 GUI; 需要重启时不抢先启动.
Function LaunchApplication
    ${If} $RequiresReboot == 0
        ExecShell "open" "$INSTDIR\app\PbiUtils.Desktop.exe"
    ${EndIf}
FunctionEnd

; 完成页显式选择后以系统记事本打开已安装声明, 不依赖 Markdown 文件关联; 启动失败给出文件位置.
Function OpenLicenseNotices
    ClearErrors
    Exec '"$WINDIR\System32\notepad.exe" "$INSTDIR\app\THIRD-PARTY-NOTICES.txt"'
    ${If} ${Errors}
        MessageBox MB_ICONEXCLAMATION "$(OpenNoticesFailed)" /SD IDOK
    ${EndIf}
FunctionEnd

; NSIS 的卸载器从临时副本执行; 原安装目录交给相同维护引擎精确清理.
Function un.onInit
    System::Call 'kernel32::SetEnvironmentVariableW(w "PSModulePath", p 0)'
    SetRegView 64
    SetShellVarContext current
    StrCpy $PowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
FunctionEnd

Section "Uninstall"
    SetOutPath "$TEMP"
    StrCpy $RegistrationNonInteractive 0
    IfSilent 0 +2
        StrCpy $RegistrationNonInteractive 1
    nsExec::ExecToLog '"$PowerShell" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\maintenance\Install-Application.ps1" -Operation Uninstall -InstallRoot "$INSTDIR" -RegistrationNonInteractive $RegistrationNonInteractive'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_ICONSTOP "$(UninstallFailed)" /SD IDOK
        ${If} $0 == "error"
        ${OrIf} $0 == "timeout"
            StrCpy $0 4
        ${EndIf}
        SetErrorLevel $0
        Abort
    ${EndIf}
SectionEnd
