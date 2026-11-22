<template>
  <main class="container">
    <div v-if="loading" class="spinner">Loading shipment…</div>

    <div v-else-if="!s" class="card empty">Shipment not found. <RouterLink to="/admin">Back</RouterLink></div>

    <template v-else>
      <nav style="margin-bottom:1rem"><RouterLink to="/admin">← All shipments</RouterLink></nav>

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
          <dd>{{ s.description }}</dd>
          <dt>Transport</dt>
          <dd>{{ modeLabel(s.transportMode) }}</dd>
          <dt>Registered</dt>
          <dd>{{ fmtDateTime(s.createdAt) }}</dd>
          <dt>Delivered</dt>
          <dd>{{ s.deliveredAt ? fmtDateTime(s.deliveredAt) : 'Pending' }}</dd>
        </dl>
      </div>

      <div class="card">
        <h2>Status</h2>
        <div class="btnrow">
          <button
            v-for="next in forwardTransitions"
            :key="next"
            class="btn"
            :class="destinationClass(next)"
            :title="'Move this shipment to “' + statusLabel(next) + '”'"
            @click="changeStatus(next)"
          >
            {{ statusLabel(next) }}
          </button>
          <span v-if="transitionsEmpty()" class="muted small">
            {{ backStatus ? 'No further forward moves.' : 'Final state — no further transitions.' }}
          </span>
        </div>
        <p v-if="statusError" class="callout callout--error small" style="margin:.8rem 0 0">{{ statusError }}</p>

        <div v-if="backStatus" class="btnrow" style="margin-top:.9rem; border-top:1px solid var(--line); padding-top:.9rem">
          <button class="btn" :title="'Step the status one back to “' + statusLabel(backStatus) + '” — for fixing a mis-click'" @click="revertTo(backStatus)">
            ← Back to {{ statusLabel(backStatus) }}
          </button>
          <span class="muted small" style="align-self:center">
            One step back on the main path. Leaving Delivered clears the delivery date and sends the recipient a correction.
          </span>
        </div>
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
          <button class="btn btn--primary" :title="s.tripId ? 'Move the shipment to the trip selected above' : 'Put the shipment on the trip selected above'" :disabled="tripChoice === (s.tripId ?? 0)" @click="assignTrip">
            {{ s.tripId ? 'Reassign' : 'Assign' }}
          </button>
          <button v-if="s.tripId" class="btn btn--danger" title="Take the shipment off its trip — the shipment itself stays untouched" @click="removeTrip">Remove from trip</button>
        </div>
        <p v-if="s.tripStatus" class="muted small" style="margin-top:.5rem">
          Current trip: <strong>{{ s.tripTitle }}</strong> ({{ tripStatusLabel(s.tripStatus) }})
        </p>
      </div>

      <div class="card">
        <h2>Custody — who holds the parcel</h2>
        <p class="muted small">
          Custody is separate from trips: a parcel can sit with a courier who has no trip planned yet.
          Assigning a trip sets custody to that trip's courier; removing it leaves custody untouched.
        </p>
        <p style="margin-bottom:.9rem">
          Currently with:
          <strong>{{ s.assignedCourierName ?? 'nobody' }}</strong>
        </p>

        <div v-if="custodyLocked" class="callout callout--warn small">
          This parcel is {{ statusLabel(s.status).toLowerCase() }} — hand it over only after reverting the status.
        </div>

        <template v-else>
          <div style="display:flex; gap:.6rem; flex-wrap:wrap; align-items:end">
            <div style="flex:1; min-width:240px">
              <select v-model.number="courierChoice">
                <option :value="0">— nobody (clear custody) —</option>
                <option v-for="c in clients" :key="c.id" :value="c.id">
                  {{ c.name }}<template v-if="!c.telegramChatId"> · no Telegram</template>
                </option>
              </select>
            </div>
            <button
              class="btn btn--primary"
              :disabled="custodyBusy || courierChoice === 0 || courierChoice === (s.assignedCourierClientId ?? 0)"
              title="Hand the parcel to the selected client — pushes a Telegram note to them and to the recipient"
              @click="handover(false)"
            >
              {{ s.assignedCourierClientId ? 'Hand over' : 'Assign custody' }}
            </button>
            <button
              v-if="s.assignedCourierClientId"
              class="btn btn--danger"
              :disabled="custodyBusy"
              title="Forget who holds the parcel — no timeline handover, just clears the holder"
              @click="handover(true)"
            >
              Clear custody
            </button>
          </div>

          <p v-if="selectedCourierMissingChat" class="callout callout--warn small" style="margin:.8rem 0 0">
            {{ selectedCourierName }} has no Telegram chat bound — they will not be notified.
          </p>

          <div class="field-row" style="margin-top:.9rem">
            <label style="display:flex; gap:.45rem; align-items:center">
              <input v-model="releaseFromTrip" type="checkbox" :disabled="!s.tripId" />
              <span>Detach from the current trip</span>
            </label>
            <label style="display:flex; gap:.45rem; align-items:center">
              <input v-model="revertStatus" type="checkbox" :disabled="!backStatus" />
              <span>Step the status back ({{ backStatus ? statusLabel(backStatus) : 'n/a' }})</span>
            </label>
          </div>
          <input v-model="handoverNote" placeholder="Optional note appended to the handover timeline entry" style="margin-top:.6rem" />

          <p v-if="custodyError" class="callout callout--error small" style="margin:.8rem 0 0">{{ custodyError }}</p>
        </template>
      </div>

      <div class="card">
        <h2>Access code</h2>
        <p class="muted small">Six-digit code unlocking full details on the tracking page. Regenerate to get a new one.</p>
        <p class="mono copynum" style="font-size:1.2rem">
          <strong>{{ s.pin }}</strong>
          <button type="button" @click="copy(s.pin)">copy</button>
        </p>
        <div class="btnrow">
          <button class="btn" title="Copy the public tracking URL to the clipboard" @click="copyLink">
            {{ linkCopied ? 'Tracking link copied ✓' : 'Copy tracking link' }}
          </button>
          <button class="btn" title="Invalidates the current six-digit code and generates a fresh one" @click="regeneratePin">Generate new access code</button>
        </div>
      </div>

      <div class="card">
        <h2>Notes</h2>
        <p class="muted small">
          Internal, but shown to the recipient on the tracking page after unlocking.
          Empty note means none.
        </p>
        <textarea v-model="notes" rows="3" style="width:100%; margin-bottom:.6rem"></textarea>
        <div class="btnrow">
          <button class="btn btn--primary" :disabled="!notesDirty || notesSaving" @click="saveNotes">
            {{ notesSaving ? 'Saving…' : 'Save notes' }}
          </button>
          <span v-if="notesSaved" class="muted small" style="align-self:center">Saved ✓</span>
        </div>
      </div>

      <div class="card">
        <h2>Timeline</h2>
        <div style="display:flex; gap:.5rem; margin-bottom:1rem">
          <input v-model="note" placeholder="Add a manual event — e.g. 'Transported via Regio, driverBlendon supervised'" @keyup.enter="addNote" />
          <button class="btn" title="Append this note to the shipment's timeline" :disabled="!note.trim()" @click="addNote">Add event</button>
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
import type { Client, ShipmentDetail, Trip } from '@/types'
import { BACK_TRANSITIONS, TRANSITIONS, fmtDate, fmtDateTime, modeLabel, statusLabel, tripStatusLabel } from '@/format'
import StatusBadge from '@/components/StatusBadge.vue'
import Timeline from '@/components/Timeline.vue'

