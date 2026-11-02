<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">API keys</h1>
    </div>

    <!-- One-time reveal: the only moment the full key is ever visible. -->
    <div v-if="freshKey" class="card">
      <h2>Key created — copy it now</h2>
      <div class="callout callout--ok" style="display:flex; gap:.6rem; align-items:center; flex-wrap:wrap">
        <code class="mono" style="font-size:1.05rem; word-break:break-all">{{ freshKey }}</code>
        <button class="btn btn--sm" title="Copy the key to the clipboard" @click="copyKey">
          {{ copied ? 'Copied ✓' : 'Copy' }}
        </button>
      </div>
      <p class="small" style="margin:.6rem 0 0">
        This key will never be shown again. It is stored as a hash only — if you lose it, revoke it and mint a new one.
      </p>
    </div>

    <div class="card">
      <h2>New key</h2>
      <div style="display:flex; gap:.6rem; flex-wrap:wrap; align-items:end">
        <div style="flex:1; min-width:220px">
          <label style="margin-top:0">Name (what are the scripts for?)</label>
          <input v-model="newName" maxlength="64" placeholder="e.g. Dispatch bot" @keyup.enter="create" />
        </div>
        <button class="btn btn--primary" title="Mint a new API key" :disabled="!newName.trim()" @click="create">Create key</button>
      </div>

      <p class="small" style="margin:.8rem 0 .4rem">
        The key can only call the endpoints you tick here. It can never mint or revoke keys.
      </p>
      <div style="display:grid; grid-template-columns:repeat(auto-fit, minmax(220px, 1fr)); gap:.3rem 2rem; max-width:640px">
        <div v-for="res in resources" :key="res" style="display:flex; gap:1.2rem">
          <span style="min-width:5.5rem">{{ res[0]!.toUpperCase() + res.slice(1) }}</span>
          <label style="display:flex; gap:.3rem; align-items:center; margin:0">
            <input v-model="newPerms[`${res}.read` as ApiKeyPermission]" type="checkbox" /> read
          </label>
          <label style="display:flex; gap:.3rem; align-items:center; margin:0">
            <input v-model="newPerms[`${res}.write` as ApiKeyPermission]" type="checkbox" /> write
          </label>
        </div>
      </div>
      <p v-if="error" class="callout callout--error small" style="margin:.8rem 0 0">{{ error }}</p>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="keys.length" class="table" style="cursor:default">
        <thead>
          <tr><th>Name</th><th>Key</th><th>Permissions</th><th>Created</th><th>Last used</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="k in keys" :key="k.id" :style="k.revokedAt ? 'text-decoration:line-through; opacity:.55' : ''">
            <td><strong>{{ k.name }}</strong></td>
            <td class="mono">{{ k.keyPrefix }}…</td>
            <td>
              <span v-if="isFullAccess(k.permissions)" class="badge badge--trip">full access</span>
              <span v-else>
                <span v-for="p in k.permissions" :key="p" class="badge badge--picked_up" style="margin-right:.3rem">{{ p }}</span>
              </span>
            </td>
            <td>{{ fmtDateTime(k.createdAt) }}</td>
            <td>{{ fmtDateTime(k.lastUsedAt) }}</td>
            <td>
              <span v-if="k.revokedAt">revoked {{ fmtDate(k.revokedAt) }}</span>
              <span v-else-if="confirming === k.id" style="display:flex; gap:.4rem">
                <button class="btn btn--sm btn--danger" title="Revoke immediately — scripts using this key get 401" @click="revoke(k)">Confirm</button>
                <button class="btn btn--sm" @click="confirming = null">Keep</button>
              </span>
              <button v-else class="btn btn--sm btn--danger" title="Stop this key from working (scripts will get 401)" @click="confirming = k.id">Revoke</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No API keys yet. Scripts speak Bearer; browsers speak cookies.</p>
    </div>
  </main>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api } from '@/api'
import type { ApiKeyCreated, ApiKeyPermission, ApiKeyRow } from '@/types'
import { fmtDate, fmtDateTime } from '@/format'

const ALL_PERMS: ApiKeyPermission[] = [
  'shipments.read', 'shipments.write',
  'clients.read', 'clients.write',
  'trips.read', 'trips.write',
  'cities.read', 'cities.write',
]
const resources = ['shipments', 'clients', 'trips', 'cities']

const keys = ref<ApiKeyRow[]>([])
const newName = ref('')
const newPerms = reactive<Record<ApiKeyPermission, boolean>>(
  Object.fromEntries(ALL_PERMS.map((p) => [p, true])) as Record<ApiKeyPermission, boolean>)
const freshKey = ref<string | null>(null)
const copied = ref(false)
const error = ref('')
const confirming = ref<number | null>(null)

function isFullAccess(perms: ApiKeyPermission[]): boolean {
  return ALL_PERMS.every((p) => perms.includes(p))
}

async function reload() {
  keys.value = await api.get<ApiKeyRow[]>('/api/admin/keys')
}

async function create() {
  error.value = ''
  const perms = ALL_PERMS.filter((p) => newPerms[p])
  try {
    const created = await api.post<ApiKeyCreated>('/api/admin/keys', {
      name: newName.value.trim(),
      permissions: perms,
    })
    freshKey.value = created.key
    copied.value = false
    newName.value = ''
    // defaults restored to all-ticked for the next mint
    ALL_PERMS.forEach((p) => (newPerms[p] = true))
    await reload()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to create the key.'
  }
}

async function copyKey() {
  if (!freshKey.value) return
  await navigator.clipboard.writeText(freshKey.value)
  copied.value = true
}

async function revoke(k: ApiKeyRow) {
  try {
    await api.post(`/api/admin/keys/${k.id}/revoke`)
    confirming.value = null
    await reload()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to revoke the key.'
  }
}

onMounted(reload)
</script>
