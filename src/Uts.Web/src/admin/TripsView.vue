<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Planned trips</h1>
      <button class="btn btn--primary" @click="openAdd">+ New trip</button>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="trips.length" class="table">
        <thead>
          <tr><th>Trip</th><th>Route</th><th>Planned</th><th>Status</th><th>Shipments</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="t in trips" :key="t.id" @click="select(t)">
            <td>
              <strong>{{ t.title }}</strong>
            </td>
            <td class="mono">{{ t.originCode ?? '???' }} → {{ t.destinationCode ?? '???' }}</td>
            <td class="small">{{ fmtDate(t.plannedDate) }}</td>
            <td><span class="badge" :class="'badge--' + tripBadge(t.status)">{{ tripStatusLabel(t.status) }}</span></td>
            <td><span class="chip">{{ t.shipmentCount }}</span></td>
            <td><button class="btn btn--sm" @click.stop="openEdit(t)">Edit</button></td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No trips planned. UTS is technically at rest.</p>
    </div>

    <div v-if="selected" class="card">
      <div class="toolbar">
        <h2 style="margin:0">Haul for “{{ selected.title }}”</h2>
        <button class="btn" @click="selected = null">Close</button>
      </div>
      <table v-if="selectedShipments.length" class="table">
        <thead>
          <tr><th>Number</th><th>Recipient</th><th>Contents</th><th>Status</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="s in selectedShipments" :key="s.id" style="cursor:default">
            <td class="mono">{{ s.trackingNumber }}</td>
            <td>{{ s.recipientName }}</td>
            <td style="max-width:240px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap">{{ s.description }}</td>
            <td><StatusBadge :status="s.status" /></td>
            <td><button class="btn btn--sm btn--danger" @click="unassign(s)">Unassign</button></td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No shipments assigned to this trip yet.</p>

      <button class="btn" style="margin-top:1rem" @click="pickOpen = true; showCandidates()">+ Add shipment to trip</button>
    </div>

    <!-- pick modal -->
    <Modal :open="pickOpen" :title="`Assign shipment to “${selected?.title}”`" @close="pickOpen = false">
      <p v-if="candidates.length === 0" class="empty">No unassigned shipments available.</p>
      <table v-else class="table">
        <thead>
          <tr><th>Number</th><th>Destination</th><th>Status</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="s in candidates" :key="s.id">
            <td class="mono">{{ s.trackingNumber }}</td>
            <td class="mono">{{ s.destinationCode }}</td>
            <td><StatusBadge :status="s.status" /></td>
            <td><button class="btn btn--sm" @click="assign(s)">Assign</button></td>
          </tr>
        </tbody>
      </table>
      <p v-if="pickError" class="callout callout--error small">{{ pickError }}</p>
    </Modal>

    <!-- edit modal -->
    <Modal :open="showEdit" :title="editing.id ? 'Edit trip' : 'New trip'" @close="showEdit = false">
      <form @submit.prevent="saveTrip">
        <label>Title *</label>
        <input v-model="editing.title" required placeholder="e.g. WRO→KRK weekend haul" />
        <div class="field-row">
          <div>
            <label>Origin</label>
            <select v-model="editing.originCode">
              <option value="">—</option>
              <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
            </select>
          </div>
          <div>
            <label>Destination</label>
            <select v-model="editing.destinationCode">
              <option value="">—</option>
              <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
            </select>
          </div>
        </div>
        <label>Planned date</label>
        <input v-model="editing.plannedDate" type="date" />
        <label>Notes</label>
        <input v-model="editing.notes" />
        <div v-if="editing.id" class="field-row">
          <div>
            <label>Status</label>
            <select v-model="editing.status">
              <option value="planned">Planned</option>
              <option value="completed">Completed</option>
              <option value="cancelled">Cancelled</option>
            </select>
          </div>
        </div>
        <p v-if="error" class="callout callout--error small" style="margin-top:.8rem">{{ error }}</p>
        <div class="modal__actions">
          <button type="button" class="btn" @click="showEdit = false">Cancel</button>
          <button type="submit" class="btn btn--primary" :disabled="!editing.title">Save</button>
        </div>
      </form>
    </Modal>
  </main>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { City, ShipmentListItem, Trip, TripListItem } from '@/types'
