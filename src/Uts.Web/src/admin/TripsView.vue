<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Planned trips</h1>
      <button class="btn" @click="openTemplates">🕘 Templates</button>
      <button class="btn btn--primary" @click="openAdd">+ New trip</button>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="trips.length" class="table">
        <thead>
          <tr><th>Trip</th><th>Route</th><th>Planned</th><th>Status</th><th>Mode</th><th>Shipments</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="t in trips" :key="t.id" @click="select(t)">
            <td>
              <strong>{{ t.title }}</strong>
            </td>
            <td class="mono">{{ routeStr(t) }}</td>
            <td class="small">{{ fmtDate(t.plannedDate) }}</td>
            <td><span class="badge" :class="'badge--' + tripBadge(t.status)">{{ tripStatusLabel(t.status) }}</span></td>
            <td>{{ modeIcon(t.transportMode) }}</td>
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
      <p v-if="selected.shipmentCount" class="muted small" style="margin:.3rem 0 .8rem">
        Packlist — make sure every parcel below is physically packed before you leave.
      </p>
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

      <p v-if="bulkResult" class="callout callout--ok small" style="margin-top:.8rem">
        {{ bulkResult }}
      </p>

      <div class="toolbar" style="margin-top:1rem">
        <button class="btn btn--primary" :disabled="!selectedShipments.length" @click="bulkMark('in_transit')">
          🚆 Leaving — mark all in transit
        </button>
        <button class="btn btn--primary" :disabled="!selectedShipments.length" @click="bulkMark('at_destination')">
          📍 Arrived — mark all at destination
        </button>
        <button class="btn" style="margin-left:auto" @click="pickOpen = true; showCandidates()">+ Add shipment to trip</button>
      </div>
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
        <label>Intermediate stops (deliveries en route)</label>
        <div v-if="editing.stops.length" class="chip" style="display:flex; align-items:center; gap:.4rem; width:fit-content">
          <template v-for="(s, i) in editing.stops" :key="s">
            <span class="mono">{{ s }}</span>
            <button type="button" style="padding:0 .3rem; border:none; background:none; cursor:pointer; font-size:1rem" @click="removeStop(i)">×</button>
            <span v-if="i < editing.stops.length - 1">→</span>
          </template>
        </div>
        <select @change="addStop($event)" :disabled="editing.stops.length >= 10">
          <option value="">+ add stop…</option>
          <option v-for="c in stopChoices" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
        </select>
        <label>Notes</label>
        <input v-model="editing.notes" />
        <div class="field-row">
          <div v-if="editing.id">
            <label>Status</label>
            <select v-model="editing.status">
              <option value="planned">Planned</option>
              <option value="completed">Completed</option>
              <option value="cancelled">Cancelled</option>
            </select>
          </div>
          <div>
            <label>Transport mode</label>
            <select v-model="editing.transportMode">
              <option value="train">🚆 Train-friendly</option>
              <option value="car">🚗 Car required</option>
              <option value="unknown">Unknown / both</option>
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

    <!-- templates picker -->
    <Modal :open="templatesOpen" title="Trip templates" @close="templatesOpen = false">
      <p v-if="templates.length === 0" class="empty">No templates yet — save your first recurring route below.</p>
      <table v-if="templates.length" class="table">
        <thead>
          <tr><th>Template</th><th>Route</th><th>Mode</th><th>Create for</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="tpl in templates" :key="tpl.id">
            <td><strong>{{ tpl.title }}</strong></td>
            <td class="mono">{{ tplRouteStr(tpl) }}</td>
            <td>{{ modeIcon(tpl.transportMode) }}</td>
            <td><input v-model="tplPlannedDate" type="date" style="min-width:130px" /></td>
            <td style="white-space:nowrap">
              <button class="btn btn--sm btn--primary" @click="createFromTemplate(tpl, false)">Trip</button>
              <button class="btn btn--sm" @click="createFromTemplate(tpl, true)">+ return</button>
              <button class="btn btn--sm" @click="openTemplateEdit(tpl)">Edit</button>
              <button class="btn btn--sm btn--danger" @click="deleteTemplate(tpl)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
      <button class="btn" style="margin-top:1rem" @click="openTemplateEdit(null)">+ New template</button>
      <p v-if="tplCreated" class="callout callout--ok small" style="margin-top:.8rem">{{ tplCreated }}</p>
      <p v-if="tplError" class="callout callout--error small" style="margin-top:.8rem">{{ tplError }}</p>
    </Modal>

    <!-- template editor -->
    <Modal :open="showTemplateEdit" :title="tplForm.id ? 'Edit template' : 'New template'" @close="showTemplateEdit = false">
      <form @submit.prevent="saveTemplate">
        <label>Title *</label>
        <input v-model="tplForm.title" required placeholder="e.g. KRK→WRO weekly round trip" />
        <div class="field-row">
          <div>
            <label>Origin</label>
            <select v-model="tplForm.originCode">
              <option value="">—</option>
              <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
            </select>
          </div>
          <div>
            <label>Destination</label>
            <select v-model="tplForm.destinationCode">
              <option value="">—</option>
              <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
            </select>
          </div>
        </div>
        <label>Intermediate stops</label>
        <div v-if="tplForm.stops.length" class="chip" style="display:flex; align-items:center; gap:.4rem; width:fit-content">
          <template v-for="(s, i) in tplForm.stops" :key="s">
            <span class="mono">{{ s }}</span>
            <button type="button" style="padding:0 .3rem; border:none; background:none; cursor:pointer; font-size:1rem" @click="removeTemplateStop(i)">×</button>
            <span v-if="i < tplForm.stops.length - 1">→</span>
          </template>
        </div>
        <select @change="addTemplateStop($event)" :disabled="tplForm.stops.length >= 10">
          <option value="">+ add stop…</option>
          <option v-for="c in tplStopChoices" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
        </select>
        <label>Transport mode</label>
        <select v-model="tplForm.transportMode">
          <option value="train">🚆 Train-friendly</option>
          <option value="car">🚗 Car required</option>
          <option value="unknown">Unknown / both</option>
        </select>
        <p v-if="tplFormError" class="callout callout--error small" style="margin-top:.8rem">{{ tplFormError }}</p>
        <div class="modal__actions">
          <button type="button" class="btn" @click="showTemplateEdit = false">Cancel</button>
          <button type="submit" class="btn btn--primary" :disabled="!tplForm.title">Save</button>
        </div>
      </form>
    </Modal>
  </main>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { City, ShipmentListItem, Trip, TripListItem, TripTemplate } from '@/types'
