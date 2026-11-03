<!--
 * FilePath    : pbi-utils\web\src\app\RecentConnectionsPanel.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 为本地文件与已登录云模型复用最近连接列表及全局显示开关.
-->
<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue"
import { ElMessageBox } from "element-plus"
import { useI18n } from "@/composables/i18n/useI18n"
import UiIcon from "@/ui/UiIcon.vue"
import UiPanel from "@/ui/UiPanel.vue"

/** 仅展示调用方已按来源和账号权限筛选的记录, 点击只回传宿主绑定标识. */
const props = defineProps<{
    items: { id: string; name: string; detail: string }[]
    disabled?: boolean
    visibilityDisabled?: boolean
    emptyDescription?: string
}>()
/** 删除只发出已确认的宿主记录标识, 确认状态供父层锁定来源和导航. */
const emit = defineEmits<{ open: [id: string]; hide: []; remove: [id: string]; confirming: [value: boolean] }>()
const { t } = useI18n()
const confirming = ref(false)
let mounted = true
let confirmationPending = false
let confirmationInvalid = false
let confirmedItem: { id: string; name: string; detail: string } | undefined

/** 仅接受仍存在且显示身份未变的记录, 不让旧确认覆盖新的列表内容. */
function canRemove(): boolean {
    const selected = confirmedItem
    return (
        mounted &&
        !props.disabled &&
        !confirmationInvalid &&
        !!selected &&
        props.items.some((item) => item.id === selected.id && item.name === selected.name && item.detail === selected.detail)
    )
}

/** 确认中出现请求锁或记录变化后永久作废本次确认, 恢复列表也不重新授予删除权限. */
watch(
    () => [props.disabled, props.items],
    () => {
        if (confirming.value && !canRemove()) confirmationInvalid = true
    },
    { deep: true, flush: "sync" },
)

/** 结束确认时先解除父层锁, 让后续删除事件进入独立的宿主请求锁. */
function endConfirmation(): void {
    if (!confirming.value) return
    confirming.value = false
    emit("confirming", false)
}

/** 按当前宿主标识发出打开事件, 确认及外部请求锁期间不触发连接. */
function open(id: string): void {
    if (!props.disabled && !confirming.value && props.items.some((item) => item.id === id)) emit("open", id)
}

/** 隐藏模块只改变全局展示偏好, 确认与偏好保存期间保持原状态. */
function hide(): void {
    if (!props.visibilityDisabled && !confirming.value) emit("hide")
}

/** 先以记录原名称确认移除, 取消或关闭静默返回, 复核记录及外部锁后才发出删除事件. */
async function remove(id: string): Promise<void> {
    if (props.disabled || confirmationPending) return
    const item = props.items.find((value) => value.id === id)
    if (!item) return
    confirmedItem = { ...item }
    confirmationPending = true
    confirmationInvalid = false
    confirming.value = true
    emit("confirming", true)
    let accepted = false
    try {
        await ElMessageBox.confirm(t("app.recentRemoveConfirm", { name: item.name }), t("app.recentRemoveTitle"), {
            confirmButtonText: t("app.recentRemove"),
            cancelButtonText: t("app.cancel"),
            type: "warning",
        })
        accepted = canRemove()
    } catch {
        // 用户取消或关闭确认框时保留原记录.
    } finally {
        endConfirmation()
    }
    // 父层解除导航锁后, 等待来源面板的 disabled 属性同步, 避免合法删除被旧锁拒绝.
    if (accepted) await nextTick()
    if (accepted && canRemove()) emit("remove", id)
    confirmedItem = undefined
    confirmationPending = false
}

/** 父层卸载列表后撤回确认状态, 延迟返回的确认结果不得发出删除事件. */
onBeforeUnmount(() => {
    mounted = false
    confirmationInvalid = true
    endConfirmation()
})
</script>
<template>
    <UiPanel class="recent-files-panel">
        <div class="recent-files-heading">
            <h2>{{ t("app.recent") }}</h2>
            <el-switch
                :model-value="true"
                :aria-label="t('uiSettings.recentFiles')"
                :active-text="t('uiSettings.recentFiles')"
                :disabled="visibilityDisabled || confirming"
                @change="hide"
            />
        </div>
        <el-empty v-if="!items.length" :description="emptyDescription ?? t('app.recentEmpty')" :image-size="64" />
        <div class="recent-projects">
            <div v-for="item in items" :key="item.id" class="recent-project-row">
                <el-button class="recent-project-open" :disabled="disabled || confirming" :title="item.detail" @click="open(item.id)">
                    <strong>{{ item.name }}</strong>
                    <small>{{ item.detail }}</small>
                </el-button>
                <el-button
                    class="recent-project-remove"
                    :disabled="disabled || confirming"
                    :aria-label="t('app.recentRemoveLabel', { name: item.name })"
                    :title="t('app.recentRemove')"
                    @click="remove(item.id)"
                >
                    <UiIcon name="trash-2" />
                </el-button>
            </div>
        </div>
    </UiPanel>
</template>

<style scoped lang="scss">
/* 删除操作使用实色危险态增强辨识度, 固定尺寸并保留禁用与键盘焦点边界. */
.recent-project-row .recent-project-remove {
    &:hover:not(:disabled),
    &:focus-visible:not(:disabled) {
        color: #fff;
        border-color: var(--error);
        background: var(--error);

        :deep(.ui-icon) {
            filter: brightness(0) invert(1);
        }
    }

    &:focus-visible:not(:disabled) {
        outline: 3px solid var(--brand-accent);
        outline-offset: 2px;
    }
}
</style>
