<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { ArrowUpRight, Download, RefreshCw, Plug, LoaderCircle, PackagePlus, Search, Trash2 } from 'lucide-vue-next';
import { command, desktop, errorText } from '../bridge';
import { useLauncher } from '../store';
import { t } from '../i18n';
import type { DownloadRecord, DownloadTask, ModPackage } from '../types';
import PopoverBox from './PopoverBox.vue';
import SlideDrawer from './SlideDrawer.vue';
import DownloadQueue from './DownloadQueue.vue';

interface Mod { id: number; name: string; author: string; summary: string; version: string }
interface ModFile { id: number; name: string; version: string; fileName: string; size: number }
interface Account { name: string; premium: boolean }

const store = useLauncher();
const nxm = ref('');
const mods = ref<Mod[]>([]);
const search = ref('');
const loading = ref(false);
const error = ref('');
const account = ref<Account | null>(null);
const key = ref('');
const accountOpen = ref(false);
const queueDrawer = ref(false);
const connecting = ref(false);
const selected = ref<Mod | null>(null);
const files = ref<ModFile[]>([]);
const loadingFiles = ref(false);
const downloads = ref<DownloadRecord[]>([]);
const installing = ref('');
const queueing = ref('');
const target = ref<Record<string, string>>({});

const tasks = computed(() => store.tasks);
const filtered = computed(() =>
  mods.value.filter((m) => `${m.name} ${m.author} ${m.summary}`.toLowerCase().includes(search.value.toLowerCase())),
);
const packages = computed<ModPackage[]>(() => {
  const seen = new Map<string, ModPackage>();
  for (const mod of store.data?.mods || []) if (mod.package) seen.set(mod.package.id, mod.package);
  return [...seen.values()];
});
const gameRunning = computed(() => store.running);

let disposed = false;

async function load() {
  loading.value = true;
  error.value = '';
  try {
    account.value = await command<Account | null>('nexus_account');
    const result = await command<{ mods: Mod[]; live: boolean }>('online_catalog');
    mods.value = result.mods;
    downloads.value = await command<DownloadRecord[]>('list_downloads');
    await store.loadTasks();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loading.value = false;
  }
}

async function connect() {
  connecting.value = true;
  try {
    account.value = await command<Account>('connect_nexus', { apiKey: key.value });
    key.value = '';
    accountOpen.value = false;
    store.notify(t('discover.connected'), 'success');
    await load();
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    connecting.value = false;
  }
}

async function disconnect() {
  try {
    await command('disconnect_nexus');
    account.value = null;
    accountOpen.value = false;
    selected.value = null;
    await load();
  } catch (e) {
    store.notify(errorText(e), 'error');
  }
}

async function openSource(id?: number, accountOnly = false) {
  try {
    if (desktop) await command('open_mod_source', { modId: id ?? null, account: accountOnly });
    else {
      window.open(
        accountOnly
          ? 'https://www.nexusmods.com/users/myaccount?tab=api'
          : id
            ? `https://www.nexusmods.com/flotsam/mods/${id}?tab=files`
            : 'https://www.nexusmods.com/flotsam/mods/',
        '_blank',
        'noopener,noreferrer',
      );
    }
  } catch (e) {
    store.notify(errorText(e), 'error');
  }
}

async function choose(mod: Mod) {
  nxm.value = '';
  if (!account.value) {
    accountOpen.value = true;
    return;
  }
  selected.value = mod;
  queueDrawer.value = true;
  files.value = [];
  loadingFiles.value = true;
  try {
    files.value = await command<ModFile[]>('online_files', { modId: mod.id });
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    loadingFiles.value = false;
  }
}

