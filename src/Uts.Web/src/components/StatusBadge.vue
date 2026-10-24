<template>
  <span :class="'badge badge--' + status">{{ statusLabel(status) }}</span>
</template>

<script setup lang="ts">
import { statusLabel } from '@/format'

defineProps<{ status: string }>()
</script>
