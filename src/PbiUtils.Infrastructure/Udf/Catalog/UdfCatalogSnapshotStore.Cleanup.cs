// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Udf\Catalog\UdfCatalogSnapshotStore.Cleanup.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅在成功发布后退役旧缓存, 活跃读者保护旧文件直到后续成功刷新回收.

using PbiUtils.Infrastructure.Storage;

namespace PbiUtils.Infrastructure.Udf.Catalog;

internal sealed partial class UdfCatalogSnapshotStore
{
    /// <summary>仅退役上一已发布指针证明过的代; 未发布或未知提交候选不参与扫描清理.</summary>
    private void RetirePublished(Pointer? previous, string current)
    {
        if (previous is not null && previous.Generation != current)
        {
            var path = Path.Combine(folder, previous.Generation);
            UserDataStore.Check(path);
            if (Directory.Exists(path)) UserDataStore.Write(Path.Combine(path, "retired"), []);
        }
        foreach (var path in Directory.EnumerateDirectories(folder))
        {
            if (!Generation(Path.GetFileName(path)) || Path.GetFileName(path) == current) continue;
            var marker = Path.Combine(path, "retired");
            UserDataStore.Check(marker);
            if (!File.Exists(marker)) continue;
            // 独占打开与读者共享句柄互斥, 且允许本清理句柄存活期间删除; 不存在先释放再删除的竞争窗口.
            var manifest = Path.Combine(path, "manifest.json");
            UserDataStore.Check(manifest);
            FileStream lease;
            try { lease = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Delete); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { continue; }
            using (lease) DeleteOwnedGeneration(path);
        }
    }
}
