<template>
  <div class="landing">
  <main class="container">
    <section class="hero">
      <span class="hero__eyebrow">Now with 0 (zero) depots</span>
      <h1>Unhinged Transport Solutions</h1>
      <p class="hero__tag">
        Your parcel doesn't need a warehouse.<br />
        It needs someone brave enough to be going your way anyway.
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
      <p class="muted small" style="margin-top:.4rem">
        The number above isn't yours. Yours is the one the courier scribbled down, probably.
      </p>
      <p v-if="error" class="muted small" style="margin-top:.6rem">{{ error }}</p>
    </section>

    <section class="card" style="max-width: 720px; margin-left: auto; margin-right: auto">
      <h2>How does this work?</h2>
      <p>
        Most carriers move your parcel from A to B. We move it the way <em>nature intended</em>:
        inside a backpack of someone riding the same train you'd expect it to ride.
        Distance: covered. Timing: metaphysical.
      </p>
      <p class="muted">
        Every shipment gets an official-looking number and a 6-digit access code.
        Type the number above; once you're on the tracking page, unlock the details
        — sender, contents, planned trip — with the access code from your courier.
      </p>
    </section>

    <footer class="muted small" style="text-align:center">
      Your parcel exists and does not exist until observed by you. Fitting for a courier who only
      claims to be at the post office until checked. ·
      <a href="/admin">Staff area</a>
    </footer>
  </main>
  </div>
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
