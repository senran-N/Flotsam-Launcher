import { test, expect, type Page } from '@playwright/test';

/** Controlled renderer-only IPC tests. Every invocation is intercepted before
 * app modules load. No native command, actual backup or user save is touched. */
const backup = { backup: '存档-1794067200000', files: 3, bytes: 4096 };
const rollback = 'C:/controlled-test-only/.flotsam-save-restore/rollback-fixture';
interface IpcCall { name: string; args: Record<string, unknown> }
interface FixtureOptions { language?: 'zh' | 'en'; running?: boolean; connected?: boolean; restoreError?: string; listError?: string; snapshotRefreshError?: string }
interface TestWindow {
  saveRestoreCalls: IpcCall[];
  saveRestoreFixture: FixtureOptions & { listFailed?: boolean; restored?: boolean };
}
async function fixture(page: Page, options: FixtureOptions = {}) {
  await page.addInitScript(({ options, backup, rollback }) => {
    const state = { ...options, listFailed: false, restored: false };
    const calls: IpcCall[] = [];
    Object.assign(window, {
      saveRestoreFixture: state,
      saveRestoreCalls: calls,
      isTauri: true,
      __TAURI_INTERNALS__: {
        invoke: async (name: string, args: Record<string, unknown> = {}) => {
          calls.push({ name, args });
          if (name === 'get_snapshot') {
            if (state.restored && state.snapshotRefreshError) throw new Error(state.snapshotRefreshError);
            return {
              settings: { gamePath: 'C:/controlled-test-only/Flotsam', backupBeforeLaunch: true, reducedMotion: true, language: state.language || 'zh' },
              installed: true, loaderInstalled: true, mods: [], backups: 1,
              saves: state.restored ? [{ name: 'Fixture restored save', folder: 'Fixture', modified: 1, size: backup.bytes, files: backup.files }] : [],
              gameVersion: 'test-only', gameRunning: Boolean(state.running), removedPackages: [], packageRecovery: false,
            };
          }
          if (name === 'list_save_backups') {
            if (state.listError && !state.listFailed) { state.listFailed = true; throw new Error(state.listError); }
            return [backup];
          }
          if (name === 'room_status') return { connected: Boolean(state.connected) };
          if (name === 'restore_save_backup') {
            if (state.restoreError) throw new Error(state.restoreError);
            if (args.backup !== backup.backup) throw new Error('Unexpected test backup identifier');
            state.restored = true;
            return { ...backup, rollbackGeneration: rollback };
          }
          if (['list_mod_profiles', 'download_tasks', 'list_downloads', 'last_join_differences'].includes(name)) return [];
          throw new Error(`Unexpected controlled test command: ${name}`);
        },
      },
    });
  }, { options, backup, rollback });
  await page.goto('/');
  await page.getByRole('button', { name: options.language === 'en' ? 'Saves' : '存档', exact: true }).click();
  await expect(page.getByTestId('save-backup-panel').getByRole('heading', { name: backup.backup, exact: true })).toBeVisible();
}
const panel = (page: Page) => page.getByTestId('save-backup-panel');
const calls = (page: Page) => page.evaluate(() => (window as unknown as TestWindow).saveRestoreCalls);
const restores = async (page: Page) => (await calls(page)).filter((call) => call.name === 'restore_save_backup');
async function review(page: Page) {
  await panel(page).getByRole('button', { name: '还原此备份', exact: true }).click();
  const first = page.getByRole('dialog', { name: '第 1 步：核对还原范围', exact: true });
  await expect(first).toBeVisible();
  return first;
}
async function finalReview(page: Page) {
  const first = await review(page);
  await first.getByRole('button', { name: '已核对，继续', exact: true }).click();
  const second = page.getByRole('dialog', { name: '第 2 步：确认还原存档', exact: true });
  await expect(second).toBeVisible();
  return second;
}

test('save restore preview never lists real backups or offers a fake restore', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: '存档', exact: true }).click();
  await expect(panel(page)).toContainText('网页预览不会读取或修改游戏存档');
  await expect(panel(page).getByRole('button', { name: '刷新备份', exact: true })).toBeDisabled();
  await expect(panel(page).getByRole('button', { name: '还原此备份', exact: true })).toHaveCount(0);
});

