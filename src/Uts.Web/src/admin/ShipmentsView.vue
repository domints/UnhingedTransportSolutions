<template>
  <main class="container container--wide">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Shipments</h1>
      <button class="btn btn--primary" title="Register a new parcel: sender, recipient, destination — generates its tracking number and access code" @click="openCreate">+ New shipment</button>
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
      <button
        type="button"
        class="btn"
        :title="showDelivered ? 'Hide delivered shipments from the list' : 'Include delivered shipments in the list'"
        @click="showDelivered = !showDelivered"
      >
        {{ showDelivered ? 'Hide delivered' : 'Show delivered' }}<template v-if="!showDelivered && hiddenDeliveredCount"> ({{ hiddenDeliveredCount }})</template>
      </button>
      <select v-model="cityFilter" @change="reload">
        <option value="">All cities</option>
        <option v-for="c in cities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
      </select>
      <select v-model="courierFilter" @change="reload" title="Filter by who physically holds the parcel">
        <option value="">Any custodian</option>
        <option value="none">No custodian</option>
        <option v-for="c in clients" :key="c.id" :value="String(c.id)">{{ c.name }}</option>
      </select>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="visibleShipments.length" class="table">
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
            <th>Custodian</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="s in visibleShipments" :key="s.id" @click="go(s)">
            <td class="mono">
              <span class="copynum">
                {{ s.trackingNumber }}
                <button type="button" @click.stop="copy(s.trackingNumber)">copy</button>
              </span>
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
            <td>
              <template v-if="s.assignedCourierName">
                <span class="badge badge--trip">{{ s.assignedCourierName }}</span>
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
import { computed, onMounted, ref, watch } from 'vue'
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
const courierFilter = ref('')
const modalOpen = ref(false)
const created = ref<{ number: string; pin: string } | null>(null)
const activeCities = computed(() => cities.value.filter((c) => c.active))

// Delivered parcels are hidden by default; picking "Delivered" in the status filter shows them regardless.
// The choice is remembered in localStorage (storage can be unavailable, e.g. blocked cookies — then it just isn't persisted).
const SHOW_DELIVERED_KEY = 'uts.admin.shipments.showDelivered'
function loadShowDelivered(): boolean {
  try {
    return localStorage.getItem(SHOW_DELIVERED_KEY) === '1'
  } catch {
    return false
  }
}
const showDelivered = ref(loadShowDelivered())
watch(showDelivered, (v) => {
  try {
    localStorage.setItem(SHOW_DELIVERED_KEY, v ? '1' : '0')
  } catch {
    /* ignore */
  }
})
const deliveredHidden = computed(() => !showDelivered.value && statusFilter.value !== 'delivered')
const visibleShipments = computed(() =>
  deliveredHidden.value ? shipments.value.filter((s) => s.status !== 'delivered') : shipments.value)
const hiddenDeliveredCount = computed(() => shipments.value.length - visibleShipments.value.length)

let debounce: ReturnType<typeof setTimeout> | undefined

async function reload() {
  const params = new URLSearchParams()
  if (q.value.trim()) params.set('q', q.value.trim())
  if (statusFilter.value) params.set('status', statusFilter.value)
  if (cityFilter.value) params.set('city', cityFilter.value)
  if (courierFilter.value) params.set('courierId', courierFilter.value)
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
