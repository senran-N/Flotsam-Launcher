//! Mod profiles (模组组合): capture the enabled set, keep a per-profile copy of
//! BepInEx configuration, apply profile + configuration as one recoverable
//! transaction, and export/import bundles that include the configuration.
//!
//! Transaction model
//! -----------------
//! A switch first stages every configuration file it is going to write and keeps
//! the previous bytes as a backup. A journal is then written, followed by the
//! configuration writes and the `*.dll` / `*.dll.disabled` renames. A crash at any
//! point leaves the journal behind and `packages::recover` (which calls
//! [`recover_configuration`]) restores the exact pre-switch state. Files that the
//! profile does not mention are never touched, and only files created by a
//! transaction are ever deleted during rollback.
use crate::{
    mods::{self, MetadataCache, ModEntry},
    packages::{boundary, is_link, lock_path, manager_dir},
    service::{app_root, err, game_root, load_settings, require_stopped, LauncherState, Result},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

const MAX_PROFILES: usize = 64;
const MAX_PROFILE_MODS: usize = 2048;
const MAX_CONFIG_FILES: usize = 64;
const MAX_CONFIG_BYTES: u64 = 512 * 1024;
const MAX_JOURNAL_BYTES: u64 = 1024 * 1024;
const MAX_IMPORT_BYTES: usize = 8 * 1024 * 1024;
const BACKUP_KEEP: usize = 10;
const JOURNAL: &str = "profile-transaction.json";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileMod {
    /// Path relative to BepInEx/plugins, without the `.disabled` suffix.
    pub file: String,
    pub name: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub plugins: Vec<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModProfile {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub note: String,
    pub created_at: u64,
    pub updated_at: u64,
    pub mods: Vec<ProfileMod>,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Store {
    #[serde(default)]
    schema: u32,
    #[serde(default)]
    profiles: Vec<ModProfile>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplyReport {
    pub enabled: Vec<String>,
    pub disabled: Vec<String>,
    /// Present in the profile but absent locally.
    pub missing: Vec<String>,
    /// Installed but not part of the profile (disabled by the apply).
    pub extras: Vec<String>,
    pub unchanged: usize,
    /// Configuration files restored from the profile.
    pub configuration: usize,
}
/// One rename performed while switching profiles.
#[derive(Clone, Debug, Serialize, Deserialize)]
struct Rename {
    /// Relative to BepInEx/plugins, forward slashes.
    from: String,
    to: String,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
struct ConfigChange {
    /// Plain file name inside BepInEx/config.
    name: String,
    /// Whether the file existed before the transaction (and was backed up).
    existed: bool,
}
#[derive(Serialize, Deserialize)]
struct Journal {
    schema: u32,
    token: String,
    id: String,
    changes: Vec<ConfigChange>,
    renames: Vec<Rename>,
}

fn store_path(app: &tauri::AppHandle) -> Result<PathBuf> {
    Ok(app_root(app)?.join("mod-profiles.json"))
}
fn lock_store(app: &tauri::AppHandle) -> Result<File> {
    lock_path(&app_root(app)?.join("mod-profiles.lock"))
}
/// Directory holding one profile's captured configuration (`<id>/<plugin>.cfg`).
fn profile_config_dir(app: &tauri::AppHandle, id: &str) -> Result<PathBuf> {
    valid_id(id)?;
    Ok(app_root(app)?.join("profile-config").join(id))
}
fn read_store_file(path: &Path) -> Result<Store> {
    if !path.exists() {
        return Ok(Store {
            schema: 1,
            profiles: vec![],
        });
    }
    let meta = fs::symlink_metadata(path).map_err(err)?;
    if is_link(&meta) || meta.len() > 4 * 1024 * 1024 {
        return Err("模组组合文件过大或包含链接，请检查".into());
    }
    let mut store: Store = serde_json::from_slice(&fs::read(path).map_err(err)?)
        .map_err(|_| "模组组合文件无法读取".to_string())?;
    store.schema = 1;
    store.profiles.retain(|p| valid_profile(p).is_ok());
    if store.profiles.len() > MAX_PROFILES {
        store.profiles.truncate(MAX_PROFILES);
    }
    Ok(store)
}
fn read_store(app: &tauri::AppHandle) -> Result<Store> {
    read_store_file(&store_path(app)?)
}
fn write_store_file(path: &Path, store: &Store) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(err)?;
    }
    write_atomic(path, &serde_json::to_vec_pretty(store).map_err(err)?)
}
fn write_store(app: &tauri::AppHandle, store: &Store) -> Result<()> {
    write_store_file(&store_path(app)?, store)
}
fn valid_id(value: &str) -> Result<()> {
    if value.len() != 24 || !value.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err("组合标识无效".into());
    }
    Ok(())
}
fn valid_profile(profile: &ModProfile) -> Result<()> {
    valid_id(&profile.id)?;
    if profile.name.trim().is_empty() || profile.name.chars().count() > 60 {
        return Err("组合名称需为 1–60 个字符".into());
    }
    if profile.note.chars().count() > 500 || profile.name.chars().any(char::is_control) {
        return Err("组合信息无效".into());
    }
    if profile.mods.len() > MAX_PROFILE_MODS {
        return Err("组合包含的模组过多".into());
    }
    let mut seen = HashSet::new();
    for entry in &profile.mods {
        safe_relative(&entry.file)?;
        if !seen.insert(normalized(&entry.file)) {
            return Err("组合包含重复模组".into());
        }
    }
    Ok(())
}
fn safe_relative(name: &str) -> Result<()> {
    if name.is_empty()
        || name.starts_with('/')
        || name.contains('\\')
        || name.contains(':')
        || name.split('/').any(|p| p.is_empty() || p == "." || p == "..")
        || name.chars().any(char::is_control)
    {
        return Err("组合中的模组路径无效".into());
    }
    Ok(())
}
fn normalized(name: &str) -> String {
    let name = name.replace('\\', "/").to_lowercase();
    name.strip_suffix(".disabled").unwrap_or(&name).to_string()
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
fn new_id() -> Result<String> {
    let mut bytes = [0u8; 12];
    getrandom::fill(&mut bytes).map_err(err)?;
    Ok(bytes.iter().map(|b| format!("{b:02x}")).collect())
}
fn token() -> Result<String> {
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(err)?;
    Ok(bytes.iter().map(|b| format!("{b:02x}")).collect())
}
fn valid_token(value: &str) -> Result<()> {
    if value.len() != 32
        || !value
            .bytes()
            .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
    {
        return Err("组合事务标识无效".into());
    }
    Ok(())
}
fn capture(mods: &[ModEntry], include_disabled: bool) -> Vec<ProfileMod> {
    mods.iter()
        .filter(|entry| include_disabled || entry.enabled)
        .map(|entry| ProfileMod {
            file: normalized(&entry.file),
            name: entry.name.clone(),
            version: entry
                .metadata
                .plugins
                .first()
                .map(|p| p.version.clone())
                .or_else(|| entry.metadata.assembly_version.clone()),
            plugins: entry.metadata.plugins.iter().map(|p| p.id.clone()).collect(),
        })
        .collect()
}
fn scan(root: &Path) -> Result<Vec<ModEntry>> {
    let mut cache = MetadataCache::default();
    mods::scan(root, &mut cache)
}
fn plugin_dir(root: &Path) -> PathBuf {
    root.join("BepInEx/plugins")
}
fn config_dir(root: &Path) -> PathBuf {
    root.join("BepInEx/config")
}
/// Compute the rename operations that move the current state to `profile`.
/// Operations carry `/`-separated paths relative to `BepInEx/plugins`.
fn plan(mods: &[ModEntry], profile: &ModProfile) -> (Vec<(String, bool)>, Vec<String>, Vec<String>) {
    let wanted: HashSet<String> = profile.mods.iter().map(|m| normalized(&m.file)).collect();
    let mut operations = vec![];
    let mut extras = vec![];
    for entry in mods {
        let want = wanted.contains(&normalized(&entry.file));
        if !want {
            extras.push(entry.name.clone());
        }
        if want != entry.enabled {
            operations.push((slash(&entry.file), want));
        }
    }
    let have: HashSet<String> = mods.iter().map(|m| normalized(&m.file)).collect();
    let missing = profile
        .mods
        .iter()
        .filter(|m| !have.contains(&normalized(&m.file)))
        .map(|m| m.name.clone())
        .collect();
    (operations, extras, missing)
}
fn slash(name: &str) -> String {
    name.replace('\\', "/")
}
/// Reject a profile that would introduce new blocking errors (for example a
/// missing dependency). Errors that already exist are preserved so the user can
/// still apply a profile that repairs part of the set.
fn preflight(mods: &mut [ModEntry], operations: &[(String, bool)], root: &Path) -> Result<()> {
    let existing: HashSet<(String, String, String)> = mods
        .iter()
        .flat_map(|m| {
            m.issues
                .iter()
                .filter(|i| i.severity == "error")
                .map(|i| (m.file.clone(), i.code.clone(), i.message.clone()))
        })
        .collect();
    for (file, enabled) in operations {
        let target = normalized(file);
        if let Some(entry) = mods.iter_mut().find(|m| normalized(&m.file) == target) {
            entry.enabled = *enabled;
        }
    }
    mods::diagnose(mods, root.join("BepInEx/core/BepInEx.dll").is_file());
    for entry in mods.iter() {
        for issue in entry.issues.iter().filter(|i| i.severity == "error") {
            if !existing.contains(&(entry.file.clone(), issue.code.clone(), issue.message.clone())) {
                return Err(format!("组合会引入问题：{} · {}", entry.name, issue.message));
            }
        }
    }
    Ok(())
}

// ---------------------------------------------------------------- filesystem

fn make_dir(root: &Path, path: &Path) -> Result<()> {
    boundary(root, path)?;
    fs::create_dir_all(path).map_err(err)
}
fn copy_sync(from: &Path, to: &Path) -> Result<()> {
    if let Some(parent) = to.parent() {
        fs::create_dir_all(parent).map_err(err)?;
    }
    fs::copy(from, to).map_err(err)?;
    OpenOptions::new()
        .write(true)
        .open(to)
        .map_err(err)?
        .sync_all()
        .map_err(err)
}
fn write_atomic(path: &Path, bytes: &[u8]) -> Result<()> {
    let name = path.file_name().ok_or("路径无效")?.to_string_lossy();
    let temporary = path.with_file_name(format!("{name}.pending"));
    let _ = fs::remove_file(&temporary);
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(err)?;
    let result = (|| {
        file.write_all(bytes).map_err(err)?;
        file.sync_all().map_err(err)?;
        drop(file);
        fs::rename(&temporary, path).map_err(err)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}
/// Remove a directory tree that this launcher created. Refuses to follow links.
fn remove_owned_tree(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(meta) if is_link(&meta) || !meta.is_dir() => return Err("组合目录不是普通目录，未删除".into()),
        Ok(_) => {},
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(err(error)),
    }
    fn check(path: &Path, depth: usize) -> Result<()> {
        if depth > 8 {
            return Err("组合目录层级过深".into());
        }
        for entry in fs::read_dir(path).map_err(err)? {
            let entry = entry.map_err(err)?;
            let meta = fs::symlink_metadata(entry.path()).map_err(err)?;
            if is_link(&meta) {
                return Err("组合目录包含链接，未删除".into());
            }
            if meta.is_dir() {
                check(&entry.path(), depth + 1)?;
            }
        }
        Ok(())
    }
    check(path, 0)?;
    fs::remove_dir_all(path).map_err(err)
}
fn config_entry_name(name: &str) -> Result<()> {
    if !name.to_ascii_lowercase().ends_with(".cfg") || name.len() > 200 {
        return Err("组合配置文件名无效".into());
    }
    crate::service::safe_name(name)
}
fn config_file_name(plugin_id: &str) -> Option<String> {
    if plugin_id.is_empty() || plugin_id.len() > 180 {
        return None;
    }
    let name = format!("{plugin_id}.cfg");
    crate::service::safe_name(&name).ok()?;
    Some(name)
}
/// Read the captured configuration files of a profile, sorted by name.
fn configuration_sources(dir: &Path) -> Result<Vec<(String, PathBuf)>> {
    if !dir.exists() {
        return Ok(vec![]);
    }
    let meta = fs::symlink_metadata(dir).map_err(err)?;
    if is_link(&meta) || !meta.is_dir() {
        return Err("组合配置目录无效".into());
    }
    let mut result = vec![];
    for entry in fs::read_dir(dir).map_err(err)? {
        let entry = entry.map_err(err)?;
        let meta = fs::symlink_metadata(entry.path()).map_err(err)?;
        if is_link(&meta) {
            return Err("组合配置包含链接，已停止切换".into());
        }
        if !meta.is_file() {
            return Err("组合配置目录包含子目录，已停止切换".into());
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        config_entry_name(&name)?;
        if meta.len() > MAX_CONFIG_BYTES {
            return Err(format!("{name} 超过 512 KB，未切换"));
        }
        result.push((name, entry.path()));
    }
    if result.len() > MAX_CONFIG_FILES {
        return Err("组合配置文件过多".into());
    }
    result.sort_by(|a, b| a.0.cmp(&b.0));
    Ok(result)
}
/// Snapshot the current `BepInEx/config/<plugin>.cfg` files of a profile.
fn capture_configuration(app: &tauri::AppHandle, root: &Path, profile: &ModProfile) -> Result<usize> {
    let dest = profile_config_dir(app, &profile.id)?;
    remove_owned_tree(&dest)?;
    let config = config_dir(root);
    boundary(root, &config)?;
    let mut seen = HashSet::new();
    let mut files = vec![];
    for entry in &profile.mods {
        for plugin in &entry.plugins {
            let Some(name) = config_file_name(plugin) else {
                continue;
            };
            if !seen.insert(name.to_lowercase()) {
                continue;
            }
            let source = config.join(&name);
            match fs::symlink_metadata(&source) {
                Ok(meta) if is_link(&meta) => {
                    return Err(format!("{name} 是链接，未保存配置"))
                }
                Ok(meta) if meta.is_file() => {
                    if meta.len() > MAX_CONFIG_BYTES {
                        continue;
                    }
                    files.push((name, source));
                }
                Ok(_) => {}
                Err(e) if e.kind() == ErrorKind::NotFound => {}
                Err(e) => return Err(err(e)),
            }
        }
    }
    if files.len() > MAX_CONFIG_FILES {
        return Err("组合配置文件过多".into());
    }
    if files.is_empty() {
        return Ok(0);
    }
    fs::create_dir_all(&dest).map_err(err)?;
    for (name, source) in &files {
        copy_sync(source, &dest.join(name))?;
    }
    Ok(files.len())
}

// ------------------------------------------------------------------ renames

fn plan_renames(root: &Path, operations: &[(String, bool)]) -> Result<Vec<Rename>> {
    if operations.is_empty() {
        return Ok(vec![]);
    }
    // Do not canonicalize: on Windows that yields a `\\?\` verbatim prefix which
    // no longer starts with the launcher's game root, so `boundary` would reject
    // every path as out of bounds.
    let plugins = plugin_dir(root);
    boundary(root, &plugins)?;
    let meta = fs::symlink_metadata(&plugins).map_err(|_| "模组目录不存在".to_string())?;
    if is_link(&meta) || !meta.is_dir() {
        return Err("模组目录无效，请检查文件位置".into());
    }
    let mut result = vec![];
    let mut seen = HashSet::new();
    for (file, want) in operations {
        safe_relative(file)?;
        let relative = Path::new(file);
        let source = plugins.join(relative);
        boundary(root, &source)?;
        let meta = fs::symlink_metadata(&source).map_err(|_| "模组状态已变化，请刷新".to_string())?;
        if is_link(&meta) || !meta.is_file() {
            return Err("模组文件无效，请刷新".into());
        }
        let name = source
            .file_name()
            .ok_or("模组文件名无效")?
            .to_string_lossy()
            .into_owned();
        let destination = if *want {
            let Some(stripped) = name.strip_suffix(".disabled") else {
                return Err("模组状态已变化，请刷新".into());
            };
            stripped.to_string()
        } else {
            if !name.to_ascii_lowercase().ends_with(".dll") {
                return Err("模组状态已变化，请刷新".into());
            }
            format!("{name}.disabled")
        };
        let target = source.with_file_name(&destination);
        boundary(root, &target)?;
        if fs::symlink_metadata(&target).is_ok() {
            return Err(format!("目标文件已存在：{destination}，未切换"));
        }
        let from = relative.to_string_lossy().replace('\\', "/");
        if !seen.insert(from.to_lowercase()) {
            return Err("组合包含重复模组".into());
        }
        let to = Path::new(&from)
            .with_file_name(&destination)
            .to_string_lossy()
            .replace('\\', "/");
        result.push(Rename { from, to });
    }
    Ok(result)
}
fn rename_labels(operations: &[(String, bool)]) -> (Vec<String>, Vec<String>) {
    let mut enabled = vec![];
    let mut disabled = vec![];
    for (file, want) in operations {
        let name = Path::new(file)
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned();
        if *want {
            if let Some(stripped) = name.strip_suffix(".disabled") {
                enabled.push(stripped.to_string());
            }
        } else {
            disabled.push(name);
        }
    }
    (enabled, disabled)
}

// -------------------------------------------------------------- transaction

fn journal_path(root: &Path) -> PathBuf {
    manager_dir(root).join(JOURNAL)
}
fn restore_configuration(root: &Path, tx: &Path, changes: &[ConfigChange]) -> Result<()> {
    let config = config_dir(root);
    for change in changes.iter().rev() {
        let target = config.join(&change.name);
        boundary(root, &target)?;
        if change.existed {
            let original = tx.join("original").join(&change.name);
            boundary(root, &original)?;
            if !original.is_file() {
                return Err(format!("{} 的配置备份缺失", change.name));
            }
            copy_sync(&original, &target)?;
        } else if fs::symlink_metadata(&target).is_ok() {
            let meta = fs::symlink_metadata(&target).map_err(err)?;
            if is_link(&meta) || !meta.is_file() {
                return Err(format!("{} 被其他程序替换，未删除", change.name));
            }
            fs::remove_file(&target).map_err(err)?;
        }
    }
    Ok(())
}
fn undo_renames(root: &Path, renames: &[Rename], count: usize) -> Result<()> {
    let plugins = plugin_dir(root);
    for rename in renames.iter().take(count).rev() {
        let from = plugins.join(&rename.from);
        let to = plugins.join(&rename.to);
        boundary(root, &from)?;
        boundary(root, &to)?;
        if fs::symlink_metadata(&to).is_ok() {
            if fs::symlink_metadata(&from).is_ok() {
                return Err(format!("{} 与 {} 同时存在，未覆盖", rename.from, rename.to));
            }
            fs::rename(&to, &from).map_err(err)?;
        }
    }
    Ok(())
}
fn prune_backups(root: &Path) {
    let dir = manager_dir(root).join("config-backup");
    let Ok(entries) = fs::read_dir(&dir) else {
        return;
    };
    let mut items: Vec<_> = entries
        .filter_map(|e| e.ok())
        .filter(|e| valid_token(&e.file_name().to_string_lossy()).is_ok())
        .map(|e| {
            let modified = e
                .metadata()
                .ok()
                .and_then(|m| m.modified().ok())
                .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            (modified, e.path())
        })
        .collect();
    items.sort_by_key(|(modified, _)| std::cmp::Reverse(*modified));
    for (_, path) in items.into_iter().skip(BACKUP_KEEP) {
        let _ = remove_owned_tree(&path);
    }
}
/// Apply configuration restore + enable/disable renames as one transaction.
fn apply_transaction(
    root: &Path,
    profile_id: &str,
    sources: &[(String, PathBuf)],
    operations: &[(String, bool)],
    check: &impl Fn() -> Result<()>,
) -> Result<(Vec<String>, Vec<String>, usize)> {
    valid_id(profile_id)?;
    let renames = plan_renames(root, operations)?;
    if sources.is_empty() && renames.is_empty() {
        return Ok((vec![], vec![], 0));
    }
    let token = token()?;
    let tx = manager_dir(root).join("profile-tx").join(&token);
    let staged = tx.join("staged");
    let original = tx.join("original");
    let config = config_dir(root);
    let mut changes: Vec<ConfigChange> = vec![];
    let staged_ok = (|| -> Result<()> {
        make_dir(root, &staged)?;
        make_dir(root, &original)?;
        for (name, source) in sources {
            let target = config.join(name);
            boundary(root, &target)?;
            let existed = match fs::symlink_metadata(&target) {
                Ok(meta) if is_link(&meta) => {
                    return Err(format!("配置 {name} 是链接，已停止切换"));
                }
                Ok(meta) if meta.is_file() => true,
                Ok(_) => return Err(format!("配置 {name} 不是普通文件，已停止切换")),
                Err(e) if e.kind() == ErrorKind::NotFound => false,
                Err(e) => return Err(err(e)),
            };
            copy_sync(source, &staged.join(name))?;
            if existed {
                copy_sync(&target, &original.join(name))?;
            }
            changes.push(ConfigChange {
                name: name.clone(),
                existed,
            });
        }
        Ok(())
    })();
    if let Err(error) = staged_ok {
        let _ = remove_owned_tree(&tx);
        return Err(error);
    }
    let journal = Journal {
        schema: 2,
        token: token.clone(),
        id: profile_id.to_string(),
        changes: changes.clone(),
        renames: renames.clone(),
    };
    if let Err(error) = check() {
        let _ = remove_owned_tree(&tx);
        return Err(error);
    }
    if let Err(error) = write_atomic(
        &journal_path(root),
        &serde_json::to_vec_pretty(&journal).map_err(err)?,
    ) {
        let _ = remove_owned_tree(&tx);
        return Err(error);
    }
    let mut renamed = 0usize;
    let result = (|| -> Result<()> {
        if !changes.is_empty() {
            make_dir(root, &config)?;
        }
        for change in &changes {
            check()?;
            copy_sync(&staged.join(&change.name), &config.join(&change.name))?;
        }
        let plugins = plugin_dir(root);
        for rename in &renames {
            check()?;
            fs::rename(plugins.join(&rename.from), plugins.join(&rename.to)).map_err(err)?;
            renamed += 1;
        }
        Ok(())
    })();
    if let Err(error) = result {
        let mut failures = vec![];
        if let Err(e) = undo_renames(root, &renames, renamed) {
            failures.push(e);
        }
        if let Err(e) = restore_configuration(root, &tx, &changes) {
            failures.push(e);
        }
        if failures.is_empty() {
            let _ = fs::remove_file(journal_path(root));
            let _ = remove_owned_tree(&tx);
            return Err(format!("切换组合失败，已还原：{error}"));
        }
        return Err(format!(
            "切换组合失败：{error}；还原失败：{}；已保留恢复记录，请在启动器下次启动时自动恢复",
            failures.join("；")
        ));
    }
    if fs::remove_file(journal_path(root)).is_err() {
        // The journal is the only recoverable record; keep the staging tree too.
        return Err("组合已切换，但恢复记录未能删除，请刷新后重试".into());
    }
    if changes.iter().any(|c| c.existed) {
        // Keep the previous configuration for manual rollback. The backup
        // directory must not exist yet because the tree is renamed into place.
        let backup = manager_dir(root).join("config-backup").join(&token);
        if let Some(parent) = backup.parent() {
            if make_dir(root, parent).is_ok() && fs::rename(&original, &backup).is_err() {
                let _ = remove_owned_tree(&original);
            }
        }
        prune_backups(root);
    }
    remove_owned_tree(&tx)?;
    let (enabled, disabled) = rename_labels(operations);
    Ok((enabled, disabled, changes.len()))
}
/// Complete or roll back an interrupted profile switch. Must be called while the
/// game lock (`packages::lock`) is held; `packages::recover` calls this.
pub(crate) fn recover_configuration(root: &Path) -> Result<()> {
    let path = journal_path(root);
    boundary(root, &path)?;
    if !path.exists() {
        return Ok(());
    }
    let meta = fs::symlink_metadata(&path).map_err(err)?;
    if is_link(&meta) || meta.len() > MAX_JOURNAL_BYTES {
        return Err("组合恢复记录无效".into());
    }
    let journal: Journal =
        serde_json::from_slice(&fs::read(&path).map_err(err)?).map_err(err)?;
    if journal.schema > 2 {
        return Err("组合恢复记录版本不受支持".into());
    }
    valid_token(&journal.token)?;
    valid_id(&journal.id)?;
    if journal.changes.len() > MAX_CONFIG_FILES * 4 || journal.renames.len() > MAX_PROFILE_MODS {
        return Err("组合恢复记录无效".into());
    }
    let tx = manager_dir(root).join("profile-tx").join(&journal.token);
    boundary(root, &tx)?;
    // Validate every backup path before changing even the first plugin name.
    for change in &journal.changes {
        config_entry_name(&change.name)?;
        let target = config_dir(root).join(&change.name);
        boundary(root, &target)?;
        if let Ok(meta) = fs::symlink_metadata(&target) {
            if is_link(&meta) || !meta.is_file() { return Err("组合配置目标被外部替换，未恢复".into()); }
        }
        if change.existed {
            let original = tx.join("original").join(&change.name);
            boundary(root, &original)?;
            let meta = fs::symlink_metadata(&original).map_err(err)?;
            if is_link(&meta) || !meta.is_file() || meta.len() > MAX_CONFIG_BYTES {
                return Err(format!("{} 的配置备份无效", change.name));
            }
        }
    }
    // Validate all names/states before the first rename so one malformed trailing
    // entry cannot leave a new partially-restored plugin selection.
    let plugins = plugin_dir(root);
    for rename in &journal.renames {
        safe_relative(&rename.from)?;
        safe_relative(&rename.to)?;
        let from = plugins.join(&rename.from);
        let to = plugins.join(&rename.to);
        boundary(root, &from)?;
        boundary(root, &to)?;
        if from.exists() == to.exists() { return Err("组合恢复文件状态不明确，未执行重命名".into()); }
    }
    // Undo only renames that are in the half-applied state.
    for rename in journal.renames.iter().rev() {
        safe_relative(&rename.from)?;
        safe_relative(&rename.to)?;
        let from = plugins.join(&rename.from);
        let to = plugins.join(&rename.to);
        boundary(root, &from)?;
        boundary(root, &to)?;
        let from_exists = fs::symlink_metadata(&from).is_ok();
        let to_exists = fs::symlink_metadata(&to).is_ok();
        match (from_exists, to_exists) {
            (false, true) => fs::rename(&to, &from).map_err(err)?,
            (true, false) => {}
            (true, true) => return Err(format!("{} 与 {} 同时存在，未自动处理", rename.from, rename.to)),
            (false, false) => return Err(format!("恢复记录缺少 {}", rename.from)),
        }
    }
    let config = config_dir(root);
    for change in journal.changes.iter().rev() {
        config_entry_name(&change.name)?;
        let target = config.join(&change.name);
        boundary(root, &target)?;
        if change.existed {
            let original = tx.join("original").join(&change.name);
            let meta = fs::symlink_metadata(&original)
                .map_err(|_| format!("{} 的配置备份缺失", change.name))?;
            if is_link(&meta) || !meta.is_file() {
                return Err(format!("{} 的配置备份无效", change.name));
            }
            copy_sync(&original, &target)?;
        } else if let Ok(meta) = fs::symlink_metadata(&target) {
            if is_link(&meta) || !meta.is_file() {
                return Err(format!("{} 被其他程序替换，未删除", change.name));
            }
            fs::remove_file(&target).map_err(err)?;
        }
    }
    fs::remove_file(&path).map_err(err)?;
    remove_owned_tree(&tx)?;
    Ok(())
}

// ------------------------------------------------------------ export/import

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn from_hex(value: &str) -> Result<Vec<u8>> {
    if !value.len().is_multiple_of(2) || value.len() > MAX_CONFIG_BYTES as usize * 2 {
        return Err("组合配置内容无效".into());
    }
    let mut bytes = Vec::with_capacity(value.len() / 2);
    let raw = value.as_bytes();
    for pair in raw.chunks(2) {
        let text = std::str::from_utf8(pair).map_err(|_| "组合配置内容无效".to_string())?;
        let byte = u8::from_str_radix(text, 16).map_err(|_| "组合配置内容无效".to_string())?;
        bytes.push(byte);
    }
    Ok(bytes)
}
fn build_export(profile: &ModProfile, sources: &[(String, PathBuf)]) -> Result<serde_json::Value> {
    let mut entries = vec![];
    let mut total = 0usize;
    for (name, path) in sources {
        config_entry_name(name)?;
        let meta = fs::symlink_metadata(path).map_err(err)?;
        if is_link(&meta) || !meta.is_file() || meta.len() > MAX_CONFIG_BYTES {
            return Err(format!("{name} 无法导出"));
        }
        let bytes = fs::read(path).map_err(err)?;
        total += bytes.len();
        if total > MAX_IMPORT_BYTES {
            return Err("组合配置过大".into());
        }
        entries.push(serde_json::json!({
            "name": name,
            "dataHex": to_hex(&bytes),
        }));
    }
    Ok(serde_json::json!({
        "schema": 1,
        "kind": "flotsam-mod-profile",
        "profile": profile,
        "config": entries,
    }))
}
type ImportedProfile = (ModProfile, Vec<(String, Vec<u8>)>);
fn parse_export(value: serde_json::Value) -> Result<ImportedProfile> {
    let (raw, configs) = if value.get("kind").and_then(|k| k.as_str()) == Some("flotsam-mod-profile")
    {
        let configs = value
            .get("config")
            .cloned()
            .unwrap_or_else(|| serde_json::json!([]));
        (value.get("profile").cloned().ok_or("组合文件缺少内容")?, configs)
    } else {
        (value, serde_json::json!([]))
    };
    let profile: ModProfile =
        serde_json::from_value(raw).map_err(|_| "组合文件格式无效".to_string())?;
    let mut files = vec![];
    let mut total = 0usize;
    if let Some(entries) = configs.as_array() {
        if entries.len() > MAX_CONFIG_FILES {
            return Err("组合配置文件过多".into());
        }
        let mut seen = HashSet::new();
        for entry in entries {
            let name = entry
                .get("name")
                .and_then(|n| n.as_str())
                .ok_or("组合配置文件名无效")?;
            config_entry_name(name)?;
            if !seen.insert(name.to_lowercase()) {
                return Err("组合配置包含重复文件".into());
            }
            let data = entry
                .get("dataHex")
                .and_then(|d| d.as_str())
                .ok_or("组合配置内容无效")?;
            let bytes = from_hex(data)?;
            total += bytes.len();
            if total > MAX_IMPORT_BYTES {
                return Err("组合配置过大".into());
            }
            files.push((name.to_string(), bytes));
        }
    } else if !configs.is_null() {
        return Err("组合配置格式无效".into());
    }
    Ok((profile, files))
}
fn write_profile_configuration(app: &tauri::AppHandle, id: &str, files: &[(String, Vec<u8>)]) -> Result<()> {
    let dest = profile_config_dir(app, id)?;
    remove_owned_tree(&dest)?;
    if files.is_empty() {
        return Ok(());
    }
    fs::create_dir_all(&dest).map_err(err)?;
    for (name, bytes) in files {
        config_entry_name(name)?;
        write_atomic(&dest.join(name), bytes)?;
    }
    Ok(())
}

// ---------------------------------------------------------------- commands

#[tauri::command]
pub fn list_mod_profiles(app: tauri::AppHandle) -> Result<Vec<ModProfile>> {
    Ok(read_store(&app)?.profiles)
}
#[tauri::command]
pub fn save_mod_profile(
    app: tauri::AppHandle,
    name: String,
    note: String,
    include_disabled: bool,
) -> Result<ModProfile> {
    let name = name.trim().to_string();
    if name.is_empty() || name.chars().count() > 60 || name.chars().any(char::is_control) {
        return Err("组合名称需为 1–60 个字符".into());
    }
    let root = game_root(&load_settings(&app)?)?;
    let mods = scan(&root)?;
    if mods.is_empty() {
        return Err("当前没有可保存的模组".into());
    }
    let _game_lock = crate::packages::lock(&root)?;
    crate::packages::recover(&root)?;
    let _store_lock = lock_store(&app)?;
    let mut store = read_store(&app)?;
    if store.profiles.len() >= MAX_PROFILES {
        return Err("组合数量已达上限，请先删除旧组合".into());
    }
    let stamp = now();
    let profile = ModProfile {
        id: new_id()?,
        name,
        note: note.chars().take(500).collect(),
        created_at: stamp,
        updated_at: stamp,
        mods: capture(&mods, include_disabled),
    };
    valid_profile(&profile)?;
    capture_configuration(&app, &root, &profile)?;
    store.profiles.push(profile.clone());
    write_store(&app, &store)?;
    Ok(profile)
}
#[tauri::command]
pub async fn apply_mod_profile(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    id: String,
) -> Result<ApplyReport> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = state.mutation.lock().map_err(err)?;
        crate::network::require_disconnected(&network)?;
        require_stopped(&state)?;
        valid_id(&id)?;
        let root = game_root(&load_settings(&app)?)?;
        let _lock = crate::packages::lock(&root)?;
        crate::packages::recover(&root)?;
        let _store_lock = lock_store(&app)?;
        let store = read_store(&app)?;
        let profile = store
            .profiles
            .iter()
            .find(|p| p.id == id)
            .cloned()
            .ok_or("组合不存在")?;
        let sources = configuration_sources(&profile_config_dir(&app, &id)?)?;
        let mut mods = scan(&root)?;
        let (operations, extras, missing) = plan(&mods, &profile);
        preflight(&mut mods, &operations, &root)?;
        let unchanged = mods.len().saturating_sub(operations.len());
        let (enabled, disabled, configuration) = apply_transaction(
            &root,
            &profile.id,
            &sources,
            &operations,
            &|| require_stopped(&state),
        )?;
        Ok(ApplyReport {
            enabled,
            disabled,
            missing,
            extras,
            unchanged,
            configuration,
        })
    })
    .await
    .map_err(err)?
}
#[tauri::command]
pub fn delete_mod_profile(app: tauri::AppHandle, id: String) -> Result<()> {
    valid_id(&id)?;
    let _store_lock = lock_store(&app)?;
    let mut store = read_store(&app)?;
    let before = store.profiles.len();
    store.profiles.retain(|p| p.id != id);
    if store.profiles.len() == before {
        return Err("组合不存在".into());
    }
    write_store(&app, &store)?;
    let _ = remove_owned_tree(&profile_config_dir(&app, &id)?);
    Ok(())
}
#[tauri::command]
pub async fn export_mod_profile(app: tauri::AppHandle, id: String) -> Result<Option<String>> {
    valid_id(&id)?;
    let _store_lock = lock_store(&app)?;
    let store = read_store(&app)?;
    let profile = store
        .profiles
        .iter()
        .find(|p| p.id == id)
        .cloned()
        .ok_or("组合不存在")?;
    let sources = configuration_sources(&profile_config_dir(&app, &id)?)?;
    let export = build_export(&profile, &sources)?;
    drop(_store_lock);
    let Some(target) = rfd::AsyncFileDialog::new()
        .set_title("导出模组组合")
        .set_file_name(format!("浮游组合-{}.json", safe_file_stem(&profile.name)))
        .add_filter("浮游组合", &["json"])
        .save_file()
        .await
    else {
        return Ok(None);
    };
    let path = target.path().to_path_buf();
    fs::write(&path, serde_json::to_vec_pretty(&export).map_err(err)?).map_err(err)?;
    Ok(Some(path.to_string_lossy().into_owned()))
}
fn safe_file_stem(name: &str) -> String {
    let value: String = name
        .chars()
        .map(|c| if "\\/:*?\"<>|".contains(c) { '_' } else { c })
        .take(60)
        .collect();
    if value.trim().is_empty() {
        "组合".into()
    } else {
        value
    }
}
#[tauri::command]
pub async fn import_mod_profile(app: tauri::AppHandle) -> Result<Option<ModProfile>> {
    let Some(picked) = rfd::AsyncFileDialog::new()
        .set_title("导入模组组合")
        .add_filter("浮游组合", &["json"])
        .pick_file()
        .await
    else {
        return Ok(None);
    };
    let meta = fs::symlink_metadata(picked.path()).map_err(err)?;
    if meta.len() > MAX_IMPORT_BYTES as u64 {
        return Err("组合文件过大".into());
    }
    let bytes = fs::read(picked.path()).map_err(err)?;
    let value: serde_json::Value =
        serde_json::from_slice(&bytes).map_err(|_| "组合文件无法读取".to_string())?;
    let (mut profile, files) = parse_export(value)?;
    profile.id = new_id()?;
    let stamp = now();
    profile.created_at = stamp;
    profile.updated_at = stamp;
    valid_profile(&profile)?;
    let _store_lock = lock_store(&app)?;
    let mut store = read_store(&app)?;
    if store.profiles.len() >= MAX_PROFILES {
        return Err("组合数量已达上限，请先删除旧组合".into());
    }
    if store.profiles.iter().any(|p| p.name == profile.name) {
        profile.name = format!("{}（导入）", profile.name);
        if profile.name.chars().count() > 60 {
            profile.name = profile.name.chars().take(60).collect();
        }
    }
    write_profile_configuration(&app, &profile.id, &files)?;
    store.profiles.push(profile.clone());
    if let Err(error) = write_store(&app, &store) {
        let _ = remove_owned_tree(&profile_config_dir(&app, &profile.id)?);
        return Err(error);
    }
    Ok(Some(profile))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mods::{Issue, Metadata, PluginMetadata};

    struct Sandbox {
        root: PathBuf,
        dir: PathBuf,
    }
    impl Sandbox {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "flotsam-profile-{name}-{}-{}",
                std::process::id(),
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            let root = dir.join("game");
            fs::create_dir_all(root.join("BepInEx/plugins")).unwrap();
            fs::create_dir_all(root.join("BepInEx/core")).unwrap();
            fs::create_dir_all(root.join("BepInEx/config")).unwrap();
            fs::write(root.join("BepInEx/core/BepInEx.dll"), b"loader").unwrap();
            Self { root, dir }
        }
        fn profile_dir(&self) -> PathBuf {
            self.dir.join("profile-config")
        }
    }
    impl Drop for Sandbox {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
        }
    }
    fn entry(file: &str, enabled: bool, plugins: &[(&str, &str)]) -> ModEntry {
        ModEntry {
            name: file.to_string(),
            file: file.into(),
            enabled,
            size: 1,
            metadata: Metadata {
                assembly_name: None,
                assembly_version: Some("1.0.0.0".into()),
                company: None,
                plugins: plugins
                    .iter()
                    .map(|(id, version)| PluginMetadata {
                        id: id.to_string(),
                        name: id.to_string(),
                        version: version.to_string(),
                        dependencies: vec![],
                        incompatible: vec![],
                        processes: vec![],
                    })
                    .collect(),
                metadata_error: None,
            },
            issues: Vec::<Issue>::new(),
            package: None,
        }
    }
    fn profile(mods: &[(&str, &str)]) -> ModProfile {
        ModProfile {
            id: "a".repeat(24),
            name: "远航".into(),
            note: String::new(),
            created_at: 0,
            updated_at: 0,
            mods: mods
                .iter()
                .map(|(file, name)| ProfileMod {
                    file: file.to_string(),
                    name: name.to_string(),
                    version: None,
                    plugins: vec![],
                })
                .collect(),
        }
    }
    #[test]
    fn plan_enables_disables_and_reports_missing() {
        let mods = vec![
            entry("a.dll", true, &[]),
            entry("nested/b.dll.disabled", false, &[]),
            entry("c.dll.disabled", false, &[]),
        ];
        let profile = profile(&[("a.dll", "A"), ("nested/b.dll", "B"), ("gone.dll", "Gone")]);
        let (operations, extras, missing) = plan(&mods, &profile);
        assert_eq!(operations, vec![("nested/b.dll.disabled".to_string(), true)]);
        assert_eq!(extras, vec!["c.dll.disabled".to_string()]);
        assert_eq!(missing, vec!["Gone".to_string()]);
    }
    #[test]
    fn rejects_profile_paths_that_escape_the_plugin_folder() {
        assert!(safe_relative("../evil.dll").is_err());
        assert!(safe_relative("C:/abs.dll").is_err());
        assert!(safe_relative("a/../../b.dll").is_err());
        assert!(safe_relative("sub/Ok.dll").is_ok());
    }
    #[test]
    fn profile_validation_rejects_duplicates_and_bad_ids() {
        let mut value = profile(&[("a.dll", "A")]);
        value.id = "zz".into();
        assert!(valid_profile(&value).is_err());
        let mut duplicate = profile(&[("a.dll", "A"), ("A.dll", "A2")]);
        duplicate.id = "b".repeat(24);
        assert!(valid_profile(&duplicate).is_err());
    }
    #[test]
    fn normalized_ignores_disabled_suffix_and_case() {
        assert_eq!(normalized("Sub\\Tool.dll.disabled"), "sub/tool.dll");
        assert_eq!(normalized("tool.dll"), "tool.dll");
    }
    #[test]
    fn preflight_allows_existing_errors_but_rejects_new_ones() {
        let sandbox = Sandbox::new("preflight");
        let root = &sandbox.root;
        let mut mods = vec![
            entry("dep.dll.disabled", false, &[("fixture.dep", "1.0.0")]),
            entry("main.dll", true, &[("fixture.main", "1.0.0")]),
        ];
        let operations = vec![("dep.dll.disabled".to_string(), true)];
        assert!(preflight(&mut mods, &operations, root).is_ok());
        // Without the dependency enabled, enabling main is a new blocking error.
        let mut mods = vec![
            entry("dep.dll.disabled", false, &[("fixture.dep", "1.0.0")]),
            entry("main.dll.disabled", false, &[("fixture.main", "1.0.0")]),
        ];
        mods[1].metadata.plugins[0]
            .dependencies
            .push(mods::Dependency {
                plugin_id: "fixture.dep".into(),
                minimum_version: None,
                optional: false,
            });
        assert!(preflight(&mut mods, &[("main.dll.disabled".to_string(), true)], root).is_err());
    }
    #[test]
    fn configuration_sources_are_validated_and_sorted() {
        let sandbox = Sandbox::new("sources");
        let dir = sandbox.profile_dir().join("a".repeat(24));
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("b.cfg"), b"b").unwrap();
        fs::write(dir.join("a.cfg"), b"a").unwrap();
        let sources = configuration_sources(&dir).unwrap();
        assert_eq!(
            sources.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(),
            vec!["a.cfg", "b.cfg"]
        );
        fs::write(dir.join("escape.txt"), b"x").unwrap();
        assert!(configuration_sources(&dir).is_err());
        fs::write(dir.join("a.cfg"), vec![b'x'; MAX_CONFIG_BYTES as usize + 1]).unwrap();
        fs::remove_file(dir.join("escape.txt")).unwrap();
        assert!(configuration_sources(&dir).is_err());
    }
    #[test]
    fn switch_restores_configuration_and_renames_plugins() {
        let sandbox = Sandbox::new("switch");
        let root = &sandbox.root;
        let plugins = root.join("BepInEx/plugins");
        fs::write(plugins.join("mod.dll.disabled"), b"MZ-mod").unwrap();
        fs::write(root.join("BepInEx/config/mod.cfg"), b"old").unwrap();
        let profile_dir = sandbox.profile_dir().join("b".repeat(24));
        fs::create_dir_all(&profile_dir).unwrap();
        fs::write(profile_dir.join("mod.cfg"), b"new").unwrap();
        let sources = configuration_sources(&profile_dir).unwrap();
        let (enabled, disabled, configuration) = apply_transaction(
            root,
            &"b".repeat(24),
            &sources,
            &[("mod.dll.disabled".to_string(), true)],
            &|| Ok(()),
        )
        .unwrap();
        assert_eq!(enabled, vec!["mod.dll".to_string()]);
        assert!(disabled.is_empty());
        assert_eq!(configuration, 1);
        assert!(plugins.join("mod.dll").is_file());
        assert_eq!(fs::read(root.join("BepInEx/config/mod.cfg")).unwrap(), b"new");
        assert!(!journal_path(root).exists());
        // The overwritten configuration is kept as a rollback backup.
        let backups = manager_dir(root).join("config-backup");
        let count = fs::read_dir(&backups).unwrap().count();
        assert_eq!(count, 1);
        let backup = fs::read_dir(&backups)
            .unwrap()
            .next()
            .unwrap()
            .unwrap()
            .path();
        assert_eq!(fs::read(backup.join("mod.cfg")).unwrap(), b"old");
    }
    #[test]
    fn failed_switch_restores_configuration_and_plugin_state() {
        let sandbox = Sandbox::new("failure");
        let root = &sandbox.root;
        let plugins = root.join("BepInEx/plugins");
        fs::write(plugins.join("mod.dll.disabled"), b"MZ-mod").unwrap();
        fs::write(root.join("BepInEx/config/mod.cfg"), b"old").unwrap();
        let profile_dir = sandbox.profile_dir().join("c".repeat(24));
        fs::create_dir_all(&profile_dir).unwrap();
        fs::write(profile_dir.join("mod.cfg"), b"new").unwrap();
        let sources = configuration_sources(&profile_dir).unwrap();
        let calls = std::cell::Cell::new(0);
        let result = apply_transaction(
            root,
            &"c".repeat(24),
            &sources,
            &[("mod.dll.disabled".to_string(), true)],
            &|| {
                calls.set(calls.get() + 1);
                if calls.get() >= 2 {
                    Err("游戏已启动".into())
                } else {
                    Ok(())
                }
            },
        );
        assert!(result.unwrap_err().contains("已还原"));
        assert_eq!(fs::read(root.join("BepInEx/config/mod.cfg")).unwrap(), b"old");
        assert!(plugins.join("mod.dll.disabled").is_file());
        assert!(!plugins.join("mod.dll").exists());
        assert!(!journal_path(root).exists());
        assert_eq!(fs::read_dir(manager_dir(root).join("profile-tx")).unwrap().count(), 0);
    }
    #[test]
    fn recovery_rolls_back_an_interrupted_switch() {
        let sandbox = Sandbox::new("recover");
        let root = &sandbox.root;
        let plugins = root.join("BepInEx/plugins");
        fs::write(plugins.join("mod.dll.disabled"), b"MZ-mod").unwrap();
        fs::write(root.join("BepInEx/config/mod.cfg"), b"old").unwrap();
        // Simulate a crash after the journal, the configuration write and the
        // rename happened but before cleanup.
        let token = token().unwrap();
        let tx = manager_dir(root).join("profile-tx").join(&token);
        fs::create_dir_all(tx.join("original")).unwrap();
        fs::write(tx.join("original/mod.cfg"), b"old").unwrap();
        fs::write(root.join("BepInEx/config/mod.cfg"), b"new").unwrap();
        fs::rename(plugins.join("mod.dll.disabled"), plugins.join("mod.dll")).unwrap();
        let journal = Journal {
            schema: 2,
            token: token.clone(),
            id: "d".repeat(24),
            changes: vec![ConfigChange {
                name: "mod.cfg".into(),
                existed: true,
            }],
            renames: vec![Rename {
                from: "mod.dll.disabled".into(),
                to: "mod.dll".into(),
            }],
        };
        write_atomic(
            &journal_path(root),
            &serde_json::to_vec_pretty(&journal).unwrap(),
        )
        .unwrap();
        recover_configuration(root).unwrap();
        assert_eq!(fs::read(root.join("BepInEx/config/mod.cfg")).unwrap(), b"old");
        assert!(plugins.join("mod.dll.disabled").is_file());
        assert!(!plugins.join("mod.dll").exists());
        assert!(!journal_path(root).exists());
        assert!(!tx.exists());
    }
    #[test]
    fn export_import_round_trip_includes_configuration() {
        let sandbox = Sandbox::new("export");
        let profile_dir = sandbox.profile_dir().join("e".repeat(24));
        fs::create_dir_all(&profile_dir).unwrap();
        fs::write(profile_dir.join("mod.cfg"), b"[Section]\nKey = Value\n").unwrap();
        let sources = configuration_sources(&profile_dir).unwrap();
        let value = build_export(&profile(&[("mod.dll", "Mod")]), &sources).unwrap();
        let (mut imported, files) = parse_export(value).unwrap();
        assert_eq!(imported.name, "远航");
        assert_eq!(files.len(), 1);
        assert_eq!(files[0].0, "mod.cfg");
        assert_eq!(files[0].1, b"[Section]\nKey = Value\n");
        imported.id = "f".repeat(24);
        assert!(valid_profile(&imported).is_ok());
        // Unsafe entries are rejected before anything is written.
        let bad = serde_json::json!({
            "kind": "flotsam-mod-profile",
            "profile": profile(&[("mod.dll", "Mod")]),
            "config": [{"name": "../escape.cfg", "dataHex": "00"}],
        });
        assert!(parse_export(bad).is_err());
        let bad = serde_json::json!({
            "kind": "flotsam-mod-profile",
            "profile": profile(&[("mod.dll", "Mod")]),
            "config": [{"name": "mod.cfg", "dataHex": "zz"}],
        });
        assert!(parse_export(bad).is_err());
    }
    #[test]
    fn legacy_bundle_without_configuration_still_imports() {
        let value = serde_json::json!({
            "kind": "flotsam-mod-profile",
            "profile": profile(&[("mod.dll", "Mod")]),
        });
        let (imported, files) = parse_export(value).unwrap();
        assert!(files.is_empty());
        assert_eq!(imported.mods.len(), 1);
    }
}
