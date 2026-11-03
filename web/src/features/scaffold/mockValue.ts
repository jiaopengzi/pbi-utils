/**
 * FilePath    : pbi-utils\web\src\features\scaffold\mockValue.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式浏览器模拟的JSON传输值复制, 不访问磁盘或网络.
 */
/** 通过 JSON 复制传输值, 与宿主序列化边界保持一致. */
export function copy<T>(value: T): T {
    return JSON.parse(JSON.stringify(value)) as T
}
