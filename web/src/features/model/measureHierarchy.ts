/**
 * FilePath    : pbi-utils\web\src\features\model\measureHierarchy.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按准确对象身份构建度量值的表与显示文件夹层级及路径搜索.
 */
import type { ProjectSnapshot } from "@/bridge/client"

export type HierarchyMeasure = ProjectSnapshot["model"]["measures"][number]
export type MeasureHierarchyNode = { key: string; label: string; path: string; children: MeasureHierarchyNode[]; measure?: HierarchyMeasure }

/** table/name 是操作身份, 与可重复出现的显示文件夹路径无关; 返回无歧义复合键. */
export function measureKey(measure: { table: string; name: string }): string {
    return JSON.stringify([measure.table, measure.name])
}

/** 分号分隔多个显示位置, 反斜线分隔层级; 空路径归所属表, 不改写实际模型属性. */
function folderPaths(measure: HierarchyMeasure): string[][] {
    const unique = new Map<string, string[]>()
    for (const location of (measure.displayFolder || "").split(";")) {
        const parts = location.split("\\").filter((part) => part.trim().length > 0)
        unique.set(JSON.stringify(parts), parts)
    }
    return [...unique.values()]
}

/** 每个搜索词均匹配同一显示路径、表名、名称或 DAX; 空查询返回全部, 不抛出用户正则错误. */
function matches(measure: HierarchyMeasure, path: string[], query: string): boolean {
    const text = `${measure.table} ${path.join("\\")} ${measure.name} ${measure.expression}`.toLowerCase()
    return query
        .toLowerCase()
        .trim()
        .split(/\s+/u)
        .every((word) => text.includes(word))
}

/** 返回匹配路径的唯一度量值集合, 保持权威顺序, 全选结果不会重复提交多文件夹内的同一对象. */
export function filterHierarchyMeasures(measures: HierarchyMeasure[], query: string): HierarchyMeasure[] {
    return measures.filter((measure) => folderPaths(measure).some((path) => matches(measure, path, query)))
}

/** 为匹配显示位置保留完整祖先路径, 同表同路径合并目录, 同名跨表仍保持独立身份; 返回新的展示树. */
export function buildMeasureHierarchy(measures: HierarchyMeasure[], query: string): MeasureHierarchyNode[] {
    const roots: MeasureHierarchyNode[] = []
    const groups = new Map<string, MeasureHierarchyNode>()
    for (const measure of measures) {
        for (const folders of folderPaths(measure)) {
            if (!matches(measure, folders, query)) continue
            const parts = [measure.table, ...folders]
            let children = roots
            for (let depth = 0; depth < parts.length; depth++) {
                const path = parts.slice(0, depth + 1)
                const key = JSON.stringify(["group", ...path])
                let group = groups.get(key)
                if (!group) {
                    group = { key, label: parts[depth]!, path: path.join("\\"), children: [] }
                    groups.set(key, group)
                    children.push(group)
                }
                children = group.children
            }
            children.push({ key: JSON.stringify(["measure", ...parts, measure.name]), label: measure.name, path: parts.join("\\"), children: [], measure })
        }
    }
    return roots
}

/** 收集目录身份以管理折叠状态, 不把叶子当目录, 不改变模型或选择. */
export function hierarchyGroupKeys(nodes: MeasureHierarchyNode[]): string[] {
    return nodes.flatMap((node) => (node.measure ? [] : [node.key, ...hierarchyGroupKeys(node.children)]))
}

/** 收集当前展示子树中的唯一对象身份, 包括折叠目录后代; 多显示位置不重复, 不包含搜索已过滤的对象. */
export function hierarchyMeasureKeys(nodes: MeasureHierarchyNode[]): string[] {
    const keys = new Set<string>()
    /** 深度遍历子树并沿首次出现顺序收集身份, 不修改输入节点或模型定义. */
    function collect(children: MeasureHierarchyNode[]): void {
        for (const node of children) {
            if (node.measure) keys.add(measureKey(node.measure))
            else collect(node.children)
        }
    }
    collect(nodes)
    return [...keys]
}
