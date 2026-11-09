<script setup lang="ts">
import { computed } from 'vue';
import { X, RotateCw, CircleCheck, CircleX, LoaderCircle, Download, Clock } from 'lucide-vue-next';
import { useLauncher } from '../store';
import { t } from '../i18n';
import type { DownloadTask } from '../types';
const store = useLauncher();
const tasks = computed(() => store.tasks);
const running = computed(() => tasks.value.filter((task) => task.state === 'running').length);
const pending = computed(() => tasks.value.filter((task) => task.state === 'queued').length);
const hasFinished = computed(() => tasks.value.some((task) => task.state !== 'running' && task.state !== 'queued'));
function label(state: DownloadTask['state']) {
  if (state === 'queued') return t('downloads.queued');
  if (state === 'running') return t('downloads.running');
  if (state === 'done') return t('downloads.done');
  if (state === 'failed') return t('downloads.failed');
  return t('downloads.cancelled');
}
function percent(task: DownloadTask) {
  if (!task.total || !task.received) return 0;
  return Math.min(100, Math.round((task.received / task.total) * 100));
}
function size(n: number) {
  if (!n) return '';
  return n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`;
}
async function cancel(task: DownloadTask) {
  const ok = await store.confirm({
    title: t('downloads.confirmCancelTitle'),
    body: t('downloads.confirmCancelBody', { name: task.name || task.id }),
    confirm: t('downloads.cancelDownload'),
    danger: true,
  });
  if (!ok) return;
  await store.cancelTask(task.id);
  const current = store.tasks.find((entry) => entry.id === task.id);
  if (current?.state === 'cancelled') store.notify(t('downloads.cancelledNotice'), 'info');
}
async function retry(task: DownloadTask) {
  await store.retryTask(task.id);
  const current = store.tasks.find((entry) => entry.id === task.id);
  if (current?.state === 'queued') store.notify(t('downloads.retryQueued'), 'success');
}
</script>

<template>
  <section class="download-queue" :aria-label="t('downloads.title')">
    <header>
      <h2>{{ t('downloads.title') }}</h2>
      <span v-if="running || pending">{{ t('downloads.active', { count: running + pending }) }} · {{ t('downloads.limit', { count: 2 }) }}</span>
      <span v-else>{{ t('downloads.limit', { count: 2 }) }}</span>
      <button v-if="hasFinished" class="text-button" @click="store.clearFinishedTasks()">{{ t('downloads.clearDone') }}</button>
    </header>
    <div v-for="task in tasks" :key="task.id" class="queue-row" :data-state="task.state">
      <LoaderCircle v-if="task.state === 'running'" :size="16" class="spin"/>
      <CircleCheck v-else-if="task.state === 'done'" :size="16"/>
      <CircleX v-else-if="task.state === 'failed'" :size="16"/>
      <Clock v-else-if="task.state === 'queued'" :size="16"/>
      <Download v-else :size="16"/>
      <div class="queue-identity">
        <h3>{{ task.name || `#${task.fileId}` }}</h3>
        <span>{{ label(task.state) }}<template v-if="task.error"> · {{ task.error }}</template></span>
      </div>
      <div class="queue-progress">
        <progress :value="task.received" :max="task.total || undefined" :aria-label="`${task.name || task.id} ${label(task.state)}`"></progress>
        <span>{{ size(task.received) }}<template v-if="task.total"> / {{ size(task.total) }}</template><template v-if="task.state === 'running' && task.total"> · {{ percent(task) }}%</template></span>
      </div>
      <button v-if="task.state === 'running' || task.state === 'queued'" class="text-button" @click="cancel(task)"><X :size="14"/>{{ t('downloads.cancel') }}</button>
      <button v-else-if="task.state === 'failed' || task.state === 'cancelled'" class="text-button" @click="retry(task)"><RotateCw :size="14"/>{{ t('downloads.retry') }}</button>
    </div>
    <p v-if="!tasks.length" class="catalog-note">{{ t('downloads.empty') }}</p>
  </section>
</template>

<style scoped>
.download-queue{padding:16px 20px;background:var(--surface);border:2.5px solid var(--outline);border-radius:14px;box-shadow:var(--shadow-hard)}
.download-queue header{display:flex;align-items:baseline;justify-content:space-between;gap:14px;margin-bottom:8px}
.download-queue header h2{flex:none;font-size:16px}
.download-queue header span{font-size:12px;color:var(--muted);font-weight:700}
.download-queue header .text-button{margin-left:auto}
.queue-row{display:grid;grid-template-columns:20px minmax(0,1fr) 220px auto;align-items:center;gap:14px;padding:12px 0;border-top:1.5px dashed var(--line)}
.queue-row>svg{color:var(--ink-soft)}
.queue-row[data-state=failed]>svg{color:var(--danger)}
.queue-identity h3{font-size:14px;font-weight:800;overflow-wrap:anywhere}
.queue-identity span{font-size:12px;color:var(--muted);overflow-wrap:anywhere}
.queue-progress{display:flex;flex-direction:column;gap:5px;min-width:0}
.queue-progress progress{width:100%;height:8px;appearance:none;border:1.5px solid var(--outline);border-radius:999px;background:var(--paper-bright);overflow:hidden}
.queue-progress progress::-webkit-progress-bar{background:var(--paper-bright)}
.queue-progress progress::-webkit-progress-value{background:var(--accent)}
.queue-progress span{font-size:11px;color:var(--muted);font-variant-numeric:tabular-nums;font-weight:700}
@media(max-width:760px){.queue-row{grid-template-columns:20px minmax(0,1fr) auto;gap:10px}.queue-progress{grid-column:2/-1}.download-queue header{flex-wrap:wrap}.download-queue header .text-button{margin-left:0}}
</style>
