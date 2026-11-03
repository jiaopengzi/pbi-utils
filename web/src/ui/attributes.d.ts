/**
 * FilePath    : pbi-utils\web\src\ui\attributes.d.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为严格模板检查声明合法的无障碍与 DOM 透传属性, 保留未知业务属性和事件诊断.
 */

import type { AriaAttributes, HTMLAttributes } from "vue"

declare module "vue" {
    /** 单根组件允许透传这些已知 DOM 属性, 不开放任意字符串属性或未知事件. */
    interface ComponentCustomProps extends AriaAttributes, Pick<HTMLAttributes, "role" | "title" | "onSubmit"> {
        [attribute: `data-${string}`]: unknown
    }
    /** HTML 允许任意 data 属性, 属性值仍按业务原值透传. */
    interface HTMLAttributes {
        [attribute: `data-${string}`]: unknown
    }
}
