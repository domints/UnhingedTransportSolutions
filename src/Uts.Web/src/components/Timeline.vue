<template>
  <p v-if="events.length === 0" class="empty">No events yet — this shipment is beautifully uneventful.</p>
  <ol v-else class="timeline">
    <li v-for="(e, i) in events" :key="i">
      <span class="timeline__dot" :class="'timeline__dot--' + e.type"></span>
      <div class="timeline__msg">{{ e.message }}</div>
      <div class="timeline__time">{{ fmtDateTime(e.createdAt) }}</div>
    </li>
  </ol>
</template>

<script setup lang="ts">
import type { TrackEvent } from '@/types'
import { fmtDateTime } from '@/format'

defineProps<{ events: TrackEvent[] }>()
</script>
