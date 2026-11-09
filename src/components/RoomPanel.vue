<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import {
  ArrowRight, Copy, LogOut, Send, Users, Anchor, LoaderCircle, Download, BookOpen, Wifi,
  Play, ShieldAlert, ClipboardPaste, Dices, UserMinus, Megaphone, SlidersHorizontal, HelpCircle,
} from 'lucide-vue-next';
import { command, desktop, errorText } from '../bridge';
import { useLauncher } from '../store';
import { t } from '../i18n';
import type { Difference, NetInterface, RoomRules } from '../types';
import PopoverBox from './PopoverBox.vue';
import SlideDrawer from './SlideDrawer.vue';

interface Member { id: number; name: string; host?: boolean; address?: string; latency?: number }
interface Room {
  connected: boolean;
  host: boolean;
  mode: string;
  address: string;
  invite: string;
  members: Member[];
  messages: { id: number; name: string; text: string; time: number }[];
  error: string;
  notice?: string;
  differences: Difference[];
  latency: number;
  capacity?: number;
  rules?: RoomRules;
}
interface Multiplayer {
  installed: boolean;
  version: string;
  runtime: null | { status: string; ready: boolean; replicaReady: boolean; connected: boolean };
}

const CAPTAIN_NAMES_ZH = ['漂流船长', '海鸥领航员', '灯塔守望者', '拾荒机械师', '远洋舵手', '潮汐船匠'];
const CAPTAIN_NAMES_EN = ['Drift Captain', 'Gull Navigator', 'Beacon Keeper', 'Scrap Wright', 'Tide Helmsman', 'Salvage Skipper'];

const store = useLauncher();
const room = ref<Room | null>(null);
const role = ref<'host' | 'join'>(store.onlineRole);
const name = ref(localStorage.getItem('flotsam-nickname') || '');
const mode = ref<'independent' | 'shared'>(store.onlineMode);
const invite = ref('');
const advertise = ref('');
const port = ref(27025);
const text = ref('');
const noticeDraft = ref('');
const autoStart = ref(false);
const busy = ref(false);
const error = ref('');
const rules = ref<RoomRules>({ stealing: true, sabotage: true, trading: true, friendlyFire: false });
const multiplayer = ref<Multiplayer | null>(null);
const gameBusy = ref(false);
const failures = ref<Difference[]>([]);
const adapters = ref<NetInterface[]>([]);

// Contextual Overlays (Popovers & Drawers — zero page-stretching accordions)
const netPop = ref(false);
const guidePop = ref(false);
const diffDrawer = ref(false);
const tutorialDrawer = ref(false);

const installed = computed(() => multiplayer.value?.installed ?? false);
const gameRunning = computed(() => store.running);
const activeRules = computed(() => room.value?.rules || rules.value);
const activeDiffs = computed(() => (room.value?.connected ? room.value.differences : failures.value));
const syncPort = computed(() => Math.min(65535, Math.max(1024, Number(port.value) || 27025) + 2));

const selectedAdapterLabel = computed(() => {
  const custom = advertise.value.trim();
  if (!custom) {
    const first = adapters.value[0];
    return first ? `${first.name} (${first.ip})` : t('online.autoAdapter');
  }
  const hit = adapters.value.find((item) => item.ip === custom);
  return hit ? `${hit.name} (${hit.ip})` : custom;
});

watch(() => store.onlineRole, (v) => { role.value = v; });
watch(() => store.onlineMode, (v) => { mode.value = v; });

let timer: ReturnType<typeof setTimeout> | undefined;
let disposed = false;

function defaultNickname() {
  return store.locale === 'en' ? 'Drift Captain' : '漂流船长';
}

function randomNickname() {
  const source = store.locale === 'en' ? CAPTAIN_NAMES_EN : CAPTAIN_NAMES_ZH;
  const pool = source.filter((item) => item !== name.value);
  name.value = pool[Math.floor(Math.random() * pool.length)] || defaultNickname();
}

async function loadAdapters() {
  try {
    adapters.value = await command<NetInterface[]>('network_interfaces');
  } catch { /* ignore in degraded environments */ }
}

