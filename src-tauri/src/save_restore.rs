//! Whole-directory save restoration. No UI confirmation is implied by this IPC:
//! the caller MUST ask the human before invoking `restore_save_backup`.
//!
//! Lock order: LauncherState::mutation -> game directory -> app save-backup.lock.
//! `recover_before_launch` requires the first two locks already held and acquires
//! the third itself. Call it before ANY save backup/scan or game process spawn,
//! including multiplayer launch. An error MUST abort launch.
//!
//! Existing launcher backups contain the CONTENTS of Saves, not a Saves child.
//! Their root launcher-settings.json is launcher metadata, never save content.
//! Staging and retained rollback generations are siblings of Saves, so both
//! directory renames stay on the save volume (not the app-data volume).
//! A write-once journal precedes the first rename; a separate synced commit
//! marker follows the second. Uncommitted transactions roll back on recovery.
//! No save generation is recursively deleted, including failed/restaged data.
//! File syncs and rename ordering cover process interruption. Like std::fs on
//! Windows, this does not claim power-loss atomicity or protection from a hostile
//! process replacing directories between path checks and filesystem operations.
use crate::{
    network::{require_disconnected, NetworkState},
    packages::{boundary, is_link, lock, lock_path},
    service::{app_root, err, game_root, load_settings, require_stopped, LauncherState, Result},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Read, Write},
    path::{Component, Path, PathBuf},
};

const MAX_NODES: usize = 32768;
const MAX_DEPTH: usize = 32;
const MAX_FILE_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_TOTAL_BYTES: u64 = 16 * 1024 * 1024 * 1024;
const MAX_JOURNAL_BYTES: u64 = 16 * 1024 * 1024;
const STORE: &str = ".flotsam-save-restore";
const JOURNAL: &str = "transaction.json";
const ANCILLARY: &str = "launcher-settings.json";

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveBackup {
    /// Stable directory identifier accepted by restore_save_backup.
    pub backup: String,
    pub files: usize,
    pub bytes: u64,
    pub towns: Vec<String>,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RestoreSaveReport {
    pub backup: String,
    pub files: usize,
    pub bytes: u64,
    /// Absolute path of the untouched pre-restore Saves generation, when present.
    pub rollback_generation: Option<String>,
}
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct SaveFile {
    path: String,
    bytes: u64,
    sha256: String,
}
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Manifest {
    directories: Vec<String>,
    files: Vec<SaveFile>,
}
impl Manifest {
    fn bytes(&self) -> u64 { self.files.iter().map(|file| file.bytes).sum() }
}
#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Journal {
    schema: u32,
    token: String,
    backup: String,
    had_live: bool,
    manifest: Manifest,
}
struct Layout {
    app: PathBuf,
    backups: PathBuf,
    parent: PathBuf,
    live: PathBuf,
    store: PathBuf,
}

