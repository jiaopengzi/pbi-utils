
; Python 生成 nsi 脚本
; 定义变量名称
!define PRODUCT_NAME "pbi-utils"
!define MAIN_EXE "pbi-utils.exe"
!define INSTALL_FOLDER "pbi-utils"
!define PRODUCT_VERSION "1.0.1.0"
!define PRODUCT_PUBLISHER "jiaopengzi"
!define PRODUCT_WEB_SITE "https://jiaopengzi.com/2880.html"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\${MAIN_EXE}"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"

; 压缩方式
SetCompressor lzma

; MUI 1.67 compatible
!include "MUI.nsh"

; MUI Settings
!define MUI_ABORTWARNING
; 安装logo
!define MUI_ICON "C:\desktop\pbi-utils\favicon.ico"
; 卸载logo
!define MUI_UNICON "C:\desktop\pbi-utils\favicon.ico"
; 安装欢迎图(需要位图)
!define MUI_WELCOMEFINISHPAGE_BITMAP "C:\desktop\pbi-utils\image\logo_w164_h314.bmp"
; 禁止拉升
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH
; 卸载欢迎图(需要位图)
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "C:\desktop\pbi-utils\image\logo_w164_h314.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP_NOSTRETCH

; 设置语言
; Language Selection Dialog Settings
!define MUI_LANGDLL_REGISTRY_ROOT "${PRODUCT_UNINST_ROOT_KEY}"
!define MUI_LANGDLL_REGISTRY_KEY "${PRODUCT_UNINST_KEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "NSIS:Language"

; Welcome page
!insertmacro MUI_PAGE_WELCOME

; License page
!define MUI_LICENSEPAGE_CHECKBOX
!insertmacro MUI_PAGE_LICENSE "C:\desktop\pbi-utils\LICENSE.txt"

; Directory page
!insertmacro MUI_PAGE_DIRECTORY

; Instfiles page
!insertmacro MUI_PAGE_INSTFILES

; Finish page
!define MUI_FINISHPAGE_RUN "$INSTDIR\${MAIN_EXE}"
!insertmacro MUI_PAGE_FINISH

; Uninstaller pages
!insertmacro MUI_UNPAGE_INSTFILES

; Language files
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

