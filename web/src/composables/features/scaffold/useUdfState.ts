/**
 * FilePath    : pbi-utils\web\src\composables\features\scaffold\useUdfState.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : UDF选择器当前挂载的状态与来源身份, 不跨文档保存请求.
 */
import { computed, reactive, ref, shallowRef } from "vue"
import type { ModelRef } from "vue"
import type { BridgeClient } from "@/bridge/client"
import type {
    LocalUdfDefinition,
    LocalUdfEntry,
    UdfCatalogStatus,
    UdfCatalogPackage,
    UdfPackage,
    UdfSelection,
    UdfSourceEntry,
} from "@/features/scaffold/contracts"
/** 创建目录和管理器状态, 来源身份继续绑定宿主稳定 ID 与基线.
 * @param bridge 当前文档的类型化宿主桥接.
 * @param model 外部持有的显式函数选择.
 * @param disabled 当前外层业务禁用状态的读取器.
 * @returns 仅属于当前挂载的目录和模态状态.
 */
export function useUdfState(bridge: BridgeClient, model: ModelRef<UdfSelection[]>, disabled: () => boolean) {
    const source = ref("daxlib"),
        query = ref(""),
        searchQuery = ref(""),
        searching = ref(false),
        prerelease = ref(false)
    const sources = ref<UdfSourceEntry[]>([]),
        sourceManager = ref(false),
        sourceDeleteTarget = shallowRef<UdfSourceEntry>(),
        sourceName = ref(""),
        sourceUrl = ref(""),
        sourceGitRef = ref(""),
        searched = ref(false)
    const locals = ref<LocalUdfEntry[]>([]),
        localPackageTags = ref<Record<string, string>>({})
    const catalogStatus = shallowRef<UdfCatalogStatus>(),
        statuses = ref<Record<string, UdfCatalogStatus>>({}),
        resolved = ref<Record<string, UdfPackage>>({})
    const catalogCache = ref(false),
        catalogRetrievedAt = ref<string | null>(null)
    const packageItems = ref<UdfCatalogPackage[]>([]),
        page = ref(1),
        pageSize = ref(50),
        total = ref(0),
        packageTags = ref<Record<string, string>>({}),
        tagOptions = ref<Record<string, string[]>>({}),
        icons = ref<Record<string, string | null>>({}),
        detail = shallowRef<UdfPackage>(),
        detailOpen = ref(false),
        localDetailOpen = ref(false),
        tagPickerOpen = ref(false),
        tagPickerId = ref(""),
        tagPickerValue = ref("latest"),
        operation = ref<"read" | "refresh">("read"),
        uncertain = ref(false)
    const busy = ref(false),
        requestId = ref(""),
        cancelling = ref(false),
        lastErrorCode = ref(""),
        committed = ref(false),
        error = ref(""),
        bulkImport = ref(false),
        revalidation = ref(false),
        editor = ref(false),
        manager = ref(false)
    const target = shallowRef<LocalUdfEntry>(),
        deleteTarget = shallowRef<LocalUdfEntry>()
    const definition = reactive<LocalUdfDefinition>({ schemaVersion: 1, name: "", version: "1.0.0", expression: "", description: "", dependencies: [] })
    const locked = computed(() => disabled() || busy.value)
    const currentSource = computed(() => sources.value.find((item) => item.id === source.value))
    const catalogOrigin = computed(() => ({
        source: source.value,
        ...(currentSource.value?.kind === "custom" ? { sourceBaseline: currentSource.value.baseline } : {}),
    }))
    const catalogReadOrigin = computed(() => ({ ...catalogOrigin.value, catalogBaseline: catalogStatus.value?.baseline }))
    const lifecycle = { disposed: false, callGeneration: 0 }
    return {
        bridge,
        model,
        disabled,
        lifecycle,
        source,
        query,
        searchQuery,
        searching,
        prerelease,
        sources,
        sourceManager,
        sourceDeleteTarget,
        sourceName,
        sourceUrl,
        sourceGitRef,
        catalogStatus,
        statuses,
        resolved,
        searched,
        locals,
        localPackageTags,
        catalogCache,
        catalogRetrievedAt,
        packageItems,
        page,
        pageSize,
        total,
        packageTags,
        tagOptions,
        icons,
        detail,
        detailOpen,
        localDetailOpen,
        tagPickerOpen,
        tagPickerId,
        tagPickerValue,
        operation,
        uncertain,
        busy,
        requestId,
        cancelling,
        lastErrorCode,
        committed,
        error,
        bulkImport,
        revalidation,
        editor,
        manager,
        target,
        deleteTarget,
        definition,
        locked,
        currentSource,
        catalogOrigin,
        catalogReadOrigin,
    }
}
export type UdfState = ReturnType<typeof useUdfState>
