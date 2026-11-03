<!--
 * FilePath    : pbi-utils\web\src\ui\UiSteps.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 可自由往返的品牌步骤条, 使用真实按钮提供键盘选择和在途切换保护.
-->
<script setup lang="ts">
import { computed, nextTick } from "vue"

const props = defineProps<{ modelValue: string; items: string[][]; disabled?: boolean }>()
const emit = defineEmits<{ "update:modelValue": [value: string] }>()
const active = computed(() =>
    Math.max(
        0,
        props.items.findIndex((item) => item[0] === props.modelValue),
    ),
)

/** 仅切换显示步骤, 不将前面的可选步骤标为业务完成. */
function select(index: number): void {
    const id = props.items[index]?.[0]
    if (!props.disabled && id) emit("update:modelValue", id)
}

/** 方向及首尾键移动当前步骤和焦点, 在途保留 Tab 离开且阻止导航冒泡到外层页签. */
async function navigate(event: KeyboardEvent, index: number): Promise<void> {
    if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return
    event.preventDefault()
    event.stopPropagation()
    if (props.disabled) return
    const next = event.key === "Home" ? 0 : event.key === "End" ? props.items.length - 1 : index + (["ArrowLeft", "ArrowUp"].includes(event.key) ? -1 : 1)
    const target = Math.max(0, Math.min(props.items.length - 1, next))
    const root = (event.currentTarget as HTMLElement).closest(".ui-steps")
    select(target)
    await nextTick()
    root?.querySelectorAll<HTMLButtonElement>(".ui-step-button")[target]?.focus()
}
</script>
<template>
    <nav class="ui-steps" :aria-disabled="disabled || undefined">
        <el-steps :active="active" finish-status="wait" align-center>
            <el-step v-for="(item, index) in items" :key="item[0]">
                <template #title>
                    <button
                        :id="`step-${item[0]}`"
                        type="button"
                        class="ui-step-button"
                        :data-step="item[0]"
                        :tabindex="index === active ? 0 : -1"
                        :aria-current="index === active ? 'step' : undefined"
                        :aria-disabled="disabled || undefined"
                        :aria-controls="`section-${item[0]}`"
                        @click="select(index)"
                        @keydown="navigate($event, index)"
                    >
                        {{ item[1] }}
                    </button>
                </template>
            </el-step>
        </el-steps>
    </nav>
</template>
<style lang="scss">
.ui-steps {
    container: workflow-steps / inline-size;
    min-width: 0;
    padding: 8px 0;
    .el-steps {
        min-width: 0;
        flex-wrap: wrap;
        gap: 12px 0;
    }
    .el-step {
        min-width: 0;
        // 覆盖组件按步骤总数生成的行内 basis/max-width, 让最后一步同样参与按可用宽度换行.
        flex: 1 1 130px !important;
        max-width: none !important;
    }
    .el-step__main {
        min-width: 0;
    }
    // 真实标题按钮的伪元素覆盖整步, 数字、连线附近和文字共用同一点击目标.
    .ui-step-button::after {
        content: "";
        position: absolute;
        inset: 0;
        z-index: 2;
        border-radius: var(--el-border-radius-base);
    }
    .el-step__head.is-process {
        color: var(--brand-primary);
        border-color: var(--brand-primary);
        // 当前步骤以品牌黄实底配深蓝数字, 深蓝外圈维持与浅色画布的清晰边界.
        .el-step__icon.is-text {
            background-color: var(--brand-accent);
        }
    }
}
// 步骤换行后连接线无法表达跨行关系, 仅隐藏装饰连线, 保留真实数字与按钮.
@container workflow-steps (max-width: 649px) {
    .ui-steps .el-step__line {
        display: none;
    }
}
.ui-step-button {
    max-width: 100%;
    overflow-wrap: anywhere;
    font: inherit;
    font-weight: 500;
    color: var(--el-text-color-regular);
    background: transparent;
    border: 1px solid transparent;
    border-radius: var(--el-border-radius-base);
    padding: 6px 10px;
    cursor: pointer;
    line-height: 1.5;
    &:hover,
    &[aria-current="step"] {
        color: var(--el-color-primary);
    }
    &[aria-current="step"] {
        font-weight: 700;
        // 加粗和下划线同时指示当前步骤, 不仅依赖颜色差别.
        text-decoration-line: underline;
        text-decoration-color: var(--brand-accent);
        text-decoration-thickness: 3px;
        text-underline-offset: 6px;
    }
    &:focus-visible {
        outline: none;
        &::after {
            outline: 2px solid var(--el-color-warning);
            outline-offset: -2px;
        }
    }
    &[aria-disabled="true"] {
        cursor: default;
    }
}
</style>
