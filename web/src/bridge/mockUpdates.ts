/**
 * FilePath    : pbi-utils\web\src\bridge\mockUpdates.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 显式模拟更新设置与无发布结果, 不联网、不保存用户数据或打开浏览器.
 */
import type { ProductUpdateCheck, ProductUpdateSettings } from "./updateContracts"
import type { MockSend } from "./mockRequest"

/** 仅由显式 MockTransport 持有的更新模拟状态. */
export class MockUpdates {
    private settings: ProductUpdateSettings = { automaticCheck: true, source: "github" }
    private checks = new Map<string, ProductUpdateCheck>()
    /** 绑定模拟响应出口, 不访问真实宿主资源. */
    constructor(private send: MockSend) {}
    /** 按真实白名单处理模拟请求; 参数非法时返回固定错误, 未匹配时交回分发器. */
    handle(request: { id: string; method: string; params: Record<string, unknown> }): boolean {
        const { id, method, params } = request
        if (!method.startsWith("updates.")) return false
        const reply = (result: unknown) => this.send({ protocolVersion: 1, id, result })
        const fail = (code: string) => this.send({ protocolVersion: 1, id, error: { code, message: code } })
        const source = params.source
        const expected =
            method === "updates.status"
                ? []
                : method === "updates.save"
                  ? ["source", "automaticCheck"]
                  : method === "updates.check"
                    ? ["source", "automatic"]
                    : method === "updates.open"
                      ? ["source"]
                      : null
        if (
            !expected ||
            (method === "updates.save" ? Object.keys(params).length === 0 : Object.keys(params).length !== expected.length) ||
            Object.keys(params).some((key) => !expected.includes(key)) ||
            (method !== "updates.status" && (method !== "updates.save" || Object.hasOwn(params, "source")) && source !== "github" && source !== "gitee")
        )
            fail("VALIDATION_ERROR")
        else if (method === "updates.status") reply({ settings: { ...this.settings }, lastCheck: this.checks.get(this.settings.source) ?? null })
        else if (method === "updates.save") {
            if (Object.hasOwn(params, "automaticCheck") && typeof params.automaticCheck !== "boolean") fail("VALIDATION_ERROR")
            else {
                if (typeof params.automaticCheck === "boolean") this.settings.automaticCheck = params.automaticCheck
                if (source === "github" || source === "gitee") this.settings.source = source
                reply({ settings: { ...this.settings }, lastCheck: this.checks.get(this.settings.source) ?? null })
            }
        } else if (method === "updates.check") {
            if (typeof params.automatic !== "boolean") fail("VALIDATION_ERROR")
            else if (params.automatic && (!this.settings.automaticCheck || this.checks.has(String(source))))
                reply({ checkedNow: false, check: this.checks.get(String(source)) ?? null })
            else {
                const check: ProductUpdateCheck = {
                    source: source as ProductUpdateSettings["source"],
                    currentVersion: "browser-preview",
                    status: "noRelease",
                    checkedAtUtc: new Date().toISOString(),
                    latestVersion: null,
                    releases: [],
                    errorCode: null,
                    isSimulation: true,
                }
                this.checks.set(check.source, check)
                reply({ checkedNow: true, check })
            }
        } else reply({ opened: false, isSimulation: true })
        return true
    }
}
