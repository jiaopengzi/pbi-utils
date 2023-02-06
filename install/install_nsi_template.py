# -*- encoding: utf-8 -*-
"""
@File    :   install_nsi_template.py
@Time    :   2022/11/16 10:13
@Author  :   焦棚子
@Email   :   jiaopengzi@qq.com
@Blog    :   https://jiaopengzi.com/
@Version :   1.0.0"
"""

INSTALL_NSI_TEMPLATE = """
; Python 生成 nsi 脚本
; 定义变量名称
!define PRODUCT_NAME "${_PRODUCT_NAME}"
!define MAIN_EXE "${_MAIN_EXE}"
!define INSTALL_FOLDER "${_INSTALL_FOLDER}"
!define PRODUCT_VERSION "${_PRODUCT_VERSION}"
!define PRODUCT_PUBLISHER "${_PRODUCT_PUBLISHER}"
!define PRODUCT_WEB_SITE "${_PRODUCT_WEB_SITE}"
!define PRODUCT_DIR_REGKEY "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\$${MAIN_EXE}"
!define PRODUCT_UNINST_KEY "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\$${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"

; 压缩方式
SetCompressor ${_SetCompressor}

; MUI 1.67 compatible
!include "MUI.nsh"

; MUI Settings
!define MUI_ABORTWARNING
; 安装logo
!define MUI_ICON "${_MUI_ICON}"
; 卸载logo
!define MUI_UNICON "${_MUI_UNICON}"
; 安装欢迎图(需要位图)
!define MUI_WELCOMEFINISHPAGE_BITMAP "${_MUI_WELCOMEFINISHPAGE_BITMAP}"
; 禁止拉升
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH
; 卸载欢迎图(需要位图)
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${_MUI_UNWELCOMEFINISHPAGE_BITMAP}"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP_NOSTRETCH

; 设置语言
; Language Selection Dialog Settings
!define MUI_LANGDLL_REGISTRY_ROOT "$${PRODUCT_UNINST_ROOT_KEY}"
!define MUI_LANGDLL_REGISTRY_KEY "$${PRODUCT_UNINST_KEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "NSIS:Language"

; Welcome page
!insertmacro MUI_PAGE_WELCOME

; License page
!define MUI_LICENSEPAGE_CHECKBOX
!insertmacro MUI_PAGE_LICENSE "${_MUI_PAGE_LICENSE}"

; Directory page
!insertmacro MUI_PAGE_DIRECTORY

; Instfiles page
!insertmacro MUI_PAGE_INSTFILES

; Finish page
!define MUI_FINISHPAGE_RUN "$$INSTDIR\\$${MAIN_EXE}"
!insertmacro MUI_PAGE_FINISH

; Uninstaller pages
!insertmacro MUI_UNPAGE_INSTFILES

; Language files
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

; 安装包元数据
VIProductVersion "${_VIProductVersion}"
VIFileVersion "${_VIFileVersion}"
VIAddVersionKey "CompanyName" "${_CompanyName}"
VIAddVersionKey "FileDescription" "${_FileDescription}"
VIAddVersionKey "ProductVersion" "${_ProductVersion}"
VIAddVersionKey "LegalCopyright" "${_LegalCopyright}"
VIAddVersionKey "OriginalFilename" "${_OriginalFilename}"
VIAddVersionKey "ProductName" "${_ProductName}"
; 品牌文字
BrandingText " ${_BrandingText}"
; MUI end ------

Name "$${PRODUCT_NAME} $${PRODUCT_VERSION}"
OutFile "${_OutFile}"
InstallDir "$$PROGRAMFILES\\$${INSTALL_FOLDER}"
InstallDirRegKey HKLM "$${PRODUCT_DIR_REGKEY}" ""
ShowInstDetails show
ShowUnInstDetails show

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Section "MainSection" SEC01
  ; Python 生成 创建信息
  ${_add_str}
SectionEnd

Section -ShortCut
  ; 创建快捷方式
  CreateDirectory "$$SMPROGRAMS\\$${INSTALL_FOLDER}"
  CreateShortCut "$$SMPROGRAMS\\$${INSTALL_FOLDER}\\$${PRODUCT_NAME}.lnk" "$$INSTDIR\\$${MAIN_EXE}"
  CreateShortCut "$$DESKTOP\\$${PRODUCT_NAME}.lnk" "$$INSTDIR\\$${MAIN_EXE}"
SectionEnd

Section -AdditionalIcons
  WriteIniStr "$$INSTDIR\\$${PRODUCT_NAME}.url" "InternetShortcut" "URL" "$${PRODUCT_WEB_SITE}"
  CreateShortCut "$$SMPROGRAMS\\$${PRODUCT_NAME}\\Website.lnk" "$$INSTDIR\\$${PRODUCT_NAME}.url"
  CreateShortCut "$$SMPROGRAMS\\$${PRODUCT_NAME}\\Uninstall.lnk" "$$INSTDIR\\uninst.exe"
SectionEnd

Section -Post
  WriteUninstaller "$$INSTDIR\\uninst.exe"
  WriteRegStr HKLM "$${PRODUCT_DIR_REGKEY}" "" "$$INSTDIR\\$${MAIN_EXE}"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "DisplayName" "$$(^Name)"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "UninstallString" "$$INSTDIR\\uninst.exe"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "DisplayIcon" "$$INSTDIR\\$${MAIN_EXE}"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "DisplayVersion" "$${PRODUCT_VERSION}"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "URLInfoAbout" "$${PRODUCT_WEB_SITE}"
  WriteRegStr $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}" "Publisher" "$${PRODUCT_PUBLISHER}"
SectionEnd

Function un.onUninstSuccess
  HideWindow
  MessageBox MB_ICONINFORMATION|MB_OK "$$(^Name) 已成功地从你的计算机移除。"
FunctionEnd

Function un.onInit
!insertmacro MUI_UNGETLANGUAGE
  MessageBox MB_ICONQUESTION|MB_YESNO|MB_DEFBUTTON2 "你确实要完全移除 $$(^Name) ，其及所有的组件？" IDYES +2
  Abort
FunctionEnd

Section Uninstall
  ; 删除注册表和安装包信息
  Delete "$$INSTDIR\\$${PRODUCT_NAME}.url"
  Delete "$$INSTDIR\\uninst.exe"
  Delete "$$SMPROGRAMS\\$${PRODUCT_NAME}\\Uninstall.lnk"
  Delete "$$SMPROGRAMS\\$${PRODUCT_NAME}\\Website.lnk"
  Delete "$$DESKTOP\\$${PRODUCT_NAME}.lnk"
  Delete "$$SMPROGRAMS\\$${INSTALL_FOLDER}\\$${PRODUCT_NAME}.lnk"
  DeleteRegKey $${PRODUCT_UNINST_ROOT_KEY} "$${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKLM "$${PRODUCT_DIR_REGKEY}"
  ; Python 生成删除信息
  ${_del_str}
  
  SetAutoClose true
SectionEnd
"""