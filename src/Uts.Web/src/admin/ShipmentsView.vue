<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Shipments</h1>
      <button class="btn btn--primary" @click="openCreate">+ New shipment</button>
    </div>

    <div v-if="created" class="callout callout--ok">
      <strong>Shipment created!</strong>
      <div style="margin-top:.4rem">
        Tracking number:
        <span class="mono copynum">
          <strong>{{ created.number }}</strong>
          <button type="button" @click="copy(created.number)">copy</button>
        </span>
      </div>
      <div style="margin-top:.3rem">
        Access code (shown once):
        <span class="mono copynum">
          <strong>{{ created.pin }}</strong>
          <button type="button" @click="copy(created.pin)">copy</button>
        </span>
      </div>
      <button class="btn btn--sm" style="margin-top:.6rem" @click="created = null">Got it</button>
    </div>

    <div class="filters">
      <input v-model="q" class="mono" placeholder="Search number / client / contents…" @input="reloadDebounced" />
      <select v-model="statusFilter" @change="reload">
        <option value="">All statuses</option>
        <option v-for="s in allStatuses" :key="s" :value="s">{{ statusLabel(s) }}</option>
      </select>
      <select v-model="cityFilter" @change="reload">
        <option value="">All cities</option>
        <option v-for="c in cities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
      </select>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="shipments.length" class="table">
        <thead>
          <tr>
            <th>Number</th>
            <th>Route</th>
            <th>Sender</th>
            <th>Recipient</th>
            <th>Contents</th>
            <th>Mode</th>
            <th>Status</th>
            <th>Trip</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="s in shipments" :key="s.id" @click="go(s)">
            <td class="mono copynum">
              {{ s.trackingNumber }}
              <button type="button" @click.stop="copy(s.trackingNumber)">copy</button>
            </td>
            <td class="mono">{{ s.originCode ?? '???' }} → {{ s.destinationCode }}</td>
            <td>{{ s.senderName }}</td>
            <td>{{ s.recipientName }}</td>
            <td style="max-width:220px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap">{{ s.description }}</td>
            <td class="small muted">{{ modeLabel(s.transportMode) }}</td>
            <td><StatusBadge :status="s.status" /></td>
            <td>
              <template v-if="s.tripTitle">
                <span class="badge badge--trip">{{ s.tripTitle }}</span>
                <div v-if="s.tripPlannedDate" class="small muted">{{ fmtDate(s.tripPlannedDate) }}</div>
              </template>
              <span v-else class="muted small">—</span>
            </td>
            <td class="small muted">{{ fmtDateTime(s.createdAt) }}</td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No shipments match. The logistics van is conveniently empty.</p>
    </div>

    <ShipmentModal :open="modalOpen" :clients="clients" :cities="activeCities" @close="modalOpen = false" @created="onCreated" />
  </main>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { City, Client, ShipmentListItem } from '@/types'
import { STATUS_LABELS, fmtDate, fmtDateTime, modeLabel, statusLabel } from '@/format'
import StatusBadge from '@/components/StatusBadge.vue'
import ShipmentModal from '@/components/ShipmentModal.vue'

const router = useRouter()
const shipments = ref<ShipmentListItem[]>([])
const cities = ref<City[]>([])
const clients = ref<Client[]>([])
const allStatuses = Object.keys(STATUS_LABELS)

const q = ref('')
const statusFilter = ref('')
const cityFilter = ref('')
const modalOpen = ref(false)
const created = ref<{ number: string; pin: string } | null>(null)
const activeCities = computed(() => cities.value.filter((c) => c.active))

let debounce: ReturnType<typeof setTimeout> | undefined

async function reload() {
  const params = new URLSearchParams()
  if (q.value.trim()) params.set('q', q.value.trim())
  if (statusFilter.value) params.set('status', statusFilter.value)
  if (cityFilter.value) params.set('city', cityFilter.value)
  const qs = params.toString()
  shipments.value = await api.get<ShipmentListItem[]>(`/api/admin/shipments${qs ? '?' + qs : ''}`)
}

function reloadDebounced() {
  clearTimeout(debounce)
  debounce = setTimeout(reload, 250)
}

function openCreate() {
  created.value = null
  modalOpen.value = true
}

async function onCreated(res: { pin: string; shipment: { trackingNumber: string } }) {
  modalOpen.value = false
  created.value = { pin: res.pin, number: res.shipment.trackingNumber }
  await reload()
}

function go(s: ShipmentListItem) {
  router.push(`/admin/shipments/${s.id}`)
}

function copy(text: string) {
  navigator.clipboard?.writeText(text)
}

onMounted(async () => {
  await reload()
  cities.value = await api.get<City[]>('/api/admin/cities')
  clients.value = await api.get<Client[]>('/api/admin/clients')
})
</script>
