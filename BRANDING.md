<!--
 * FilePath    : pbi-utils\BRANDING.md
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 记录品牌资源来源, 固定版本, 主题映射与图标生成方式.
-->

# 品牌资源

来源：[jiaopengzi/logo](https://github.com/jiaopengzi/logo)，固定提交 `5292ff432729e84b86aa7b4dcef259f4062dfcbd`，核对日期 2026-10-09。

采用该提交的 [logo.svg](https://github.com/jiaopengzi/logo/blob/5292ff432729e84b86aa7b4dcef259f4062dfcbd/logo.svg)：深蓝底、金色 J。上游没有单独的 pbi-utils 字标或主题配置文件，因此保留 pbi-utils 产品名称，从 SVG 的填充色提取品牌原色，不将带“焦棚子”文字的横版图替代产品名。

| 用途 | 色值 | 应用位置 |
| --- | --- | --- |
| 品牌主色 | `#1E2858` | 产品名、主按钮、选中导航文字、键盘焦点、浏览器 theme-color |
| 品牌强调色 | `#C89828` | 图标 J、选中导航边线、结果边线、运行状态点 |
| 正文 / 次要文字 | `#252C45` / `#626B80` | 页面说明、表单、事件与页脚 |
| 页面 / 侧栏 / 面板 | `#F5F6FA` / `#ECEEF5` / `#FFFFFF` | 中性背景与层次 |
| 结果 / 提示 | `#E9EDF8` / `#F7EFD9` | 主色与强调色的浅色衍生背景 |

只有前两项为上游原色，其余为应用配色。金色不用作浅色背景上的正文；提示文字采用深棕 `#70531A`，错误仍使用独立红色语义。产品主题变量集中在 [style.scss](web/src/app/style.scss)，Element Plus 的主色、金色强调及交互色阶由 [ui/_theme.scss](web/src/ui/_theme.scss) 映射。本次不增加深色模式或主题切换。

模板组件与程序式组件（浮动提示、确认框等）必须统一经过品牌 SCSS：两个 `ElementPlusResolver` 均使用 `importStyle: "sass"`，显式样式使用 `element-plus/es/components/<组件>/style/index`。不要混入 `style/css` 或预编译的 `theme-chalk/*.css`，它们会重新注入默认全局变量，将品牌深蓝和金色覆盖为组件库默认色。验收时检查实际渲染的按钮、悬停/禁用状态、输入焦点、选中项、弹窗和浮动提示，不能只检查源码中的品牌变量。

## 本地资源与使用

- [logo.svg](web/public/branding/logo.svg) 保留上游原始字节和原始注释，作为页面品牌图与 SVG favicon。
- [logo.ico](web/public/branding/logo.ico) 从同一 SVG 生成，包含 16、24、32、48、64、128、256 px，用作 favicon、桌面 EXE、CLI EXE 及 WinForms 窗口图标。
- 桌面窗口从程序集嵌入资源读取 ICO，并在窗口释放时清理；不依赖启动工作目录。两个 EXE 在编译时使用同一 ICO。
- Vite 将本地资源复制到 dist，现有桌面构建/发布将其打包至 web/branding。运行时不请求 GitHub、不下载主题、不放宽 CSP；CLI 输出契约不变。

SHA256：

```text
logo.svg  A7E9C8C7AF6663C1571D751B6070A092A76DF83C4321055A608CECCF202B57F5
logo.ico  03F3BA00BA0DA0D7B5EE538A914C58176BC9DF333A4F176C2D8BE7D238713318
```

## 更新资源

### NSIS 安装向导竖图

安装欢迎页、安装完成页和卸载完成页使用用户提供的 `logo_w550_h1054` 竖图: 金色背景、深蓝 J 与“焦棚子 / JIAOPENGZI”字标. 来源为本地 logo 仓库, 固定提交 `b90951beef86feb8f57af17b7a05aa7f0be872d1`, 核对日期 2026-11-02.

- [原始 SVG](scripts/installer/assets/logo_w550_h1054.svg) 保留上游原始字节与署名, 用于后续维护.
- [安装器 BMP](scripts/installer/assets/logo_w550_h1054.bmp) 为同一 SVG 导出的 550×1054、24 位无透明 BMP3, 供 MUI2 原生加载.
- [installer.nsi](scripts/installer/installer.nsi) 通过 `${__FILEDIR__}` 引用同目录下资源, 不依赖开发者桌面路径、网络或当前工作目录. 安装和卸载共用图片, 使用 MUI2 默认 FitControl 缩放, 不沿用旧安装器的 NOSTRETCH.
- SVG/BMP 随源码维护; 正常打包只读取已生成 BMP, 不要求 ImageMagick. 图片由 NSIS 嵌入安装器与卸载器, 不额外复制到应用运行目录.

SHA256:

```text
logo_w550_h1054.svg  F340F4EC47D43752E4FA8DEF54108119C460EC193467E81F62A8E57D68251153
logo_w550_h1054.bmp  AE491D4B2FAD2BA031263EC8DF842C87C719E27069313DD420DD1B6B6051D415
```

需要更新时从仓库根目录运行以下维护命令, 核对图像和哈希后同时更新两个资源:

```powershell
magick -background '#C89828' scripts/installer/assets/logo_w550_h1054.svg -alpha off -depth 8 -type TrueColor BMP3:scripts/installer/assets/logo_w550_h1054.bmp
magick identify scripts/installer/assets/logo_w550_h1054.bmp
```

### 应用图标

先核对上游提交、SVG 内容及颜色，再显式更新本地文件和本说明。只更换图标时无需修改依赖锁文件或产品版本。

本次使用现有 ImageMagick 7.1.2-32 生成 ICO（仅为资源维护工具，不加入开发或运行前置条件）：

```powershell
magick -background none -density 384 web/public/branding/logo.svg -define icon:auto-resize=16,24,32,48,64,128,256 web/public/branding/logo.ico
magick identify web/public/branding/logo.ico
```

更换品牌后通过统一入口执行 All/Release 检查、构建、测试及本地发布；验证发布文件哈希、两个 EXE 的图标和真实桌面窗口，点击 Hello/mock Agent 并保存截图。浏览器预览不替代 WebView2 验收。
