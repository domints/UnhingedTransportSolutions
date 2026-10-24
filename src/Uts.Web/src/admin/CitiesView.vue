<template>
  <main class="container">
    <div class="toolbar">
      <h1 style="font-size:1.4rem; color:var(--navy); margin:0">Cities</h1>
    </div>

    <div class="card">
      <h2>Add city</h2>
      <div style="display:flex; gap:.6rem; flex-wrap:wrap; align-items:end">
        <div style="width:110px">
          <label style="margin-top:0">Code</label>
          <input v-model="newCode" maxlength="3" class="mono" placeholder="e.g. WRO" @input="newCode = newCode.toUpperCase()" />
        </div>
        <div style="flex:1; min-width:180px">
          <label style="margin-top:0">Name</label>
          <input v-model="newName" placeholder="e.g. Wrocław" @keyup.enter="add" />
        </div>
        <button class="btn btn--primary" :disabled="!newCode || !newName" @click="add">Add</button>
      </div>
      <p v-if="error" class="callout callout--error small" style="margin:.8rem 0 0">{{ error }}</p>
    </div>

    <div class="card" style="overflow-x:auto">
      <table v-if="cities.length" class="table" style="cursor:default">
        <thead>
          <tr><th>Code</th><th>Name</th><th>Active</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="c in cities" :key="c.code">
            <td class="mono"><strong>{{ c.code }}</strong></td>
            <td>
              <span v-if="editing === c.code" style="display:flex; gap:.4rem">
                <input v-model="editName" @keyup.enter="saveName(c)" />
                <button class="btn btn--sm" @click="saveName(c)">Save</button>
              </span>
              <template v-else>{{ c.name }}</template>
            </td>
            <td>
              <button
                class="btn btn--sm"
                :class="c.active ? '' : 'btn--danger'"
                @click="toggleActive(c)"
                :title="c.active ? 'Deactivate (hides from new shipments)' : 'Activate'"
              >
                {{ c.active ? 'Active' : 'Inactive' }}
              </button>
            </td>
            <td>
              <button v-if="editing !== c.code" class="btn btn--sm" @click="startRename(c)">Rename</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty">No cities. The map is a lie.</p>
    </div>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import type { City } from '@/types'

const cities = ref<City[]>([])
const newCode = ref('')
const newName = ref('')
const error = ref('')
const editing = ref<string | null>(null)
const editName = ref('')

async function reload() {
  cities.value = await api.get<City[]>('/api/admin/cities')
}

async function add() {
  error.value = ''
  try {
    await api.post<City>('/api/admin/cities', { code: newCode.value, name: newName.value })
    newCode.value = ''
    newName.value = ''
    await reload()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to add city.'
  }
}

function startRename(c: City) {
  editing.value = c.code
  editName.value = c.name
}

async function saveName(c: City) {
  try {
    await api.put<City>(`/api/admin/cities/${c.code}`, { name: editName.value })
    editing.value = null
    await reload()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to rename.'
  }
}

async function toggleActive(c: City) {
  try {
    await api.put<City>(`/api/admin/cities/${c.code}`, { active: !c.active })
    await reload()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to toggle city.'
  }
}

onMounted(reload)
</script>
