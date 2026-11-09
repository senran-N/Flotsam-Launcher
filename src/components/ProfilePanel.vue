<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { Plus, Download, Trash2, ArrowRight, LoaderCircle, Layers, Upload } from 'lucide-vue-next';
import { command, errorText } from '../bridge';
import { useLauncher } from '../store';
import { t } from '../i18n';
import type { ApplyReport, ModProfile } from '../types';
import PopoverBox from './PopoverBox.vue';

const store = useLauncher();
const profiles = ref<ModProfile[]>([]);
const loading = ref(false);
const busy = ref('');
const error = ref('');
const name = ref('');
const note = ref('');
const includeDisabled = ref(false);
const showSave = ref(false);
const loaded = ref(false);

const total = computed(() => profiles.value.length);
const localMods = computed(() => store.data?.mods.length || 0);
const gameRunning = computed(() => store.running);
const anyBusy = computed(() => Boolean(busy.value) || store.busy);

async function load() {
  loading.value = true;
  error.value = '';
  try {
    profiles.value = await command<ModProfile[]>('list_mod_profiles');
    loaded.value = true;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (!name.value.trim() || anyBusy.value) return;
  busy.value = 'save';
  try {
    await command<ModProfile>('save_mod_profile', {
      name: name.value.trim(),
      note: note.value.trim(),
      includeDisabled: includeDisabled.value,
    });
    name.value = '';
    note.value = '';
    includeDisabled.value = false;
    showSave.value = false;
    store.notify(t('profiles.saved'), 'success');
    await load();
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    busy.value = '';
  }
}

function report(profileName: string, result: ApplyReport) {
  const parts = [t('profiles.applied', { name: profileName })];
  if (result.enabled.length) parts.push(t('profiles.enabled', { count: result.enabled.length }));
  if (result.disabled.length) parts.push(t('profiles.disabled', { count: result.disabled.length }));
  if (result.missing.length) parts.push(t('profiles.missing', { names: result.missing.join('、') }));
  return parts.join(' · ');
}

function extras(profile: ModProfile) {
  const wanted = new Set(profile.mods.map((m) => m.file.replace(/\\/g, '/').toLowerCase()));
  return (store.data?.mods || []).filter(
    (m) => !wanted.has(m.file.replace(/\\/g, '/').replace(/\.disabled$/, '').toLowerCase()),
  );
}

async function apply(profile: ModProfile) {
  if (anyBusy.value) return;
  if (gameRunning.value) { store.notify(t('profiles.applyNeedsStop'), 'error'); return; }
  const outside = extras(profile);
  const ok = await store.confirm({
    title: t('profiles.confirmApplyTitle'),
    body: t('profiles.confirmApplyBody', { count: outside.length, enabled: outside.filter((m) => m.enabled).length }),
    detail: outside.slice(0, 6).map((m) => m.name).join('、') + (outside.length > 6 ? '…' : ''),
    confirm: t('profiles.doApply'),
  });
  if (!ok) return;
  busy.value = profile.id;
  try {
    const result = await command<ApplyReport>('apply_mod_profile', { id: profile.id });
    await store.refresh();
    store.chooseProfile(store.enabledMods > 0 ? '本地模组' : '原版');
    store.notify(report(profile.name, result), result.missing.length ? 'info' : 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    busy.value = '';
  }
}

async function remove(profile: ModProfile) {
  if (anyBusy.value) return;
  const ok = await store.confirm({
    title: t('profiles.confirmDeleteTitle'),
    body: t('profiles.confirmDeleteBody', { name: profile.name }),
    confirm: t('profiles.doDelete'),
    danger: true,
  });
  if (!ok) return;
  busy.value = profile.id;
  try {
    await command('delete_mod_profile', { id: profile.id });
    store.notify(t('profiles.deleted'), 'success');
    await load();
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    busy.value = '';
  }
}

async function exportProfile(profile: ModProfile) {
  if (anyBusy.value) return;
  busy.value = profile.id;
  try {
    const path = await command<string | null>('export_mod_profile', { id: profile.id });
    if (path) store.notify(t('profiles.exported'), 'success');
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    busy.value = '';
  }
}

async function importProfile() {
  if (anyBusy.value) return;
  busy.value = 'import';
  try {
    const profile = await command<ModProfile | null>('import_mod_profile');
    if (profile) {
      store.notify(t('profiles.imported'), 'success');
      await load();
    }
  } catch (e) {
    store.notify(errorText(e), 'error');
  } finally {
    busy.value = '';
  }
}

function stamp(seconds: number) {
  return seconds
    ? new Date(seconds * 1000).toLocaleDateString(store.locale === 'en' ? 'en-GB' : 'zh-CN', { month: 'long', day: 'numeric' })
    : '';
}

onMounted(load);
defineExpose({ load });
</script>

<template>
  <div class="profiles-stage">
    <div class="top-bar">
      <!-- Create Profile Popover instead of layout-pushing form -->
      <PopoverBox :open="showSave" align="left" width="320px" @close="showSave = false">
        <template #trigger>
          <button type="button" class="button primary compact" :disabled="!localMods || anyBusy" @click="showSave = !showSave">
            <Plus :size="15"/>{{ t('profiles.save') }}
          </button>
        </template>
        <form class="pop-form" @submit.prevent="save">
          <label class="pop-label" for="profile-name">{{ t('profiles.name') }}</label>
          <input id="profile-name" v-model="name" class="room-input" maxlength="60" :placeholder="t('profiles.namePlaceholder')" required/>
          <label class="pop-label" for="profile-note">{{ t('profiles.note') }}</label>
          <input id="profile-note" v-model="note" class="room-input" maxlength="500" :placeholder="t('profiles.notePlaceholder')"/>
          <label class="pop-check"><input v-model="includeDisabled" type="checkbox"/>{{ t('profiles.saveIncludeDisabled') }}</label>
          <button class="button primary compact" :disabled="!name.trim() || busy === 'save'">
            <LoaderCircle v-if="busy === 'save'" :size="14" class="spin"/>
            {{ t('profiles.save') }}
          </button>
        </form>
      </PopoverBox>

      <button type="button" class="button ghost compact" :disabled="anyBusy" @click="importProfile">
        <LoaderCircle v-if="busy === 'import'" :size="14" class="spin"/>
        <Upload v-else :size="14"/>
        {{ t('profiles.import') }}
      </button>
    </div>

    <div v-if="error" class="inline-error" role="alert">
      <span>{{ error }}</span>
      <button type="button" class="text-button" @click="load">{{ t('common.retry') }}</button>
    </div>

    <div v-if="total" class="scroll-surface">
      <article v-for="profile in profiles" :key="profile.id" class="profile-row">
        <div class="profile-identity">
          <h2>{{ profile.name }}</h2>
          <span>{{ t('profiles.detail', { count: profile.mods.length, date: stamp(profile.updatedAt) }) }}</span>
          <p v-if="profile.note">{{ profile.note }}</p>
        </div>
        <div class="profile-buttons">
          <button type="button" class="button secondary compact" :disabled="anyBusy || gameRunning" @click="apply(profile)">
            <LoaderCircle v-if="busy === profile.id" :size="14" class="spin"/>
            <ArrowRight v-else :size="14"/>
            {{ t('profiles.apply') }}
          </button>
          <button type="button" class="text-button" :disabled="anyBusy" @click="exportProfile(profile)">
            {{ t('profiles.export') }}<Download :size="13"/>
          </button>
          <button type="button" class="icon-button" :aria-label="`${t('profiles.delete')} ${profile.name}`" :disabled="anyBusy" @click="remove(profile)">
            <Trash2 :size="15"/>
          </button>
        </div>
      </article>
    </div>
    <div v-else-if="!loading && loaded" class="empty-stage">
      <Layers :size="36" :stroke-width="1.4"/>
      <h2>{{ t('profiles.title') }}</h2>
      <p>{{ t('profiles.empty') }}</p>
    </div>
  </div>
</template>

<style scoped>
.profiles-stage {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
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

.pop-check {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--ink-soft);
}

.profile-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  padding: 14px 18px;
  border-bottom: 1.5px dashed var(--line);
}

.profile-row:last-child {
  border-bottom: 0;
}

.profile-identity {
  min-width: 0;
}

.profile-identity h2 {
  font-size: 15.5px;
}

.profile-identity span,
.profile-identity p {
  font-size: 12px;
  color: var(--muted);
  display: block;
  margin-top: 2px;
}

.profile-buttons {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

@media (max-width: 650px) {
  .profile-row {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
