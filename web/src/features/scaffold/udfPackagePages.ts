/**
 * FilePath    : pbi-utils\web\src\features\scaffold\udfPackagePages.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 补足当前包显示页的短传输页, 复核身份与同代见证, 不预取包正文.
 */
import type { UdfRequest } from "@/composables/features/scaffold/useUdfRequests"
import type { UdfPackagePage } from "./contracts"
/** 读取一个显示页; 参数为来源见证和分页查询, 混代/重复/不完整回执抛错, 不返回部分列表. */
export async function readUdfPackagePage(
    request: UdfRequest,
    origin: { source: string; sourceBaseline?: string | null; catalogBaseline?: string | null },
    query: { query: string; includePrerelease: boolean; offset: number; limit: number; packageVersions?: Record<string, string> },
    invalid: () => Error,
): Promise<UdfPackagePage> {
    const items: UdfPackagePage["items"] = [],
        identities = new Set<string>()
    let total: number | undefined,
        status: UdfPackagePage["status"] | undefined,
        offset = query.offset,
        nextOffset: number | null = offset
    if (!Number.isInteger(offset) || offset < 0 || !Number.isInteger(query.limit) || query.limit < 1 || query.limit > 500) throw invalid()
    while (items.length < query.limit && nextOffset !== null) {
        // oxlint-disable-next-line no-await-in-loop -- 后续短页依赖上一回执 offset, 同一取消身份串行排空.
        const response = await request("udf.catalog", {
            action: "packages",
            ...origin,
            ...query,
            catalogBaseline: status?.baseline ?? origin.catalogBaseline,
            offset,
            limit: query.limit - items.length,
        })
        const page = response.packagePage
        if (
            !page ||
            !Number.isInteger(page.total) ||
            page.total < 0 ||
            (total !== undefined && page.total !== total) ||
            page.items.length > query.limit - items.length ||
            !page.status.initialized ||
            !page.status.baseline ||
            (origin.catalogBaseline && page.status.baseline !== origin.catalogBaseline) ||
            (status && page.status.baseline !== status.baseline)
        )
            throw invalid()
        total = page.total
        status = page.status
        for (const item of page.items) {
            const key = item.id.toLowerCase()
            if (
                !key ||
                identities.has(key) ||
                !item.version ||
                !item.hash ||
                typeof item.description !== "string" ||
                typeof item.authors !== "string" ||
                !Array.isArray(item.tags) ||
                item.tags.some((tag) => typeof tag !== "string") ||
                !Number.isInteger(item.functionCount) ||
                item.functionCount < 1 ||
                (item.committedAt !== null && (typeof item.committedAt !== "string" || !Number.isFinite(Date.parse(item.committedAt)))) ||
                (item.iconKey !== null && !/^[a-f0-9]{64}$/.test(item.iconKey))
            )
                throw invalid()
            identities.add(key)
            items.push(item)
        }
        offset += page.items.length
        if (
            offset > total ||
            (page.nextOffset === null && offset !== total) ||
            (page.nextOffset !== null && (!Number.isInteger(page.nextOffset) || page.nextOffset !== offset || !page.items.length || page.nextOffset >= total))
        )
            throw invalid()
        nextOffset = page.nextOffset
    }
    if (!status || total === undefined) throw invalid()
    return { items, total, nextOffset, status }
}