; 安装包元数据
VIProductVersion "1.0.1.0"
VIFileVersion "1.0.1.0"
VIAddVersionKey "CompanyName" "jiaopengzi"
VIAddVersionKey "FileDescription" "about pbi-utils setup"
VIAddVersionKey "ProductVersion" "1.0.1.0"
VIAddVersionKey "LegalCopyright" "${U+00A9} jiaopengzi. All rights reserved."
VIAddVersionKey "OriginalFilename" "pbi-utils-setup.exe"
VIAddVersionKey "ProductName" "pbi-utils-setup"
; 品牌文字
BrandingText " www.jiaopengzi.com"
; MUI end ------

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "C:\desktop\pbi-utils-release\pbi-utils-1.0.1.0-setup.exe"
InstallDir "$PROGRAMFILES\${INSTALL_FOLDER}"
InstallDirRegKey HKLM "${PRODUCT_DIR_REGKEY}" ""
ShowInstDetails show
ShowUnInstDetails show

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Section "MainSection" SEC01
  ; Python 生成 创建信息
  SetOutPath $INSTDIR
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-console-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-datetime-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-debug-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-errorhandling-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-file-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-file-l1-2-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-file-l2-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-handle-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-heap-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-interlocked-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-libraryloader-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-localization-l1-2-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-memory-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-namedpipe-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-processenvironment-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-processthreads-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-processthreads-l1-1-1.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-profile-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-rtlsupport-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-string-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-synch-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-synch-l1-2-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-sysinfo-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-timezone-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-core-util-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-conio-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-convert-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-environment-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-filesystem-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-heap-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-locale-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-math-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-process-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-runtime-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-stdio-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-string-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-time-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\api-ms-win-crt-utility-l1-1-0.dll
File C:\desktop\pbi-utils\dist\pbi-utils\base_library.zip
File C:\desktop\pbi-utils\dist\pbi-utils\libcrypto-1_1.dll
File C:\desktop\pbi-utils\dist\pbi-utils\libssl-1_1.dll
File C:\desktop\pbi-utils\dist\pbi-utils\LICENSE.txt
File C:\desktop\pbi-utils\dist\pbi-utils\pbi-utils.exe
File C:\desktop\pbi-utils\dist\pbi-utils\python3.dll
File C:\desktop\pbi-utils\dist\pbi-utils\python39.dll
File C:\desktop\pbi-utils\dist\pbi-utils\select.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\tinyaes.cp39-win_amd64.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\ucrtbase.dll
File C:\desktop\pbi-utils\dist\pbi-utils\unicodedata.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\VCRUNTIME140.dll
File C:\desktop\pbi-utils\dist\pbi-utils\VCRUNTIME140_1.dll
File C:\desktop\pbi-utils\dist\pbi-utils\_bz2.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_decimal.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_hashlib.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_lzma.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_socket.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_ssl.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\_uuid.pyd
SetOutPath $INSTDIR\pbi_tools
File C:\desktop\pbi-utils\dist\pbi-utils\pbi_tools\pbi-tools.exe
File C:\desktop\pbi-utils\dist\pbi-utils\pbi_tools\pbi-tools.exe.config
File C:\desktop\pbi-utils\dist\pbi-utils\pbi_tools\pbi-tools.pdb
SetOutPath $INSTDIR\PySide6
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\pyside6.abi3.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\Qt6Core.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\Qt6Gui.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\Qt6Svg.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\Qt6Widgets.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\QtCore.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\QtGui.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\QtWidgets.pyd
SetOutPath $INSTDIR\PySide6\plugins
SetOutPath $INSTDIR\PySide6\plugins\iconengines
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\iconengines\qsvgicon.dll
SetOutPath $INSTDIR\PySide6\plugins\platforms
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\platforms\qdirect2d.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\platforms\qminimal.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\platforms\qoffscreen.dll
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\platforms\qwindows.dll
SetOutPath $INSTDIR\PySide6\plugins\styles
File C:\desktop\pbi-utils\dist\pbi-utils\PySide6\plugins\styles\qwindowsvistastyle.dll
SetOutPath $INSTDIR\shiboken6
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\MSVCP140.dll
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\MSVCP140_1.dll
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\MSVCP140_2.dll
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\Shiboken.pyd
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\shiboken6.abi3.dll
File C:\desktop\pbi-utils\dist\pbi-utils\shiboken6\VCRUNTIME140_1.dll
SetOutPath $INSTDIR\template
SetOutPath $INSTDIR\template\expressions
File C:\desktop\pbi-utils\dist\pbi-utils\template\expressions\Path_Sample.json
SetOutPath $INSTDIR\template\navigation_button
File C:\desktop\pbi-utils\dist\pbi-utils\template\navigation_button\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\navigation_button\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\navigation_button\visualContainer.json
SetOutPath $INSTDIR\template\queries
File C:\desktop\pbi-utils\dist\pbi-utils\template\queries\C01_ReportVisualTemplates.m
File C:\desktop\pbi-utils\dist\pbi-utils\template\queries\C02_ReportPages.m
File C:\desktop\pbi-utils\dist\pbi-utils\template\queries\C03_PowerBIUsers.m
File C:\desktop\pbi-utils\dist\pbi-utils\template\queries\Path_Sample.m
SetOutPath $INSTDIR\template\sections
SetOutPath $INSTDIR\template\sections\000_Home
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\section.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\00000_Template
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_Template\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_Template\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\00000_TopRight
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_TopRight\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_TopRight\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\00000_TransparentMask
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_TransparentMask\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_TransparentMask\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_TransparentMask\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_URLButton\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_URLButton\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_URLButton\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\00000_URLButton\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_LogoMatrix\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_LogoMatrix\dataTransforms.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_LogoMatrix\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_LogoMatrix\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_LogoMatrix\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\01000_Next
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Next\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Next\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Next\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Next\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_PageTitle\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_PageTitle\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_PageTitle\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_PageTitle\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\01000_Version
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Version\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Version\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Version\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\01000_Version\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\02000_Author
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Author\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Author\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Author\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Author\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\02000_Footer
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Footer\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\02000_Footer\visualContainer.json
SetOutPath $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\03000_URLDisplay\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\03000_URLDisplay\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\03000_URLDisplay\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\000_Home\visualContainers\03000_URLDisplay\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\section.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_PageTitle\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_PageTitle\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_PageTitle\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_PageTitle\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_RefreshTime\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_RefreshTime\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_RefreshTime\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_RefreshTime\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\00000_Template
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_Template\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_Template\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_URLButton\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_URLButton\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_URLButton\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\00000_URLButton\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Previous\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Previous\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Previous\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Previous\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\01000_TransparentMask
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_TransparentMask\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_TransparentMask\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_TransparentMask\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Version\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Version\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Version\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\01000_Version\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_Author\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_Author\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_Author\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_Author\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoLeft\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoLeft\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoLeft\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoLeft\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\dataTransforms.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\03000_TopRight
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_TopRight\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_TopRight\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_URLDisplay\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_URLDisplay\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_URLDisplay\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\03000_URLDisplay\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\visualContainer.json
SetOutPath $INSTDIR\template\sections\001_Navigation\visualContainers\04000_Footer
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_Footer\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\001_Navigation\visualContainers\04000_Footer\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\section.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_PageTitle\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_PageTitle\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_PageTitle\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_PageTitle\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_Template
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_Template\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_Template\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_URLButton\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_URLButton\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_URLButton\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\00000_URLButton\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Previous\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Previous\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Previous\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Previous\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_TransparentMask
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Version\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Version\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Version\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\01000_Version\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_Author\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_Author\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_Author\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_Author\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\dataTransforms.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_TopRight
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_TopRight\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_TopRight\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\filters.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\query.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\visualContainer.json
SetOutPath $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_Footer
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_Footer\config.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\sections\002_NoPermission\visualContainers\04000_Footer\visualContainer.json
SetOutPath $INSTDIR\template\tables
SetOutPath $INSTDIR\template\tables\C01_ReportVisualTemplates
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\table.json
SetOutPath $INSTDIR\template\tables\C01_ReportVisualTemplates\columns
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\dataCategory.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\description.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\displayFolder.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\name.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\status.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C01_ReportVisualTemplates\columns\value.json
SetOutPath $INSTDIR\template\tables\C02_ReportPages
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\table.json
SetOutPath $INSTDIR\template\tables\C02_ReportPages\columns
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\displayName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\displayOption.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\height.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\name.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonBackgroundColorNo.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonBackgroundColorYes.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonDisplayName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonTextColorNo.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonTextColorYes.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonTooltipNo.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\navigationButtonTooltipYes.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\note.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\ordinal.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\pageTitleBackgroundColor.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\pageTitleText.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\pageTitleTextColor.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\verticalAlignment.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\visibility.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C02_ReportPages\columns\width.json
SetOutPath $INSTDIR\template\tables\C03_PowerBIUsers
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\table.json
SetOutPath $INSTDIR\template\tables\C03_PowerBIUsers\columns
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\dimension.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\permissionName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\userID.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\userName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\userPrincipalName.json
File C:\desktop\pbi-utils\dist\pbi-utils\template\tables\C03_PowerBIUsers\columns\value.json
SectionEnd

