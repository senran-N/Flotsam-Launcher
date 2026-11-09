<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue';
import { X } from 'lucide-vue-next';
import { t } from '../i18n';

withDefaults(defineProps<{
  open: boolean;
  title: string;
  width?: string;
}>(), {
  width: '440px',
});

const emit = defineEmits<{ close: [] }>();

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close');
}

onMounted(() => document.addEventListener('keydown', onKeyDown));
onUnmounted(() => document.removeEventListener('keydown', onKeyDown));
</script>

<template>
  <div v-if="open" class="drawer-scrim" @click.self="emit('close')">
    <aside class="drawer-sheet" :style="{ width: `min(${width}, 100vw)` }" role="dialog" aria-modal="true" :aria-label="title">
      <header class="drawer-header">
        <h2>{{ title }}</h2>
        <button type="button" class="icon-button" :aria-label="t('common.close')" @click="emit('close')">
          <X :size="18"/>
        </button>
      </header>
      <div class="drawer-body">
        <slot/>
      </div>
    </aside>
  </div>
</template>

<style scoped>
.drawer-scrim {
  position: fixed;
  inset: 0;
  z-index: 85;
  background: rgba(11, 29, 48, 0.42);
  backdrop-filter: blur(2px);
  display: flex;
  justify-content: flex-end;
}

.drawer-sheet {
  height: 100%;
  display: flex;
  flex-direction: column;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 520px;
  border-left: 3px solid var(--outline);
  box-shadow: -8px 0 24px rgba(10, 26, 44, 0.35);
  animation: slide-in 0.16s ease-out;
}

.drawer-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 18px;
  border-bottom: 2.5px solid var(--outline);
  background: rgba(255, 253, 246, 0.65);
  flex: none;
}

.drawer-header h2 {
  font-size: 18px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.drawer-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 16px 18px 22px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

@keyframes slide-in {
  from {
    transform: translateX(28px);
    opacity: 0.6;
  }
  to {
    transform: translateX(0);
    opacity: 1;
  }
}
</style>
