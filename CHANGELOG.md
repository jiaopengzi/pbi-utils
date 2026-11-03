# Changelog

本文件记录 pbi-utils 的重要变更, 最新版本置于最前.
采用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 的分类方式.

版本数字以 `Directory.Build.props` 的 `Version` 为准, 不由发布脚本自动修改.
版本标题必须精确使用 `## [desktop-v<版本>] - YYYY-MM-DD`, 并填写实际变更说明.
`main` 使用稳定标签 `desktop-v<版本>`; `v2` 流程测试使用 `desktop-v<版本>-v2.<正整数>`, 例如 `desktop-v0.1.0-v2.1`.
同一个精确标签只能出现一次, 已发布标签不可覆盖或重用.
分类按需使用 Added、Changed、Deprecated、Removed、Fixed、Security, 不保留空分类.

## [desktop-v0.1.0-v2.6] - 2026-11-03

### Changed

- Gitee 发版拆为独立工作流, 在 GitHub 发布成功后调用, 并提供指定已发布标签的单独执行入口.
- 使用现成的 action-gitee-release 上传精确 ZIP, 保留发行版身份、公开附件长度与 SHA256 校验, 不自动重复上传或覆盖冲突附件.
- 构建所用 pnpm Action 同步到最新稳定版, 项目 Node 与 pnpm 工具链版本继续由原配置决定.

## [desktop-v0.1.0-v2.5] - 2026-11-03

### Changed

- 安装包 ZIP 仅包含 EXE, 安装器 SHA256 与精确文件名写入 GitHub/Gitee 发布说明.

### Fixed

- 正常 pnpm 包脚本回显按普通信息显示, 不再被 PowerShell 当作错误染红; 保留其他诊断与原始退出码.
- 正确处理 Gitee 查询尚不存在发行版时的 `HTTP 200 + null`, 并严格拒绝异常响应.

## [desktop-v0.1.0-v2.4] - 2026-11-03

### Fixed

- 隔离安装器测试子脚本的变量作用域, 保留原参数、中文输出及成功/失败退出语义.
- 发布构建仅自动响应发布标签, 代码同步仅自动响应 main, 避免同一提交在分支和标签上重复构建.

## [desktop-v0.1.0-v2.3] - 2026-11-03

### Fixed

- 为英文 Windows 和无窗口测试进程统一 UTF-8 输出边界, 保留 Bash 发布别名和两种 PowerShell 安装器测试的中文断言.

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