Section -ShortCut
  ; 创建快捷方式
  CreateDirectory "$SMPROGRAMS\${INSTALL_FOLDER}"
  CreateShortCut "$SMPROGRAMS\${INSTALL_FOLDER}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
SectionEnd

Section -AdditionalIcons
  WriteIniStr "$INSTDIR\${PRODUCT_NAME}.url" "InternetShortcut" "URL" "${PRODUCT_WEB_SITE}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\Website.lnk" "$INSTDIR\${PRODUCT_NAME}.url"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk" "$INSTDIR\uninst.exe"
SectionEnd

Section -Post
  WriteUninstaller "$INSTDIR\uninst.exe"
  WriteRegStr HKLM "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\${MAIN_EXE}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayName" "$(^Name)"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\uninst.exe"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\${MAIN_EXE}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
  WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
SectionEnd

Function un.onUninstSuccess
  HideWindow
  MessageBox MB_ICONINFORMATION|MB_OK "$(^Name) 已成功地从你的计算机移除。"
FunctionEnd

Function un.onInit
!insertmacro MUI_UNGETLANGUAGE
  MessageBox MB_ICONQUESTION|MB_YESNO|MB_DEFBUTTON2 "你确实要完全移除 $(^Name) ，其及所有的组件？" IDYES +2
  Abort
FunctionEnd

Section Uninstall
  ; 删除注册表和安装包信息
  Delete "$INSTDIR\${PRODUCT_NAME}.url"
  Delete "$INSTDIR\uninst.exe"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Website.lnk"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${INSTALL_FOLDER}\${PRODUCT_NAME}.lnk"
  DeleteRegKey ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKLM "${PRODUCT_DIR_REGKEY}"
  ; Python 生成删除信息
  Delete $INSTDIR\api-ms-win-core-console-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-datetime-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-debug-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-errorhandling-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-file-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-file-l1-2-0.dll
