import { invoke, isTauri } from '@tauri-apps/api/core';
import type { Snapshot, Settings } from './types';
import catalog from './data/mod-catalog.json';

export const desktop = isTauri();
const defaults: Settings = { gamePath: 'D:\\\\Epic Games\\\\Flotsam', backupBeforeLaunch: true, reducedMotion: false, language: 'zh', displayMode: 'fullscreen' };

/**
 * Two execution paths, both honest:
 * - Desktop: every call goes to the real Tauri command and errors come back as
 *   the Rust message.
 * - Browser preview: only data the preview itself owns is answered. Anything
 *   that would touch the game, the disk or the network is rejected with the
 *   same kind of message the desktop would give for an unsupported action.
 */
export async function command<T>(name: string, args: Record<string, unknown> = {}): Promise<T> {
  if (desktop) return invoke<T>(name, args);
  switch (name) {
    case 'get_snapshot': {
      let settings = defaults;
      try {
        const stored = JSON.parse(localStorage.getItem('flotsam-settings') || '{}') as Partial<Settings>;
        const mode = stored.displayMode === 'windowed' || stored.displayMode === 'game' ? stored.displayMode : 'fullscreen';
        settings = { ...defaults, ...stored, language: stored.language === 'en' ? 'en' : 'zh', displayMode: mode };
      } catch { /* a corrupt preview preference is not a launcher failure */ }
      return { settings, installed: false, loaderInstalled: false, mods: [], saves: [], backups: 0, gameVersion: null, gameRunning: false, removedPackages: [], packageRecovery: false } as T;
    }
    case 'save_settings': {
      const next = args.settings as Partial<Settings> | undefined;
      if (!next) throw new Error('设置内容缺失');
      const mode = next.displayMode === 'windowed' || next.displayMode === 'game' ? next.displayMode : 'fullscreen';
      localStorage.setItem('flotsam-settings', JSON.stringify({
        gamePath: typeof next.gamePath === 'string' ? next.gamePath : defaults.gamePath,
        backupBeforeLaunch: Boolean(next.backupBeforeLaunch),
        reducedMotion: Boolean(next.reducedMotion),
        language: next.language === 'en' ? 'en' : 'zh',
        displayMode: mode,
      }));
      return undefined as T;
    }
    case 'online_catalog': return { mods: catalog, live: false } as T;
    case 'list_downloads':
    case 'download_tasks':
    case 'list_mod_profiles':
    case 'last_join_differences': return [] as T;
    case 'network_interfaces': return [
      { name: 'WLAN', ip: '192.168.1.24', kind: 'lan' },
      { name: 'Tailscale', ip: '100.101.20.5', kind: 'vlan' },
    ] as T;
    case 'clear_join_differences': return undefined as T;
    case 'nexus_account': return null as T;
    case 'compatibility_report': return { game: '', loader: false, plugins: [], patchers: [], fingerprint: '', warning: '' } as T;
    case 'room_status': return { connected: false, host: false, mode: '', address: '', invite: '', members: [], messages: [], error: '', differences: [], latency: 0 } as T;
    case 'multiplayer_status': return { installed: false, version: '0.0.1-pre', runtime: null } as T;
    case 'check_launcher_update': return { currentVersion: '0.0.1-pre', latestVersion: '0.0.1-pre', hasUpdate: false, releaseUrl: 'https://github.com/senran-N/Flotsam-Launcher/releases', downloadUrl: 'https://github.com/senran-N/Flotsam-Launcher/releases', notes: '', publishedAt: '' } as T;
    case 'open_github_url': return undefined as T;
    default: throw new Error('请在桌面版中使用此功能');
  }
}

export const snapshot = () => command<Snapshot>('get_snapshot');

/** Surface command failures without losing the real message. */
export function errorText(error: unknown): string {
  if (typeof error === 'string') return error;
  if (error instanceof Error) return error.message;
  if (error && typeof error === 'object' && 'message' in error) return String((error as { message: unknown }).message);
  return String(error);
}
