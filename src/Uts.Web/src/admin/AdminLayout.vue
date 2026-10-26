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
import { onMounted, ref } from 'vue'
import { RouterLink, RouterView, useRouter } from 'vue-router'
import { api } from '@/api'
import type { AuthState } from '@/types'

const router = useRouter()
const state = ref<AuthState | null>(null)
const loading = ref(true)
const blocked = ref(false)
const adminGroup = 'uts-admins'

onMounted(async () => {
  try {
    const s = await api.get<AuthState>('/api/auth/state')
    if (!s.authenticated) {
      window.location.href = `/auth/login?next=${encodeURIComponent(router.currentRoute.value.fullPath)}`
      return
    }
    if (!s.isAdmin) {
      blocked.value = true
    }
    state.value = s

    // Unmapped couriers go straight to onboarding ("who are you?"), mapped
    // ones are bounced off it. Keeps the rest of the admin behind a claim.
    if (s.isAdmin && s.clientId === null && router.currentRoute.value.path !== '/admin/onboarding') {
      await router.replace('/admin/onboarding')
    }
    if (s.isAdmin && s.clientId !== null && router.currentRoute.value.path === '/admin/onboarding') {
      await router.replace('/admin')
    }
  } catch (e) {
    blocked.value = true
    state.value = { authenticated: false, isAdmin: false, name: null, clientId: null, clientName: null }
  } finally {
    loading.value = false
  }
})
</script>
