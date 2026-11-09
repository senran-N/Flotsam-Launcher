<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

withDefaults(defineProps<{
  open: boolean;
  align?: 'left' | 'right';
  width?: string;
}>(), {
  align: 'right',
  width: '300px',
});

const emit = defineEmits<{ close: [] }>();
const root = ref<HTMLElement | null>(null);

function onPointerDown(event: MouseEvent) {
  if (!root.value) return;
  if (!root.value.contains(event.target as Node)) emit('close');
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close');
}

onMounted(() => {
  document.addEventListener('mousedown', onPointerDown);
  document.addEventListener('keydown', onKeyDown);
});

onUnmounted(() => {
  document.removeEventListener('mousedown', onPointerDown);
  document.removeEventListener('keydown', onKeyDown);
});
</script>

<template>
  <div ref="root" class="popover-anchor">
    <slot name="trigger"/>
    <div
      v-if="open"
      :class="['popover-layer', align]"
      :style="{ width }"
      role="dialog"
    >
      <slot/>
    </div>
  </div>
</template>

<style scoped>
.popover-anchor {
  position: relative;
  display: inline-flex;
}

.popover-layer {
  position: absolute;
  top: calc(100% + 8px);
  z-index: 70;
  max-width: calc(100vw - 28px);
  padding: 14px 16px;
  background-color: var(--surface);
  background-image: url('/art/game/paper-map.webp');
  background-size: 460px;
  border: 2.5px solid var(--outline);
  border-radius: 13px;
  box-shadow: 0 6px 0 var(--outline), 0 14px 28px rgba(12, 32, 52, 0.32);
  color: var(--ink);
  display: flex;
  flex-direction: column;
  gap: 10px;
  animation: pop-in 0.12s ease-out;
}

.popover-layer.right {
  right: 0;
}

.popover-layer.left {
  left: 0;
}

@keyframes pop-in {
  from {
    opacity: 0;
    transform: translateY(-4px) scale(0.98);
  }
  to {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}
</style>
