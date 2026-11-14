/**
 * FilePath    : pbi-utils\web\src\app\productReleaseName.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 使用宿主提供的精确发布标签统一显示产品版本.
 */

/** 将已核验的发布标签显示为完整产品名称, 缺失时返回统一未知值. */
export function productReleaseName(tag: string | null | undefined): string {
    return tag ? `pbi-utils ${tag}` : "-"
}
