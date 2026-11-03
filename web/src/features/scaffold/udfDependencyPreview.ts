/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfDependencyPreview.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 本地精确依赖闭包预览, 保持同来源、版本一致、循环和容量限制.
 */
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
import type { UdfPackage } from "./contracts"
/** 按声明的精确版本串行读取同来源依赖, 不搜索 latest 或访问网络.
 * @param request 当前逻辑操作的本地桥接入口, 保留单一取消身份.
 * @param origin 当前登记来源和配置基线, 所有依赖沿用同一身份.
 * @param root 待核验的根包, 包括在 128 包预算内.
 * @param invalid 产生本地化错误, 缺包、循环、版本冲突和超限均整轮失败.
 * @returns 不含根包的完整依赖包事实, 用于版本、函数和许可审阅.
 */
export async function readUdfDependencyPreview(
    request: UdfRequest,
    origin: { source: string; sourceBaseline?: string | null; catalogBaseline?: string | null },
    root: UdfPackage,
    invalid: () => Error,
): Promise<UdfPackage[]> {
    const packages = new Map<string, UdfPackage>(),
        visiting = new Set<string>()
    /** DFS 检查精确身份, 首错终止; 已读取事实仅在本轮预览复用. */
    async function visit(pkg: UdfPackage): Promise<void> {
        const key = pkg.id.toLowerCase(),
            previous = packages.get(key)
        if (visiting.has(key) || (previous && previous.version !== pkg.version)) throw invalid()
        if (previous) return
        if (packages.size >= 128) throw invalid()
        packages.set(key, pkg)
        visiting.add(key)
        await pkg.dependencies.reduce(async (pending, dependency) => {
            await pending
            const known = packages.get(dependency.id.toLowerCase())
            if (known) {
                if (known.version !== dependency.version || visiting.has(dependency.id.toLowerCase())) throw invalid()
                return
            }
            const result = await request("udf.catalog", { action: "package", ...origin, packageId: dependency.id, version: dependency.version })
            if (!result.package || result.package.id.toLowerCase() !== dependency.id.toLowerCase() || result.package.version !== dependency.version)
                throw invalid()
            await visit(result.package)
        }, Promise.resolve())
        visiting.delete(key)
    }
    await visit(root)
    return [...packages.values()].filter((pkg) => pkg !== root)
}
