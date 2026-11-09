import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { command, desktop, errorText, snapshot } from './bridge';
import { t, useLocale } from './i18n';
import type { Activity, ConfirmRequest, DownloadTask, ModSection, Page, Settings, Snapshot } from './types';

type Kind = Activity['kind'];

export const useLauncher = defineStore('launcher', () => {
  const data = ref<Snapshot | null>(null);
  const busy = ref(false);
  /** Name of the command currently running; the UI can say which action waits. */
  const busyAction = ref('');
  const toast = ref('');
  const toastKind = ref<Kind>('info');
  const activity = ref<Activity[]>([]);
  const confirmRequest = ref<(ConfirmRequest & { resolve: (value: boolean) => void }) | null>(null);
  const tasks = ref<DownloadTask[]>([]);
  const tasksError = ref('');
  /** Navigation is shared so a fix action in one panel can open the right page. */
  const page = ref<Page>('home');
  const modTab = ref<ModSection>('local');
  const onlineRole = ref<'host' | 'join'>('host');
  const onlineMode = ref<'independent' | 'shared'>('independent');
  const profile = ref(localStorage.getItem('flotsam-profile') || '原版');
  const { locale, setLocale } = useLocale();
  let toastTimer: ReturnType<typeof setTimeout> | undefined;
  let refreshing: Promise<void> | null = null;

  const enabledMods = computed(() => data.value?.mods.filter((m) => m.enabled).length || 0);
  const running = computed(() => Boolean(data.value?.gameRunning));
  const loaderInstalled = computed(() => Boolean(data.value?.loaderInstalled));
  const activeTasks = computed(() => tasks.value.filter((t) => t.state === 'queued' || t.state === 'running'));

  function notify(text: string, kind: Kind = 'info') {
    toast.value = text;
    toastKind.value = kind;
    activity.value.unshift({
      id: Date.now() + Math.random(),
      time: new Date().toLocaleTimeString(locale.value === 'en' ? 'en-GB' : 'zh-CN', { hour: '2-digit', minute: '2-digit' }),
      text,
      kind,
    });
    activity.value = activity.value.slice(0, 40);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toast.value = ''; }, kind === 'error' ? 8000 : 4500);
  }

  function refresh(force = false): Promise<void> {
    if (refreshing) return force ? refreshing.then(() => refresh()) : refreshing;
    refreshing = (async () => {
      try {
        const next = await snapshot();
        data.value = next;
        if (next.settings?.language) setLocale(next.settings.language);
      } catch (error) {
        notify(errorText(error), 'error');
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  }

  /** Run one mutating command, then re-read the real snapshot. Never fakes success. */
  async function run<T>(name: string, args: Record<string, unknown> = {}, message?: string): Promise<T | undefined> {
    if (busy.value) return;
    busy.value = true;
    busyAction.value = name;
    try {
      const result = await command<T>(name, args);
      await refresh(true);
      if (message) notify(message, 'success');
      return result;
    } catch (error) {
      notify(errorText(error), 'error');
      return undefined;
    } finally {
      busy.value = false;
      busyAction.value = '';
    }
  }

  async function save(settings: Settings) {
    setLocale(settings.language);
    await run('save_settings', { settings }, t('settings.saved'));
  }

  function chooseProfile(value: string) {
    profile.value = value;
    localStorage.setItem('flotsam-profile', value);
  }

  function applyLanguage(value: Settings['language']) {
    setLocale(value);
  }

  /** Ask before a destructive or hard-to-undo step. Resolves false when dismissed. */
  function confirm(request: ConfirmRequest): Promise<boolean> {
    confirmRequest.value?.resolve(false);
    return new Promise((resolve) => { confirmRequest.value = { ...request, resolve }; });
  }
  function answerConfirm(value: boolean) {
    const request = confirmRequest.value;
    confirmRequest.value = null;
    request?.resolve(value);
  }
  /** Shared click handler for InlineError-style retries. */
  async function retryLast<T>(name: string, args: Record<string, unknown> = {}, message?: string): Promise<T | undefined> {
    return run<T>(name, args, message);
  }

  async function loadTasks(report = false): Promise<DownloadTask[]> {
    try {
      tasks.value = await command<DownloadTask[]>('download_tasks');
      tasksError.value = '';
    } catch (error) {
      tasksError.value = errorText(error);
      if (report) notify(tasksError.value, 'error');
    }
    return tasks.value;
  }

  /** Download queue lives here so the queue page and the catalogue stay in sync. */
  async function queueChanged(next: DownloadTask[]) {
    const before = tasks.value;
    tasks.value = next;
    const justDone = next.find((task) => task.state === 'done' && before.find((old) => old.id === task.id)?.state !== 'done');
    if (justDone) {
      notify(locale.value === 'en' ? `${justDone.name || justDone.id} finished downloading` : `${justDone.name || justDone.id} 下载完成`, 'success');
    }
    const justFailed = next.find((task) => task.state === 'failed' && before.find((old) => old.id === task.id)?.state !== 'failed');
    if (justFailed) notify(justFailed.error || '下载失败', 'error');
  }

  async function cancelTask(id: string) {
    try {
      await command('cancel_download', { id });
      await loadTasks();
    } catch (error) {
      notify(errorText(error), 'error');
    }
  }
  async function retryTask(id: string) {
    try {
      await command('retry_download', { id });
      await loadTasks();
    } catch (error) {
      notify(errorText(error), 'error');
    }
  }
  async function clearFinishedTasks() {
    try {
      await command('clear_downloads');
      await loadTasks();
    } catch (error) {
      notify(errorText(error), 'error');
    }
  }

  const isDesktop = desktop;

  /** Navigate without hiding the reason: cross-page fix actions use this. */
  function go(next: Page, section?: ModSection) {
    page.value = next;
    if (section) modTab.value = section;
  }

  function goOnline(role: 'host' | 'join', mode: 'independent' | 'shared' = 'independent') {
    onlineRole.value = role;
    onlineMode.value = mode;
    page.value = 'online';
  }

  return {
    data, busy, busyAction, toast, toastKind, activity, confirmRequest, tasks, tasksError, profile, locale,
    page, modTab, onlineRole, onlineMode, enabledMods, running, loaderInstalled, activeTasks, isDesktop,
    refresh, run, retryLast, notify, save, chooseProfile, applyLanguage, confirm, answerConfirm, go, goOnline,
    loadTasks, queueChanged, cancelTask, retryTask, clearFinishedTasks,
  };
});
