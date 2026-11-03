/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfCatalogPages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 串行枚举有界本地目录页面, 校验同代基线、完整数量与稳定身份.
 */
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
import type { ScaffoldMethods, UdfPackageSummary } from "./contracts"
/** 读取完整本地摘要, 不受界面搜索文本影响; 任一页失败不返回部分结果.
 * @param request 当前逻辑操作的串行桥接请求.
 * @param origin 来源身份、配置和已核验的目录基线见证.
 * @param includePrerelease 是否包含仅预发行库.
 * @param invalid 本地化的无效目录错误工厂.
 * @returns 在同一基线下核验分页元数据后合并的完整摘要; 缺失或不一致时抛出 invalid 错误.
 */
export async function readUdfCatalogPages(
    request: UdfRequest,
    origin: { source: string; sourceBaseline?: string | null; catalogBaseline?: string | null },
    includePrerelease: boolean,
    invalid: () => Error,
): Promise<ScaffoldMethods["udf.catalog"]["result"]> {
    const packages: UdfPackageSummary[] = [],
        identities = new Set<string>()
    let expectedTotal: number | undefined,
        baseline = origin.catalogBaseline
    /** 每页均绑定同一目录见证, 下一 offset 必须精确对应已取得的摘要数. */
    async function readPage(offset: number): Promise<ScaffoldMethods["udf.catalog"]["result"]> {
        const page = await request("udf.catalog", { action: "search", ...origin, catalogBaseline: baseline, query: "", includePrerelease, offset, limit: 100 })
        if (!page.packages || packages.length + page.packages.length > 1024) throw invalid()
        if (page.status) {
            if (baseline && baseline !== page.status.baseline) throw invalid()
            baseline = page.status.baseline
        }
        for (const item of page.packages) {
            const id = item.id.toLowerCase()
            if (identities.has(id)) throw invalid()
            identities.add(id)
            packages.push(item)
        }
        if (!Number.isInteger(page.total) || page.total! < 0 || page.total! > 1024 || (expectedTotal !== undefined && page.total !== expectedTotal))
            throw invalid()
        expectedTotal = page.total
        if (page.nextOffset === null) {
            if (packages.length !== expectedTotal) throw invalid()
            return { ...page, packages, nextOffset: null }
        }
        if (!Number.isInteger(page.nextOffset) || page.nextOffset !== packages.length || page.nextOffset! <= offset || page.nextOffset! >= expectedTotal!)
            throw invalid()
        return await readPage(page.nextOffset!)
    }
    return await readPage(0)
}
