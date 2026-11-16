<!--
 * FilePath    : pbi-utils\BRANDING.md
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 记录品牌资源来源, 固定版本, 主题映射与图标生成方式.
-->

# 品牌资源

应用和安装向导的品牌资源取自 [jiaopengzi/logo 固定提交](https://github.com/jiaopengzi/logo/tree/58374853f41be580fe54390955e874d9d543e239)，核对日期为 2026-11-16。品牌图形由 jiaopengzi 创作，保留所有权利。随包声明收录[品牌许可原文](licenses/source/jiaopengzi-logo-LICENSE.txt)。

应用使用深蓝底、金色 J 的方形图标，界面并排显示产品名称 pbi-utils。安装向导使用金色底、深蓝 J 与“焦棚子 / JIAOPENGZI”双行字标的竖图。两种品牌原色来自方形 SVG 的填充色。

| 用途 | 色值 | 应用位置 |
| --- | --- | --- |
| 品牌主色 | `#1E2858` | 产品名、主按钮、选中导航文字、键盘焦点、浏览器 theme-color |
| 品牌强调色 | `#C89828` | 图标 J、选中导航边线、结果边线、运行状态点 |
| 正文 / 次要文字 | `#252C45` / `#626B80` | 页面说明、表单、事件与页脚 |
| 页面 / 侧栏 / 面板 | `#F5F6FA` / `#ECEEF5` / `#FFFFFF` | 中性背景与层次 |
| 结果 / 提示 | `#E9EDF8` / `#F7EFD9` | 主色与强调色的浅色衍生背景 |

只有前两项为上游原色，其余为应用配色。浅色背景上的提示文字使用深棕 `#70531A`，错误使用独立的红色。产品主题变量集中在 [style.scss](web/src/app/style.scss)，Element Plus 的主色、金色强调及交互色阶由 [ui/_theme.scss](web/src/ui/_theme.scss) 映射。

模板组件和程序式组件共用品牌 SCSS。两个 `ElementPlusResolver` 均使用 `importStyle: "sass"`，显式样式使用 `element-plus/es/components/<组件>/style/index`。`style/css` 与预编译的 `theme-chalk/*.css` 会重新注入组件库默认全局变量。修改主题时检查实际渲染的按钮、悬停和禁用状态、输入焦点、选中项、弹窗和浮动提示。

## 本地资源与使用

- [logo.svg](web/public/branding/logo.svg) 保留固定提交中的原始字节和注释，用于应用侧栏、SVG favicon 和 Power BI Desktop 外部工具。
- [logo.ico](web/public/branding/logo.ico) 从同一 SVG 导出，包含 16、24、32、48、64、128、256 px，用于 ICO favicon、桌面与 CLI EXE、WinForms 窗口及安装与卸载程序图标。WinForms 窗口从程序集内嵌资源读取 ICO，并在窗口释放时清理。
- [logo_w550_h1054.svg](scripts/installer/assets/logo_w550_h1054.svg) 保留固定提交中的竖图原始字节和署名。
- [logo_w164_h314.bmp](scripts/installer/assets/logo_w164_h314.bmp) 从竖图 SVG 导出，为 164×314、24 位无透明 BMP3。[installer.nsi](scripts/installer/installer.nsi) 通过 `${__FILEDIR__}` 引用该文件。MUI2 将其适配到安装欢迎页、安装完成页和卸载完成页的图像控件。
- Vite 将方形资源复制到 `dist/branding`，桌面发布包从 `web/branding` 本地加载。NSIS 将竖图嵌入安装与卸载程序。

| 文件 | SHA-256 |
| --- | --- |
| logo.svg | `A7E9C8C7AF6663C1571D751B6070A092A76DF83C4321055A608CECCF202B57F5` |
| logo.ico | `EF9F2DA9420D65F616BFC9A0D6C9372E05C5C6546071584FA35AE8FE398830B1` |
| logo_w550_h1054.svg | `57FE86A4D6AD871D9A8359D77848B6DFFEE9768A89AFE7F2583E060A8D67C37F` |
| logo_w164_h314.bmp | `83D3C08824AFDB2733C4063DCDCBFC2FA93101170F9C00073C7224E540FDED71` |
| jiaopengzi-logo-LICENSE.txt | `C162703A02396F3762BE897463435BFF0397FC59B14CED050AB028AA535BC8F5` |

## 更新资源

从来源仓库的固定提交读取两份 SVG 和许可原文，按原始字节存入对应路径。从项目根目录运行下列命令导出图像。当前资源使用 ImageMagick 7.1.2-35 生成，日常构建和打包直接读取已有资源。

```powershell
magick -background none -density 384 web/public/branding/logo.svg -filter Triangle -define icon:auto-resize=16,24,32,48,64,128,256 web/public/branding/logo.ico
magick -density 384 scripts/installer/assets/logo_w550_h1054.svg -filter Triangle -resize 164x314 -alpha off -type TrueColor BMP3:scripts/installer/assets/logo_w164_h314.bmp
magick identify web/public/branding/logo.ico scripts/installer/assets/logo_w164_h314.bmp
```

核对资源哈希、ICO 各尺寸和 BMP 格式，同步更新本说明及[离线许可清单](licenses/catalog.json)。通过 [release 动作](.agents/skills/pbi-utils-development/references/validation-and-delivery.md#生成轻量安装包)验证本地构建和打包，检查发布文件哈希、桌面和 CLI EXE 图标、真实应用侧栏与窗口，以及安装和卸载向导中的竖图。
