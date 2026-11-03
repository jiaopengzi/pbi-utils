/**
 * FilePath    : pbi-utils\web\src\main.ts
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 挂载 Vue 应用并加载应用样式.
 */

import { createApp } from "vue"
import "./app/style.scss"

const page = import("./app/App.vue")
void page.then(({ default: component }) => createApp(component).mount("#app"))
