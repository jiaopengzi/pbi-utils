# Changelog

本文件记录 pbi-utils 的重要变更, 最新版本置于最前.
采用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 的分类方式.

发版准备时, `git savetag --prepare` 从首个二级标题生成根目录 `version`. 构建和打包从 `version` 取得完整标签并推导数字版本, 安装向导显示完整标签.
发版前须提交 `CHANGELOG.md` 和 `version`, 使 Git 标签、提交中的 `version` 与最新章节标题标签一致.

## [v2.0.0-rc1] - 2026-11-16

### Changed

- v2 预览版发布, 用于验证安装向导、构建和发版流程.