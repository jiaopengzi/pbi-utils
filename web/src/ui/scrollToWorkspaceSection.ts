/**
 * FilePath    : pbi-utils\web\src\ui\scrollToWorkspaceSection.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 搜索定位工作区分区时按真实吸顶栏高度保留可见间距.
 */

/** 将已挂载的 target 滚动到实际工作区顶栏之下并留出 18px 间距; 无目标不操作, 使用浏览器原生滚动行为. */
export function scrollToWorkspaceSection(target?: HTMLElement): void {
    if (!target) return
    const header = document.querySelector<HTMLElement>(".workspace-header")
    target.style.scrollMarginBlockStart = `${(header?.getBoundingClientRect().height ?? 0) + 18}px`
    target.scrollIntoView({ block: "start" })
}
