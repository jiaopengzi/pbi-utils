/**
 * FilePath    : pbi-utils\web\src\features\code\editorPhrases.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 按当前翻译函数生成 CodeMirror 本地化标签, 不缓存语言状态.
 */

/** 向编辑器官方 phrases 提供当前语言, 参数标记保留供 CodeMirror 插入安全行号与数量. */
export function editorPhrases(t: (key: string) => string): Record<string, string> {
    return {
        Find: t("model.searchFind"),
        Replace: t("model.searchReplace"),
        next: t("model.searchNext"),
        previous: t("model.searchPrevious"),
        all: t("model.searchAll"),
        "match case": t("model.searchMatchCase"),
        regexp: t("model.searchRegexp"),
        "by word": t("model.searchByWord"),
        replace: t("model.searchReplaceButton"),
        "replace all": t("model.searchReplaceAll"),
        close: t("model.searchClose"),
        "Go to line": t("model.searchGoToLine"),
        go: t("model.searchGo"),
        "replaced match on line $": t("model.searchReplacedLine"),
        "replaced $ matches": t("model.searchReplacedCount"),
        "current match": t("model.searchCurrentMatch"),
        "on line": t("model.searchOnLine"),
        "Control character": t("model.searchControlCharacter"),
    }
}