async function toggleNetPop() {
  netPop.value = !netPop.value;
  if (netPop.value) void loadAdapters();
}

function pickAdapter(ip: string) {
  advertise.value = ip;
}

async function pasteInvite() {
  try {
    const clip = (await navigator.clipboard.readText()).trim();
    if (clip) {
      invite.value = clip;
      store.notify(t('online.pasted'), 'success');
    } else {
      store.notify(t('online.pasteEmpty'), 'info');
    }
  } catch {
    store.notify(t('online.copyFailed'), 'error');
  }
}

async function refresh() {
  try {
    const [next, component] = await Promise.all([
      command<Room>('room_status'),
      command<Multiplayer>('multiplayer_status'),
    ]);
    multiplayer.value = component;
    if (next.connected || next.error || !room.value?.connected) {
      room.value = next;
      if (next.notice && !noticeDraft.value) noticeDraft.value = next.notice;
    }
    if (!next.connected) failures.value = await command<Difference[]>('last_join_differences');
  } catch (e) {
    error.value = errorText(e);
  } finally {
    if (!disposed) {
      if (timer) clearTimeout(timer);
      timer = setTimeout(refresh, 900);
    }
  }
}

async function installLoader() {
  if (gameBusy.value || gameRunning.value) return;
  gameBusy.value = true;
  try {
    await command('install_loader');
    await store.refresh();
    store.notify(t('loader.installed'), 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    gameBusy.value = false;
  }
}

async function install() {
  if (gameBusy.value || gameRunning.value) return;
  gameBusy.value = true;
  try {
    await command('install_multiplayer');
    multiplayer.value = await command<Multiplayer>('multiplayer_status');
    await store.refresh();
    store.notify(t('online.installed'), 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    gameBusy.value = false;
  }
}

async function launch() {
  if (gameBusy.value) return;
  gameBusy.value = true;
  error.value = '';
  try {
    await command('launch_multiplayer', autoStart.value ? { autoStart: true } : {});
    multiplayer.value = await command<Multiplayer>('multiplayer_status').catch(() => multiplayer.value);
    await store.refresh();
    store.notify(room.value?.host ? t('online.hostHint') : t('online.guestHint'), 'info');
  } catch (e) {
    error.value = errorText(e);
  } finally {
    gameBusy.value = false;
  }
}

async function connect() {
  if (busy.value) return;
  const chosenName = name.value.trim() || defaultNickname();
  name.value = chosenName;
  busy.value = true;
  error.value = '';
  failures.value = [];
  await command('clear_join_differences').catch(() => undefined);
  try {
    room.value = await command<Room>(
      role.value === 'host' ? 'host_room' : 'join_room',
      role.value === 'host'
        ? { name: chosenName, mode: mode.value, port: port.value, advertise: advertise.value, rules: rules.value }
        : { name: chosenName, invite: invite.value.trim() },
    );
    localStorage.setItem('flotsam-nickname', chosenName);
    multiplayer.value = await command<Multiplayer>('multiplayer_status').catch(() => multiplayer.value);
    await store.refresh();
    store.notify(role.value === 'host' ? t('online.roomCreated') : t('online.roomJoined'), 'success');
  } catch (e) {
    error.value = errorText(e);
    failures.value = await command<Difference[]>('last_join_differences').catch(() => []);
    if (failures.value.length) diffDrawer.value = true;
  } finally {
    busy.value = false;
  }
}

async function leave() {
  try {
    await command('leave_room');
    await command('clear_join_differences').catch(() => undefined);
    room.value = null;
    error.value = '';
    failures.value = [];
  } catch (e) {
    error.value = errorText(e);
  }
}

async function send() {
  if (!text.value.trim()) return;
  try {
    await command('room_chat', { text: text.value });
    text.value = '';
  } catch (e) {
    store.notify(errorText(e), 'error');
  }
}

async function saveNotice() {
  if (!room.value?.host) return;
  try {
    await command('room_notice', { text: noticeDraft.value });
    store.notify(t('online.noticeSaved'), 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  }
}

async function kick(memberId: number) {
  try { await command('room_kick', { id: memberId }); }
  catch (e) { store.notify(errorText(e), 'error'); }
}

async function copy() {
  try {
    await navigator.clipboard.writeText(room.value?.invite || '');
    store.notify(t('online.copied'), 'success');
  } catch {
    store.notify(t('online.copyFailed'), 'error');
  }
}

function fixAction(difference: Difference) {
  diffDrawer.value = false;
  void command('clear_join_differences').catch(() => undefined);
  failures.value = [];
  if (difference.action === 'install_loader' || (difference.kind === 'loader' && !difference.action)) {
    void installLoader();
    return;
  }
  store.go('mods', 'local');
}

function fixLabel(difference: Difference) {
  if (difference.action === 'install_loader' || (difference.kind === 'loader' && !difference.action)) {
    return t('online.fixInstallLoader');
  }
  return t('online.fixOpenMods');
}

onMounted(() => {
  void loadAdapters();
  void refresh();
});
onUnmounted(() => { disposed = true; clearTimeout(timer); });
</script>

<template>
  <div class="room-stage">
    <div v-if="error || room?.error" class="inline-error" role="alert">
      <span>{{ error || room?.error }}</span>
      <div style="display:flex;gap:8px;align-items:center">
        <button v-if="activeDiffs.length" type="button" class="text-button" @click="diffDrawer = true">
          {{ t('online.differences') }} ({{ activeDiffs.length }})
        </button>
        <button type="button" class="text-button" @click="tutorialDrawer = true">
          <BookOpen :size="13"/>{{ t('online.tutorialBtn') }}
        </button>
      </div>
    </div>

    <!-- CONNECTED ROOM: Fixed 2-Row Split (Zero Outer Scroll) -->
    <template v-if="room?.connected">
      <section class="room-command-bar">
        <div class="room-meta-left">
          <img :src="room.mode === 'independent' ? '/art/game/ship-support.webp' : '/art/game/ship-classic.webp'" alt="" class="room-ship-icon"/>
          <div>
            <div class="room-title-line">
              <h2>{{ room.host ? t('online.myRoom') : t('online.joined') }}</h2>
              <span class="mode-badge">{{ room.mode === 'shared' ? t('online.modeShared') : t('online.modeIndependent') }}</span>
              <span v-if="room.latency" class="ping-badge">{{ t('online.latency', { ms: room.latency }) }}</span>
            </div>
            <p class="room-sub-line">{{ multiplayer?.runtime?.status || room.address }}</p>
          </div>
        </div>

        <div class="room-command-actions">
          <button type="button" class="button ghost compact" :aria-label="t('online.copy')" @click="copy">
            <Copy :size="14"/>{{ t('online.copy') }}
          </button>

          <button type="button" class="button ghost compact" @click="tutorialDrawer = true">
            <BookOpen :size="14"/>{{ t('online.tutorialBtn') }}
          </button>

          <button v-if="room.differences.length" type="button" class="button secondary compact" @click="diffDrawer = true">
            <ShieldAlert :size="15"/>{{ t('online.differences') }} ({{ room.differences.length }})
          </button>

          <!-- Anchored Popover for Rules & In-Game Controls (Both Independent & Shared!) -->
          <PopoverBox :open="guidePop" width="330px" @close="guidePop = false">
            <template #trigger>
              <button
                type="button"
                class="icon-button"
                :aria-label="room.mode === 'independent' ? t('online.fleetGuideTitle') : t('online.sharedGuideTitle')"
                @click="guidePop = !guidePop"
              >
                <HelpCircle :size="18"/>
              </button>
            </template>
            <strong class="pop-title">{{ room.mode === 'independent' ? t('online.fleetGuideTitle') : t('online.sharedGuideTitle') }}</strong>
            <p class="pop-copy">{{ room.mode === 'independent' ? t('online.fleetGuideBody') : t('online.sharedGuideBody') }}</p>
            <div v-if="room.mode === 'independent'" class="pop-rules">
              <span :class="['rule-chip', { on: activeRules.trading }]">{{ t('online.ruleTrading') }}</span>
              <span :class="['rule-chip', { on: activeRules.stealing }]">{{ t('online.ruleStealing') }}</span>
              <span :class="['rule-chip', { on: activeRules.sabotage }]">{{ t('online.ruleSabotage') }}</span>
              <span :class="['rule-chip', { on: activeRules.friendlyFire }]">{{ t('online.ruleFriendlyFire') }}</span>
            </div>
          </PopoverBox>

          <label v-if="room.host" class="autostart-pill">
            <input v-model="autoStart" type="checkbox"/>
            <span>{{ t('online.autoStart') }}</span>
          </label>

          <button class="button primary" :disabled="gameBusy || gameRunning" @click="launch">
            <LoaderCircle v-if="gameBusy" :size="17" class="spin"/>
            <Play v-else :size="17" fill="currentColor"/>
            {{ room.mode === 'independent' ? (room.host ? t('online.launchIndependent') : t('online.enterIndependent')) : (room.host ? t('online.launch') : t('online.enter')) }}
          </button>

          <button class="icon-button" :aria-label="room.host ? t('online.close') : t('online.leave')" :title="room.host ? t('online.close') : t('online.leave')" @click="leave">
            <LogOut :size="17"/>
          </button>
        </div>
      </section>

      <div class="room-split">
        <section class="room-pane crew-pane">
          <header class="pane-head">
            <h2>{{ t('online.members') }}</h2>
            <span class="crew-count">{{ room.members.length }} / {{ room.capacity || 8 }}</span>
          </header>

          <div class="pane-scroll">
            <div v-for="member in room.members" :key="member.id" class="member-item">
              <img src="/art/game/icon-boat.png" alt="" class="member-boat"/>
              <span class="member-name">{{ member.name }}</span>
              <span v-if="member.id === 0 || member.host" class="role-tag">{{ t('online.roleHost') }}</span>
              <span v-else-if="member.latency" class="ping-tag">{{ member.latency }} ms</span>
              <button v-if="room.host && member.id !== 0" type="button" class="text-button danger-link" @click="kick(member.id)">
                <UserMinus :size="13"/>{{ t('online.kick') }}
              </button>
            </div>
          </div>

          <form v-if="room.host" class="notice-form" @submit.prevent="saveNotice">
            <Megaphone :size="15"/>
            <input v-model="noticeDraft" maxlength="200" :placeholder="t('online.noticePlaceholder')"/>
            <button type="submit" class="text-button notice-submit">{{ t('online.publishNotice') }}</button>
          </form>
          <p v-else-if="room.notice" class="notice-banner"><Megaphone :size="14"/> {{ room.notice }}</p>
        </section>

        <section class="room-pane chat-pane">
          <header class="pane-head">
            <h2>{{ t('online.chat') }}</h2>
          </header>
          <div class="pane-scroll chat-scroll" aria-live="polite">
            <p v-if="!room.messages.length" class="empty-chat">{{ t('online.chatEmpty') }}</p>
            <div v-for="(message, index) in room.messages" :key="`${message.time}-${message.id}-${index}`" class="chat-line">
              <strong>{{ message.name }}</strong>
              <span>{{ message.text }}</span>
            </div>
          </div>
          <form class="inline-field" @submit.prevent="send">
            <input v-model="text" :aria-label="t('online.chat')" :placeholder="t('online.chatPlaceholder')" maxlength="160"/>
            <button class="button secondary compact" :aria-label="t('online.send')" :disabled="!text.trim()"><Send :size="15"/></button>
          </form>
        </section>
      </div>
    </template>

    <!-- LOBBY FORM: Single Centered Parchment Card, Zero Scroll -->
    <form v-else class="lobby-card" @submit.prevent="connect">
      <div class="lobby-top">
        <div class="tabs" role="group">
          <button type="button" :class="{ selected: role === 'host' }" @click="role = 'host'">{{ t('online.host') }}</button>
          <button type="button" :class="{ selected: role === 'join' }" @click="role = 'join'">{{ t('online.join') }}</button>
        </div>

        <div class="lobby-tools">
          <button v-if="desktop && !installed" type="button" class="button ghost compact" :disabled="gameBusy || gameRunning" @click="install">
            <LoaderCircle v-if="gameBusy" :size="14" class="spin"/>
            <Download v-else :size="14"/>
            {{ t('online.install') }}
          </button>

          <button type="button" class="button secondary compact" @click="tutorialDrawer = true">
            <BookOpen :size="14"/>{{ t('online.tutorialBtn') }}
          </button>

          <!-- Network & 1-Click Adapter Picker Popover -->
          <PopoverBox v-if="role === 'host'" :open="netPop" width="356px" @close="netPop = false">
            <template #trigger>
              <button type="button" class="button ghost compact" @click="toggleNetPop">
                <SlidersHorizontal :size="14"/>{{ t('online.network') }}
              </button>
            </template>
            <strong class="pop-title">{{ t('online.network') }}</strong>

            <div v-if="adapters.length" class="adapter-section">
              <span class="pop-label">{{ t('online.detectedAdapters') }}</span>
              <div class="adapter-chips">
                <button
                  type="button"
                  :class="['adapter-chip', { active: !advertise.trim() }]"
                  @click="pickAdapter('')"
                >
                  <span>{{ t('online.autoAdapter') }}</span>
                </button>
                <button
                  v-for="item in adapters"
                  :key="item.ip"
                  type="button"
                  :class="['adapter-chip', item.kind, { active: advertise.trim() === item.ip }]"
                  @click="pickAdapter(item.ip)"
                >
                  <span class="adapter-kind">{{ item.kind === 'vlan' ? t('online.adapterVlan') : t('online.adapterLan') }}</span>
                  <strong>{{ item.name }}</strong>
                  <code>{{ item.ip }}</code>
                </button>
              </div>
            </div>

            <label class="pop-label" for="room-ip">{{ t('online.address') }}</label>
            <input id="room-ip" v-model="advertise" class="room-input" :placeholder="t('online.addressPlaceholder')"/>
            <label class="pop-label" for="room-port">{{ t('online.port') }}</label>
            <input id="room-port" v-model.number="port" class="room-input" type="number" min="1024" max="65533" required/>
            <div class="pop-foot-row">
              <span class="pop-hint">TCP {{ port || 27025 }} / {{ syncPort }}</span>
              <button type="button" class="text-button" @click="netPop = false; tutorialDrawer = true">
                <BookOpen :size="13"/>{{ t('online.tutorialBtn') }}
              </button>
            </div>
          </PopoverBox>
        </div>
      </div>

      <div class="lobby-row">
        <label class="field-label" for="nickname">{{ t('online.nickname') }}</label>
        <div class="inline-field">
          <input id="nickname" v-model="name" class="room-input" maxlength="24" :placeholder="t('online.nickPlaceholder')"/>
          <button type="button" class="icon-button" :aria-label="t('online.randomNick')" :title="t('online.randomNick')" @click="randomNickname">
            <Dices :size="17"/>
          </button>
        </div>
      </div>

      <template v-if="role === 'host'">
        <div class="lobby-row">
          <span class="field-label">{{ t('online.mode') }}</span>
          <div class="mode-grid">
            <button
              type="button"
              :aria-pressed="mode === 'independent'"
              :class="['ship-mode-card', { selected: mode === 'independent' }]"
              @click="mode = 'independent'"
            >
              <img src="/art/game/ship-support.webp" alt=""/>
              <div>
                <strong><Anchor :size="15"/>{{ t('online.modeIndependent') }}</strong>
                <small>{{ t('online.modeIndependentHint') }}</small>
              </div>
            </button>
            <button
              type="button"
              :aria-pressed="mode === 'shared'"
              :class="['ship-mode-card', { selected: mode === 'shared' }]"
              @click="mode = 'shared'"
            >
              <img src="/art/game/ship-classic.webp" alt=""/>
              <div>
                <strong><Users :size="15"/>{{ t('online.modeShared') }}</strong>
                <small>{{ t('online.modeSharedHint') }}</small>
              </div>
            </button>
          </div>
        </div>

        <div v-if="mode === 'independent'" class="rules-bar" role="group" :aria-label="t('online.ownerRules')">
          <label :class="['rule-toggle', { on: rules.trading }]"><input v-model="rules.trading" type="checkbox"/><span>{{ t('online.ruleTrading') }}</span></label>
          <label :class="['rule-toggle', { on: rules.stealing }]"><input v-model="rules.stealing" type="checkbox"/><span>{{ t('online.ruleStealing') }}</span></label>
          <label :class="['rule-toggle', { on: rules.sabotage }]"><input v-model="rules.sabotage" type="checkbox"/><span>{{ t('online.ruleSabotage') }}</span></label>
          <label :class="['rule-toggle', { on: rules.friendlyFire }]"><input v-model="rules.friendlyFire" type="checkbox"/><span>{{ t('online.ruleFriendlyFire') }}</span></label>
        </div>
      </template>

      <template v-else>
        <div class="lobby-row">
          <label class="field-label" for="invite">{{ t('online.invite') }}</label>
          <div class="inline-field">
            <input id="invite" v-model="invite" class="room-input" placeholder="flotsam://…" required spellcheck="false"/>
            <button type="button" class="button secondary compact" @click="pasteInvite">
              <ClipboardPaste :size="14"/>{{ t('online.pasteInvite') }}
            </button>
          </div>
        </div>
      </template>

      <div class="lobby-footer">
        <span v-if="!desktop" class="preview-hint">{{ t('online.desktopOnly') }}</span>
        <button v-else-if="role === 'host'" type="button" class="ip-status-pill" @click="toggleNetPop">
          <Wifi :size="13"/>
          <span>{{ t('online.usingIp', { label: selectedAdapterLabel }) }}</span>
        </button>
        <span v-else class="preview-hint">{{ t('home.fleetDesc') }}</span>

        <button class="button primary connect-btn" :disabled="busy || !desktop || (role === 'join' && !invite.trim())">
          <LoaderCircle v-if="busy" class="spin" :size="17"/>
          <ArrowRight v-else :size="17"/>
          {{ busy ? t('online.connect') : role === 'host' ? t('online.create') : t('online.joinAction') }}
        </button>
      </div>
    </form>

    <!-- Slide-Over Drawer 1: Multiplayer & Virtual-LAN Connection Tutorial -->
    <SlideDrawer :open="tutorialDrawer" :title="t('online.tutorialTitle')" width="450px" @close="tutorialDrawer = false">
      <section class="tut-card">
        <h3>{{ t('online.tutLanTitle') }}</h3>
        <p>{{ t('online.tutLanBody') }}</p>
      </section>

      <section class="tut-card recommended">
        <h3>{{ t('online.tutVlanTitle') }}</h3>
        <ol class="tut-steps">
          <li>{{ t('online.tutVlanStep1') }}</li>
          <li>{{ t('online.tutVlanStep2') }}</li>
          <li>{{ t('online.tutVlanStep3') }}</li>
        </ol>
      </section>

      <section class="tut-card">
        <h3>{{ t('online.tutPortTitle') }}</h3>
        <p>{{ t('online.tutPortBody') }}</p>
        <div class="port-badges">
          <code>TCP {{ port || 27025 }}</code>
          <code>TCP {{ syncPort }}</code>
        </div>
      </section>

      <section class="tut-card">
        <h3>{{ t('online.tutPlayTitle') }}</h3>
        <p>{{ t('online.tutPlayBody') }}</p>
      </section>
    </SlideDrawer>

    <!-- Slide-Over Drawer 2: Compatibility Differences -->
    <SlideDrawer :open="diffDrawer" :title="room?.connected ? t('online.differences') : t('online.lastJoinFail')" @close="diffDrawer = false">
      <div v-for="item in activeDiffs" :key="`${item.kind}:${item.detail}`" class="diff-card">
        <strong>{{ item.detail }}</strong>
        <p>{{ item.fix }}</p>
        <button type="button" class="button secondary compact" @click="fixAction(item)">
          {{ fixLabel(item) }}<ArrowRight :size="14"/>
        </button>
      </div>
    </SlideDrawer>
  </div>
</template>

<style scoped>
.room-stage {
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

/* Lobby Card (Fits comfortably inside 620px+ viewport with ZERO scroll) */
.lobby-card {
  width: 100%;
  max-width: 780px;
  margin: auto;
  padding: 20px 24px;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 520px;
  border: 2.5px solid var(--outline);
  border-radius: 16px;
  box-shadow: var(--shadow-hard);
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.lobby-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.lobby-tools {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.lobby-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.field-label {
  font-size: 13px;
  font-weight: 800;
}

.mode-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.ship-mode-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 11px 14px;
  border-radius: 13px;
  border: 2.5px solid var(--outline);
  background: var(--paper-bright);
  text-align: left;
  cursor: pointer;
  box-shadow: 0 3px 0 var(--outline);
  transition: transform 0.1s ease, background 0.12s ease;
}

.ship-mode-card:hover {
  transform: translateY(-1px);
}

.ship-mode-card.selected {
  background: #fff0c2;
  box-shadow: 0 4px 0 var(--outline);
}

.ship-mode-card img {
  width: 68px;
  height: 54px;
  object-fit: contain;
  flex: none;
}

.ship-mode-card strong {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 14.5px;
}

.ship-mode-card small {
  display: block;
  font-size: 11.5px;
  color: var(--muted);
  margin-top: 3px;
  line-height: 1.4;
}

.rules-bar {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 8px;
}

.rule-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 8px 8px;
  border-radius: 10px;
  border: 2px solid var(--line);
  background: var(--paper-bright);
  font-size: 12.5px;
  font-weight: 800;
  cursor: pointer;
  user-select: none;
}

.rule-toggle.on {
  border-color: var(--outline);
  background: #dff8ec;
}

.lobby-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  padding-top: 4px;
  flex-wrap: wrap;
}

.preview-hint {
  font-size: 12px;
  color: var(--muted);
}

.ip-status-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  border-radius: 999px;
  border: 1.5px dashed var(--outline);
  background: var(--paper-bright);
  color: var(--ink-soft);
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
}

.ip-status-pill:hover {
  background: #fff0c2;
}

.connect-btn {
  min-width: 168px;
  font-size: 15px;
  margin-left: auto;
}

/* Adapter Picker inside Network Popover */
.adapter-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.adapter-chips {
  display: flex;
  flex-direction: column;
  gap: 5px;
  max-height: 150px;
  overflow-y: auto;
}

.adapter-chip {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 9px;
  border-radius: 8px;
  border: 1.5px solid var(--line);
  background: var(--paper-bright);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.adapter-chip:hover {
  border-color: var(--outline);
}

.adapter-chip.active {
  border-color: var(--outline);
  background: #fff0c2;
  font-weight: 800;
}

.adapter-kind {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 800;
  background: var(--raised);
  border: 1px solid var(--outline);
  flex: none;
}

.adapter-chip.vlan .adapter-kind {
  background: #dff8ec;
}

.adapter-chip strong {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
}

.adapter-chip code {
  font-family: Consolas, monospace;
  font-size: 11.5px;
  color: var(--ink-soft);
}

.pop-foot-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

/* Tutorial Drawer Cards */
.tut-card {
  padding: 12px 14px;
  border-radius: 12px;
  background: var(--paper-bright);
  border: 2px solid var(--outline);
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.tut-card.recommended {
  background: #fff7dc;
  box-shadow: 0 3px 0 var(--outline);
}

.tut-card h3 {
  font-size: 13.5px;
  font-weight: 800;
}

.tut-card p,
.tut-steps {
  margin: 0;
  font-size: 12.5px;
  color: var(--ink-soft);
  line-height: 1.58;
}

.tut-steps {
  padding-left: 18px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.port-badges {
  display: flex;
  gap: 8px;
  margin-top: 2px;
}

.port-badges code {
  padding: 2px 8px;
  border-radius: 7px;
  background: var(--raised);
  border: 1.5px solid var(--outline);
  font-family: Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
}

/* Connected Room Split */
.room-command-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 16px;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 500px;
  border: 2.5px solid var(--outline);
  border-radius: 15px;
  box-shadow: var(--shadow-hard);
  flex-wrap: wrap;
  flex: none;
}

.room-meta-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.room-ship-icon {
  width: 52px;
  height: 42px;
  object-fit: contain;
}

.room-title-line {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.room-title-line h2 {
  font-size: 17px;
}

.mode-badge,
.ping-badge {
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 800;
  border: 1.5px solid var(--outline);
  background: var(--gold);
}

.ping-badge {
  background: #dff8ec;
}

.room-sub-line {
  font-size: 12px;
  color: var(--muted);
  margin-top: 2px;
}

.room-command-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.autostart-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: 9px;
  background: var(--paper-bright);
  border: 1.5px solid var(--outline);
  font-size: 12px;
  font-weight: 800;
  cursor: pointer;
}

.room-split {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr);
  gap: 12px;
}

.room-pane {
  min-height: 0;
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

.pane-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex: none;
}

.pane-head h2 {
  font-size: 15.5px;
}

.crew-count {
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--gold);
  border: 1.5px solid var(--outline);
  font-size: 11.5px;
  font-weight: 800;
}

.pane-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 7px;
}

.chat-scroll {
  padding: 10px;
  border-radius: 10px;
  background: var(--paper-bright);
  border: 2px solid var(--outline);
}

.member-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border-radius: 9px;
  background: var(--paper-bright);
  border: 1.5px solid var(--outline);
  font-size: 13px;
  font-weight: 700;
}

