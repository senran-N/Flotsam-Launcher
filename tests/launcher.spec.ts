import { test, expect, type Page } from '@playwright/test';

/** Shared Tauri IPC shim. Only data owned by the test is supplied; every other
 *  command returns the real preview error so nothing is silently faked. */
async function shim(page: Page, handler: string) {
  await page.addInitScript(`(() => {
    const calls = [];
    window.testCalls = calls;
    Object.assign(window, { isTauri: true, __TAURI_INTERNALS__: { invoke: async (name, args = {}) => {
      calls.push({ name, args });
      const handle = (${handler});
      const value = await handle(name, args, calls);
      if (value !== undefined) return value;
      if (name === 'list_mod_profiles' || name === 'download_tasks' || name === 'list_downloads' || name === 'last_join_differences' || name === 'list_save_backups') return [];
      throw new Error('请在桌面版中使用此功能');
    } } });
  })();`);
}
const calls = (page: Page) =>
  page.evaluate(() => (window as unknown as { testCalls: { name: string; args: Record<string, unknown> }[] }).testCalls);
const snapshot = (over: Record<string, unknown> = {}) => ({
  settings: { gamePath: 'D:\\\\Epic Games\\\\Flotsam', backupBeforeLaunch: true, reducedMotion: false, language: 'zh' },
  installed: true, loaderInstalled: true, mods: [], saves: [], backups: 0,
  gameVersion: '1.0.1f9', gameRunning: false, removedPackages: [], packageRecovery: false, ...over,
});

test('browser preview keeps native actions honest', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '启航', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '启动游戏', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('浏览器预览不支持启动游戏');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await expect(page.getByRole('heading', { name: '船坞还很安静' })).toBeVisible();
  await page.getByRole('textbox', { name: '搜索模组' }).fill('missing');
  await expect(page.getByRole('heading', { name: '没有找到模组' })).toBeVisible();
  await page.getByRole('button', { name: '清除搜索' }).click();
  await expect(page.getByRole('textbox', { name: '搜索模组' })).toHaveValue('');
});