import { STATUS_LABELS, fmtDate, tripStatusLabel } from '@/format'
import Modal from '@/components/Modal.vue'
import StatusBadge from '@/components/StatusBadge.vue'

const trips = ref<TripListItem[]>([])
const cities = ref<City[]>([])
const selected = ref<TripListItem | null>(null)
const selectedShipments = ref<ShipmentListItem[]>([])

const editing = ref<TripEditForm>({ id: undefined, title: '', originCode: '', destinationCode: '', plannedDate: '', status: 'planned', notes: '', stops: [], transportMode: 'unknown' })
const showEdit = ref(false)
const pickOpen = ref(false)
const candidates = ref<ShipmentListItem[]>([])
const pickError = ref('')
const error = ref('')
const bulkResult = ref('')

const templates = ref<TripTemplate[]>([])
const templatesOpen = ref(false)
const tplPlannedDate = ref('')
const tplCreated = ref('')
const tplError = ref('')
const showTemplateEdit = ref(false)
const tplFormError = ref('')
const tplForm = ref<TemplateEditForm>({ id: undefined, title: '', originCode: '', destinationCode: '', transportMode: 'train', stops: [] })

interface TemplateEditForm {
  id?: number
  title: string
  originCode: string
  destinationCode: string
  transportMode: string
  stops: string[]
}

function modeIcon(mode: string | undefined) {
  return mode === 'train' ? '🚆' : mode === 'car' ? '🚗' : '·'
}

function tplRouteStr(t: TripTemplate) {
  const chain = [t.originCode ?? '???', ...t.stops, t.destinationCode ?? '???']
  return chain.join(' → ')
}

interface TripEditForm {
  id?: number
  title: string
  originCode: string
  destinationCode: string
  plannedDate: string
  status: string
  notes: string
  stops: string[]
  transportMode: string
}

const activeCities = computed(() => cities.value.filter((c) => c.active))

const stopChoices = computed(() =>
  activeCities.value.filter(
    (c) => c.code !== editing.value.originCode && c.code !== editing.value.destinationCode && !editing.value.stops.includes(c.code)))

function routeStr(t: TripListItem) {
  const chain = [t.originCode ?? '???', ...t.stops, t.destinationCode ?? '???']
  return chain.join(' → ')
}

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
  bulkResult.value = ''
  await loadTripShipments()
}

async function loadTripShipments() {
  if (!selected.value) return
  selectedShipments.value = await api.get<ShipmentListItem[]>(`/api/admin/shipments?tripId=${selected.value.id}`)
}