test('both save restore dialogs can cancel without issuing a restore command', async ({ page }) => {
  await fixture(page);
  const first = await review(page);
  await expect(first).toContainText('整个当前存档目录');
  await expect(first).toContainText('回滚副本');
  await first.getByRole('button', { name: '取消，不还原', exact: true }).click();
  await expect(first).toHaveCount(0);
  expect(await restores(page)).toEqual([]);
  const second = await finalReview(page);
  expect(await restores(page)).toEqual([]);
  await second.getByRole('button', { name: '取消，不还原', exact: true }).click();
  await expect(second).toHaveCount(0);
  await expect(panel(page).getByRole('button', { name: '还原此备份', exact: true })).toBeEnabled();
  expect(await restores(page)).toEqual([]);
});

test('confirmed restore uses the exact backup identifier, displays the real report and refreshes snapshot', async ({ page }) => {
  await fixture(page);
  await expect(panel(page)).toContainText('3 个文件 · 4 KiB');
  const second = await finalReview(page);
  expect(await restores(page)).toEqual([]);
  await second.getByRole('button', { name: '确认还原', exact: true }).click();
  await expect(panel(page).getByRole('heading', { name: `已还原备份：${backup.backup}`, exact: true })).toBeVisible();
  await expect(panel(page).locator('code')).toHaveText(rollback);
  expect(await restores(page)).toEqual([{ name: 'restore_save_backup', args: { backup: backup.backup } }]);
  const seen = await calls(page);
  const restoreIndex = seen.findIndex((call) => call.name === 'restore_save_backup');
  expect(seen.slice(restoreIndex + 1).some((call) => call.name === 'get_snapshot')).toBeTruthy();
  expect(seen.slice(restoreIndex + 1).some((call) => call.name === 'list_save_backups')).toBeTruthy();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  }
});

test('backend restore failures remain honest and never render a successful restore', async ({ page }) => {
  await fixture(page, { restoreError: 'Controlled backend refusal: game started' });
  const second = await finalReview(page);
  await second.getByRole('button', { name: '确认还原', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toContainText('Controlled backend refusal: game started');
  await expect(panel(page).getByRole('heading', { name: /^已还原备份/ })).toHaveCount(0);
  await expect(panel(page).locator('code')).toHaveCount(0);
  expect(await restores(page)).toHaveLength(1);
});

test('a successful restore does not hide a failed snapshot refresh', async ({ page }) => {
  await fixture(page, { snapshotRefreshError: 'Controlled snapshot refresh failure' });
  const second = await finalReview(page);
  await second.getByRole('button', { name: '确认还原', exact: true }).click();
  await expect(panel(page).getByRole('heading', { name: `已还原备份：${backup.backup}`, exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Controlled snapshot refresh failure' })).toBeVisible();
});

test('restore is disabled while the game is running', async ({ page }) => {
  await fixture(page, { running: true });
  await expect(panel(page).getByRole('button', { name: '还原此备份', exact: true })).toBeDisabled();
  await expect(panel(page)).toContainText('请退出游戏后再还原');
  expect(await restores(page)).toEqual([]);
});

test('connected multiplayer blocks restore before confirmation', async ({ page }) => {
  await fixture(page, { connected: true });
  await panel(page).getByRole('button', { name: '还原此备份', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toContainText('联机仍处于连接状态');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await restores(page)).toEqual([]);
});

test('room safety is checked again after both confirmations', async ({ page }) => {
  await fixture(page);
  const second = await finalReview(page);
  await page.evaluate(() => { (window as unknown as TestWindow).saveRestoreFixture.connected = true; });
  await second.getByRole('button', { name: '确认还原', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toContainText('联机仍处于连接状态');
  expect(await restores(page)).toEqual([]);
});

test('backup listing can recover from a real read error without restoring', async ({ page }) => {
  // Fail a refresh after the initial successful read, keeping the stale row
  // visible without hiding the error or reporting a fictitious empty list.
  await fixture(page, { language: 'en' });
  await page.evaluate(() => { const state = (window as unknown as TestWindow).saveRestoreFixture; state.listError = 'Controlled backup read failure'; state.listFailed = false; });
  await panel(page).getByRole('button', { name: 'Refresh backups', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toContainText('Controlled backup read failure');
  await panel(page).getByRole('button', { name: 'Retry loading backups', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(panel(page).getByRole('button', { name: 'Restore this backup', exact: true })).toBeEnabled();
  expect(await restores(page)).toEqual([]);
});
