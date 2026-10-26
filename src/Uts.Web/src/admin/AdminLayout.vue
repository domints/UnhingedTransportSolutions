<template>
  <div>
    <header class="adminbar">
      <div class="adminbar__inner">
        <RouterLink to="/" class="brand">
          <span class="brand__mark mono">UTS</span>
          <span class="brand__light">Transport Solutions</span>
        </RouterLink>
        <template v-if="state?.clientId != null">
          <RouterLink to="/admin">Shipments</RouterLink>
          <RouterLink to="/admin/clients">Clients</RouterLink>
          <RouterLink to="/admin/cities">Cities</RouterLink>
          <RouterLink to="/admin/trips">Trips</RouterLink>
        </template>
        <span class="adminbar__spacer"></span>
        <span v-if="state" class="adminbar__user">
          {{ state.name }}
          <a href="/auth/logout">Log out</a>
        </span>
      </div>
    </header>

    <div v-if="loading" class="spinner">Checking your staff badge…</div>

    <div v-else-if="blocked" class="container">
      <div class="hero">
        <h1>Access denied</h1>
        <p class="hero__tag">
          You are signed in, but not as UTS staff.
          Ask an administrator to add you to the <strong>{{ adminGroup }}</strong> group in Authentik.
        </p>
        <RouterLink to="/">Back to the public site</RouterLink>
      </div>
    </div>

    <div v-else-if="state">
      <RouterView />
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { RouterLink, RouterView, useRouter } from 'vue-router'
import { api } from '@/api'
import type { AuthState } from '@/types'

const router = useRouter()
const state = ref<AuthState | null>(null)
const loading = ref(true)
const blocked = ref(false)
const adminGroup = 'uts-admins'

// Unmapped couriers go straight to onboarding ("who are you?"), mapped
// ones are bounced off it. Keeps the rest of the admin behind a claim.
function applyGuards(s: AuthState, path: string) {
  if (!s.isAdmin) return
  if (s.clientId === null && path !== '/admin/onboarding') {
    void router.replace('/admin/onboarding')
  }
  if (s.clientId !== null && path === '/admin/onboarding') {
    void router.replace('/admin')
  }
}

async function refreshState(path: string) {
  try {
    const s = await api.get<AuthState>('/api/auth/state')
    if (!s.authenticated) {
      window.location.href = `/auth/login?next=${encodeURIComponent(path)}`
      return
    }
    if (!s.isAdmin) {
      blocked.value = true
    }
    state.value = s
    applyGuards(s, path)
  } catch {
    blocked.value = true
    state.value = { authenticated: false, isAdmin: false, name: null, clientId: null, clientName: null }
  }
}

onMounted(() => refreshState(router.currentRoute.value.fullPath).finally(() => (loading.value = false)))

// The layout persists across child-route changes, so the claim flow's
// router.replace back to /admin would otherwise leave the stale
// clientId=null state here and the nav menu would stay hidden until a
// hard reload. Re-fetch while we still believe the user is unclaimed.
watch(
  () => router.currentRoute.value.path,
  async (path) => {
    if (state.value?.isAdmin && state.value.clientId === null) {
      await refreshState(path)
    }
  },
)
</script>
