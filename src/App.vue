<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import {
  ArrowUpRight, Play, FolderOpen, Plus, RefreshCw, Minus, X, Square,
  ArrowRight, Search, LoaderCircle, Download, Menu, FileText,
  RotateCcw, PackageCheck, ShieldAlert, FolderClock, Anchor, HelpCircle, Trash2, Power,
} from 'lucide-vue-next';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { useLauncher } from './store';
import { command, desktop, errorText } from './bridge';
import { t } from './i18n';
import type { Language, ModEntry, ModPackage, Page, RemovedPackage, SaveEntry, Settings } from './types';
import OnlineMods from './components/OnlineMods.vue';
import RoomPanel from './components/RoomPanel.vue';
import LocalMod from './components/LocalMod.vue';
import ProfilePanel from './components/ProfilePanel.vue';
import DownloadQueue from './components/DownloadQueue.vue';
import CompatPanel from './components/CompatPanel.vue';
import SaveBackupPanel from './components/SaveBackupPanel.vue';
import ConfirmDialog from './components/ConfirmDialog.vue';
import SlideDrawer from './components/SlideDrawer.vue';

const store = useLauncher();
const page = computed(() => store.page);
const modTab = computed(() => store.modTab);
const menu = ref(false);
const search = ref('');
const modFilter = ref('all');
const aboutOpen = ref(false);
const downloadsDrawer = ref(false);
const removedDrawer = ref(false);
const inspectedModFile = ref<string | null>(null);
const inspectedSaveFolder = ref<string | null>(null);

const draft = ref<Settings>({
  gamePath: 'D:\\\\Epic Games\\\\Flotsam',
  backupBeforeLaunch: true,
  reducedMotion: false,
  language: 'zh',
});

const nav = computed(() => [
  { id: 'home', label: t('nav.home'), art: '/art/game/icon-helm.png', count: 0 },
  { id: 'online', label: t('nav.online'), art: '/art/game/icon-fleet.png', count: 0 },
  { id: 'mods', label: t('nav.mods'), art: '/art/game/icon-build.png', count: store.enabledMods },
  { id: 'saves', label: t('nav.saves'), art: '/art/game/icon-storage.png', count: store.data?.saves.length || 0 },
] as const);

const titles = computed<Record<Page, string>>(() => ({
  home: t('nav.home'),
  mods: t('nav.mods'),
  saves: t('nav.saves'),
  online: t('nav.online'),
  settings: t('nav.settings'),
  activity: t('nav.activity'),
}));

const filters = computed(() => [
  { id: 'all', label: t('mods.all') },
  { id: 'enabled', label: t('mods.enabled') },
  { id: 'disabled', label: t('mods.disabled') },
]);

const filteredMods = computed(() =>
  store.data?.mods.filter(
    (m) =>
      (modFilter.value === 'all' || (modFilter.value === 'enabled' ? m.enabled : !m.enabled)) &&
      [m.name, m.file, m.company, ...(m.plugins || []).map((p) => p.id)]
        .filter(Boolean)
        .join(' ')
        .toLowerCase()
        .includes(search.value.toLowerCase()),
  ) || [],
);

const inspectedMod = computed<ModEntry | null>(() =>
  store.data?.mods.find((m) => m.file === inspectedModFile.value) || null,
);

const inspectedSave = computed<SaveEntry | null>(() =>
  store.data?.saves.find((s) => s.folder === inspectedSaveFolder.value) || null,
);

const latest = computed(() => store.data?.saves[0]);
const running = computed(() => store.running);
const removedPackages = computed(() => store.data?.removedPackages || []);
const needsLoader = computed(() => Boolean(store.data?.installed) && !store.loaderInstalled);
const saveBackups = ref<InstanceType<typeof SaveBackupPanel> | null>(null);

async function backupSaves() {
  const backup = await store.run<string>('backup_saves', {}, '存档已备份');
  if (backup) await saveBackups.value?.load();
}

watch(
  () => store.data?.settings,
  (v) => {
    if (!v) return;
    draft.value = { ...v };
    document.documentElement.dataset.reducedMotion = String(v.reducedMotion);
  },
  { immediate: true },
);

watch(page, () => {
  menu.value = false;
});

let modalReturnFocus: HTMLElement | null = null;
watch(aboutOpen, async (value) => {
  if (value) {
    modalReturnFocus = document.activeElement as HTMLElement;
    await nextTick();
    document.querySelector<HTMLElement>('.about-modal button')?.focus();
  } else {
    modalReturnFocus?.focus();
  }
});

let timer: ReturnType<typeof setInterval> | undefined;
onMounted(async () => {
  await store.refresh();
  await store.loadTasks();
  timer = setInterval(() => {
    if (document.visibilityState !== 'visible' || store.busy) return;
    store.refresh();
  }, 15000);
});

onUnmounted(() => {
  clearInterval(timer);
});

function bytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1048576).toFixed(1)} MB`;
}

function dateTime(n: number) {
  if (!n) return '—';
  const d = new Date(n * 1000);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function shortDateTime(n: number) {
  if (!n) return '—';
  const d = new Date(n * 1000);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function playDuration(seconds?: number | null) {
  if (seconds == null || seconds < 0) return '';
  const mins = Math.max(1, Math.round(seconds / 60));
  if (mins < 60) return t('saves.playMinutes', { m: mins });
  return t('saves.playHours', { h: Math.floor(mins / 60), m: mins % 60 });
}

function saveTypeLabel(kind?: string | null) {
  if (kind === 'autosave') return t('saves.typeAuto');
  if (kind === 'quicksave') return t('saves.typeQuick');
  if (kind === 'snapshot') return t('saves.typeSnapshot');
  if (kind === 'manual') return t('saves.typeManual');
  return '';
}

function homeSaveSubtitle(save: SaveEntry) {
  const parts: string[] = [];
  if (save.day != null) parts.push(t('saves.day', { day: save.day }));
  if (save.drifters != null) parts.push(t('saves.drifters', { count: save.drifters }));
  parts.push(shortDateTime(save.modified));
  if (!parts.length) parts.push(bytes(save.size));
  return parts.join(' · ');
}

async function windowAction(action: 'minimize' | 'toggleMaximize' | 'close') {
  if (desktop) {
    try { await getCurrentWindow()[action](); }
    catch (e) { store.notify(String(e), 'error'); }
  }
}

async function persistSettings(next: Settings) {
  draft.value = { ...next };
  document.documentElement.dataset.reducedMotion = String(next.reducedMotion);
  await store.save({ ...next });
}

async function browse() {
  const path = await store.run<string | null>('select_game_directory');
  if (path) {
    await persistSettings({ ...draft.value, gamePath: path });
  }
}

async function savePathOnBlur() {
  if (store.data?.settings && draft.value.gamePath !== store.data.settings.gamePath) {
    await persistSettings({ ...draft.value });
  }
}

async function toggleAutoBackup() {
  await persistSettings({ ...draft.value, backupBeforeLaunch: !draft.value.backupBeforeLaunch });
}

async function toggleReducedMotion() {
  await persistSettings({ ...draft.value, reducedMotion: !draft.value.reducedMotion });
}

async function setLanguage(value: Language) {
  if (draft.value.language === value) return;
  store.applyLanguage(value);
  await persistSettings({ ...draft.value, language: value });
}

async function launch() {
  if (!desktop) { store.notify(t('common.browserLaunch'), 'error'); return; }
  if (!store.data?.installed) { await browse(); return; }
  await store.run('launch_game', { mode: store.profile === '原版' ? 'vanilla' : 'modded' }, '启动请求已发送');
}

async function installLoader() {
  if (!desktop) { store.notify(t('common.browserOnly'), 'error'); return; }
  if (running.value) { store.notify(t('loader.requiresStopped'), 'error'); return; }
  await store.run('install_loader', {}, t('loader.installed'));
}

async function importMod() {
  if (!desktop) { store.notify(t('common.browserImport'), 'error'); return; }
  const name = await store.run<string | null>('import_mod');
  if (name) store.notify(`已导入 ${name}`, 'success');
}

async function changePackage(pkg: ModPackage | RemovedPackage, action: string) {
  const name = pkg.name;
  if (action === 'remove') {
    const ok = await store.confirm({
      title: t('package.confirmRemoveTitle'),
      body: t('package.confirmRemoveBody', { name }),
      confirm: t('package.doRemove'),
      danger: true,
    });
    if (!ok) return;
    inspectedModFile.value = null;
    await store.run('change_mod_package', { id: pkg.id, action: 'remove' }, t('package.removed', { name }));
    return;
  }
  if (action === 'rollback') {
    const ok = await store.confirm({
      title: t('package.confirmRollbackTitle'),
      body: t('package.confirmRollbackBody', { name }),
      confirm: t('package.doRollback'),
    });
    if (!ok) return;
    await store.run('change_mod_package', { id: pkg.id, action: 'rollback' }, t('package.rolledBack', { name }));
    return;
  }
  if (action === 'disable') {
    await store.run('change_mod_package', { id: pkg.id, action: 'disable' }, t('package.disabled'));
    return;
  }
  if (action === 'enable') {
    await store.run('change_mod_package', { id: pkg.id, action: 'enable' }, t('package.enabled'));
    return;
  }
  if (action === 'restore') {
    await store.run('change_mod_package', { id: pkg.id, action: 'restore' }, t('package.restored', { name }));
    if (!removedPackages.value.length) removedDrawer.value = false;
    return;
  }
  if (action === 'update') {
    const picked = await store.run<string | null>('update_mod_package', { id: pkg.id });
    if (picked === null) store.notify(t('package.updateCancelled'), 'info');
    else if (picked) store.notify(t('package.updatePicked', { name: picked }), 'success');
  }
}

async function recoverPackages() {
  await store.run('change_mod_package', { id: '', action: 'recover' }, t('package.recovered'));
}

function open(kind: string, subpath?: string) {
  if (!desktop) {
    store.notify(t('common.browserOnly'), 'error');
    return;
  }
  command('open_directory', subpath ? { kind, subpath } : { kind }).catch((e) => {
    store.notify(errorText(e), 'error');
  });
}
</script>

<template>
  <div class="launcher">
    <!-- Permanent Left Driftwood Rail -->
    <aside class="sidebar" :class="{ expanded: menu }">
      <a class="brand" href="#home" :aria-label="`${t('app.name')} ${t('nav.home')}`" @click.prevent="store.go('home')">
        <img class="brand-logo" src="/art/game/logo-bordered.png" alt="Flotsam"/>
      </a>

      <nav :aria-label="t('nav.home')">
        <button
          v-for="item in nav"
          :key="item.id"
          :class="['nav-item', { active: page === item.id }]"
          @click="store.go(item.id as Page)"
        >
          <img class="nav-game-icon" :src="item.art" alt=""/>
          <span>{{ item.label }}</span>
          <span v-if="item.count" class="nav-badge" aria-hidden="true">{{ item.count }}</span>
        </button>
      </nav>

      <div class="sidebar-bottom">
        <!-- Persistent Download Tray Pill -> Opens Drawer -->
        <button v-if="store.activeTasks.length" type="button" class="download-tray-pill" @click="downloadsDrawer = true">
          <LoaderCircle :size="15" class="spin"/>
          <span>{{ t('downloads.active', { count: store.activeTasks.length }) }}</span>
        </button>

        <div class="sidebar-utility-row">
          <button :class="['nav-item', { active: page === 'settings' }]" @click="store.go('settings')">
            <img class="nav-game-icon" src="/art/game/icon-gear.png" alt=""/>
            <span>{{ t('nav.settings') }}</span>
          </button>
          <button type="button" class="about-icon-btn" :aria-label="t('settings.aboutLink')" :title="t('settings.aboutLink')" @click="aboutOpen = true">
            <HelpCircle :size="18"/>
          </button>
        </div>
      </div>
    </aside>

    <!-- Right Workspace: Fixed 42px Bar + Locked Stage -->
    <div class="workspace" @click="menu && (menu = false)">
      <header class="titlebar" data-tauri-drag-region>
        <div class="titlebar-left" data-tauri-drag-region>
          <button class="icon-button mobile-menu" :aria-label="t('nav.open')" @click.stop="menu = !menu">
            <Menu :size="18"/>
          </button>
          <h1 class="window-title" data-tauri-drag-region>{{ titles[page] }}</h1>
          <span v-if="page === 'home'" class="status-chip">
            <span :class="['status-dot', { detected: store.data?.installed }]"></span>
            {{ running ? t('status.running') : store.data?.installed ? t('status.found') : desktop ? t('status.missing') : t('status.desktopOnly') }}
          </span>
        </div>

        <div class="window-actions">
          <button v-if="page === 'home'" class="window-button" :aria-label="t('status.refresh')" :disabled="store.busy" @click="store.refresh()">
            <RefreshCw :size="14" :class="{ spin: store.busy }"/>
          </button>
          <button v-if="desktop" class="window-button" :aria-label="t('window.minimize')" @click="windowAction('minimize')"><Minus :size="14"/></button>
          <button v-if="desktop" class="window-button" :aria-label="t('window.maximize')" @click="windowAction('toggleMaximize')"><Square :size="11"/></button>
          <button v-if="desktop" class="window-button close-window" :aria-label="t('window.close')" @click="windowAction('close')"><X :size="16"/></button>
        </div>
      </header>

      <main class="stage">
        <!-- 1. HOME: Full-Stage Hero + 3-Card Deck (Zero Scroll) -->
        <div v-if="page === 'home'" class="home-stage">
          <section class="hero-card" :aria-label="t('home.launch')">
            <img class="hero-backdrop" src="/art/game/hero-flotsam.webp" alt="浮游世界海上灯塔城镇" fetchpriority="high"/>
            <div class="hero-gradient"></div>

            <div class="hero-dock">
              <div class="mode-pills" role="group" :aria-label="t('online.mode')">
                <button
                  type="button"
                  :aria-pressed="store.profile === '原版'"
                  :class="['mode-pill', { active: store.profile === '原版' }]"
                  @click="store.chooseProfile('原版')"
                >
                  <img src="/art/game/icon-boat.png" alt=""/>
                  <span>{{ t('home.vanilla') }}</span>
                </button>
                <button
                  type="button"
                  :aria-pressed="store.profile !== '原版'"
                  :class="['mode-pill', { active: store.profile !== '原版' }]"
                  @click="store.chooseProfile('本地模组')"
                >
                  <img src="/art/game/icon-build.png" alt=""/>
                  <span>{{ t('home.modded') }}</span>
                  <small>{{ store.enabledMods }}</small>
                </button>
              </div>

              <div class="dock-right">
                <button class="icon-button" :aria-label="t('home.openFolder')" :title="t('home.openFolder')" @click="open('game')">
                  <FolderOpen :size="18"/>
                </button>
                <button class="button launch-button" :disabled="store.busy || running" @click="launch">
                  <LoaderCircle v-if="store.busy" :size="19" class="spin"/>
                  <Play v-else :size="18" fill="currentColor"/>
                  <span>{{ store.busy ? t('home.launching') : running ? t('status.running') : store.data?.installed || !desktop ? t('home.launch') : t('home.choose') }}</span>
                </button>
              </div>
            </div>
          </section>

          <div class="home-deck">
            <section class="deck-card">
              <div class="deck-head">
                <img class="deck-thumb" src="/art/game/banner-radio.png" alt=""/>
                <div class="deck-body">
                  <h2>{{ t('home.fleetTitle') }}</h2>
                  <p>{{ t('home.fleetDesc') }}</p>
                </div>
              </div>
              <div class="deck-actions">
                <button class="button secondary compact" @click="store.goOnline('host', 'independent')">
                  <Anchor :size="13"/>{{ t('home.quickFleet') }}
                </button>
                <button class="text-button" @click="store.goOnline('join', 'independent')">
                  {{ t('home.quickJoin') }}<ArrowRight :size="13"/>
                </button>
              </div>
            </section>

            <section class="deck-card">
              <div class="deck-head">
                <img class="deck-thumb" src="/art/game/banner-tugger.png" alt=""/>
                <div class="deck-body">
                  <h2>{{ latest?.name || t('home.noSave') }}</h2>
                  <p>{{ latest ? homeSaveSubtitle(latest) : t('home.noSaveHint') }}</p>
                </div>
              </div>
              <div class="deck-actions">
                <button v-if="latest" class="button secondary compact" :disabled="store.busy || running" @click="backupSaves">
                  <FolderClock :size="13"/>{{ t('home.quickBackup') }}
                </button>
                <button class="text-button" @click="store.go('saves')">
                  {{ t('home.allSaves') }}<ArrowRight :size="13"/>
                </button>
              </div>
            </section>

            <section class="deck-card">
              <div class="deck-head">
                <img class="deck-thumb" src="/art/game/banner-architect.png" alt=""/>
                <div class="deck-body">
                  <h2>{{ store.enabledMods ? t('home.enabledCount', { count: store.enabledMods }) : t('home.yard') }}</h2>
                  <p>{{ t('mods.emptyHint') }}</p>
                </div>
              </div>
              <div class="deck-actions">
                <button class="button secondary compact" :disabled="store.busy" @click="importMod">
                  <Plus :size="13"/>{{ t('mods.import') }}
                </button>
                <button class="text-button" @click="store.go('mods', 'local')">
                  {{ t('home.manageMods') }}<ArrowRight :size="13"/>
                </button>
              </div>
            </section>
          </div>
        </div>

        <!-- 2. MODS: Bounded Workbench + Detail/Download Drawers -->
        <template v-else-if="page === 'mods'">
          <div class="top-bar">
            <div class="tabs" role="group">
              <button :class="{ selected: modTab === 'local' }" @click="store.go('mods', 'local')">{{ t('mods.local') }}</button>
              <button :class="{ selected: modTab === 'online' }" @click="store.go('mods', 'online')">{{ t('mods.discover') }}</button>
              <button :class="{ selected: modTab === 'profiles' }" @click="store.go('mods', 'profiles')">{{ t('mods.profiles') }}</button>
            </div>

            <template v-if="modTab === 'local'">
              <div class="tabs" role="group">
                <button
                  v-for="filter in filters"
                  :key="filter.id"
                  :class="{ selected: modFilter === filter.id }"
                  @click="modFilter = filter.id"
                >
                  {{ filter.label }}
                  <span>{{ filter.id === 'all' ? store.data?.mods.length || 0 : filter.id === 'enabled' ? store.enabledMods : (store.data?.mods.length || 0) - store.enabledMods }}</span>
                </button>
              </div>

              <label class="search">
                <Search :size="15"/>
                <input v-model="search" :aria-label="t('mods.search')" :placeholder="t('mods.search')"/>
              </label>

              <div style="display:flex;gap:7px;align-items:center">
                <button v-if="removedPackages.length" type="button" class="button ghost compact" @click="removedDrawer = true">
                  <RotateCcw :size="14"/>{{ t('mods.removed') }} ({{ removedPackages.length }})
                </button>
                <button v-if="needsLoader" type="button" class="button secondary compact" :disabled="store.busy || running" @click="installLoader">
                  <LoaderCircle v-if="store.busyAction === 'install_loader'" :size="14" class="spin"/>
                  <Download v-else :size="14"/>
                  {{ t('loader.install') }}
                </button>
                <button type="button" class="button primary compact" :disabled="store.busy" @click="importMod">
                  <Plus :size="15"/>{{ t('mods.import') }}
                </button>
                <button type="button" class="icon-button" :aria-label="t('mods.openFolder')" @click="open('mods')">
                  <FolderOpen :size="17"/>
                </button>
              </div>
            </template>
          </div>

          <OnlineMods v-if="modTab === 'online'"/>
          <ProfilePanel v-else-if="modTab === 'profiles'"/>
          <template v-else>
            <div v-if="store.data?.packageRecovery" class="inline-error" role="alert">
              <span>{{ t('mods.recovery') }}</span>
              <button class="text-button" :disabled="store.busy || running" @click="recoverPackages">
                <RotateCcw :size="14"/>{{ t('mods.recover') }}
              </button>
            </div>

            <div v-if="needsLoader && !filteredMods.length" class="banner-strip">
              <span><ShieldAlert :size="16" style="vertical-align:-3px;margin-right:6px"/><h2 style="font-size:13px;margin:0;display:inline">{{ t('loader.needed') }}</h2> — {{ t('home.autoLoaderHint') }}</span>
              <button class="button secondary compact" :disabled="store.busy || running" @click="installLoader">
                {{ t('loader.install') }}
              </button>
            </div>

            <div v-if="filteredMods.length" class="scroll-surface">
              <LocalMod
                v-for="mod in filteredMods"
                :key="mod.file"
                :mod="mod"
                :running="running"
                @package="changePackage"
                @select="inspectedModFile = $event.file"
              />
            </div>
            <div v-else class="empty-stage">
              <img src="/art/game/banner-architect.png" alt="" width="72" height="58" style="object-fit:contain"/>
              <h2>{{ search ? t('mods.none') : t('mods.empty') }}</h2>
              <p v-if="!search">{{ t('mods.emptyHint') }}</p>
              <button class="button secondary" @click="search ? (search = '') : importMod()">
                {{ search ? t('mods.clear') : t('mods.import') }}
                <Plus v-if="!search" :size="15"/>
              </button>
            </div>
          </template>
        </template>

        <!-- 3. SAVES: 2-Column Split (Left Current Saves | Right Backups, Zero Outer Scroll) -->
        <template v-else-if="page === 'saves'">
          <div class="top-bar">
            <span style="font-weight:800;font-size:14px;color:#fff;text-shadow:0 1px 2px rgba(0,0,0,.4)">
              {{ t('saves.count', { count: store.data?.saves.length || 0 }) }}
            </span>
            <div style="display:flex;gap:8px;align-items:center">
              <button class="button primary compact" :disabled="store.busy || !store.data?.saves.length || running" @click="backupSaves">
                <FolderClock :size="15"/>{{ t('saves.backup') }}
              </button>
              <button class="button ghost compact" @click="open('saves')">
                <FolderOpen :size="15"/>{{ t('saves.saveDir') }}
              </button>
              <button class="button ghost compact" @click="open('backups')">
                {{ t('saves.backupDir') }}<ArrowUpRight :size="14"/>
              </button>
            </div>
          </div>

          <div class="saves-split">
            <div v-if="store.data?.saves.length" class="scroll-grid">
              <div class="saves-grid">
                <article
                  v-for="(save, index) in store.data.saves"
                  :key="save.folder"
                  class="save-card"
                  @click="inspectedSaveFolder = save.folder"
                >
                  <div class="save-card-top">
                    <img class="save-ship-badge" :src="index % 2 ? '/art/game/ship-support.webp' : '/art/game/ship-classic.webp'" alt=""/>
                    <div class="save-title-wrap">
                      <div class="save-title-row">
                        <h2>{{ save.name }}</h2>
                        <span v-if="saveTypeLabel(save.saveType)" :class="['save-type-pill', save.saveType]">{{ saveTypeLabel(save.saveType) }}</span>
                      </div>
                      <div class="save-pills">
                        <span v-if="save.day != null" class="save-stat-pill">{{ t('saves.day', { day: save.day }) }}</span>
                        <span v-if="save.drifters != null" class="save-stat-pill">{{ t('saves.drifters', { count: save.drifters }) }}</span>
                        <span v-if="playDuration(save.playTimeSeconds)" class="save-stat-pill">{{ playDuration(save.playTimeSeconds) }}</span>
                        <span class="save-stat-pill muted-pill">
                          {{ save.slots?.length ? t('saves.slotsCount', { count: save.slots.length }) : t('saves.filesCount', { count: save.files }) }} · {{ bytes(save.size) }}
                        </span>
                      </div>
                    </div>
                    <button
                      type="button"
                      class="icon-button"
                      :aria-label="t('saves.openFolder', { name: save.name })"
                      :title="t('saves.openFolder', { name: save.name })"
                      @click.stop="open('saves', save.folder)"
                    >
                      <FolderOpen :size="16"/>
                    </button>
                  </div>

                  <div class="save-card-foot">
                    <span>{{ t('saves.lastSaved', { time: dateTime(save.modified) }) }}</span>
                    <span>{{ t('saves.folder', { folder: save.folder }) }}<template v-if="save.latestSlot && save.latestSlot !== save.name"> · {{ save.latestSlot }}</template></span>
                  </div>
                </article>
              </div>
            </div>
            <div v-else class="empty-stage">
              <img src="/art/game/header-saves.png" alt="" width="68" height="54" style="object-fit:contain"/>
              <h2>{{ t('saves.empty') }}</h2>
              <p>{{ desktop ? t('saves.emptyHint') : t('saves.emptyPreview') }}</p>
              <button class="button secondary" @click="launch">{{ t('saves.start') }}<ArrowRight :size="15"/></button>
            </div>

            <SaveBackupPanel ref="saveBackups"/>
          </div>
        </template>

        <!-- 4. ONLINE: Fleet Multiplayer Lobby & Connected Room -->
        <template v-else-if="page === 'online'">
          <RoomPanel/>
        </template>

        <!-- 5. SETTINGS: Instant Auto-Save Card (Zero Scroll, Zero Manual Save Button!) -->
        <template v-else-if="page === 'settings'">
          <section class="settings-card">
            <div class="setting-row">
              <div class="setting-info">
                <h2><label for="game-path">{{ t('settings.gamePath') }}</label></h2>
              </div>
              <div class="path-control">
                <input id="game-path" v-model="draft.gamePath" spellcheck="false" @blur="savePathOnBlur" @keydown.enter.prevent="savePathOnBlur"/>
                <button type="button" class="icon-button" :aria-label="t('settings.chooseDir')" @click="browse"><FolderOpen :size="17"/></button>
                <button type="button" class="icon-button" :aria-label="t('settings.openExplorer')" :title="t('settings.openExplorer')" @click="open('game')"><ArrowUpRight :size="17"/></button>
              </div>
            </div>

            <div class="setting-row">
              <div class="setting-info">
                <h2><label for="auto-backup">{{ t('settings.autoBackup') }}</label></h2>
              </div>
              <button
                id="auto-backup"
                type="button"
                role="switch"
                :aria-checked="draft.backupBeforeLaunch"
                :class="['switch', { on: draft.backupBeforeLaunch }]"
                @click="toggleAutoBackup"
              ><span></span></button>
            </div>

            <div class="setting-row">
              <div class="setting-info">
                <h2><label for="reduced-motion">{{ t('settings.reducedMotion') }}</label></h2>
              </div>
              <button
                id="reduced-motion"
                type="button"
                role="switch"
                :aria-checked="draft.reducedMotion"
                :class="['switch', { on: draft.reducedMotion }]"
                @click="toggleReducedMotion"
              ><span></span></button>
            </div>

            <div class="setting-row">
              <div class="setting-info">
                <h2>{{ t('settings.language') }}</h2>
              </div>
              <div class="language-switch" role="group" :aria-label="t('settings.language')">
                <button type="button" :aria-pressed="draft.language === 'zh'" :class="{ selected: draft.language === 'zh' }" @click="setLanguage('zh')">中文</button>
                <button type="button" :aria-pressed="draft.language === 'en'" :class="{ selected: draft.language === 'en' }" @click="setLanguage('en')">English</button>
              </div>
            </div>

            <div class="setting-row">
              <div class="setting-info">
                <h2>{{ t('settings.aboutLink') }}</h2>
              </div>
              <div style="display:flex;gap:8px;flex-wrap:wrap">
                <button type="button" class="button ghost compact" @click="open('logs')">
                  <FileText :size="14"/>{{ t('settings.logs') }}
                </button>
                <button type="button" class="button secondary compact" @click="aboutOpen = true">
                  <HelpCircle :size="14"/>{{ t('settings.aboutLink') }}
                </button>
              </div>
            </div>
          </section>
        </template>
      </main>
    </div>

    <!-- DRAWER 1: Mod Inspector Drawer (Dependencies, Config, Package Actions) -->
    <SlideDrawer :open="Boolean(inspectedMod)" :title="inspectedMod?.name || ''" @close="inspectedModFile = null">
      <template v-if="inspectedMod">
        <ul v-if="inspectedMod.issues?.length" class="drawer-issues">
          <li v-for="issue in inspectedMod.issues" :key="`${issue.code}:${issue.message}`">{{ issue.message }}</li>
        </ul>

        <dl class="drawer-dl">
          <dt>{{ t('mods.file') }}</dt><dd>{{ inspectedMod.file }}</dd>
          <dt>{{ t('mods.company') }}</dt><dd>{{ inspectedMod.company || t('mods.unknown') }}</dd>
          <template v-if="inspectedMod.package">
            <dt>{{ t('mods.ownedByPackage') }}</dt><dd>{{ inspectedMod.package.name }} ({{ inspectedMod.package.files }})</dd>
          </template>
        </dl>

        <div v-for="plugin in inspectedMod.plugins || []" :key="plugin.id" class="drawer-plugin-card">
          <div style="display:flex;justify-content:space-between;align-items:center;gap:8px">
            <strong>{{ plugin.id }}</strong>
            <button
              type="button"
              class="button ghost compact"
              :disabled="store.busy || running"
              @click="store.run('open_mod_file', { file: inspectedMod.file, kind: 'config', pluginId: plugin.id })"
            >
              <FileText :size="13"/>{{ t('mods.config') }}
            </button>
          </div>
          <dl v-if="plugin.dependencies.length" class="drawer-dl" style="margin-top:8px">
            <template v-for="dep in plugin.dependencies" :key="dep.pluginId">
              <dt>{{ dep.optional ? t('mods.depOptional') : t('mods.dep') }}</dt>
              <dd>{{ dep.pluginId }}<span v-if="dep.minimumVersion"> ≥ {{ dep.minimumVersion }}</span></dd>
            </template>
          </dl>
        </div>

        <div class="drawer-actions">
          <button type="button" class="button ghost compact" :disabled="store.busy" @click="store.run('open_mod_file', { file: inspectedMod.file, kind: 'location' })">
            <FolderOpen :size="14"/>{{ t('mods.location') }}
          </button>
          <template v-if="inspectedMod.package">
            <button type="button" class="button secondary compact" :disabled="store.busy || running" @click="changePackage(inspectedMod.package, 'update')">
              <PackageCheck :size="14"/>{{ t('mods.update') }}
            </button>
            <button
              type="button"
              class="button ghost compact"
              :disabled="store.busy || running"
              @click="changePackage(inspectedMod.package, (inspectedMod.package.enabled ?? inspectedMod.enabled) ? 'disable' : 'enable')"
            >
              <Power :size="14"/>{{ (inspectedMod.package.enabled ?? inspectedMod.enabled) ? t('package.disable') : t('package.enable') }}
            </button>
            <button
              v-if="inspectedMod.package.canRollback"
              type="button"
              class="button ghost compact"
              :disabled="store.busy || running"
              @click="changePackage(inspectedMod.package, 'rollback')"
            >
              <RotateCcw :size="14"/>{{ t('mods.rollback') }}
            </button>
            <button type="button" class="button danger compact" :disabled="store.busy || running" @click="changePackage(inspectedMod.package, 'remove')">
              <Trash2 :size="14"/>{{ t('mods.remove') }}
            </button>
          </template>
        </div>
      </template>
    </SlideDrawer>

    <!-- DRAWER 2: Uninstalled Packages Recovery Drawer -->
    <SlideDrawer :open="removedDrawer" :title="t('mods.removed')" @close="removedDrawer = false">
      <div v-for="item in removedPackages" :key="item.id" class="drawer-plugin-card" style="display:flex;justify-content:space-between;align-items:center">
        <div>
          <strong>{{ item.name }}</strong>
          <div style="font-size:12px;color:var(--muted)">{{ t('mods.ownedByPackage') }} · {{ item.files }}</div>
        </div>
        <button type="button" class="button secondary compact" :disabled="store.busy || running" @click="changePackage(item, 'restore')">
          <RotateCcw :size="14"/>{{ t('mods.restore') }}
        </button>
      </div>
    </SlideDrawer>

    <!-- DRAWER 3: Active Downloads Drawer -->
    <SlideDrawer :open="downloadsDrawer" :title="t('downloads.title')" @close="downloadsDrawer = false">
      <DownloadQueue/>
    </SlideDrawer>

    <!-- DRAWER 4: Save Inspector Drawer (Full Slot Timeline & Metadata) -->
    <SlideDrawer :open="Boolean(inspectedSave)" :title="inspectedSave?.name || ''" @close="inspectedSaveFolder = null">
      <template v-if="inspectedSave">
        <dl class="drawer-dl">
          <dt>{{ t('saves.folder', { folder: '' }).replace(/[：:]\s*$/, '') }}</dt>
          <dd>{{ inspectedSave.folder }}</dd>
          <dt>{{ t('saves.lastSaved', { time: '' }).replace(/[：:]\s*$/, '') }}</dt>
          <dd>{{ dateTime(inspectedSave.modified) }}</dd>
          <template v-if="inspectedSave.day != null">
            <dt>生存进度</dt>
            <dd>
              {{ t('saves.day', { day: inspectedSave.day }) }}
              <template v-if="inspectedSave.drifters != null"> · {{ t('saves.drifters', { count: inspectedSave.drifters }) }}</template>
              <template v-if="playDuration(inspectedSave.playTimeSeconds)"> · {{ playDuration(inspectedSave.playTimeSeconds) }}</template>
            </dd>
          </template>
          <template v-if="inspectedSave.gameVersion">
            <dt>存档版本</dt>
            <dd>Flotsam {{ inspectedSave.gameVersion }}</dd>
          </template>
          <dt>体积明细</dt>
          <dd>{{ t('saves.filesCount', { count: inspectedSave.files }) }} · {{ bytes(inspectedSave.size) }}</dd>
        </dl>

        <div class="drawer-actions" style="padding-top:4px;border-top:0">
          <button type="button" class="button secondary compact" @click="open('saves', inspectedSave.folder)">
            <FolderOpen :size="14"/>{{ t('saves.openFolder', { name: inspectedSave.name }) }}
          </button>
        </div>

        <template v-if="inspectedSave.slots?.length">
          <strong style="font-size:13px">{{ t('saves.slotList') }} ({{ inspectedSave.slots.length }})</strong>
          <div v-for="slot in inspectedSave.slots" :key="slot.fileName" class="drawer-plugin-card" style="display:flex;flex-direction:column;gap:5px">
            <div style="display:flex;align-items:center;justify-content:space-between;gap:8px">
              <strong style="font-size:13.5px">{{ slot.slotName }}</strong>
              <span v-if="saveTypeLabel(slot.saveType)" :class="['save-type-pill', slot.saveType]">{{ saveTypeLabel(slot.saveType) }}</span>
            </div>
            <div style="font-size:12px;color:var(--ink-soft)">
              <template v-if="slot.day != null">{{ t('saves.day', { day: slot.day }) }} · </template>
              <template v-if="slot.drifters != null">{{ t('saves.drifters', { count: slot.drifters }) }} · </template>
              <template v-if="playDuration(slot.playTimeSeconds)">{{ playDuration(slot.playTimeSeconds) }} · </template>
              {{ bytes(slot.size) }}
            </div>
            <div style="font-size:11.5px;color:var(--muted)">
              {{ dateTime(slot.modified) }} · {{ slot.fileName }}
            </div>
          </div>
        </template>
      </template>
    </SlideDrawer>

    <!-- TRANSIENT TOAST: Non-Blocking Bottom-Right Confirmation -->
    <div v-if="store.toast" :class="['toast', store.toastKind]" role="status">
      <span>{{ store.toast }}</span>
      <button class="icon-button" style="width:26px;height:26px" :aria-label="t('activity.close')" @click="store.toast = ''"><X :size="13"/></button>
    </div>

    <!-- MODAL 1: High-Stakes Destructive Confirmations ONLY -->
    <ConfirmDialog/>

    <!-- MODAL 2: About, Versions & Component Diagnostics (All versions tucked neatly here!) -->
    <div v-if="aboutOpen" class="modal-backdrop" @click.self="aboutOpen = false" @keydown.esc="aboutOpen = false">
      <section class="about-modal" role="dialog" aria-modal="true" aria-labelledby="about-title" tabindex="-1">
        <button class="icon-button modal-close" :aria-label="t('common.close')" autofocus @click="aboutOpen = false"><X :size="17"/></button>
        <div style="display:flex;align-items:center;gap:14px">
          <img src="/art/game/logo-bordered.png" alt="Flotsam" style="width:148px;height:auto"/>
          <div>
            <h2 id="about-title">{{ t('app.version') }}</h2>
            <p style="font-size:12.5px;color:var(--muted)">
              {{ store.data?.gameVersion ? `Flotsam ${store.data.gameVersion} · ` : '' }}{{ desktop ? t('app.desktop') : t('app.preview') }}
            </p>
          </div>
        </div>
        <p style="font-size:13px;line-height:1.6">{{ t('credits.body') }} {{ t('credits.features') }}</p>
        <CompatPanel/>
        <p style="font-size:11.5px;color:var(--muted);line-height:1.55">{{ t('credits.art') }}</p>
      </section>
    </div>
  </div>
</template>

<style scoped>
.saves-split {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: minmax(0, 1.3fr) minmax(320px, 0.9fr);
  gap: 14px;
}

.drawer-issues {
  margin: 0;
  padding: 10px 14px 10px 28px;
  border-radius: 10px;
  background: #fde6df;
  border: 2px solid var(--outline);
  color: #7c2419;
  font-size: 12.5px;
  font-weight: 700;
}

.drawer-dl {
  display: grid;
  grid-template-columns: 84px minmax(0, 1fr);
  gap: 6px 12px;
  margin: 0;
  font-size: 12.5px;
}

.drawer-dl dt {
  color: var(--muted);
  font-weight: 700;
}

.drawer-dl dd {
  margin: 0;
  overflow-wrap: anywhere;
}

.drawer-plugin-card {
  padding: 12px 14px;
  border-radius: 11px;
  background: var(--paper-bright);
  border: 2px solid var(--outline);
}

.drawer-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding-top: 8px;
  border-top: 1.5px dashed var(--line);
}

@media (max-width: 860px) {
  .saves-split {
    grid-template-columns: 1fr;
  }
}
</style>
