<template>
  <main class="container">
    <nav style="margin-bottom: 1rem">
      <RouterLink to="/">← Back to UTS home</RouterLink>
    </nav>

    <!-- not found -->
    <div v-if="failed" class="card" style="text-align:center; padding: 3rem 1rem">
      <h2 style="color: var(--navy)">Shipment not found</h2>
      <p class="muted">
        Tracking number <span class="mono">{{ number }}</span> does not exist (yet).
        Either a typo slipped in, or the parcel is so unhinged it registered itself under a different identity.
      </p>
      <RouterLink to="/">Try another number</RouterLink>
    </div>

    <div v-else-if="loading" class="spinner">Consulting the logistics oracle…</div>

    <template v-else-if="info">
      <!-- summary -->
      <div class="card">
        <div class="toolbar">
          <div>
            <h2 style="margin-bottom:.15rem; color:var(--navy)">
              <span class="mono">{{ info.number }}</span>
            </h2>
            <div class="muted small">
              {{ info.originCode ?? '???' }} → {{ info.destinationCode }}
              <span v-if="info.destinationName"> · {{ info.destinationName }}</span>
            </div>
          </div>
          <StatusBadge :status="info.status" />
        </div>
        <dl class="grid-dl" style="margin-top:.8rem">
          <dt>Registered</dt>
          <dd>{{ fmtDateTime(info.createdAt) }}</dd>
          <dt>Delivered</dt>
          <dd>{{ info.deliveredAt ? fmtDateTime(info.deliveredAt) : 'Pending' }}</dd>
        </dl>
      </div>

      <!-- unlock box -->
      <div v-if="info.unlockRequired" class="card">
        <h2>Protected shipment details</h2>
        <p class="small muted">
          Enter the 6-digit access code provided by your courier to see
          sender, recipient, contents and the planned departure.
        </p>
        <form class="trackbox" style="margin-top:.6rem" @submit.prevent="unlock">
          <input
            v-model="pin"
            class="mono"
            inputmode="numeric"
            maxlength="6"
            placeholder="••••••"
            autocomplete="off"
          />
          <button class="btn btn--accent" type="submit" :disabled="unlocking">
            {{ unlocking ? '…' : 'Unlock' }}
          </button>
        </form>
        <p v-if="unlockError" class="callout callout--error small" style="margin:.8rem 0 0">{{ unlockError }}</p>
      </div>

      <!-- details after unlock -->
      <div v-else class="card">
        <h2>Shipment details</h2>
        <dl class="grid-dl">
          <dt>Sender</dt>
          <dd>{{ info.senderName }}</dd>
          <dt>Recipient</dt>
          <dd>{{ info.recipientName }}</dd>
          <dt>Origin</dt>
          <dd>{{ info.originCode }}<template v-if="info.originName"> · {{ info.originName }}</template></dd>
          <dt>Destination</dt>
          <dd>{{ info.destinationCode }}<template v-if="info.destinationName"> · {{ info.destinationName }}</template></dd>
          <dt>Contents</dt>
          <dd>{{ info.description }}</dd>
          <dt>Transport</dt>
          <dd>{{ modeLabel(info.transportMode ?? 'unknown') }}</dd>
          <dt>Planned trip</dt>
          <dd>
            <template v-if="info.tripTitle">
              {{ info.tripTitle }}
              <span v-if="info.tripPlannedDate" class="chip" style="margin-left:.35rem">
                {{ fmtDate(info.tripPlannedDate) }}
              </span>
            </template>
            <template v-else>To be scheduled</template>
          </dd>
          <dt>Internal notes</dt>
          <dd>{{ info.notes || '—' }}</dd>
        </dl>
      </div>

      <!-- timeline -->
      <div class="card">
        <h2>Tracking history</h2>
        <Timeline :events="info.events" />
      </div>
    </template>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute, RouterLink } from 'vue-router'
import { api, ApiError } from '@/api'
import type { TrackInfo } from '@/types'
import { fmtDate, fmtDateTime, modeLabel } from '@/format'
import StatusBadge from '@/components/StatusBadge.vue'
import Timeline from '@/components/Timeline.vue'

const route = useRoute()
const number = ref((route.params.number as string) ?? '')
const info = ref<TrackInfo | null>(null)
const loading = ref(true)
const failed = ref(false)

const pin = ref('')
const unlocking = ref(false)
const unlockError = ref('')

async function load(num: string) {
  loading.value = true
  failed.value = false
  info.value = null
  pin.value = ''
  unlockError.value = ''
  if (!num) {
    failed.value = true
    loading.value = false
    return
  }
  try {
    info.value = await api.get<TrackInfo>(`/api/track/${encodeURIComponent(num)}`)
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

async function unlock() {
  if (!/^\d{6}$/.test(pin.value)) {
    unlockError.value = 'The access code is exactly 6 digits.'
    return
  }
  unlocking.value = true
  unlockError.value = ''
  try {
    info.value = await api.post<TrackInfo>(`/api/track/${encodeURIComponent(number.value)}/unlock`, { pin: pin.value })
  } catch (e) {
    if (e instanceof ApiError && e.status === 400) {
      unlockError.value = 'Wrong access code. Doubly和朋友-check it.' as unknown as string
    } else {
      unlockError.value = e instanceof ApiError ? e.message : 'Something went wrong.'
    }
  } finally {
    unlocking.value = false
  }
}

onMounted(() => load(number.value))
watch(
  () => route.params.number,
  (n) => {
    number.value = (n as string) ?? ''
    load(number.value)
  },
)
</script>
