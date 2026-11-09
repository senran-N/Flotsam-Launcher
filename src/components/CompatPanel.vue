<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { RefreshCw } from 'lucide-vue-next';
import { command, desktop, errorText } from '../bridge';
import { t } from '../i18n';
import type { Compatibility } from '../types';

const report = ref<Compatibility | null>(null);
const loading = ref(false);
const error = ref('');

async function load() {
  if (!desktop) return;
  loading.value = true;
  error.value = '';
  try {
    report.value = await command<Compatibility>('compatibility_report');
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loading.value = false;
  }
}

onMounted(load);
defineExpose({ load });
</script>

<template>
  <section class="compat-box">
    <div class="compat-head">
      <h3>{{ t('settings.compat') }}</h3>
      <button type="button" class="text-button" :disabled="loading || !desktop" @click="load">
        <RefreshCw :size="13" :class="{ spin: loading }"/>{{ t('settings.refreshCompat') }}
      </button>
    </div>

    <div v-if="error" class="inline-error" role="alert">
      <span>{{ error }}</span>
      <button class="text-button" @click="load">{{ t('common.retry') }}</button>
    </div>

    <template v-else-if="report">
      <dl class="compat-list">
        <dt>{{ t('settings.compatGame') }}</dt>
        <dd class="mono">{{ report.game ? `${report.game.slice(0, 16)}…` : t('settings.notInstalled') }}</dd>
        <dt>{{ t('settings.compatLoader') }}</dt>
        <dd>{{ report.loader ? t('settings.installed') : t('settings.notInstalled') }}</dd>
        <dt>{{ t('settings.compatPlugins') }}</dt>
        <dd>{{ report.plugins.length ? report.plugins.join('、') : t('settings.compatNone') }}</dd>
        <dt>{{ t('settings.patchers', { count: report.patchers.length }) }}</dt>
        <dd>{{ report.patchers.length ? report.patchers.join('、') : t('settings.compatNone') }}</dd>
        <dt>{{ t('settings.compatFingerprint') }}</dt>
        <dd class="mono">{{ report.fingerprint ? `${report.fingerprint.slice(0, 16)}…` : t('settings.compatNone') }}</dd>
      </dl>
      <ul v-if="report.blockers?.length" class="compat-warning" role="alert">
        <li v-for="item in report.blockers" :key="item.detail">{{ item.detail }} · {{ item.fix }}</li>
      </ul>
      <p v-if="report.warning" class="compat-warning">{{ t('settings.compatWarning', { warning: report.warning }) }}</p>
    </template>
    <p v-else class="compat-hint">{{ !desktop ? t('common.required') : t('settings.compatEmpty') }}</p>
  </section>
</template>

<style scoped>
.compat-box {
  padding: 11px 14px;
  border-radius: 11px;
  background: var(--paper-bright);
  border: 2px solid var(--outline);
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.compat-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.compat-head h3 {
  font-size: 13.5px;
}

.compat-hint {
  font-size: 12px;
  color: var(--muted);
}

.compat-list {
  display: grid;
  grid-template-columns: 108px minmax(0, 1fr);
  gap: 5px 14px;
  margin: 0;
  font-size: 12px;
}

.compat-list dt {
  color: var(--muted);
  font-weight: 700;
}

.compat-list dd {
  margin: 0;
  overflow-wrap: anywhere;
}

.compat-list dd.mono {
  font-family: Consolas, monospace;
  font-size: 11.5px;
  color: var(--muted);
}

.compat-warning {
  margin: 0;
  font-size: 12px;
  color: #80523f;
}
</style>
