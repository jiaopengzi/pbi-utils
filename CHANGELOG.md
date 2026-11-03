# Changelog

本文件记录 pbi-utils 的重要变更, 最新版本置于最前.
采用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 的分类方式.

版本数字以 `Directory.Build.props` 的 `Version` 为准, 不由发布脚本自动修改.
版本标题必须精确使用 `## [desktop-v<版本>] - YYYY-MM-DD`, 并填写实际变更说明.
`main` 使用稳定标签 `desktop-v<版本>`; `v2` 流程测试使用 `desktop-v<版本>-v2.<正整数>`, 例如 `desktop-v0.1.0-v2.1`.
同一个精确标签只能出现一次, 已发布标签不可覆盖或重用.
分类按需使用 Added、Changed、Deprecated、Removed、Fixed、Security, 不保留空分类.

## [desktop-v0.1.0-v2.2] - 2026-11-03

### Changed

- 发布别名统一为 `.gitalias/savetag.sh`, 保留版本与分支核验、预览和精确原子推送.
- 完整构建、GitHub 发版及 Gitee ZIP 同步发布统一在 `build.yaml`, 移除独立 `ci.yml`.
- Checkout 使用 `actions/checkout@v7`, 其余构建 Actions 使用 Node 24 版本.

### Fixed

- 修复 SourceForge 将 PowerShell 默认下载请求引导到 HTML 页导致的 NSIS 校验失败, 保留官方归档哈希、许可和版本校验.
- 构建失败时仅上传实际存在的证据, 避免查找尚未生成的产物.

## [desktop-v0.1.0-v2.1] - 2026-11-03

### Added

- 新增 Windows 安装包的 GitHub Actions 构建和 ZIP 发版流程, 用于 v2 分支验证.
- 新增 GitHub main 分支到 Gitee 的代码同步流程.
- 新增仓库本地 `git savetag` 发布别名脚本, 支持只读预览、精确版本核验和分支与注释标签的原子推送.

### Changed

- 软件更新的下载入口改为同源同版本的 ZIP 安装包, 包内包含安装器和 SHA256 校验文件, 并同步中英文说明.
