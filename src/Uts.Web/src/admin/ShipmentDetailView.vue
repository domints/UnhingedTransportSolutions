<template>
  <main class="container">
    <div v-if="loading" class="spinner">Loading shipment…</div>

    <div v-else-if="!s" class="card empty">Shipment not found. <RouterLink to="/admin">Back</RouterLink></div>

    <template v-else>
      <nav style="margin-bottom:1rem"><RouterLink to="/admin">← All shipments</RouterLink></nav>

      <div class="callout callout--ok" v-if="regeneratedPin">
        <strong>New access code (shown once):</strong>
        <span class="mono copynum" style="margin-left:.4rem">
          <strong>{{ regeneratedPin }}</strong>
          <button type="button" @click="copy(regeneratedPin)">copy</button>
        </span>
      </div>

      <div class="card">
        <div class="toolbar">
          <div>
            <h2 style="margin-bottom:.2rem; color:var(--navy)">
              <span class="mono copynum">
                {{ s.trackingNumber }}
                <button type="button" @click="copy(s.trackingNumber)">copy</button>
              </span>
            </h2>
            <div class="muted small">
              {{ s.originCode ?? '???' }} → {{ s.destinationCode }}
              <template v-if="s.destinationName"> · {{ s.destinationName }}</template>
            </div>
          </div>
          <StatusBadge :status="s.status" />
        </div>

        <dl class="grid-dl" style="margin-top:1rem">
          <dt>Sender</dt>
          <dd>{{ s.senderName }}</dd>
          <dt>Recipient</dt>
          <dd>{{ s.recipientName }}</dd>
          <dt>Origin</dt>
          <dd>{{ s.originCode }}<template v-if="s.originName"> · {{ s.originName }}</template></dd>
          <dt>Destination</dt>
          <dd>{{ s.destinationCode }} · {{ s.destinationName }}</dd>
          <dt>Contents</dt>
          <dd style="grid-column: span 3">{{ s.description }}</dd>
          <dt>Transport</dt>
          <dd>{{ modeLabel(s.transportMode) }}</dd>
          <dt>Registered</dt>
          <dd>{{ fmtDateTime(s.createdAt) }}</dd>
          <dt>Delivered</dt>
          <dd>{{ s.deliveredAt ? fmtDateTime(s.deliveredAt) : 'Pending' }}</dd>
          <template v-if="s.notes">
            <dt>Notes</dt>
            <dd style="grid-column: span 3">{{ s.notes }}</dd>
          </template>
        </dl>
      </div>

      <div class="card">
        <h2>Status</h2>
        <div class="btnrow">
          <button
            v-for="next in TRANSITIONS[s.status] ?? []"
            :key="next"
            class="btn"
            :class="destinationClass(next)"
            @click="changeStatus(next)"
          >
            {{ statusLabel(next) }}
          </button>
          <span v-if="transitionsEmpty()" class="muted small">Final state — no further transitions.</span>
        </div>
        <p v-if="statusError" class="callout callout--error small" style="margin:.8rem 0 0">{{ statusError }}</p>
      </div>

      <div class="card">
        <h2>Trip assignment</h2>
        <div style="display:flex; gap:.6rem; flex-wrap:wrap; align-items:end">
          <div style="flex:1; min-width:240px">
            <select v-model="tripChoice">
              <option :value="0">— not on any trip —</option>
              <option v-for="t in plannedTrips" :key="t.id" :value="t.id">
                {{ t.title }}<template v-if="t.plannedDate"> ({{ fmtDate(t.plannedDate) }})</template>
              </option>
            </select>
          </div>
          <button class="btn btn--primary" :disabled="tripChoice === (s.tripId ?? 0)" @click="assignTrip">
            {{ s.tripId ? 'Reassign' : 'Assign' }}
          </button>
          <button v-if="s.tripId" class="btn btn--danger" @click="removeTrip">Remove from trip</button>
        </div>
        <p v-if="s.tripStatus" class="muted small" style="margin-top:.5rem">
          Current trip: <strong>{{ s.tripTitle }}</strong> ({{ tripStatusLabel(s.tripStatus) }})
        </p>
      </div>

      <div class="card">
        <h2>Access code</h2>
        <p class="muted small">Six-digit code unlocking full details on the tracking page. Stored only as a hash; regenerate to get a new one.</p>
        <button class="btn" @click="regeneratePin">Generate new access code</button>
      </div>

      <div class="card">
        <h2>Timeline</h2>
        <div style="display:flex; gap:.5rem; margin-bottom:1rem">
          <input v-model="note" placeholder="Add a manual event — e.g. 'Transported via Regio, driverBlendon supervised'" @keyup.enter="addNote" />
          <button class="btn" :disabled="!note.trim()" @click="addNote">Add event</button>
        </div>
        <Timeline :events="s.events" />
      </div>
    </template>
  </main>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { api } from '@/api'
import type { ShipmentDetail, Trip } from '@/types'
import { TRANSITIONS, fmtDate, fmtDateTime, modeLabel, statusLabel, tripStatusLabel } from '@/format'
import StatusBadge from '@/components/StatusBadge.vue'
import Timeline from '@/components/Timeline.vue'

const route = useRoute()
const id = Number(route.params.id)

const s = ref<ShipmentDetail | null>(null)
const trips = ref<Trip[]>([])
const loading = ref(true)

const statusError = ref('')
const note = ref('')
const regeneratedPin = ref('')
const tripChoice = ref(0)

const plannedTrips = computed(() => trips.value.filter((t) => t.status === 'planned'))

const DESTINATION_STYLES: Record<string, string> = {
  picked_up: 'btn--primary',
  in_transit: 'btn--primary',
  delivered: '',
  cancelled: 'btn--danger',
  lost: 'btn--danger',
}

function destinationClass(next: string) {
  return DESTINATION_STYLES[next] ?? ''
}

function transitionsEmpty() {
  return (TRANSITIONS[s.value?.status ?? ''] ?? []).length === 0
}

async function load() {
  loading.value = true
  try {
    s.value = await api.get<ShipmentDetail>(`/api/admin/shipments/${id}`)
    trips.value = await api.get<Trip[]>('/api/admin/trips')
    tripChoice.value = s.value.tripId ?? 0
  } finally {
    loading.value = false
  }
}

async function changeStatus(next: string) {
  if (!s.value) return
  statusError.value = ''
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/status`, { status: next })
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Transition rejected.'
  }
}

async function assignTrip() {
  if (!s.value) return
  statusError.value = ''
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/trip`,
      tripChoice.value === 0 ? { tripId: null } : { tripId: tripChoice.value })
    // refetch planned trips so counts stay sane
    trips.value = await api.get<Trip[]>('/api/admin/trips')
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Trip assignment failed.'
  }
}

async function removeTrip() {
  if (!s.value) return
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/trip`, { tripId: null })
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Failed to remove trip.'
  }
}

async function regeneratePin() {
  regeneratedPin.value = await api.post<{ pin: string }>(`/api/admin/shipments/${id}/pin`).then((r) => r.pin)
}

async function addNote() {
  const message = note.value.trim()
  if (!message || !s.value) return
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/events`, { message })
    note.value = ''
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Failed to add event.'
  }
}

function copy(text: string) {
  navigator.clipboard?.writeText(text)
}

onMounted(load)
</script>
