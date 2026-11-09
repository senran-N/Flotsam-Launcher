export interface ModDependency {pluginId:string;minimumVersion:string|null;optional:boolean}
export interface ModPlugin {id:string;name:string;version:string;dependencies:ModDependency[];incompatible:string[];processes:string[]}
export interface ModPackage {id:string;name:string;files:number;canRollback:boolean;nexusModId:number|null;nexusFileId:number|null;enabled?:boolean;mixed?:boolean}
export interface ModEntry { name:string; file:string; enabled:boolean; size:number; assemblyName?:string|null;assemblyVersion?:string|null;company?:string|null;plugins?:ModPlugin[];metadataError?:string|null;issues?:{code:string;severity:'error'|'warning';message:string}[];package?:ModPackage|null }
export interface RemovedPackage {id:string;name:string;files:number;canRollback:boolean;nexusModId:number|null;nexusFileId:number|null}
export interface SaveSlot { slotName:string; fileName:string; saveType:string; day?:number|null; drifters?:number|null; playTimeSeconds?:number|null; modified:number; size:number; gameVersion?:string|null }
export interface SaveEntry { name:string; folder:string; modified:number; size:number; files:number; communityName?:string|null; latestSlot?:string|null; saveType?:string|null; day?:number|null; drifters?:number|null; playTimeSeconds?:number|null; gameVersion?:string|null; slots?:SaveSlot[] }
export type Language='zh'|'en';
export type DisplayMode='fullscreen'|'windowed'|'game';
export interface Settings { gamePath:string; backupBeforeLaunch:boolean; reducedMotion:boolean; language:Language; displayMode:DisplayMode }
export interface NetInterface { name:string; ip:string; kind:'vlan'|'lan' }
export interface Snapshot { settings:Settings; installed:boolean; loaderInstalled:boolean; mods:ModEntry[]; saves:SaveEntry[]; backups:number; gameVersion:string|null; gameRunning:boolean; removedPackages?:RemovedPackage[]; packageRecovery?:boolean }
export interface Activity { id:number; time:string; text:string; kind:'success'|'error'|'info' }
export interface ProfileMod {file:string;name:string;version?:string|null;plugins:string[]}
export interface ModProfile {id:string;name:string;note:string;createdAt:number;updatedAt:number;mods:ProfileMod[]}
export interface ApplyReport {enabled:string[];disabled:string[];missing:string[];extras:string[];unchanged:number}
export type DownloadState='queued'|'running'|'done'|'failed'|'cancelled';
export interface DownloadTask {id:string;modId:number;fileId:number;name:string;state:DownloadState;received:number;total:number;attempts:number;error:string;createdAt:number}
export interface DownloadRecord {id:string;modId:number;fileId:number;name:string;size:number;sha256:string}
export interface Compatibility {game:string;loader:boolean;plugins:string[];patchers:string[];fingerprint:string;warning:string;loaderVersion?:string;doorstop?:boolean;blockers?:Difference[];warnings?:Difference[]}
export interface Difference {kind:string;detail:string;fix:string;action?:string;blocking?:boolean}
export interface RoomRules {stealing:boolean;sabotage:boolean;trading:boolean;friendlyFire:boolean}
export type Page='home'|'mods'|'saves'|'online'|'settings';
export type ModSection='local'|'online'|'profiles';
/** One confirmation request at a time; the launcher never guesses an answer. */
export interface ConfirmRequest {title:string;body:string;detail?:string;confirm:string;cancel?:string;danger?:boolean}
