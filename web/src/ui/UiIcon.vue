<!--
 * FilePath    : pbi-utils\web\src\ui\UiIcon.vue
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 复用现有本地 SVG 图标及 BASE_URL, 不引入参考项目图标注册方案.
-->
<script setup lang="ts">
defineProps<{ name: string }>()
const base = import.meta.env.BASE_URL
</script>
<template><img class="ui-icon" :src="`${base}icons/${name}.svg`" alt="" /></template>
