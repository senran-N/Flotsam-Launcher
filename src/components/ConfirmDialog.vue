<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { AlertTriangle, X } from 'lucide-vue-next';
import { useLauncher } from '../store';
import { t } from '../i18n';
const store = useLauncher();
const dialog = ref<HTMLElement | null>(null);
const confirmButton = ref<HTMLButtonElement | null>(null);
let restoreFocus: HTMLElement | null = null;

watch(() => store.confirmRequest, async (request) => {
  if (request) {
    restoreFocus = document.activeElement as HTMLElement | null;
    await nextTick();
    confirmButton.value?.focus();
  } else {
    restoreFocus?.focus();
    restoreFocus = null;
  }
});

function keydown(event: KeyboardEvent) {
  const request = store.confirmRequest;
  if (!request) return;
  if (event.key === 'Escape') { event.preventDefault(); store.answerConfirm(false); return; }
  if (event.key !== 'Tab') return;
  const controls = Array.from(dialog.value?.querySelectorAll<HTMLElement>('button') ?? []);
  if (!controls.length) return;
  const first = controls[0], last = controls.at(-1);
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
}
onMounted(() => document.addEventListener('keydown', keydown));
onUnmounted(() => document.removeEventListener('keydown', keydown));
</script>

<template>
  <div v-if="store.confirmRequest" class="modal-backdrop" @click.self="store.answerConfirm(false)">
    <section ref="dialog" class="confirm-dialog" role="dialog" aria-modal="true" :aria-label="store.confirmRequest.title">
      <header>
        <AlertTriangle :size="20" :class="{danger: store.confirmRequest.danger}" aria-hidden="true"/>
        <h2>{{ store.confirmRequest.title }}</h2>
        <button class="icon-button" :aria-label="t('common.close')" @click="store.answerConfirm(false)"><X :size="18"/></button>
      </header>
      <p class="confirm-body">{{ store.confirmRequest.body }}</p>
      <p v-if="store.confirmRequest.detail" class="confirm-detail">{{ store.confirmRequest.detail }}</p>
      <div class="confirm-actions">
        <button class="button secondary compact" @click="store.answerConfirm(false)">{{ store.confirmRequest.cancel || t('common.dismiss') }}</button>
        <button ref="confirmButton" :class="['button','compact', store.confirmRequest.danger ? 'danger' : 'primary']" @click="store.answerConfirm(true)">{{ store.confirmRequest.confirm }}</button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.confirm-dialog{width:min(460px,100%);background-color:var(--surface);background-image:url('/art/game/paper-map.webp');background-size:520px;border:3px solid var(--outline);border-radius:16px;padding:22px 24px 20px;box-shadow:0 8px 0 var(--outline)}
.confirm-dialog header{display:flex;align-items:center;gap:11px;margin-bottom:12px}
.confirm-dialog header>svg{color:var(--accent)}
.confirm-dialog header>svg.danger{color:var(--danger)}
.confirm-dialog h2{font-size:18px;flex:1;min-width:0;overflow-wrap:anywhere}
.confirm-body{font-size:13.5px;line-height:1.75;color:var(--ink)}
.confirm-detail{font-size:12.5px;line-height:1.7;color:var(--muted);margin-top:10px;padding:8px 12px;border-radius:8px;background:var(--paper-bright);border:1.5px solid var(--outline);overflow-wrap:anywhere;white-space:pre-line}
.confirm-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:20px}
@media(max-width:540px){.confirm-dialog{padding:18px}.confirm-actions{flex-direction:column-reverse}.confirm-actions .button{width:100%}}
</style>