const route = useRoute()
const id = Number(route.params.id)

const s = ref<ShipmentDetail | null>(null)
const trips = ref<Trip[]>([])
const clients = ref<Client[]>([])
const loading = ref(true)

const statusError = ref('')
const note = ref('')
const tripChoice = ref(0)
const notes = ref('')
const notesSaving = ref(false)
const notesSaved = ref(false)

const courierChoice = ref(0)
const releaseFromTrip = ref(false)
const revertStatus = ref(false)
const handoverNote = ref('')
const custodyError = ref('')
const custodyBusy = ref(false)

const plannedTrips = computed(() => trips.value.filter((t) => t.status === 'planned'))
const notesDirty = computed(() => notes.value.trim() !== (s.value?.notes ?? '').trim())

/** One step back along the main path, or undefined when the status cannot go back. */
const backStatus = computed(() => (s.value ? BACK_TRANSITIONS[s.value.status] : undefined))
/** Forward moves and side exits only — the rollback has its own "← Back to" button. */
const forwardTransitions = computed(() =>
  (TRANSITIONS[s.value?.status ?? ''] ?? []).filter((t) => t !== backStatus.value))
/** Handover is meaningless on closed parcels (the API rejects it too). */
const custodyLocked = computed(() => ['delivered', 'cancelled', 'lost'].includes(s.value?.status ?? ''))
const selectedCourier = computed(() => clients.value.find((c) => c.id === courierChoice.value))
const selectedCourierName = computed(() => selectedCourier.value?.name ?? 'This client')
const selectedCourierMissingChat = computed(() => !!selectedCourier.value && !selectedCourier.value.telegramChatId)

