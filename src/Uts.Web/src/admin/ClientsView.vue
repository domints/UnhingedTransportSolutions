<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Clients</h1>
      <button class="btn btn--primary" @click="openAdd">+ New client</button>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="clients.length" class="table" style="cursor:default">
        <thead>
          <tr><th>Name</th><th>Contact</th><th>Telegram chat ID</th><th>Notes</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="c in clients" :key="c.id">
            <td><strong>{{ c.name }}</strong></td>
            <td class="small">{{ c.contact || '—' }}</td>
            <td class="mono small">
              <template v-if="c.telegramChatId">{{ c.telegramChatId }}</template>
              <span v-else class="muted" title="The future notification bot needs this">not linked yet</span>
            </td>
            <td class="small">{{ c.notes || '—' }}</td>
            <td style="white-space:nowrap">
              <button class="btn btn--sm" @click="generateClaim(c)">TG claim</button>
              <button class="btn btn--sm" @click="openEdit(c)">Edit</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No clients yet — everyone is a stranger.</p>
    </div>

    <Modal :open="show" :title="editing.id ? 'Edit client' : 'New client'" @close="show = false">
      <form @submit.prevent="save">
        <label>Name *</label>
        <input v-model="editing.name" required />
        <label>Contact</label>
        <input v-model="editing.contact" placeholder="phone, address, pigeon etc." />
        <label>Telegram chat ID</label>
        <input v-model="editing.telegramChatId" class="mono" placeholder="for future bot notifications" />
        <label>Notes</label>
        <input v-model="editing.notes" />
        <p v-if="error" class="callout callout--error small" style="margin-top:.8rem">{{ error }}</p>
        <div class="modal__actions">
          <button type="button" class="btn" @click="show = false">Cancel</button>
          <button type="submit" class="btn btn--primary" :disabled="!editing.name">Save</button>
        </div>
      </form>
    </Modal>

    <!-- telegram claim code -->
    <Modal :open="claim !== null" title="Telegram claim" @close="claim = null">
      <p class="small">Claim code for <strong>{{ claim?.name }}</strong> (valid 7 days, single use):</p>
      <p class="mono copynum"><strong>{{ claim?.code }}</strong></p>
      <template v-if="claim?.url">
        <p class="small" style="margin-top:.8rem">One-click link (opens the bot with the claim pre-typed):</p>
        <p class="mono small">{{ claim.url }}</p>
        <a class="btn btn--primary" :href="claim.url" target="_blank" rel="noopener">Open in Telegram</a>
      </template>
      <p v-else class="muted small" style="margin-top:.8rem">
        Bot username not known yet (module disabled or bot not started) — send the code to the recipient;
        they must message the bot <span class="mono">/start &lt;code&gt;</span> themselves.
      </p>
    </Modal>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Client } from '@/types'
import Modal from '@/components/Modal.vue'

const clients = ref<Client[]>([])
const show = ref(false)
const editing = ref<ClientEditForm>({ id: undefined, name: '', contact: null, telegramChatId: null, notes: null })
const error = ref('')
const claim = ref<{ name: string; code: string; url: string | null } | null>(null)

interface ClientEditForm {
  id?: number
  name: string
  contact: string | null
  telegramChatId: string | null
  notes: string | null
}

async function reload() {
  clients.value = await api.get<Client[]>('/api/admin/clients')
}

async function generateClaim(c: Client) {
  error.value = ''
  try {
    const r = await api.post<{ code: string; url: string | null }>(`/api/admin/clients/${c.id}/claim`)
    claim.value = { name: c.name, code: r.code, url: r.url }
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Claim code generation failed.'
  }
}

function openAdd() {
  editing.value = { name: '', contact: null, telegramChatId: null, notes: null }
  error.value = ''
  show.value = true
}

function openEdit(c: Client) {
  editing.value = { id: c.id, name: c.name, contact: c.contact, telegramChatId: c.telegramChatId, notes: c.notes }
  error.value = ''
  show.value = true
}

async function save() {
  const e = editing.value
  try {
    if (e.id) {
      await api.put<Client>(`/api/admin/clients/${e.id}`, {
        name: e.name,
        contact: e.contact || null,
        telegramChatId: e.telegramChatId || null,
        notes: e.notes || null,
      })
    } else {
      await api.post<Client>('/api/admin/clients', {
        name: e.name,
        contact: e.contact || null,
        telegramChatId: e.telegramChatId || null,
        notes: e.notes || null,
      })
    }
    show.value = false
    await reload()
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Save failed.'
  }
}

onMounted(reload)
</script>
