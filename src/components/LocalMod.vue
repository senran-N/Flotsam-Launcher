<script setup lang="ts">
import { computed } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import { useLauncher } from '../store';
import { t } from '../i18n';
import type { ModEntry, ModPackage } from '../types';

const props = defineProps<{ mod: ModEntry; running: boolean }>();
const emit = defineEmits<{
  package: [pkg: ModPackage, action: string];
  select: [mod: ModEntry];
}>();

const store = useLauncher();
const issues = computed(() => props.mod.issues || []);
const pkg = computed(() => props.mod.package || null);
const packageEnabled = computed(() => pkg.value?.enabled ?? props.mod.enabled);
const toggleChecked = computed(() => (pkg.value ? packageEnabled.value : props.mod.enabled));
const blocked = computed(() => store.busy || props.running);

function bytes(n: number) {
  return n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;
}

async function toggle() {
  const managed = pkg.value;
  if (managed) {
    emit('package', managed, packageEnabled.value ? 'disable' : 'enable');
    return;
  }
  await store.run(
    'toggle_mod',
    { file: props.mod.file, enabled: !props.mod.enabled },
    props.mod.enabled ? '模组已停用' : '模组已启用',
  );
}
</script>

<template>
  <article class="local-mod-row">
    <img src="/art/game/icon-build.png" alt="" class="mod-craft-icon"/>
    <button
      type="button"
      class="mod-identity"
      :aria-label="`${mod.name} ${t('mods.detail')}`"
      @click="emit('select', mod)"
    >
      <span class="mod-title">{{ mod.name }}</span>
      <span v-if="issues.length" class="mod-problem">{{ issues[0]?.message }}</span>
      <span v-else class="mod-meta">
        {{ mod.company ? `${mod.company} · ` : '' }}{{ bytes(mod.size) }}
        <template v-if="pkg"> · {{ t('mods.ownedByPackage') }}</template>
      </span>
    </button>
    <ChevronRight :size="16" class="mod-arrow" aria-hidden="true"/>
    <button
      type="button"
      role="switch"
      :aria-checked="pkg?.mixed ? 'mixed' : toggleChecked"
      :aria-label="`${toggleChecked ? t('mods.toggleOff') : t('mods.toggleOn')} ${mod.name}`"
      :class="['switch', { on: toggleChecked }]"
      :disabled="blocked"
      @click="toggle"
    ><span></span></button>
  </article>
</template>

<style scoped>
.local-mod-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border-bottom: 1.5px dashed var(--line);
}

.local-mod-row:last-child {
  border-bottom: 0;
}

.mod-craft-icon {
  width: 24px;
  height: 24px;
  object-fit: contain;
  flex: none;
}

.mod-identity {
  flex: 1;
  min-width: 0;
  text-align: left;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 4px 0;
  background: none;
  border: 0;
  color: inherit;
  cursor: pointer;
}

.mod-title {
  font-size: 14.5px;
  font-weight: 800;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mod-meta,
.mod-problem {
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mod-problem {
  color: var(--danger);
  font-weight: 700;
}

.mod-arrow {
  color: var(--muted);
  flex: none;
}
</style>