.member-boat {
  width: 20px;
  height: 20px;
  object-fit: contain;
}

.member-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.role-tag,
.ping-tag {
  font-size: 10.5px;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--gold);
  border: 1.5px solid var(--outline);
}

.ping-tag {
  background: #e5f5ff;
}

.danger-link {
  color: var(--danger);
  padding: 2px 6px;
}

.notice-form,
.notice-banner {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 6px 10px;
  border-radius: 9px;
  background: rgba(246, 185, 59, 0.22);
  border: 1.5px solid var(--outline);
  font-size: 12px;
  flex: none;
}

.notice-form input {
  flex: 1;
  min-width: 0;
  border: 0;
  background: transparent;
  outline: none;
}

.notice-submit {
  font-size: 11.5px;
  padding: 2px 6px;
}

.empty-chat {
  font-size: 12.5px;
  color: var(--muted);
  margin: auto;
}

.chat-line {
  font-size: 13px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.chat-line strong {
  color: var(--accent-hover);
  margin-right: 6px;
}

/* Popover & Drawer Inner Helpers */
.pop-title {
  font-size: 14px;
  font-weight: 800;
}

.pop-label {
  font-size: 12px;
  font-weight: 700;
}

.pop-copy,
.pop-hint {
  font-size: 12px;
  color: var(--ink-soft);
  line-height: 1.55;
}

.pop-rules {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.rule-chip {
  padding: 3px 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 800;
  border: 1.5px solid var(--line);
  color: var(--muted);
  text-decoration: line-through;
}

.rule-chip.on {
  background: #dff8ec;
  border-color: var(--outline);
  color: var(--ink);
  text-decoration: none;
}

.diff-card {
  padding: 12px 14px;
  border-radius: 11px;
  background: var(--paper-bright);
  border: 2px solid var(--outline);
  display: flex;
  flex-direction: column;
  gap: 6px;
  align-items: flex-start;
}

.diff-card strong {
  font-size: 13.5px;
}

.diff-card p {
  font-size: 12px;
  color: var(--muted);
}

@media (max-width: 820px) {
  .mode-grid,
  .room-split {
    grid-template-columns: 1fr;
  }
  .rules-bar {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
