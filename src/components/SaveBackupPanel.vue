<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { ArchiveRestore, AlertTriangle, FolderOpen, HardDrive, LoaderCircle, RefreshCw, ShieldCheck } from 'lucide-vue-next';
import { command, desktop, errorText } from '../bridge';
import { useLauncher } from '../store';

interface SaveBackup { backup: string; files: number; bytes: number; towns?: string[] }
interface RestoreSaveReport extends SaveBackup { rollbackGeneration: string | null }

const store = useLauncher();
const locale = computed(() => (store.locale === 'en' ? 'en' : 'zh'));
const copy = {
  zh: {
    title: '还原存档备份',
    desktopOnly: '网页预览不会读取或修改游戏存档。',
    refresh: '刷新备份',
    retry: '重试读取备份',
    loading: '正在读取备份…',
    empty: '尚无存档备份',
    emptyHint: '点击左侧「备份存档」创建快照。',
    files: '{count} 个文件',
    unknownDate: '时间未知',
    restore: '还原此备份',
    restoring: '处理中…',
    stopped: '游戏正在运行，请退出游戏后再还原。',
    disconnected: '联机仍处于连接状态。请先离开房间并断开联机，再还原。',
    snapshotRequired: '尚未读取游戏状态，请稍候。',
    busy: '请等待当前操作完成。',
    reviewTitle: '第 1 步：核对还原范围',
    reviewBody: '将用所选备份替换整个当前存档目录；现有存档会另存为回滚副本，所选备份保持不变。',
    reviewDetail: '备份：{backup}\n{date} · {files} · {size}',
    reviewConfirm: '已核对，继续',
    finalTitle: '第 2 步：确认还原存档',
    finalBody: '请确认游戏已退出且联机已断开。点击“确认还原”后开始写入。',
    finalConfirm: '确认还原',
    cancel: '取消，不还原',
    failed: '还原未完成，请查看提示后重试。',
    restored: '已还原备份：{backup}',
    rollback: '旧存档回滚副本：',
  },
  en: {
    title: 'Restore backups',
    desktopOnly: 'The browser preview does not read or modify game saves.',
    refresh: 'Refresh backups',
    retry: 'Retry loading backups',
    loading: 'Loading backups…',
    empty: 'No save backups yet',
    emptyHint: 'Click “Back up saves” to create a snapshot.',
    files: '{count} files',
    unknownDate: 'Unknown time',
    restore: 'Restore this backup',
    restoring: 'Working…',
    stopped: 'The game is running. Exit the game before restoring.',
    disconnected: 'Multiplayer is still connected. Leave the room before restoring.',
    snapshotRequired: 'Game status has not loaded yet.',
    busy: 'Wait for the current operation to finish.',
    reviewTitle: 'Step 1: Review the restore scope',
    reviewBody: 'The selected backup replaces the entire current saves directory; existing saves are retained as a rollback copy.',
    reviewDetail: 'Backup: {backup}\n{date} · {files} · {size}',
    reviewConfirm: 'Reviewed, continue',
    finalTitle: 'Step 2: Confirm save restore',
    finalBody: 'Confirm the game has exited and multiplayer is disconnected before writing.',
    finalConfirm: 'Confirm restore',
    cancel: 'Cancel, do not restore',
    failed: 'Restore did not complete.',
    restored: 'Restored backup: {backup}',
    rollback: 'Rollback copy:',
  },
} as const;

type CopyKey = keyof typeof copy.zh;
function t(key: CopyKey, values: Record<string, string | number> = {}) {
  return copy[locale.value][key].replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match));
}

const backups = ref<SaveBackup[]>([]);
const loading = ref(false);
const loaded = ref(false);
const error = ref('');
const restoring = ref('');
const restored = ref<RestoreSaveReport | null>(null);
const anyBusy = computed(() => loading.value || Boolean(restoring.value) || store.busy);
let disposed = false;

function timestamp(backup: string) {
  const match = /^存档-(\d+)$/.exec(backup);
  const value = match ? Number(match[1]) : NaN;
  return Number.isSafeInteger(value) && Number.isFinite(new Date(value).getTime()) ? value : null;
}

function date(backup: string) {
  const value = timestamp(backup);
  return value === null ? t('unknownDate') : new Date(value).toLocaleString(locale.value === 'en' ? 'en-GB' : 'zh-CN');
}

