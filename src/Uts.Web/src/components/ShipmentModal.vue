<template>
  <Modal :open="open" title="New shipment" @close="emit('close')">
    <form @submit.prevent="submit">
      <div class="card" style="margin-bottom:1rem">
        <div class="toolbar" style="margin-bottom:0">
          <label style="margin:0">Sender</label>
          <button type="button" class="btn btn--sm" @click="newSender.open = !newSender.open">+ New client</button>
        </div>
        <select v-model.number="form.senderId" required>
          <option v-for="c in clients" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>        <div v-if="newSender.open" style="margin-top:.6rem">
          <input v-model="newSender.name" placeholder="Name *" />
          <input v-model="newSender.contact" placeholder="Contact (phone, party registered address…)" style="margin-top:.4rem" />
          <input v-model="newSender.telegramChatId" class="mono" placeholder="Telegram chat ID (for future bot)" style="margin-top:.4rem" />
          <button type="button" class="btn btn--sm" style="margin-top:.5rem" :disabled="!newSender.name" @click="createClient('sender')">Save client</button>
        </div>
      </div>

      <div class="card" style="margin-bottom:1rem">
        <div class="toolbar" style="margin-bottom:0">
          <label style="margin:0">Recipient</label>
          <button type="button" class="btn btn--sm" @click="newRecipient.open = !newRecipient.open">+ New client</button>
        </div>
        <select v-model.number="form.recipientId" required>
          <option v-for="c in clients" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>
        <div v-if="newRecipient.open" style="margin-top:.6rem">
          <input v-model="newRecipient.name" placeholder="Name *" />
          <input v-model="newRecipient.contact" placeholder="Contact" style="margin-top:.4rem" />
          <input v-model="newRecipient.telegramChatId" class="mono" placeholder="Telegram chat ID (for future bot)" style="margin-top:.4rem" />
          <button type="button" class="btn btn--sm" style="margin-top:.5rem" :disabled="!newRecipient.name" @click="createClient('recipient')">Save client</button>
        </div>
      </div>

      <div class="field-row">
        <div>
          <label>Origin</label>
          <select v-model="form.originCode" required>
            <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
          </select>
        </div>
        <div>
          <label>Destination</label>
          <select v-model="form.destinationCode" required>
            <option v-for="c in activeCities" :key="c.code" :value="c.code">{{ c.code }} · {{ c.name }}</option>
          </select>
        </div>
      </div>

      <label>What is being transported?</label>
      <textarea v-model="form.description" required placeholder="e.g. 1 box of home-made preserves, fragile, smells heavenly" />
      <label>Transport feasibility</label>
      <div class="btnrow">
        <button type="button" class="btn" :class="form.transportMode === 'train' ? 'btn--primary' : 'btn--ghost'" @click="form.transportMode = 'train'">🚂 Train OK</button>
        <button type="button" class="btn" :class="form.transportMode === 'car' ? 'btn--primary' : 'btn--ghost'" @click="form.transportMode = 'car'">🚗 Needs car</button>
        <button type="button" class="btn" :class="form.transportMode === 'unknown' ? 'btn--primary' : 'btn--ghost'" @click="form.transportMode = 'unknown'">🤷 Unknown</button>
      </div>
      <label>Internal notes</label>
      <input v-model="form.notes" placeholder="optional" />

      <p v-if="error" class="callout callout--error small" style="margin-top:.8rem">{{ error }}</p>

      <div class="modal__actions">
        <button type="button" class="btn" @click="emit('close')">Cancel</button>
        <button type="submit" class="btn btn--primary" :disabled="busy || !form.senderId || !form.recipientId || !form.description">
          {{ busy ? 'Registering…' : 'Create shipment' }}
        </button>
      </div>
    </form>
  </Modal>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api } from '@/api'
import type { Client, City } from '@/types'
import Modal from './Modal.vue'

const props = defineProps<{
  open: boolean
  clients: Client[]
  cities: City[]
}>()

const activeCities = computed(() => props.cities.filter((c) => c.active))

interface CreateResponse {
  pin: string
  shipment: { trackingNumber: string }
}

const emit = defineEmits<{ (e: 'close'): void; (e: 'created', res: CreateResponse): void }>()

const form = reactive({
  senderId: 0,
  recipientId: 0,
  originCode: '',
  destinationCode: '',
  description: '',
  transportMode: 'unknown',
  notes: '',
})
const error = ref('')
const busy = ref(false)

const newSender = reactive({ open: false, name: '', contact: '', telegramChatId: '' })
const newRecipient = reactive({ open: false, name: '', contact: '', telegramChatId: '' })

watch(
  () => props.open,
  (o) => {
    if (!o) return
    error.value = ''
    if (!form.originCode && props.cities.length) {
      form.originCode = props.cities[0]!.code
    }
    if (form.originCode === form.destinationCode && props.cities.length > 1) {
      form.destinationCode = props.cities[1]!.code
    }
  },
)

async function createClient(role: 'sender' | 'recipient') {
  const d = role === 'sender' ? newSender : newRecipient
  if (!d.name) return
  try {
    const c = await api.post<Client>('/api/admin/clients', {
      name: d.name,
      contact: d.contact || null,
      telegramChatId: d.telegramChatId || null,
      notes: null,
    })
    // add to the parents' list so the new option is visible immediately
    ;(props.clients as Client[]).push(c)
    if (role === 'sender') {
      form.senderId = c.id
      Object.assign(newSender, { open: false, name: '', contact: '', telegramChatId: '' })
    } else {
      form.recipientId = c.id
      Object.assign(newRecipient, { open: false, name: '', contact: '', telegramChatId: '' })
    }
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to create client.'
  }
}

async function submit() {
  if (form.originCode === form.destinationCode) {
    error.value = 'Origin and destination must differ — roundabouts are not a delivery network.'
    return
  }
  busy.value = true
  error.value = ''
  try {
    const res = await api.post<CreateResponse>('/api/admin/shipments', {
      senderId: form.senderId,
      recipientId: form.recipientId,
      originCode: form.originCode,
      destinationCode: form.destinationCode,
      description: form.description.trim(),
      transportMode: form.transportMode,
      notes: form.notes.trim() || null,
    })
    Object.assign(form, {
      senderId: 0,
      recipientId: 0,
      originCode: res.shipment ? form.originCode : form.originCode,
      description: '',
      transportMode: 'unknown',
      notes: '',
    })
    emit('created', res)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to create shipment.'
  } finally {
    busy.value = false
  }
}
</script>