import { fmtDate, tripStatusLabel } from '@/format'
import Modal from '@/components/Modal.vue'
import StatusBadge from '@/components/StatusBadge.vue'

const trips = ref<TripListItem[]>([])
const cities = ref<City[]>([])
const selected = ref<TripListItem | null>(null)
const selectedShipments = ref<ShipmentListItem[]>([])

const editing = ref<TripEditForm>({ id: undefined, title: '', originCode: '', destinationCode: '', plannedDate: '', status: 'planned', notes: '' })
const showEdit = ref(false)
const pickOpen = ref(false)
const candidates = ref<ShipmentListItem[]>([])
const pickError = ref('')
const error = ref('')

interface TripEditForm {
  id?: number
  title: string
  originCode: string
  destinationCode: string
  plannedDate: string
  status: string
  notes: string
}

const activeCities = computed(() => cities.value.filter((c) => c.active))

function tripBadge(status: string) {
  return status === 'planned' ? 'trip' : status === 'completed' ? 'delivered' : 'lost'
}

async function reload() {
  trips.value = await api.get<TripListItem[]>('/api/admin/trips')
  cities.value = await api.get<City[]>('/api/admin/cities')
}

async function select(t: TripListItem) {
  if (selected.value?.id === t.id) {
    selected.value = null
    return
  }
  selected.value = t
  await loadTripShipments()
}

async function loadTripShipments() {
  if (!selected.value) return
  selectedShipments.value = await api.get<ShipmentListItem[]>(`/api/admin/shipments?tripId=${selected.value.id}`)
}

function openAdd() {
  editing.value = { title: '', originCode: '', destinationCode: '', plannedDate: '', status: 'planned', notes: '' }
  error.value = ''
  showEdit.value = true
}

function openEdit(t: TripListItem) {
  editing.value = {
    id: t.id,
    title: t.title,
    originCode: t.originCode ?? '',
    destinationCode: t.destinationCode ?? '',
    plannedDate: t.plannedDate ?? '',
    status: t.status,
    notes: t.notes ?? '',
  }
  error.value = ''
  showEdit.value = true
}

async function saveTrip() {
  const e = editing.value
  const payload = {
    title: e.title,
    originCode: e.originCode || null,
    destinationCode: e.destinationCode || null,
    plannedDate: e.plannedDate || null,
    status: e.status,
    notes: e.notes || null,
  }
  try {
    if (e.id) await api.put<Trip>(`/api/admin/trips/${e.id}`, payload)
    else await api.post<Trip>('/api/admin/trips', payload)
    showEdit.value = false
    selected.value = null
    await reload()
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Save failed.'
  }
}

async function showCandidates() {
  pickError.value = ''
  if (!selected.value) return
  candidates.value = await api.get<ShipmentListItem[]>('/api/admin/shipments?tripId=none')
}

async function assign(s: ShipmentListItem) {
  if (!selected.value) return
  try {
    await api.post(`/api/admin/shipments/${s.id}/trip`, { tripId: selected.value.id })
    pickOpen.value = false
    await loadTripShipments()
    await reload()
  } catch (e) {
    pickError.value = e instanceof Error ? e.message : 'Assignment failed. Do the cities match?'
  }
}

async function unassign(s: ShipmentListItem) {
  try {
    await api.post(`/api/admin/shipments/${s.id}/trip`, { tripId: null })
    await loadTripShipments()
    await reload()
  } catch (e) {
    pickError.value = e instanceof Error ? e.message : 'Unassign failed.'
  }
}

onMounted(reload)
</script>