function size(bytes: number) {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${new Intl.NumberFormat(locale.value === 'en' ? 'en-GB' : 'zh-CN', { maximumFractionDigits: unit ? 1 : 0 }).format(value)} ${units[unit]}`;
}

async function load() {
  if (!desktop || loading.value) return;
  loading.value = true;
  error.value = '';
  try {
    const result = await command<SaveBackup[]>('list_save_backups');
    if (disposed) return;
    backups.value = [...result].sort((a, b) => (timestamp(b.backup) ?? 0) - (timestamp(a.backup) ?? 0));
    loaded.value = true;
  } catch (e) {
    if (!disposed) error.value = errorText(e);
  } finally {
    loading.value = false;
  }
}

async function checkReady() {
  if (!store.data) throw new Error(t('snapshotRequired'));
  if (store.running) throw new Error(t('stopped'));
  if (store.busy) throw new Error(t('busy'));
  const room = await command<{ connected: boolean }>('room_status');
  if (room.connected) throw new Error(t('disconnected'));
}

async function restore(backup: SaveBackup) {
  if (!desktop || anyBusy.value) return;
  restoring.value = backup.backup;
  restored.value = null;
  error.value = '';
  try {
    await checkReady();
    if (disposed) return;
    const detail = t('reviewDetail', {
      backup: backup.backup,
      date: date(backup.backup),
      files: t('files', { count: backup.files }),
      size: size(backup.bytes),
    });
    if (!await store.confirm({
      title: t('reviewTitle'),
      body: t('reviewBody'),
      detail,
      confirm: t('reviewConfirm'),
      cancel: t('cancel'),
      danger: true,
    }) || disposed) return;
    if (!await store.confirm({
      title: t('finalTitle'),
      body: t('finalBody'),
      detail,
      confirm: t('finalConfirm'),
      cancel: t('cancel'),
      danger: true,
    }) || disposed) return;
    await checkReady();
    if (disposed) return;
    const result = await store.run<RestoreSaveReport>('restore_save_backup', { backup: backup.backup });
    if (disposed) return;
    if (result === undefined) {
      error.value = store.toastKind === 'error' && store.toast ? store.toast : t('failed');
      return;
    }
    restored.value = result;
    await load();
  } catch (e) {
    if (!disposed) {
      error.value = errorText(e);
      store.notify(error.value, 'error');
    }
  } finally {
    restoring.value = '';
  }
}

function openBackupDir(backup: string) {
  command('open_directory', { kind: 'backups', subpath: backup }).catch((e) => {
    store.notify(errorText(e), 'error');
  });
}

onMounted(load);
onUnmounted(() => { disposed = true; });
defineExpose({ load });
</script>

<template>
  <section class="backup-pane" :aria-label="t('title')" data-testid="save-backup-panel">
    <header class="backup-head">
      <h2><ArchiveRestore :size="17" aria-hidden="true"/>{{ t('title') }}</h2>
      <button type="button" class="button ghost compact" :disabled="!desktop || anyBusy" @click="load">
        <RefreshCw :size="13" :class="{ spin: loading }" aria-hidden="true"/>{{ t('refresh') }}
      </button>
    </header>

    <p v-if="!desktop" class="backup-empty-note">{{ t('desktopOnly') }}</p>
    <template v-else>
      <div v-if="error" class="inline-error" role="alert">
        <span>{{ error }}</span>
        <button type="button" class="text-button" :disabled="anyBusy" @click="load">{{ t('retry') }}</button>
      </div>

      <p v-if="store.running" class="backup-warn" role="status">
        <AlertTriangle :size="14" aria-hidden="true"/>{{ t('stopped') }}
      </p>

      <div v-if="restored" class="backup-result" role="status">
        <h3><ShieldCheck :size="15" aria-hidden="true"/>{{ t('restored', { backup: restored.backup }) }}</h3>
        <template v-if="restored.rollbackGeneration">
          <p>{{ t('rollback') }}</p>
          <code>{{ restored.rollbackGeneration }}</code>
        </template>
      </div>

      <div v-if="backups.length" class="backup-scroll" :aria-busy="loading">
        <article v-for="backup in backups" :key="backup.backup" class="backup-item">
          <div class="backup-meta">
            <h3>{{ backup.backup }}</h3>
            <strong v-if="backup.towns?.length" class="backup-towns">{{ backup.towns.join(' · ') }}</strong>
            <span>{{ date(backup.backup) }} · {{ t('files', { count: backup.files }) }} · {{ size(backup.bytes) }}</span>
          </div>
          <div style="display:flex;align-items:center;gap:6px">
            <button
              type="button"
              class="icon-button"
              style="width:32px;height:32px"
              :title="backup.backup"
              :disabled="!desktop"
              @click="openBackupDir(backup.backup)"
            >
              <FolderOpen :size="14" aria-hidden="true"/>
            </button>
            <button
              type="button"
              class="button secondary compact"
              :disabled="anyBusy || store.running || !store.data"
              @click="restore(backup)"
            >
              <LoaderCircle v-if="restoring === backup.backup" :size="14" class="spin" aria-hidden="true"/>
              <ArchiveRestore v-else :size="14" aria-hidden="true"/>
              {{ restoring === backup.backup ? t('restoring') : t('restore') }}
            </button>
          </div>
        </article>
      </div>
      <div v-else-if="loaded && !loading && !error" class="backup-empty-box">
        <HardDrive :size="26" :stroke-width="1.4" aria-hidden="true"/>
        <h3>{{ t('empty') }}</h3>
        <p>{{ t('emptyHint') }}</p>
      </div>
    </template>
  </section>
</template>

<style scoped>
.backup-pane {
  min-height: 0;
  height: 100%;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 16px;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 480px;
  border: 2.5px solid var(--outline);
  border-radius: 15px;
  box-shadow: var(--shadow-hard);
}

.backup-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex: none;
}

.backup-head h2 {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 16px;
}

.backup-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.backup-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 10px;
  background: var(--paper-bright);
  border: 1.5px solid var(--outline);
  flex-wrap: wrap;
}

.backup-meta h3 {
  font-size: 13.5px;
}

.backup-towns {
  display: block;
  font-size: 12px;
  color: var(--ink);
  margin-top: 2px;
}

.backup-meta span {
  display: block;
  font-size: 11.5px;
  color: var(--muted);
  margin-top: 2px;
}

.backup-empty-note,
.backup-empty-box {
  margin: auto;
  text-align: center;
  font-size: 12.5px;
  color: var(--muted);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
}

.backup-warn,
.backup-result {
  padding: 9px 12px;
  border-radius: 10px;
  border: 1.5px solid var(--outline);
  background: #fff3d4;
  font-size: 12px;
  flex: none;
}

.backup-result {
  background: #dff8ec;
}

.backup-result h3 {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}

.backup-result code {
  display: block;
  margin-top: 4px;
  padding: 4px 7px;
  border-radius: 6px;
  background: var(--paper-bright);
  font-size: 11px;
  overflow-wrap: anywhere;
}
</style>