Delete $INSTDIR\api-ms-win-core-file-l2-1-0.dll
Delete $INSTDIR\api-ms-win-core-handle-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-heap-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-interlocked-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-libraryloader-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-localization-l1-2-0.dll
Delete $INSTDIR\api-ms-win-core-memory-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-namedpipe-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-processenvironment-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-processthreads-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-processthreads-l1-1-1.dll
Delete $INSTDIR\api-ms-win-core-profile-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-rtlsupport-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-string-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-synch-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-synch-l1-2-0.dll
Delete $INSTDIR\api-ms-win-core-sysinfo-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-timezone-l1-1-0.dll
Delete $INSTDIR\api-ms-win-core-util-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-conio-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-convert-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-environment-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-filesystem-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-heap-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-locale-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-math-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-process-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-runtime-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-stdio-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-string-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-time-l1-1-0.dll
Delete $INSTDIR\api-ms-win-crt-utility-l1-1-0.dll
Delete $INSTDIR\base_library.zip
Delete $INSTDIR\libcrypto-1_1.dll
Delete $INSTDIR\libssl-1_1.dll
Delete $INSTDIR\LICENSE.txt
Delete $INSTDIR\pbi-utils.exe
Delete $INSTDIR\python3.dll
Delete $INSTDIR\python39.dll
Delete $INSTDIR\select.pyd
Delete $INSTDIR\tinyaes.cp39-win_amd64.pyd
Delete $INSTDIR\ucrtbase.dll
Delete $INSTDIR\unicodedata.pyd
Delete $INSTDIR\VCRUNTIME140.dll
Delete $INSTDIR\VCRUNTIME140_1.dll
Delete $INSTDIR\_bz2.pyd
Delete $INSTDIR\_decimal.pyd
Delete $INSTDIR\_hashlib.pyd
Delete $INSTDIR\_lzma.pyd
Delete $INSTDIR\_socket.pyd
Delete $INSTDIR\_ssl.pyd
Delete $INSTDIR\_uuid.pyd
Delete $INSTDIR\pbi_tools\pbi-tools.exe
Delete $INSTDIR\pbi_tools\pbi-tools.exe.config
Delete $INSTDIR\pbi_tools\pbi-tools.pdb
Delete $INSTDIR\PySide6\pyside6.abi3.dll
Delete $INSTDIR\PySide6\Qt6Core.dll
Delete $INSTDIR\PySide6\Qt6Gui.dll
Delete $INSTDIR\PySide6\Qt6Svg.dll
Delete $INSTDIR\PySide6\Qt6Widgets.dll
Delete $INSTDIR\PySide6\QtCore.pyd
Delete $INSTDIR\PySide6\QtGui.pyd
Delete $INSTDIR\PySide6\QtWidgets.pyd
Delete $INSTDIR\PySide6\plugins\iconengines\qsvgicon.dll
Delete $INSTDIR\PySide6\plugins\platforms\qdirect2d.dll
Delete $INSTDIR\PySide6\plugins\platforms\qminimal.dll
Delete $INSTDIR\PySide6\plugins\platforms\qoffscreen.dll
Delete $INSTDIR\PySide6\plugins\platforms\qwindows.dll
Delete $INSTDIR\PySide6\plugins\styles\qwindowsvistastyle.dll
Delete $INSTDIR\shiboken6\MSVCP140.dll
Delete $INSTDIR\shiboken6\MSVCP140_1.dll
Delete $INSTDIR\shiboken6\MSVCP140_2.dll
Delete $INSTDIR\shiboken6\Shiboken.pyd
Delete $INSTDIR\shiboken6\shiboken6.abi3.dll
Delete $INSTDIR\shiboken6\VCRUNTIME140_1.dll
Delete $INSTDIR\template\expressions\Path_Sample.json
Delete $INSTDIR\template\navigation_button\config.json
Delete $INSTDIR\template\navigation_button\filters.json
Delete $INSTDIR\template\navigation_button\visualContainer.json
Delete $INSTDIR\template\queries\C01_ReportVisualTemplates.m
Delete $INSTDIR\template\queries\C02_ReportPages.m
Delete $INSTDIR\template\queries\C03_PowerBIUsers.m
Delete $INSTDIR\template\queries\Path_Sample.m
Delete $INSTDIR\template\sections\000_Home\config.json
Delete $INSTDIR\template\sections\000_Home\filters.json
Delete $INSTDIR\template\sections\000_Home\section.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_Template\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_Template\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_TopRight\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_TopRight\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_TransparentMask\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_TransparentMask\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_TransparentMask\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix\dataTransforms.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Next\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Next\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Next\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Next\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Version\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Version\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Version\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\01000_Version\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Author\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Author\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Author\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Author\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Footer\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\02000_Footer\visualContainer.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay\config.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay\filters.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay\query.json
Delete $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\config.json
Delete $INSTDIR\template\sections\001_Navigation\filters.json
Delete $INSTDIR\template\sections\001_Navigation\section.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_Template\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_Template\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_TransparentMask\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_TransparentMask\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_TransparentMask\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\dataTransforms.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_TopRight\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_TopRight\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\filters.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\query.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor\visualContainer.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_Footer\config.json
Delete $INSTDIR\template\sections\001_Navigation\visualContainers\04000_Footer\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\config.json
Delete $INSTDIR\template\sections\002_NoPermission\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\section.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_Template\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_Template\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_TransparentMask\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\dataTransforms.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_TopRight\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_TopRight\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\filters.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\query.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor\visualContainer.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_Footer\config.json
Delete $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_Footer\visualContainer.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\table.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\dataCategory.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\description.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\displayFolder.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\name.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\status.json
Delete $INSTDIR\template\tables\C01_ReportVisualTemplates\columns\value.json
Delete $INSTDIR\template\tables\C02_ReportPages\table.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\displayName.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\displayOption.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\height.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\name.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonBackgroundColorNo.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonBackgroundColorYes.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonDisplayName.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonName.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonTextColorNo.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonTextColorYes.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonTooltipNo.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\navigationButtonTooltipYes.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\note.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\ordinal.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\pageTitleBackgroundColor.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\pageTitleText.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\pageTitleTextColor.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\verticalAlignment.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\visibility.json
Delete $INSTDIR\template\tables\C02_ReportPages\columns\width.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\table.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\dimension.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\permissionName.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\userID.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\userName.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\userPrincipalName.json
Delete $INSTDIR\template\tables\C03_PowerBIUsers\columns\value.json
RMDir $INSTDIR\template\sections\000_Home\visualContainers\00000_Template
RMDir $INSTDIR\template\sections\000_Home\visualContainers\00000_TopRight
RMDir $INSTDIR\template\sections\000_Home\visualContainers\00000_TransparentMask
RMDir $INSTDIR\template\sections\000_Home\visualContainers\00000_URLButton
RMDir $INSTDIR\template\sections\000_Home\visualContainers\01000_LogoMatrix
RMDir $INSTDIR\template\sections\000_Home\visualContainers\01000_Next
RMDir $INSTDIR\template\sections\000_Home\visualContainers\01000_PageTitle
RMDir $INSTDIR\template\sections\000_Home\visualContainers\01000_Version
RMDir $INSTDIR\template\sections\000_Home\visualContainers\02000_Author
RMDir $INSTDIR\template\sections\000_Home\visualContainers\02000_Footer
RMDir $INSTDIR\template\sections\000_Home\visualContainers\03000_URLDisplay
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\00000_PageTitle
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\00000_RefreshTime
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\00000_Template
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\00000_URLButton
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Previous
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\01000_TransparentMask
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\01000_Version
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\02000_Author
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoLeft
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\02000_LogoMatrix
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\03000_TopRight
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\03000_URLDisplay
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\04000_BackgroundColor
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers\04000_Footer
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_PageTitle
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_RefreshTime
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_Template
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\00000_URLButton
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Previous
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_TransparentMask
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\01000_Version
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_Author
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoLeft
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\02000_LogoMatrix
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_TopRight
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\03000_URLDisplay
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_BackgroundColor
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers\04000_Footer
RMDir $INSTDIR\template\sections\000_Home\visualContainers
RMDir $INSTDIR\template\sections\001_Navigation\visualContainers
RMDir $INSTDIR\template\sections\002_NoPermission\visualContainers
RMDir $INSTDIR\template\tables\C01_ReportVisualTemplates\columns
RMDir $INSTDIR\template\tables\C02_ReportPages\columns
RMDir $INSTDIR\template\tables\C03_PowerBIUsers\columns
RMDir $INSTDIR\PySide6\plugins\iconengines
RMDir $INSTDIR\PySide6\plugins\platforms
RMDir $INSTDIR\PySide6\plugins\styles
RMDir $INSTDIR\template\sections\000_Home
RMDir $INSTDIR\template\sections\001_Navigation
RMDir $INSTDIR\template\sections\002_NoPermission
RMDir $INSTDIR\template\tables\C01_ReportVisualTemplates
RMDir $INSTDIR\template\tables\C02_ReportPages
RMDir $INSTDIR\template\tables\C03_PowerBIUsers
RMDir $INSTDIR\PySide6\plugins
RMDir $INSTDIR\template\expressions
RMDir $INSTDIR\template\navigation_button
RMDir $INSTDIR\template\queries
RMDir $INSTDIR\template\sections
RMDir $INSTDIR\template\tables
RMDir $INSTDIR\pbi_tools
RMDir $INSTDIR\PySide6
RMDir $INSTDIR\shiboken6
RMDir $INSTDIR\template
RMDir $INSTDIR
  
  SetAutoClose true
SectionEnd