async function queueMod(file: ModFile, link?: string) {
  if (!selected.value || queueing.value) return;
  queueing.value = String(file.id);
  try {
    await command<DownloadTask>('queue_download', { modId: selected.value.id, fileId: file.id, ...(link ? { nxm: link } : {}) });
    nxm.value = '';
    await store.loadTasks();
    store.notify(t('discover.queued'), 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    queueing.value = '';
  }
}

async function queueLink() {
  try {
    const url = new URL(nxm.value.trim());
    const match = url.pathname.match(/^\/mods\/(\d+)\/files\/(\d+)$/);
    if (url.protocol !== 'nxm:' || url.hostname !== 'flotsam' || Number(match?.[1]) !== selected.value?.id) {
      throw new Error(t('discover.nxmMismatch'));
    }
    const file = files.value.find((f) => f.id === Number(match?.[2]));
    if (!file) throw new Error(t('discover.nxmMissingFile'));
    await queueMod(file, nxm.value.trim());
  } catch (e) {
    store.notify(e instanceof Error ? e.message : errorText(e), 'error');
  }
}

function packageIdFor(item: DownloadRecord) {
  return packages.value.find((p) => p.nexusModId === item.modId)?.id || '';
}

async function install(item: DownloadRecord) {
  if (installing.value || gameRunning.value) return;
  const packageId = target.value[item.id] === undefined ? packageIdFor(item) : target.value[item.id];
  installing.value = item.id;
  try {
    await command<number>('install_download', { id: item.id, ...(packageId ? { packageId } : {}) });
    await store.refresh();
    store.chooseProfile('本地模组');
    store.notify(t('discover.installed', { name: item.name }), 'success');
    downloads.value = await command<DownloadRecord[]>('list_downloads');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    installing.value = '';
  }
}

async function discardCache(item: DownloadRecord) {
  if (installing.value) return;
  try {
    await command('discard_download_cache', { id: item.id });
    downloads.value = await command<DownloadRecord[]>('list_downloads');
    store.notify(t('discover.discarded'), 'info');
  } catch (e) {
    store.notify(errorText(e), 'error');
  }
}

function size(n: number) {
  return n ? `${(n / 1048576).toFixed(1)} MB` : '';
}

watch(
  () => store.tasks.filter((task) => task.state === 'done').length,
  async (doneCount, prevCount) => {
    if (doneCount > (prevCount ?? 0) && !disposed) {
      downloads.value = await command<DownloadRecord[]>('list_downloads').catch(() => downloads.value);
    }
  },
);

onMounted(load);
onUnmounted(() => { disposed = true; key.value = ''; });
</script>

<template>
  <div class="online-stage">
    <div class="online-bar">
      <label class="search">
        <Search :size="15"/>
        <input v-model="search" :aria-label="t('discover.search')" :placeholder="t('discover.search')"/>
      </label>

      <div class="online-tools">
        <button v-if="tasks.length || downloads.length" type="button" class="button secondary compact" @click="queueDrawer = true">
          <Download :size="14"/>{{ t('downloads.title') }} ({{ tasks.length + downloads.length }})
        </button>

        <button type="button" class="button ghost compact" @click="openSource()">
          {{ t('discover.browse') }}<ArrowUpRight :size="14"/>
        </button>

        <!-- Nexus Account Popover (No Layout Push!) -->
        <PopoverBox :open="accountOpen" width="320px" @close="accountOpen = false">
          <template #trigger>
            <button type="button" class="button ghost compact" @click="accountOpen = !accountOpen">
              <Plug :size="14"/>{{ account?.name || t('discover.account') }}
            </button>
          </template>
          <form v-if="!account" class="pop-form" @submit.prevent="connect">
            <label class="pop-label" for="nexus-key">{{ t('discover.apiKey') }}</label>
            <div class="inline-field">
              <input id="nexus-key" v-model="key" type="password" autocomplete="off" :placeholder="t('discover.apiKeyPlaceholder')" required/>
              <button class="button primary compact" :disabled="connecting || !key.trim()">
                <LoaderCircle v-if="connecting" :size="14" class="spin"/>
                {{ connecting ? t('discover.connecting') : t('discover.connect') }}
              </button>
            </div>
            <div class="pop-foot">
              <span>{{ t('discover.keyHint') }}</span>
              <button type="button" class="text-button" @click="openSource(undefined, true)">
                {{ t('discover.getKey') }}<ArrowUpRight :size="13"/>
              </button>
            </div>
          </form>
          <div v-else class="pop-foot">
            <strong>{{ account.name }} · {{ account.premium ? t('discover.premium') : t('discover.free') }}</strong>
            <button type="button" class="text-button" @click="disconnect">{{ t('discover.disconnect') }}</button>
          </div>
        </PopoverBox>

        <button type="button" class="icon-button" :aria-label="t('discover.refresh')" :disabled="loading" @click="load">
          <RefreshCw :size="16" :class="{ spin: loading }"/>
        </button>
      </div>
    </div>

    <div v-if="error" class="inline-error" role="alert">
      <span>{{ error }}</span>
      <button type="button" class="text-button" @click="load">{{ t('common.retry') }}</button>
    </div>

    <div v-if="filtered.length" class="scroll-grid catalog-cards">
      <article v-for="mod in filtered" :key="mod.id" class="catalog-card">
        <div class="catalog-card-body">
          <div class="catalog-card-head">
            <h2>{{ mod.name }}</h2>
            <span v-if="mod.author" class="author-tag">{{ mod.author }}</span>
          </div>
          <p>{{ mod.summary }}</p>
        </div>
        <div class="catalog-card-actions">
          <button type="button" class="text-button" :aria-label="`${t('discover.authorPage')} ${mod.name}`" @click="openSource(mod.id)">
            {{ t('discover.authorPage') }}<ArrowUpRight :size="14"/>
          </button>
          <button type="button" class="button secondary compact" :aria-label="`${t('discover.download')} ${mod.name}`" @click="choose(mod)">
            <Download :size="14"/>{{ t('discover.download') }}
          </button>
        </div>
      </article>
    </div>
    <div v-else-if="!loading && !error" class="empty-stage">
      <h2>{{ t('discover.empty') }}</h2>
    </div>

    <!-- Slide-Over Drawer for Mod Files, Download Progress & Cached Installs -->
    <SlideDrawer :open="queueDrawer" :title="selected ? selected.name : t('downloads.title')" width="460px" @close="queueDrawer = false">
      <template v-if="selected">
        <p v-if="loadingFiles" class="drawer-note">{{ t('common.loading') }}</p>
        <p v-else-if="!files.length" class="drawer-note">{{ t('discover.noFiles') }}</p>
        <template v-else>
          <p v-if="!account?.premium" class="drawer-note">{{ t('discover.freeHint') }}</p>
          <form v-if="account && !account.premium" class="inline-field" @submit.prevent="queueLink">
            <input v-model="nxm" :aria-label="t('discover.nxm')" placeholder="nxm://flotsam/…" autocomplete="off"/>
            <button class="button secondary compact" :disabled="!nxm.trim()">{{ t('discover.downloadLink') }}</button>
          </form>
          <div v-for="file in files" :key="file.id" class="drawer-item">
            <div>
              <strong>{{ file.name }}</strong>
              <span>{{ size(file.size) }}</span>
            </div>
            <button type="button" class="button secondary compact" :disabled="!account?.premium || queueing === String(file.id)" @click="queueMod(file)">
              <Download :size="14"/>{{ t('discover.queue') }}
            </button>
          </div>
        </template>
      </template>

      <DownloadQueue v-if="tasks.length"/>

      <section v-if="downloads.length" class="cached-section">
        <h3>{{ t('discover.downloaded') }}</h3>
        <div v-for="item in downloads" :key="item.id" class="drawer-item">
          <div>
            <strong>{{ item.name }}</strong>
            <span>{{ size(item.size) }}</span>
          </div>
          <select
            v-if="packages.length"
            class="pkg-select"
            :value="target[item.id] ?? packageIdFor(item)"
            :aria-label="`${t('discover.installTarget')} ${item.name}`"
            @change="target[item.id] = ($event.target as HTMLSelectElement).value"
          >
            <option value="">{{ t('discover.installAsNew') }}</option>
            <option v-for="pkg in packages" :key="pkg.id" :value="pkg.id">{{ t('discover.installAsUpdate', { name: pkg.name }) }}</option>
          </select>
          <div style="display:flex;align-items:center;gap:6px">
            <button type="button" class="button primary compact" :disabled="!!installing || gameRunning" @click="install(item)">
              <LoaderCircle v-if="installing === item.id" :size="14" class="spin"/>
              <PackagePlus v-else :size="14"/>
              {{ installing === item.id ? t('discover.installing') : t('discover.install') }}
            </button>
            <button
              type="button"
              class="icon-button"
              style="width:32px;height:32px"
              :aria-label="`${t('discover.discardCache')} ${item.name}`"
              :title="t('discover.discardCache')"
              :disabled="!!installing"
              @click="discardCache(item)"
            >
              <Trash2 :size="14"/>
            </button>
          </div>
        </div>
      </section>
    </SlideDrawer>
  </div>
</template>

<style scoped>
.online-stage {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.online-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  flex: none;
}

.online-tools {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.catalog-cards {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  align-content: start;
}

.catalog-card {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 16px;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 460px;
  border: 2.5px solid var(--outline);
  border-radius: 14px;
  box-shadow: var(--shadow-sm);
}

.catalog-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.catalog-card h2 {
  font-size: 15.5px;
}

.author-tag {
  font-size: 11px;
  font-weight: 800;
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--raised);
  border: 1.5px solid var(--outline);
}

.catalog-card p {
  font-size: 12.5px;
  color: var(--ink-soft);
  margin-top: 5px;
  line-height: 1.5;
}

.catalog-card-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.pop-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.pop-label {
  font-size: 12px;
  font-weight: 800;
}

.pop-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 11.5px;
  color: var(--muted);
  flex-wrap: wrap;
}

.drawer-note {
  font-size: 12.5px;
  color: var(--muted);
}

.drawer-item {
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

.drawer-item strong {
  display: block;
  font-size: 13.5px;
}

.drawer-item span {
  font-size: 11.5px;
  color: var(--muted);
}

.cached-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cached-section h3 {
  font-size: 14px;
}

.pkg-select {
  padding: 6px 8px;
  border-radius: 8px;
  border: 1.5px solid var(--outline);
  background: var(--surface);
  font-size: 12px;
}

@media (max-width: 780px) {
  .catalog-cards {
    grid-template-columns: 1fr;
  }
}
</style>