function openAdd() {
  editing.value = { title: '', originCode: '', destinationCode: '', plannedDate: '', status: 'planned', notes: '', stops: [], transportMode: 'unknown' }
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
    stops: [...t.stops],
    transportMode: t.transportMode || 'unknown',
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
    stops: e.stops,
    transportMode: e.transportMode || 'unknown',
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

function addStop(e: Event) {
  const code = (e.target as HTMLSelectElement).value
  if (code && !editing.value.stops.includes(code)) editing.value.stops.push(code)
  ;(e.target as HTMLSelectElement).value = ''
}

function removeStop(i: number) {
  editing.value.stops.splice(i, 1)
}

async function loadTemplates() {
  templates.value = await api.get<TripTemplate[]>('/api/admin/trip-templates')
}

async function openTemplates() {
  tplError.value = ''
  tplCreated.value = ''
  await loadTemplates()
  templatesOpen.value = true
}

const tplStopChoices = computed(() =>
  activeCities.value.filter(
    (c) => c.code !== tplForm.value.originCode && c.code !== tplForm.value.destinationCode && !tplForm.value.stops.includes(c.code)))

function addTemplateStop(e: Event) {
  const code = (e.target as HTMLSelectElement).value
  if (code && !tplForm.value.stops.includes(code)) tplForm.value.stops.push(code)
  ;(e.target as HTMLSelectElement).value = ''
}

function removeTemplateStop(i: number) {
  tplForm.value.stops.splice(i, 1)
}

function openTemplateEdit(t: TripTemplate | null) {
  tplFormError.value = ''
  tplForm.value = t
    ? { id: t.id, title: t.title, originCode: t.originCode ?? '', destinationCode: t.destinationCode ?? '', transportMode: t.transportMode || 'train', stops: [...t.stops] }
    : { title: '', originCode: '', destinationCode: '', transportMode: 'train', stops: [] }
  showTemplateEdit.value = true
}

async function saveTemplate() {
  const f = tplForm.value
  try {
    if (f.id) {
      await api.put<TripTemplate>(`/api/admin/trip-templates/${f.id}`, {
        title: f.title,
        originCode: f.originCode || null,
        destinationCode: f.destinationCode || null,
        stops: f.stops,
        transportMode: f.transportMode,
      })
    } else {
      await api.post<TripTemplate>('/api/admin/trip-templates', {
        title: f.title,
        originCode: f.originCode || null,
        destinationCode: f.destinationCode || null,
        stops: f.stops,
        transportMode: f.transportMode,
      })
    }
    showTemplateEdit.value = false
    await loadTemplates()
  } catch (e) {
    tplFormError.value = e instanceof Error ? e.message : 'Template save failed.'
  }
}

async function deleteTemplate(t: TripTemplate) {
  if (!confirm(`Delete template "${t.title}"?`)) return
  try {
    await api.delete(`/api/admin/trip-templates/${t.id}`)
    await loadTemplates()
  } catch (e) {
    tplError.value = e instanceof Error ? e.message : 'Template delete failed.'
  }
}

async function createFromTemplate(t: TripTemplate, includeReturn: boolean) {
  tplError.value = ''
  tplCreated.value = ''
  try {
    const created = await api.post<Trip[]>(`/api/admin/trips/from-template/${t.id}`,
      { plannedDate: tplPlannedDate.value || null, includeReturn })
    tplCreated.value = `Created ${created.length} trip${created.length === 1 ? '' : 's'}: ${created.map((c) => c.title).join(', ')}`
    await reload()
  } catch (e) {
    tplCreated.value = ''
    tplError.value = e instanceof Error ? e.message : 'Failed to create trip from template.'
  }
}

async function bulkMark(to: 'in_transit' | 'at_destination') {
  if (!selected.value) return
  bulkResult.value = ''
  try {
    const r = await api.post<{ updated: string[]; skipped: { trackingNumber: string; status: string }[] }>(
      `/api/admin/trips/${selected.value.id}/shipments/status`, { status: to })
    const skipped = r.skipped.map((s) => `${s.trackingNumber} (${STATUS_LABELS[s.status] ?? s.status})`)
    const parts: string[] = []
    parts.push(`${r.updated.length} shipment${r.updated.length === 1 ? '' : 's'} marked ${STATUS_LABELS[to]}.`)
    if (skipped.length) parts.push(`Skipped — cannot transition yet: ${skipped.join(', ')}.`)
    bulkResult.value = parts.join(' ')
    await loadTripShipments()
    await reload()
  } catch (e) {
    bulkResult.value = ''
    error.value = e instanceof Error ? e.message : 'Bulk update failed.'
  }
}

onMounted(reload)
</script>