/// Check lexical components BEFORE canonicalizing: canonicalization alone would
/// hide junctions/symlinks. Canonical paths are then used consistently for all
/// comparisons, including Windows' extended-length \\?\ representation.
fn check_absolute(path: &Path) -> Result<()> {
    if !path.is_absolute() { return Err("存档路径必须是绝对路径".into()); }
    let mut current = PathBuf::new();
    for component in path.components() {
        match component {
            Component::Prefix(_) | Component::RootDir => { current.push(component.as_os_str()); }
            Component::Normal(name) => {
                current.push(name);
                match fs::symlink_metadata(&current) {
                    Ok(meta) if is_link(&meta) => return Err("存档路径包含链接或重解析点".into()),
                    Ok(_) => {},
                    Err(error) if error.kind() == ErrorKind::NotFound => {},
                    Err(error) => return Err(err(error)),
                }
            }
            _ => return Err("存档路径不能包含 . 或 ..".into()),
        }
    }
    Ok(())
}
fn mkdir_checked(path: &Path) -> Result<PathBuf> {
    check_absolute(path)?;
    fs::create_dir_all(path).map_err(err)?;
    check_absolute(path)?;
    let canonical = fs::canonicalize(path).map_err(err)?;
    if !fs::symlink_metadata(&canonical).map_err(err)?.is_dir() {
        return Err("存档管理路径不是目录".into());
    }
    Ok(canonical)
}
fn app_backup_lock(app: &tauri::AppHandle) -> Result<File> {
    let root = mkdir_checked(&app_root(app)?)?;
    boundary(&root, &root.join("save-backup.lock"))?;
    lock_path(&root.join("save-backup.lock"))
}
impl Layout {
    /// Explicit paths are deliberate: tests never consult USERPROFILE or AppHandle.
    fn new(app: &Path, saves: &Path) -> Result<Self> {
        check_absolute(saves)?;
        if saves.file_name().and_then(|name| name.to_str()) != Some("Saves") {
            return Err("存档目标必须是 Saves 目录".into());
        }
        let app = mkdir_checked(app)?;
        let backups = app.join("backups");
        boundary(&app, &backups)?;
        let parent = mkdir_checked(saves.parent().ok_or("存档父目录无效")?)?;
        let live = parent.join("Saves");
        let store = mkdir_checked(&parent.join(STORE))?;
        if app.starts_with(&live) || app.starts_with(&store) || parent.starts_with(&backups) {
            return Err("备份目录和存档目录不能相互嵌套".into());
        }
        Ok(Self { app, backups, parent, live, store })
    }
    fn journal(&self) -> PathBuf { self.store.join(JOURNAL) }
    fn generation(&self, kind: &str, token: &str) -> Result<PathBuf> {
        valid_token(token)?;
        if !matches!(kind, "stage" | "rollback" | "aborted" | "commit") {
            return Err("存档事务路径无效".into());
        }
        let path = self.store.join(format!("{kind}-{token}"));
        boundary(&self.parent, &path)?;
        Ok(path)
    }
    fn checked(&self) -> Result<()> {
        check_absolute(&self.app)?;
        check_absolute(&self.parent)?;
        check_absolute(&self.store)?;
        boundary(&self.parent, &self.live)?;
        boundary(&self.app, &self.backups)
    }
}
fn valid_token(token: &str) -> Result<()> {
    if token.len() != 32 || !token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("存档事务标识无效".into());
    }
    Ok(())
}
fn token() -> Result<String> {
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(err)?;
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}
fn valid_backup(name: &str) -> Result<()> {
    let stamp = name.strip_prefix("存档-").ok_or("不是启动器创建的备份")?;
    if stamp.is_empty() || stamp.len() > 20 || !stamp.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err("备份名称无效".into());
    }
    Ok(())
}
fn valid_relative(value: &str) -> Result<()> {
    if value.is_empty() || value.len() > 2048 || value.contains('\\') {
        return Err("备份内部路径无效".into());
    }
    for part in value.split('/') {
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        if part.is_empty() || part == "." || part == ".." || part.ends_with(['.', ' '])
            || part.chars().any(|ch| ch.is_control() || ":<>\"|?*".contains(ch))
            || matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || (stem.len() == 4 && (stem.starts_with("COM") || stem.starts_with("LPT"))
                && matches!(stem.as_bytes()[3], b'1'..=b'9'))
        {
            return Err("备份内部路径不能安全还原".into());
        }
    }
    Ok(())
}
fn exists_dir(path: &Path) -> Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(meta) if is_link(&meta) || !meta.is_dir() => Err("存档路径不是普通目录或包含链接".into()),
        Ok(_) => Ok(true),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(false),
        Err(error) => Err(err(error)),
    }
}
fn source(layout: &Layout, backup: &str) -> Result<(String, PathBuf)> {
    layout.checked()?;
    let supplied = Path::new(backup);
    let name = supplied.file_name().and_then(|name| name.to_str()).ok_or("备份名称无效")?;
    valid_backup(name)?;
    let candidate = layout.backups.join(name);
    boundary(&layout.app, &candidate)?;
    if !exists_dir(&candidate)? { return Err("备份不存在".into()); }
    let expected = fs::canonicalize(&candidate).map_err(err)?;
    if supplied.is_absolute() {
        check_absolute(supplied)?;
        if fs::canonicalize(supplied).map_err(err)? != expected {
            return Err("只能还原此启动器 backups 目录内的备份".into());
        }
    } else if supplied.components().count() != 1 || supplied != Path::new(name) {
        return Err("备份名称不能包含路径".into());
    }
    // Compare canonical to canonical, never extended Windows to unextended paths.
    let backups = fs::canonicalize(&layout.backups).map_err(err)?;
    if expected.parent() != Some(backups.as_path()) {
        return Err("备份路径超出启动器备份目录".into());
    }
    Ok((name.to_string(), expected))
}
fn hash_file(path: &Path, expected_size: u64) -> Result<String> {
    let meta = fs::symlink_metadata(path).map_err(err)?;
    if is_link(&meta) || !meta.is_file() || meta.len() != expected_size || meta.len() > MAX_FILE_BYTES {
        return Err("存档文件在读取前已变化或包含链接".into());
    }
    let mut input = File::open(path).map_err(err)?;
    let mut hash = Sha256::new();
    let mut count = 0u64;
    let mut buffer = [0u8; 65536];
    loop {
        let read = input.read(&mut buffer).map_err(err)?;
        if read == 0 { break; }
        count = count.checked_add(read as u64).ok_or("存档大小溢出")?;
        if count > expected_size { return Err("存档文件在读取时增长".into()); }
        hash.update(&buffer[..read]);
    }
    if count != expected_size || fs::symlink_metadata(path).map_err(err)?.len() != expected_size {
        return Err("存档文件在读取时变化".into());
    }
    Ok(format!("{:x}", hash.finalize()))
}
fn manifest(root: &Path, omit_ancillary: bool, hashes: bool) -> Result<Manifest> {
    if !exists_dir(root)? { return Err("存档目录不存在".into()); }
    let mut result = Manifest { directories: vec![], files: vec![] };
    let mut pending = vec![(root.to_path_buf(), 0usize)];
    let mut nodes = 0usize;
    let mut total = 0u64;
    while let Some((directory, depth)) = pending.pop() {
        boundary(root, &directory)?;
        if depth > MAX_DEPTH { return Err("存档目录层级超过 32".into()); }
        for entry in fs::read_dir(&directory).map_err(err)? {
            let path = entry.map_err(err)?.path();
            nodes += 1;
            if nodes > MAX_NODES { return Err("存档文件和目录数量超过 32768".into()); }
            boundary(root, &path)?;
            let meta = fs::symlink_metadata(&path).map_err(err)?;
            if is_link(&meta) { return Err("备份或存档包含链接或重解析点".into()); }
            let relative = path.strip_prefix(root).map_err(err)?.to_str()
                .ok_or("存档文件名不是有效文本")?.replace('\\', "/");
            valid_relative(&relative)?;
            if omit_ancillary && relative == ANCILLARY {
                if !meta.is_file() { return Err("备份附属设置不是普通文件".into()); }
                continue;
            }
            if meta.is_dir() {
                result.directories.push(relative);
                pending.push((path, depth + 1));
            } else if meta.is_file() {
                total = total.checked_add(meta.len()).ok_or("存档大小溢出")?;
                if meta.len() > MAX_FILE_BYTES || total > MAX_TOTAL_BYTES {
                    return Err("存档超出安全上限（单文件 2 GiB，总计 16 GiB）".into());
                }
                let sha256 = if hashes { hash_file(&path, meta.len())? } else { String::new() };
                result.files.push(SaveFile { path: relative, bytes: meta.len(), sha256 });
            } else {
                return Err("存档包含非普通文件".into());
            }
        }
    }
    result.directories.sort();
    result.files.sort_by(|a, b| a.path.cmp(&b.path));
    validate_manifest(&result, hashes)?;
    Ok(result)
}
fn validate_manifest(value: &Manifest, hashes: bool) -> Result<()> {
    if value.directories.len() + value.files.len() > MAX_NODES {
        return Err("存档清单过大".into());
    }
    let mut names = HashSet::new();
    let mut bytes = 0u64;
    for name in value.directories.iter().map(String::as_str)
        .chain(value.files.iter().map(|file| file.path.as_str()))
    {
        valid_relative(name)?;
        if name.split('/').count() > MAX_DEPTH + 1 || !names.insert(name.to_lowercase()) {
            return Err("存档清单包含重复路径或层级过深".into());
        }
    }
    for file in &value.files {
        bytes = bytes.checked_add(file.bytes).ok_or("存档大小溢出")?;
        if file.bytes > MAX_FILE_BYTES || bytes > MAX_TOTAL_BYTES
            || (hashes && (file.sha256.len() != 64 || !file.sha256.bytes().all(|c| c.is_ascii_hexdigit())))
        { return Err("存档清单内容无效".into()); }
    }
    Ok(())
}
fn write_new(path: &Path, bytes: &[u8]) -> Result<()> {
    let mut file = OpenOptions::new().create_new(true).write(true).open(path).map_err(err)?;
    file.write_all(bytes).map_err(err)?;
    file.sync_all().map_err(err)
}
fn stage_snapshot(source: &Path, stage: &Path, expected: &Manifest) -> Result<()> {
    fs::create_dir(stage).map_err(err)?;
    for name in &expected.directories {
        let dest = stage.join(name);
        boundary(stage, &dest)?;
        fs::create_dir_all(dest).map_err(err)?;
    }
    for entry in &expected.files {
        let from = source.join(&entry.path);
        let to = stage.join(&entry.path);
        boundary(source, &from)?;
        boundary(stage, &to)?;
        if let Some(parent) = to.parent() { fs::create_dir_all(parent).map_err(err)?; }
        // Bounded streaming copy, create_new destinations, and verify the bytes
        // actually written rather than trusting source size/metadata alone.
        let meta = fs::symlink_metadata(&from).map_err(err)?;
        if is_link(&meta) || !meta.is_file() || meta.len() != entry.bytes {
            return Err("备份文件在暂存前已变化".into());
        }
        let input = File::open(&from).map_err(err)?;
        let mut limited = input.take(entry.bytes + 1);
        let mut output = OpenOptions::new().create_new(true).write(true).open(&to).map_err(err)?;
        let copied = std::io::copy(&mut limited, &mut output).map_err(err)?;
        if copied != entry.bytes { return Err("备份文件在暂存时变化".into()); }
        output.sync_all().map_err(err)?;
        if hash_file(&to, entry.bytes)? != entry.sha256 { return Err("暂存存档校验失败".into()); }
    }
    if manifest(stage, false, true)? != *expected || manifest(source, true, true)? != *expected {
        return Err("备份在还原前已变化，请重新选择备份".into());
    }
    Ok(())
}
fn read_bounded(path: &Path, max: u64) -> Result<Vec<u8>> {
    let meta = fs::symlink_metadata(path).map_err(err)?;
    if is_link(&meta) || !meta.is_file() || meta.len() > max { return Err("存档事务文件无效或过大".into()); }
    let mut bytes = vec![];
    File::open(path).map_err(err)?.take(max + 1).read_to_end(&mut bytes).map_err(err)?;
    if bytes.len() as u64 > max { return Err("存档事务文件过大".into()); }
    Ok(bytes)
}
fn rename_checked(layout: &Layout, from: &Path, to: &Path) -> Result<()> {
    layout.checked()?;
    boundary(&layout.parent, from)?;
    boundary(&layout.parent, to)?;
    if !exists_dir(from)? {
        return Err("存档事务源目录不存在；没有覆盖任何目录".into());
    }
    match fs::symlink_metadata(to) {
        Ok(_) => return Err("存档事务目标已存在；没有覆盖任何目录".into()),
        Err(error) if error.kind() == ErrorKind::NotFound => {},
        Err(error) => return Err(err(error)),
    }
    // Both paths descend from the same canonical save parent. No fs::copy
    // fallback is allowed: cross-volume or sharing errors must fail closed.
    fs::rename(from, to).map_err(err)
}
fn journal_exists(layout: &Layout) -> Result<bool> {
    boundary(&layout.parent, &layout.journal())?;
    match fs::symlink_metadata(layout.journal()) {
        Ok(meta) if is_link(&meta) || !meta.is_file() => Err("存档事务日志不是普通文件".into()),
        Ok(_) => Ok(true),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(false),
        Err(error) => Err(err(error)),
    }
}
/// Recover under all three locks. Never delete or merge any Saves generation.
fn recover(layout: &Layout) -> Result<()> {
    layout.checked()?;
    if !journal_exists(layout)? { return Ok(()); }
    let journal: Journal = serde_json::from_slice(&read_bounded(&layout.journal(), MAX_JOURNAL_BYTES)?)
        .map_err(|error| format!("存档还原日志损坏，已阻止启动：{error}"))?;
    if journal.schema != 1 { return Err("存档还原日志版本未知，已阻止启动".into()); }
    valid_token(&journal.token)?;
    valid_backup(&journal.backup)?;
    validate_manifest(&journal.manifest, true)?;
    let stage = layout.generation("stage", &journal.token)?;
    let rollback = layout.generation("rollback", &journal.token)?;
    let aborted = layout.generation("aborted", &journal.token)?;
    let commit = layout.generation("commit", &journal.token)?;
    let staged = exists_dir(&stage)?;
    let old = exists_dir(&rollback)?;
    let live = exists_dir(&layout.live)?;
    let discarded = exists_dir(&aborted)?;
    let committed = match fs::symlink_metadata(&commit) {
        Ok(meta) if is_link(&meta) || !meta.is_file() => return Err("存档提交标记无效".into()),
        Ok(_) => read_bounded(&commit, 128)? == journal.token.as_bytes(),
        Err(error) if error.kind() == ErrorKind::NotFound => false,
        Err(error) => return Err(err(error)),
    };
    if committed {
        if staged || !live || old != journal.had_live || discarded
            || manifest(&layout.live, false, true)? != journal.manifest
        { return Err("已提交的存档事务状态异常，已阻止启动；所有目录均保留".into()); }
    } else if journal.had_live {
        match (live, staged, old, discarded) {
            (true, true, false, false) => {}, // journal synced, first rename not done
            (false, true, true, false) => rename_checked(layout, &rollback, &layout.live)?,
            (true, false, true, false) => {
                // Keep even the uncommitted restored generation, never remove it.
                rename_checked(layout, &layout.live, &aborted)?;
                rename_checked(layout, &rollback, &layout.live)?;
            }
            (false, false, true, true) => rename_checked(layout, &rollback, &layout.live)?,
            (true, false, false, true) => {}, // interrupted recovery already restored old
            _ => return Err("存档还原状态不明确，已阻止启动；请保留所有目录".into()),
        }
    } else {
        match (live, staged, old, discarded) {
            (false, true, false, false) => {},
            (true, false, false, false) => rename_checked(layout, &layout.live, &aborted)?,
            (false, false, false, true) => {},
            _ => return Err("存档还原状态不明确，已阻止启动；请保留所有目录".into()),
        }
    }
    // Removing only the journal is idempotent. Token-specific commit markers and
    // generations remain as provenance; no unsafe recursive cleanup is needed.
    fs::remove_file(layout.journal()).map_err(err)
}
fn prepare(layout: &Layout, backup: &str) -> Result<(Journal, PathBuf)> {
    recover(layout)?;
    let (name, source) = source(layout, backup)?;
    let expected = manifest(&source, true, true)?;
    if expected.files.is_empty() { return Err("备份不含实际存档文件".into()); }
    let had_live = exists_dir(&layout.live)?;
    if had_live { manifest(&layout.live, false, false)?; }
    let journal = Journal { schema: 1, token: token()?, backup: name, had_live, manifest: expected };
    let stage = layout.generation("stage", &journal.token)?;
    stage_snapshot(&source, &stage, &journal.manifest)?;
    let bytes = serde_json::to_vec(&journal).map_err(err)?;
    if bytes.len() as u64 > MAX_JOURNAL_BYTES { return Err("存档事务日志超过安全上限".into()); }
    // An interrupted journal write has not renamed Saves; a malformed journal
    // intentionally blocks launch instead of guessing which generation is live.
    layout.checked()?;
    write_new(&layout.journal(), &bytes)?;
    Ok((journal, stage))
}
fn install(layout: &Layout, journal: &Journal, stage: &Path) -> Result<RestoreSaveReport> {
    let rollback = layout.generation("rollback", &journal.token)?;
    if journal.had_live { rename_checked(layout, &layout.live, &rollback)?; }
    rename_checked(layout, stage, &layout.live)?;
    // Only fully verified staged bytes are committed. Recheck after the rename
    // to fail safely if an external process changed the staged/new directory.
    if manifest(&layout.live, false, true)? != journal.manifest { return Err("安装后的存档校验失败".into()); }
    write_new(&layout.generation("commit", &journal.token)?, journal.token.as_bytes())?;
    recover(layout)?;
    Ok(RestoreSaveReport {
        backup: journal.backup.clone(),
        files: journal.manifest.files.len(),
        bytes: journal.manifest.bytes(),
        rollback_generation: journal.had_live.then(|| rollback.to_string_lossy().into_owned()),
    })
}
fn restore_at_with_preflight(
    layout: &Layout,
    backup: &str,
    preflight: impl FnOnce() -> Result<()>,
) -> Result<RestoreSaveReport> {
    let (journal, stage) = prepare(layout, backup)?;
    // Staging/hash verification may take minutes: recheck stopped/disconnected
    // immediately before the FIRST rename, not only at IPC admission.
    let installed = preflight().and_then(|()| install(layout, &journal, &stage));
    match installed {
        Ok(report) => Ok(report),
        Err(error) => match recover(layout) {
            Ok(()) => Err(format!("存档还原未完成：{error}；事务已恢复，原有存档代际已保留")),
            Err(recovery) => Err(format!("存档还原未完成：{error}；恢复检查失败：{recovery}。禁止启动游戏，请保留所有存档目录")),
        },
    }
}
#[cfg(test)]
fn restore_at(layout: &Layout, backup: &str) -> Result<RestoreSaveReport> {
    restore_at_with_preflight(layout, backup, || Ok(()))
}