const DESTINATION_STYLES: Record<string, string> = {
  picked_up: 'btn--primary',
  in_transit: 'btn--primary',
  at_destination: 'btn--primary',
  delivered: '',
  cancelled: 'btn--danger',
  lost: 'btn--danger',
}

function destinationClass(next: string) {
  return DESTINATION_STYLES[next] ?? ''
}

function transitionsEmpty() {
  return forwardTransitions.value.length === 0
}

async function load() {
  loading.value = true
  try {
    s.value = await api.get<ShipmentDetail>(`/api/admin/shipments/${id}`)
    trips.value = await api.get<Trip[]>('/api/admin/trips')
    clients.value = await api.get<Client[]>('/api/admin/clients')
    tripChoice.value = s.value.tripId ?? 0
    notes.value = s.value.notes ?? ''
    courierChoice.value = s.value.assignedCourierClientId ?? 0
  } finally {
    loading.value = false
  }
}

async function saveNotes() {
  if (!s.value) return
  notesSaving.value = true
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/notes`, { notes: notes.value })
    notesSaved.value = true
    setTimeout(() => (notesSaved.value = false), 2000)
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Failed to save notes.'
  } finally {
    notesSaving.value = false
  }
}

async function changeStatus(next: string) {
  if (!s.value) return
  statusError.value = ''
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/status`, { status: next })
    courierChoice.value = s.value.assignedCourierClientId ?? 0
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Transition rejected.'
  }
}

/** Rollback: one step back along the main path (delivered needs a confirmation). */
async function revertTo(previous: string) {
  if (!s.value) return
  if (s.value.status === 'delivered' &&
      !confirm(`Revert ${s.value.trackingNumber} from Delivered to ${statusLabel(previous)}?\n\nThe delivery date is cleared and the recipient gets a correction.`))
    return
  await changeStatus(previous)
}

async function handover(clear: boolean) {
  if (!s.value) return
  custodyError.value = ''
  custodyBusy.value = true
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/courier`, {
      courierClientId: clear ? null : courierChoice.value,
      releaseFromTrip: releaseFromTrip.value,
      revertStatus: revertStatus.value,
      note: handoverNote.value.trim() || null,
    })
    courierChoice.value = s.value.assignedCourierClientId ?? 0
    releaseFromTrip.value = false
    revertStatus.value = false
    handoverNote.value = ''
    // the trip list may have lost/gained a parcel; keep the picker honest
    trips.value = await api.get<Trip[]>('/api/admin/trips')
  } catch (e) {
    custodyError.value = e instanceof Error ? e.message : 'Handover failed.'
  } finally {
    custodyBusy.value = false
  }
}

async function assignTrip() {
  if (!s.value) return
  statusError.value = ''
  try {
    s.value = await api.post<ShipmentDetail>(`/api/admin/shipments/${id}/trip`,
      tripChoice.value === 0 ? { tripId: null } : { tripId: tripChoice.value })
    // joining a trip can move custody to the trip's courier
    courierChoice.value = s.value.assignedCourierClientId ?? 0
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
    courierChoice.value = s.value.assignedCourierClientId ?? 0
  } catch (e) {
    statusError.value = e instanceof Error ? e.message : 'Failed to remove trip.'
  }
}

async function regeneratePin() {
  await api.post<{ pin: string }>(`/api/admin/shipments/${id}/pin`)
  // refetch so the access code card shows the new value immediately
  await load()
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

// Public tracking URL — derived from wherever the admin panel is being
// browsed (localhost in dev, LAN/public host in prod), no extra config.
const trackLink = computed(() =>
  s.value ? `${window.location.origin}/track/${s.value.trackingNumber}` : '')
const linkCopied = ref(false)
let linkTimer: ReturnType<typeof setTimeout> | undefined

async function copyLink() {
  navigator.clipboard?.writeText(trackLink.value)
  linkCopied.value = true
  clearTimeout(linkTimer)
  linkTimer = setTimeout(() => { linkCopied.value = false }, 2000)
}

onMounted(load)
</script>
