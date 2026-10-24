<template>
  <main class="container">
    <section class="hero">
      <span class="hero__eyebrow">Slightly irregular logistics</span>
      <h1>Unhinged Transport Solutions</h1>
      <p class="hero__tag">
        Someone is already travelling between your two cities anyway.<br />
        Your parcel may as well ride along.
      </p>

      <form class="trackbox" @submit.prevent="track">
        <input
          v-model="number"
          class="mono"
          :placeholder="exampleNumber"
          spellcheck="false"
          autocomplete="off"
        />
        <button class="btn btn--accent" type="submit">Track</button>
      </form>
      <p v-if="error" class="muted small" style="margin-top:.6rem">{{ error }}</p>
    </section>

    <section class="card" style="max-width: 720px; margin-left: auto; margin-right: auto">
      <h2>How does this work?</h2>
      <p>
        A real courier company, minus the terminals, the fleet and – most days – the schedule.
        <strong>Unhinged Transport Solutions</strong> moves parcels along routes they would
        take anyway: trains, cars of UTS™ personnel, and goodwill.
      </p>
      <p class="muted">
        Every shipment gets an official-looking number and a 6-digit access code.
        Type the number above; once you're on the tracking page, unlock the details
        — sender, contents, planned trip — with the access code from your courier.
      </p>
    </section>

    <footer class="muted small" style="text-align:center">
      UTS™ is a licensed imaginary carrier. Complaints route to the nearest ticket inspector. ·
      <a href="/admin">Staff area</a>
    </footer>
  </main>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()
const number = ref('')
const error = ref('')

// Random 6-digit serial on every page load — otherwise the example number
// looks like a real, existing shipment someone might try.
const exampleNumber = `UTS2137${Math.floor(100000 + Math.random() * 900000)}WRO`

function track() {
  const num = number.value.replace(/\s/g, '').toUpperCase()
  if (!num) {
    error.value = 'Did you lose the tracking number too? Bold strategy.'
    return
  }
  error.value = ''
  router.push(`/track/${encodeURIComponent(num)}`)
}
</script>