/// Launch hook: caller already holds mutation + game-directory locks, has checked
/// require_stopped, and MUST propagate errors. This function acquires the app
/// save-backup lock. Do not call it while already holding that lock.
pub(crate) fn recover_before_launch(app: &tauri::AppHandle) -> Result<()> {
    let _backup_lock = app_backup_lock(app)?;
    recover(&Layout::new(&app_root(app)?, &crate::service::saves_root()?)?)
}

#[tauri::command]
pub async fn restore_save_backup(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, NetworkState>,
    backup: String,
) -> Result<RestoreSaveReport> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _mutation = state.mutation.lock().map_err(err)?;
        require_stopped(&state)?;
        require_disconnected(&network)?;
        let root = game_root(&load_settings(&app)?)?;
        let _directory_lock = lock(&root)?;
        let _backup_lock = app_backup_lock(&app)?;
        require_stopped(&state)?;
        require_disconnected(&network)?;
        let layout = Layout::new(&app_root(&app)?, &crate::service::saves_root()?)?;
        restore_at_with_preflight(&layout, &backup, || {
            require_stopped(&state)?;
            require_disconnected(&network)
        })
    }).await.map_err(err)?
}

/// Returns actual completed launcher backup folders, never .pending directories.
/// Listing bounds metadata traversal but does not hash multi-GiB saves; restoration
/// builds and verifies a fresh SHA-256 manifest immediately before its transaction.
#[tauri::command]
pub async fn list_save_backups(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
) -> Result<Vec<SaveBackup>> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _mutation = state.mutation.lock().map_err(err)?;
        let root = game_root(&load_settings(&app)?)?;
        let _directory_lock = lock(&root)?;
        let _backup_lock = app_backup_lock(&app)?;
        let app = fs::canonicalize(app_root(&app)?).map_err(err)?;
        let backups = app.join("backups");
        boundary(&app, &backups)?;
        if !exists_dir(&backups)? { return Ok(vec![]); }
        let mut result = vec![];
        for (index, entry) in fs::read_dir(&backups).map_err(err)?.enumerate() {
            if index >= 4096 { return Err("备份目录项目过多，请先手动归档".into()); }
            let entry = entry.map_err(err)?;
            let Some(name) = entry.file_name().to_str().map(str::to_string) else { continue; };
            if valid_backup(&name).is_err() { continue; }
            boundary(&app, &entry.path())?;
            let value = manifest(&entry.path(), true, false)?;
            if value.files.is_empty() { continue; }
            let mut towns = Vec::new();
            let mut seen = HashSet::new();
            for file in &value.files {
                let top = file.path.split('/').next().unwrap_or("").to_string();
                if top.is_empty() || !seen.insert(top.clone()) {
                    continue;
                }
                let mut label = top.clone();
                if file.path.to_ascii_lowercase().ends_with(".fs") {
                    if let Ok(raw) = fs::read(entry.path().join(&file.path)) {
                        if let Some((community, slot)) =
                            crate::service::parse_flotsam_save_bytes(&raw, &file.path, 0, file.bytes)
                        {
                            let cname = community.unwrap_or(top);
                            label = match slot.day {
                                Some(d) => format!("{cname}（第 {d} 天）"),
                                None => cname,
                            };
                        }
                    }
                }
                towns.push(label);
            }
            result.push(SaveBackup { backup: name, files: value.files.len(), bytes: value.bytes(), towns });
            if result.len() > 256 { return Err("备份数量超过 256，请先手动归档".into()); }
        }
        result.sort_by(|a, b| {
            let stamp = |name: &str| name.trim_start_matches("存档-").parse::<u128>().unwrap_or(0);
            stamp(&b.backup).cmp(&stamp(&a.backup))
        });
        Ok(result)
    }).await.map_err(err)?
}

#[cfg(test)]
#[path = "save_restore_tests.rs"]
mod tests;
