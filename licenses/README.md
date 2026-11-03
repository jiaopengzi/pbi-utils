# 离线许可来源

`catalog.json` 是需要人工核对的补充来源清单, 按生态、组件名称、精确版本和 SHA-256 绑定全文. `source/` 保存上游许可证与声明的原始文本; Microsoft Analysis Services 英文/中文条款同时保留原始 RTF 和便于离线阅读的 TXT. RTF 转换来源与档案条目写在清单中.

发布调用 `scripts/ThirdPartyLicenses.psm1` 的 `Export-PbiLicenseBundle`. 生成过程只读取本地文件, 不访问网络、安装依赖或下载许可:

- 从最终合并的 `*.deps.json` 取 NuGet 和 runtimepack 联集, 校验缓存 nuspec 身份及还原内容标识. 许可证、NOTICE、COPYING 和版权文件随包复制.
- All 范围从前端 `dependencies` 起始, 遍历实际安装的生产、peer 和已安装 optional 依赖; 不遍历根 `devDependencies`. 解析 npm 别名, 将同身份的不同 peer 实例所需依赖取并集, 核对仓库/已安装锁文件的精确版本.
- 从本清单补全仅在 nuspec 声明许可表达式、缺少包内全文的组件以及源码、图标、字体、主题、日历、示例与安装器来源. 未知表达式、缺失全文、缺失依赖、版本或来源 hash 漂移均阻止发布.
- 发布根包含项目 `LICENSE`、来源说明 `THIRD-PARTY-NOTICES.md`、带 UTF-8 BOM 的完整聚合 `THIRD-PARTY-NOTICES.txt` 和 `licenses/manifest.json`. `licenses/inputs/` 保留来源目录与前端输入快照; 每份分发文档记录长度及 hash, 供打包再次核验.

独立执行 `package` 时也会调用同一生成器: 先校验原发布目录和私有打包副本, 再用当前仓库来源重建副本的许可目录、聚合文本及发布清单. 原构建目录不被改写, 每轮都按内容读取最新许可, 不依据文件时间或旧许可副本复用结果. 此步骤读取现有 Node、前端安装依赖和 NuGet 缓存; 缺失时明确失败, 不自动还原. 前端依赖声明/锁文件及实际依赖集合、内嵌资源身份必须与原构建一致, 否则先重新构建; 本轮实际编译的 NSIS 独立核验版本. 许可来源在打包期间变化也会拒绝交付. 根 LICENSE 与完整聚合声明使用同一份固定副本; NSIS 阅读副本额外带 UTF-8 BOM 以正确显示中文, 安装载荷保留原文件字节.

分发文件使用 `licenses/<生态>/<完整身份 SHA-256 前16位>/01.txt` 等稳定短路径, RTF 等文档保留扩展, 避免安装器暂存路径叠加长组件名称后超过 Windows 传统文件 API 限制. 完整组件名、版本、来源 URL 和原始文档相对名称 `sourceDocument` 保留在机器清单及聚合阅读文本中, 不截断版权正文; 短标识冲突会明确失败.

NuGet 中的 MIT/Apache 等分类来自包声明和对应固定源提交, 不能按 Microsoft/其他发行方名称猜测. AnalysisServices/AdomdClient、WebView2 的 Microsoft 条款各自保留. ModelContextProtocol.Core 2.3.0 的完整上游 LICENSE 包括 Apache、历史 MIT 与文档说明, 不能简化为通用 MIT. WindowsDesktop runtime 包没有附带独立 NOTICE, 因此按其 nuspec 的同一 `dotnet/dotnet` 固定提交补全 WPF/WinForms 的 NOTICE.

两个已声明 MIT 但未附许可文件的包有明确的补充说明: `Microsoft.Identity.Client.NativeInterop` 保留 nuspec 版权与声明, `lodash-unified` 保留 npm 元数据中的作者和声明; 附标准 MIT 的完整授权与免责正文, 不编造未提供的版权年份或版权人. 这些文件明确区分包内事实与补充文本, 不伪装成上游原始 LICENSE.

更新组件时, 核对新的包声明、完整来源文本及 NOTICE, 保存对应精确版本和来源 URL, 然后更新清单 hash. 不用旧版本快照或网站链接替代新版本离线全文. 同时运行 `scripts/tests/license-bundle-tests.ps1` 与 `scripts/tests/license-bundle-validation-tests.ps1`, 最后按仓库规则完成发布门禁. NSIS 版本更新也需重新核对其分发组件许可.
