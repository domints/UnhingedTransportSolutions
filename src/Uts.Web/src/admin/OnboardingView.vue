<template>
  <main class="container" style="max-width: 640px">
    <div class="card">
      <h1 style="font-size:1.5rem; margin:0 0 .4rem">Who are you, exactly?</h1>
      <p class="muted" style="margin-top:0">
        You are signed in as staff, but your login is not bound to any client record yet.
        UTS cannot deliver parcels to a ghost. Claim an existing unclaimed client,
        or create a new one for yourself.
      </p>

      <p v-if="done" class="callout callout--ok">
        Mapped to client <strong>#{{ done.id }}</strong> ({{ done.name }}). Reloading…
      </p>

      <p v-if="error" class="callout callout--error">{{ error }}</p>

      <template v-if="!done">
        <h2 style="font-size:1.05rem">Existing clients without a login</h2>
        <table v-if="unclaimed.length" class="table">
          <tbody>
            <tr v-for="c in unclaimed" :key="c.id">
              <td><strong>{{ c.name }}</strong></td>
              <td class="small muted">{{ c.contact ?? '' }}</td>
              <td style="white-space:nowrap">
                <button class="btn btn--sm btn--primary" @click="claim(c.id)">That is me</button>
              </td>
            </tr>
          </tbody>
        </table>
        <p v-else class="empty">Every client row is already claimed. Create a new one below.</p>

        <form style="margin-top:1.2rem" @submit.prevent="createNew">
          <button type="button" class="btn" @click="creatingNew = !creatingNew">
            {{ creatingNew ? '− hide form' : '+ I am a new client' }}
          </button>
          <template v-if="creatingNew">
            <label style="margin-top:.8rem">Your name (as parcels should address you)</label>
            <input v-model="newName" required :placeholder="defaultName" />
            <div class="modal__actions" style="margin-top:.8rem">
              <button type="submit" class="btn btn--primary" :disabled="!newName.trim()">Create &amp; claim</button>
            </div>
          </template>
        </form>
      </template>
    </div>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { AdminIdentity, AuthState, UnclaimedCourier } from '@/types'

const router = useRouter()
const unclaimed = ref<UnclaimedCourier[]>([])
const defaultName = ref('')
const creatingNew = ref(false)
const newName = ref('')
const error = ref('')
const done = ref<{ id: number; name: string } | null>(null)

async function reload() {
  const me = await api.get<AdminIdentity>('/api/admin/identity')
  if (me.onboarded) {
    await router.replace('/admin')
    return
  }
  unclaimed.value = await api.get<UnclaimedCourier[]>('/api/admin/identity/unclaimed')
  const s = await api.get<AuthState>('/api/auth/state')
  // Prefill the "new client" name from the IdP display name.
  defaultName.value = s.name ?? ''
  if (!newName.value) newName.value = defaultName.value
}

async function claim(clientId: number) {
  error.value = ''
  try {
    await api.post('/api/admin/identity/claim', { clientId })
    const c = unclaimed.value.find((x) => x.id === clientId)
    done.value = { id: clientId, name: c?.name ?? `#${clientId}` }
    setTimeout(() => router.replace('/admin'), 1200)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Claim failed.'
    await reload()
  }
}

async function createNew() {
  error.value = ''
  try {
    const r = await api.post<{ clientId: number }>('/api/admin/identity/claim', { name: newName.value.trim() })
    done.value = { id: r.clientId, name: newName.value.trim() }
    setTimeout(() => router.replace('/admin'), 1200)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Client creation failed.'
  }
}

onMounted(reload)
</script>