test('settings auto-save with non-blocking toast and switch between Chinese and English', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: '设置', exact: true }).click();
  const motion = page.getByRole('switch', { name: '减少动态效果' });
  await motion.click();
  await expect(page.getByRole('status')).toContainText('设置已保存');
  await page.getByRole('button', { name: 'English', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Settings saved');
  await page.reload();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(page.getByRole('switch', { name: 'Reduce motion' })).toHaveAttribute('aria-checked', 'true');
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await page.getByRole('button', { name: '中文', exact: true }).click();
  await expect(page.getByRole('heading', { name: '设置', exact: true })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('lang', 'zh-CN');
});

test('online catalog uses popover for API key and drawer for downloadable files', async ({ page }) => {
  await page.addInitScript(() => {
    (window as unknown as { openedUrls: string[] }).openedUrls = [];
    window.open = (url?: string | URL) => {
      (window as unknown as { openedUrls: string[] }).openedUrls.push(String(url ?? ''));
      return null;
    };
  });
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('button', { name: '发现模组', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Architect Pickup All', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '作者页面 Architect Pickup All' }).click();
  const opened = await page.evaluate(() => (window as unknown as { openedUrls: string[] }).openedUrls);
  expect(opened).toContain('https://www.nexusmods.com/flotsam/mods/8?tab=files');
  await page.getByRole('textbox', { name: '搜索在线模组' }).fill('Hansjoy');
  await expect(page.getByRole('heading', { name: 'Flotsam Trainer', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '下载 Flotsam Trainer', exact: true }).click();
  await expect(page.getByLabel('Nexus API Key', { exact: true })).toBeVisible();
});

test('download queue drawer shows real progress and can cancel and retry', async ({ page }) => {
  await page.addInitScript(`(() => {
    const calls = []; window.testCalls = calls;
    const tasks = [{ id: 'nexus-8-123', modId: 8, fileId: 123, name: 'Tools.zip', state: 'running', received: 512000, total: 2048000, attempts: 1, error: '', createdAt: 0 }];
    Object.assign(window, { isTauri: true, __TAURI_INTERNALS__: { invoke: async (name, args = {}) => {
      calls.push({ name, args });
      if (name === 'get_snapshot') return ${JSON.stringify(snapshot())};
      if (name === 'nexus_account') return { name: 'Tester', premium: true };
      if (name === 'online_catalog') return { mods: [{ id: 8, name: 'Architect Pickup All', author: 'NarstyOtter', summary: '建筑工具', version: '0.5' }], live: true };
      if (name === 'online_files') return [{ id: 123, name: 'Tools', version: '0.5', fileName: 'Tools.zip', size: 2048000 }];
      if (name === 'download_tasks') return structuredClone(tasks);
      if (name === 'list_downloads') return [];
      if (name === 'queue_download') return tasks[0];
      if (name === 'cancel_download') { tasks[0].state = 'cancelled'; tasks[0].error = '已取消'; return tasks[0]; }
      if (name === 'retry_download') { tasks[0].state = 'queued'; tasks[0].error = ''; tasks[0].received = 0; return tasks[0]; }
      if (name === 'list_mod_profiles' || name === 'last_join_differences' || name === 'list_save_backups') return [];
      throw new Error('请在桌面版中使用此功能');
    } } });
  })();`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('button', { name: '发现模组', exact: true }).click();
  await page.getByRole('button', { name: '下载 Architect Pickup All', exact: true }).click();
  await page.getByRole('button', { name: '加入下载', exact: true }).click();
  await expect(page.getByText('25%')).toBeVisible();
  await page.getByRole('button', { name: '取消', exact: true }).first().click();
  const confirm = page.getByRole('dialog', { name: '取消这个下载？' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: '取消下载', exact: true }).click();
  await expect(page.getByText('已取消').first()).toBeVisible();
  await page.getByRole('button', { name: '重试', exact: true }).first().click();
  await expect(page.getByText('排队中').first()).toBeVisible();
  const seen = await calls(page);
  expect(seen.find((c) => c.name === 'queue_download')?.args).toEqual({ modId: 8, fileId: 123 });
  expect(seen.some((c) => c.name === 'cancel_download')).toBeTruthy();
  expect(seen.some((c) => c.name === 'retry_download')).toBeTruthy();
});

test('independent mode configures multi-ship rules while rooms inspect differences in a slide-over drawer', async ({ page }) => {
  await shim(page, `async (name, args) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot())};
    if (name === 'multiplayer_status') return { installed: true, version: '0.5.0', runtime: null };
    if (name === 'room_status') return globalThis.hosted || { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 };
    if (name === 'host_room') { globalThis.hosted = { connected: true, host: true, mode: args.mode, address: '192.168.1.8:27025', invite: 'flotsam://192.168.1.8:27025/' + 'a'.repeat(64), members: [{ id: 0, name: String(args.name) }, { id: 1, name: '阿岚' }], messages: [], error: '', differences: [{ kind: 'missing', detail: '缺少房主启用的模组：OceanTools', fix: '从「模组 → 发现模组」安装，或向房主要同一文件' }], latency: 42 }; return globalThis.hosted; }
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '联机', exact: true }).click();
  const independent = page.getByRole('button', { name: /独立模式/ });
  await expect(independent).toBeEnabled();
  await independent.click();
  await page.getByLabel('允许攻击玩家').check();
  await page.getByLabel('昵称', { exact: true }).fill('船长');
  await page.getByRole('button', { name: '创建房间', exact: true }).last().click();
  await expect(page.getByRole('heading', { name: '我的房间' })).toBeVisible();
  await expect(page.getByRole('button', { name: '启动多船世界', exact: true })).toBeVisible();
  await expect(page.getByText('42 ms')).toBeVisible();
  await page.getByRole('button', { name: /本机与房主的差异/ }).click();
  const drawer = page.getByRole('dialog', { name: '本机与房主的差异' });
  await expect(drawer).toContainText('缺少房主启用的模组：OceanTools');
  await expect(drawer).toContainText('从「模组 → 发现模组」安装，或向房主要同一文件');
  const host = (await calls(page)).find((c) => c.name === 'host_room');
  expect(host?.args).toMatchObject({ mode: 'independent', rules: { stealing: true, sabotage: true, trading: true, friendlyFire: true } });
});

test('shared room installs components before launching the multiplayer command', async ({ page }) => {
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({ loaderInstalled: false }))};
    if (name === 'multiplayer_status') return { installed: globalThis.installed === true, version: '0.5.0', runtime: null };
    if (name === 'install_multiplayer') { globalThis.installed = true; return null; }
    if (name === 'room_status') return { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 };
    if (name === 'host_room') return { connected: true, host: true, mode: 'shared', address: '192.168.1.8:27025', invite: 'flotsam://192.168.1.8:27025/' + 'a'.repeat(64), members: [{ id: 0, name: '船长' }], messages: [], error: '', differences: [], latency: 0 };
    if (name === 'launch_multiplayer') return null;
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '联机', exact: true }).click();
  await page.getByRole('button', { name: '安装联机组件', exact: true }).click();
  await page.getByRole('button', { name: /共建模式/ }).click();
  await page.getByLabel('昵称', { exact: true }).fill('船长');
  await page.getByRole('button', { name: '创建房间', exact: true }).last().click();
  await page.getByRole('button', { name: '启动共建世界', exact: true }).click();
  const names = (await calls(page)).map((c) => c.name);
  expect(names).toContain('install_multiplayer');
  expect(names).toContain('launch_multiplayer');
  expect(names).not.toContain('launch_game');
});

test('mod metadata opens in an inspector drawer and removed packages open in a recovery drawer', async ({ page }) => {
  await shim(page, `async (name, args) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({
      packageRecovery: true,
      mods: [{ name: '海上工坊', file: 'workshop/plugins/Metadata.Fixture.dll', enabled: true, size: 5120, company: 'Metadata fixture company', assemblyVersion: '3.2.1.0', metadataError: null, plugins: [{ id: 'fixture.main', name: '海上工坊', version: '2.1.0', dependencies: [{ pluginId: 'fixture.required', minimumVersion: '1.2.0', optional: false }], incompatible: [], processes: ['Flotsam.exe'] }], issues: [{ code: 'missing', severity: 'error', message: '缺少已启用前置：fixture.required' }], package: { id: 'aaaaaaaaaaaaaaaaaaaaaaaa', name: '海上工坊.zip', files: 2, canRollback: true, nexusModId: 8, nexusFileId: 123 } }],
      removedPackages: [{ id: 'bbbbbbbbbbbbbbbbbbbbbbbb', name: '旧船坞.zip', files: 1, canRollback: false, nexusModId: null, nexusFileId: null }],
    }))};
    if (name === 'change_mod_package') return null;
    if (name === 'open_mod_file' && args.kind === 'config') throw new Error('配置尚未生成，请先运行一次模组');
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await expect(page.getByText('缺少已启用前置：fixture.required')).toBeVisible();
  await expect(page.getByText('有中断的安装，需要恢复')).toBeVisible();
  await page.getByRole('button', { name: '恢复安装', exact: true }).click();
  await expect.poll(async () => (await calls(page)).some((c) => c.name === 'change_mod_package' && c.args.action === 'recover')).toBeTruthy();

  await page.getByRole('button', { name: '海上工坊 详情' }).click();
  const inspector = page.getByRole('dialog', { name: '海上工坊' });
  await expect(inspector).toBeVisible();
  await expect(inspector).toContainText('fixture.required');
  await expect(inspector).toContainText('≥ 1.2.0');
  await expect(inspector).toContainText('由整包管理');
  await inspector.getByRole('button', { name: '配置', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('配置尚未生成');
  await inspector.getByRole('button', { name: '卸载', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: '卸载整个模组包？' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: '卸载整包', exact: true }).click();
  await expect.poll(async () => (await calls(page)).some((c) => c.name === 'change_mod_package' && c.args.action === 'remove')).toBeTruthy();

  await page.getByRole('button', { name: /已卸载的整包/ }).click();
  await expect(page.getByRole('dialog', { name: '已卸载的整包' })).toContainText('旧船坞.zip');
});

test('mod profiles save via popover, apply, export and import through real commands', async ({ page }) => {
  await shim(page, `async (name, args, calls) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({ mods: [
      { name: '海上工坊', file: 'workshop/plugins/Metadata.Fixture.dll', enabled: true, size: 5120, plugins: [], issues: [], package: null },
      { name: 'Support Library', file: 'Support.dll', enabled: false, size: 4096, plugins: [], issues: [], package: null },
    ] }))};
    if (name === 'list_mod_profiles') return calls.filter((c) => c.name === 'save_mod_profile').length ? [{ id: 'aaaaaaaaaaaaaaaaaaaaaa11', name: args.name || '稳定联机', note: '', createdAt: 1, updatedAt: 1, mods: [{ file: 'workshop/plugins/Metadata.Fixture.dll', name: '海上工坊', plugins: [] }] }] : [];
    if (name === 'save_mod_profile') return { id: 'aaaaaaaaaaaaaaaaaaaaaa11', name: args.name, note: '', createdAt: 1, updatedAt: 1, mods: [] };
    if (name === 'apply_mod_profile') return { enabled: ['Metadata.Fixture.dll'], disabled: [], missing: ['Extra.dll'], extras: [], unchanged: 1 };
    if (name === 'export_mod_profile') return 'C:/profiles/a.json';
    if (name === 'import_mod_profile') return { id: 'cccccccccccccccccccccc33', name: '导入组合', note: '', createdAt: 1, updatedAt: 1, mods: [] };
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('button', { name: '模组组合', exact: true }).click();
  await expect(page.getByText('把当前启用的模组保存成组合，之后一键切换。')).toBeVisible();
  await page.getByRole('button', { name: '保存当前组合', exact: true }).click();
  await page.getByLabel('组合名称', { exact: true }).fill('稳定联机');
  await page.getByRole('button', { name: '保存当前组合', exact: true }).last().click();
  await expect(page.getByRole('heading', { name: '稳定联机', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '切换到此组合', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: '切换到这个组合？' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: '切换组合', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('缺少 Extra.dll');
  await page.getByRole('button', { name: '导出', exact: true }).click();
  await page.getByRole('button', { name: '导入组合', exact: true }).click();
  const seen = await calls(page);
  expect(seen.find((c) => c.name === 'save_mod_profile')?.args).toMatchObject({ name: '稳定联机', includeDisabled: false });
  expect(seen.some((c) => c.name === 'apply_mod_profile')).toBeTruthy();
  expect(seen.some((c) => c.name === 'export_mod_profile')).toBeTruthy();
  expect(seen.some((c) => c.name === 'import_mod_profile')).toBeTruthy();
});

test('native bridge renders rich save metadata, slot drawer, and exact subpath folder jumps', async ({ page }) => {
  const state = snapshot({
    mods: [{ name: 'OceanTools', file: 'OceanTools.dll', enabled: true, size: 2048, plugins: [], issues: [], package: null }],
    saves: [{
      name: '海上灯塔镇',
      folder: 'LighthouseTown',
      modified: 1794030898,
      size: 28338,
      files: 2,
      communityName: '海上灯塔镇',
      latestSlot: 'Autosave 2',
      saveType: 'autosave',
      day: 14,
      drifters: 8,
      playTimeSeconds: 5432,
      gameVersion: '1.0.1f9',
      slots: [
        { slotName: 'Autosave 2', fileName: 'Autosave 2.fs', saveType: 'autosave', day: 14, drifters: 8, playTimeSeconds: 5432, modified: 1794030898, size: 14338, gameVersion: '1.0.1f9' },
        { slotName: '手动节点 1', fileName: 'Manual 1.fs', saveType: 'manual', day: 12, drifters: 7, playTimeSeconds: 4100, modified: 1793966400, size: 14000, gameVersion: '1.0.1f9' },
      ],
    }],
  });
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(state)};
    if (name === 'toggle_mod') return null;
    if (name === 'backup_saves') return 'backup-path';
    if (name === 'open_directory') return null;
    return undefined;
  }`);
  await page.goto('/');
  await expect(page.getByText('已找到游戏')).toBeVisible();
  await expect(page.getByRole('heading', { name: '海上灯塔镇', exact: true })).toBeVisible();
  await expect(page.getByText(/第 14 天 · 8 名漂流者/)).toBeVisible();

  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('switch', { name: '停用 OceanTools' }).click();
  await expect.poll(async () => (await calls(page)).some((c) => c.name === 'toggle_mod')).toBeTruthy();
  expect((await calls(page)).find((c) => c.name === 'toggle_mod')?.args).toEqual({ file: 'OceanTools.dll', enabled: false });

  await page.getByRole('button', { name: '存档', exact: true }).click();
  await expect(page.getByText('第 14 天')).toBeVisible();
  await expect(page.getByText('8 名漂流者')).toBeVisible();
  await expect(page.getByText('游玩 1 小时 31 分')).toBeVisible();
  await expect(page.getByText(/目录：LighthouseTown/)).toBeVisible();

  // Clicking the folder icon jumps directly to that save's subfolder
  await page.getByRole('button', { name: '打开 海上灯塔镇 所在目录' }).click();
  expect((await calls(page)).find((c) => c.name === 'open_directory')?.args).toEqual({ kind: 'saves', subpath: 'LighthouseTown' });

  // Clicking the save card opens the Save Inspector SlideDrawer with full slot timeline
  await page.locator('.save-card').first().click();
  const drawer = page.getByRole('dialog', { name: '海上灯塔镇' });
  await expect(drawer).toBeVisible();
  await expect(drawer).toContainText('存档点时间线 (2)');
  await expect(drawer).toContainText('手动节点 1');
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: '备份存档', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('存档已备份');
  expect((await calls(page)).some((c) => c.name === 'backup_saves')).toBeTruthy();
});

test('missing loader offers the standalone install and reports real failures', async ({ page }) => {
  const state = snapshot({ loaderInstalled: false });
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(state)};
    if (name === 'install_loader') { globalThis.loaderTried = true; throw new Error('未找到游戏核心程序集'); }
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await expect(page.getByRole('heading', { name: '尚未安装模组加载器' })).toBeVisible();
  await page.getByRole('button', { name: '安装加载器', exact: true }).first().click();
  await expect(page.getByRole('status')).toContainText('未找到游戏核心程序集');
  const seen = await calls(page);
  expect(seen.some((c) => c.name === 'install_loader')).toBeTruthy();
  expect(seen.some((c) => c.name === 'install_multiplayer')).toBeFalsy();
});

test('package toggles work in one click without nagging confirmation modals', async ({ page }) => {
  const state = snapshot({
    mods: [{ name: '海上工坊', file: 'flotsam-aaaaaaaaaaaaaaaaaaaaaaaa/workshop.dll', enabled: true, size: 5120, plugins: [], issues: [], package: { id: 'aaaaaaaaaaaaaaaaaaaaaaaa', name: '海上工坊.zip', files: 2, canRollback: true, nexusModId: 8, nexusFileId: 123, enabled: true } }],
  });
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(state)};
    if (name === 'change_mod_package') return null;
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('switch', { name: '停用 海上工坊' }).click();
  await expect.poll(async () => (await calls(page)).some((c) => c.name === 'change_mod_package' && c.args.action === 'disable')).toBeTruthy();
  const call = (await calls(page)).find((c) => c.name === 'change_mod_package');
  expect(call?.args).toEqual({ id: 'aaaaaaaaaaaaaaaaaaaaaaaa', action: 'disable' });
});

test('home quick fleet entry opens independent room and launches without forcing manual component install', async ({ page }) => {
  await shim(page, `async (name, args) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({ loaderInstalled: false }))};
    if (name === 'multiplayer_status') return { installed: false, version: '0.5.0', runtime: null };
    if (name === 'room_status') return globalThis.hosted || { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 };
    if (name === 'host_room') { globalThis.hosted = { connected: true, host: true, mode: args.mode, address: '192.168.1.8:27025', invite: 'flotsam://192.168.1.8:27025/' + 'b'.repeat(64), members: [{ id: 0, name: String(args.name) }], messages: [], error: '', differences: [], latency: 0, rules: args.rules }; return globalThis.hosted; }
    if (name === 'launch_multiplayer') return null;
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '创建多船房间', exact: true }).click();
  await page.getByRole('button', { name: '创建房间', exact: true }).last().click();
  await expect(page.getByRole('button', { name: '启动多船世界', exact: true })).toBeEnabled();
  await page.getByLabel('进入游戏自动开新档').check();
  await page.getByRole('button', { name: '启动多船世界', exact: true }).click();
  const seen = await calls(page);
  expect(seen.find((c) => c.name === 'host_room')?.args).toMatchObject({ name: '漂流船长', mode: 'independent' });
  expect(seen.find((c) => c.name === 'launch_multiplayer')?.args).toEqual({ autoStart: true });
});

test('package update uses update_mod_package and stays silent when the picker is cancelled', async ({ page }) => {
  const state = snapshot({
    mods: [{ name: '海上工坊', file: 'flotsam-aaaaaaaaaaaaaaaaaaaaaaaa/workshop.dll', enabled: true, size: 5120, plugins: [], issues: [], package: { id: 'aaaaaaaaaaaaaaaaaaaaaaaa', name: '海上工坊.zip', files: 2, canRollback: false, nexusModId: null, nexusFileId: null, enabled: true } }],
  });
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(state)};
    if (name === 'update_mod_package') return null;
    return undefined;
  }`);
  await page.goto('/');
  await page.getByRole('button', { name: '模组', exact: true }).click();
  await page.getByRole('button', { name: '海上工坊 详情' }).click();
  await page.getByRole('dialog', { name: '海上工坊' }).getByRole('button', { name: '更新', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('未选择新版本');
  const seen = await calls(page);
  expect(seen.find((c) => c.name === 'update_mod_package')?.args).toEqual({ id: 'aaaaaaaaaaaaaaaaaaaaaaaa' });
  expect(seen.some((c) => c.name === 'change_mod_package')).toBeFalsy();
});

test('desktop shell locks to zero outer vertical scroll across all screens and keeps versions exclusively in About modal', async ({ page }) => {
  const manyMods = Array.from({ length: 18 }, (_, i) => ({
    name: `Mod-${i + 1}`, file: `Mod${i + 1}.dll`, enabled: i % 2 === 0, size: 4096, plugins: [], issues: [], package: null,
  }));
  const manySaves = Array.from({ length: 12 }, (_, i) => ({
    name: `Town-${i + 1}`, folder: `Town-${i + 1}`, modified: 1793966400, size: 28338, files: 1,
  }));
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({ mods: manyMods, saves: manySaves }))};
    if (name === 'multiplayer_status') return { installed: true, version: '0.5.0', runtime: null };
    if (name === 'room_status') return { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 };
    if (name === 'compatibility_report') return { game: '1234567890abcdef1234', loader: true, plugins: ['Flotsam.Multiplayer.dll'], patchers: [], fingerprint: 'fedcba0987654321fedc', blockers: [], warning: '' };
    return undefined;
  }`);
  await page.setViewportSize({ width: 1280, height: 820 });
  await page.goto('/');

  for (const label of ['启航', '联机', '模组', '存档', '设置'] as const) {
    if (label !== '启航') await page.getByRole('button', { name: label, exact: true }).click();
    await expect(page.getByRole('heading', { name: label, exact: true })).toBeVisible();
    const metrics = await page.evaluate(() => {
      const cards = Array.from(document.querySelectorAll<HTMLElement>('.deck-card, .save-card, .lobby-card, .settings-card'));
      const cardOverflow = cards.some((el) => el.scrollWidth > el.clientWidth + 1);
      return {
        scrollH: document.documentElement.scrollHeight,
        innerH: window.innerHeight,
        scrollW: document.documentElement.scrollWidth,
        innerW: window.innerWidth,
        cardOverflow,
      };
    });
    expect(metrics.scrollH).toBeLessThanOrEqual(metrics.innerH);
    expect(metrics.scrollW).toBeLessThanOrEqual(metrics.innerW);
    expect(metrics.cardOverflow).toBe(false);
    await expect(page.getByText('1.0.1f9')).toHaveCount(0);
    await expect(page.getByText('0.5.0')).toHaveCount(0);
  }

  // Versions and diagnostics live exclusively inside the About modal
  await page.locator('.about-icon-btn').click();
  const about = page.getByRole('dialog', { name: /浮游 · 0\.5\.0/ });
  await expect(about).toBeVisible();
  await expect(about).toContainText('Flotsam 1.0.1f9');
  await expect(about).toContainText('本机组件');
});

test('mobile navigation and every page stay within the viewport', async ({ page }) => {
  await shim(page, `async (name) => {
    if (name === 'get_snapshot') return ${JSON.stringify(snapshot({ mods: [{ name: 'OceanTools', file: 'OceanTools.dll', enabled: true, size: 2048, plugins: [], issues: [], package: null }], saves: [{ name: 'Flowway', folder: 'Flowway', modified: 1793966400, size: 28338, files: 1 }] }))};
    if (name === 'multiplayer_status') return { installed: false, version: '0.5.0', runtime: null };
    if (name === 'room_status') return { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 };
    return undefined;
  }`);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  for (const [label, heading] of [['模组', '模组'], ['存档', '存档'], ['联机', '联机']] as const) {
    await page.getByRole('button', { name: '打开导航' }).click();
    await page.getByRole('button', { name: label, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  }
});
