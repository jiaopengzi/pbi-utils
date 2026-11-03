/**
 * FilePath    : pbi-utils\web\tests\support\domApis.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 只补齐 jsdom 缺失的测试接口, 不模拟真实布局或默认允许系统剪贴板写入.
 */
/** 补齐缺失接口并保留已有实现; 无参数与返回值, 属性不可定义时直接失败而不隐藏初始化错误. */
export function installDomApiStubs(): void {
    // 保留环境已有实现; 几何桩只允许 CodeMirror 执行测量, 不提供布局事实.
    if (!("getClientRects" in Range.prototype)) {
        Object.defineProperty(Range.prototype, "getClientRects", {
            configurable: true,
            writable: true,
            /** 返回可迭代的空矩形集合; 任意索引均无测量结果. */
            value(): DOMRectList {
                return Object.assign([], { item: () => null })
            },
        })
    }
    if (!("getBoundingClientRect" in Range.prototype)) {
        Object.defineProperty(Range.prototype, "getBoundingClientRect", {
            configurable: true,
            writable: true,
            /** 返回零尺寸矩形, 不推断元素位置或真实视口布局. */
            value(): DOMRect {
                return new DOMRect()
            },
        })
    }

    if (!navigator.clipboard) {
        Object.defineProperty(navigator, "clipboard", {
            configurable: true,
            value: {
                /** 接收待复制文本但始终拒绝; 具体用例须显式模拟并断言写入参数. */
                async writeText(_text: string): Promise<void> {
                    throw new Error("剪贴板写入必须由具体测试显式模拟")
                },
            },
        })
    }
}
