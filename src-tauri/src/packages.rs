//! Whole-package management (整包管理): install, update, enable/disable, remove,
//! restore and roll back a package while recording exactly which files it owns.
//!
//! Safety model:
//! * only `BepInEx/plugins/flotsam-<id>` trees and the private store below
//!   `BepInEx/.flotsam-manager` are ever touched;
//! * every manifest is verified (size + SHA-256) before a package is changed, so
//!   user edits are never silently overwritten;
//! * a journal is written before the first rename. `recover` completes or
//!   restores an interrupted transaction; incomplete stages are never loadable;
//! * all mutating entry points take a cross-process file lock at the game root.
use crate::{
    mods::{self, MetadataCache, ModEntry},
    service::{err, Result},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Component, Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

const MARKER: &str = ".flotsam-package.json";
const LOCK_FILE: &str = ".flotsam-launcher.lock";
const SCHEMA: u32 = 2;
const MAX_FILES: usize = 1024;
const MAX_PACKAGE_BYTES: u64 = 128 * 1024 * 1024;
const MAX_JOURNAL_BYTES: u64 = 64 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Origin {
    pub nexus_mod_id: Option<u64>,
    pub nexus_file_id: Option<u64>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OwnedFile {
    path: String,
    size: u64,
    sha256: String,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    schema: u32,
    id: String,
    revision: String,
    previous_revision: Option<String>,
    name: String,
    installed_at: u64,
    origin: Origin,
    files: Vec<OwnedFile>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Package {
    pub id: String,
    pub name: String,
    pub files: usize,
    pub can_rollback: bool,
    pub nexus_mod_id: Option<u64>,
    pub nexus_file_id: Option<u64>,
    /// Every DLL of the package is enabled.
    pub enabled: bool,
    /// The package contains both enabled and disabled DLLs.
    pub mixed: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
enum JournalKind {
    /// Journal written by a launcher build that only knew install/update/remove.
    #[default]
    Legacy,
    Install,
    Revise,
    Remove,
    Toggle,
}
#[derive(Serialize, Deserialize)]
struct Journal {
    #[serde(default)]
    schema: u32,
    #[serde(default)]
    kind: JournalKind,
    id: String,
    token: String,
    old_revision: Option<String>,
    new_revision: Option<String>,
    #[serde(default)]
    enabled: Option<bool>,
}
impl Journal {
    fn resolved_kind(&self) -> Result<JournalKind> {
        if self.kind == JournalKind::Legacy {
            return Ok(if self.new_revision.is_none() {
                JournalKind::Remove
            } else if self.old_revision.is_some() {
                JournalKind::Revise
            } else {
                JournalKind::Install
            });
        }
        if self.kind == JournalKind::Toggle && self.enabled.is_none() {
            return Err("模组切换记录无效".into());
        }
        Ok(self.kind)
    }
}
fn token() -> Result<String> {
    let mut value = [0u8; 16];
    getrandom::fill(&mut value).map_err(err)?;
    Ok(value.iter().map(|v| format!("{v:02x}")).collect())
}
fn valid_token(value: &str) -> Result<()> {
    if value.len() != 32
        || !value
            .bytes()
            .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
    {
        return Err("模组包标识无效".into());
    }
    Ok(())
}
fn store(root: &Path) -> PathBuf {
    root.join("BepInEx/.flotsam-manager")
}
/// Launcher-private store inside the game directory (journal, history, staging).
pub(crate) fn manager_dir(root: &Path) -> PathBuf {
    store(root)
}
fn live(root: &Path, id: &str) -> Result<PathBuf> {
    valid_token(id)?;
    Ok(root.join("BepInEx/plugins").join(format!("flotsam-{id}")))
}
fn history(root: &Path, id: &str, revision: &str) -> Result<PathBuf> {
    valid_token(id)?;
    valid_token(revision)?;
    Ok(store(root).join("history").join(id).join(revision))
}
fn removed(root: &Path, id: &str) -> Result<PathBuf> {
    valid_token(id)?;
    Ok(store(root).join("removed").join(id))
}
fn staging(root: &Path, token: &str) -> Result<PathBuf> {
    valid_token(token)?;
    Ok(store(root).join("staging").join(token))
}
fn journal_path(root: &Path) -> PathBuf {
    store(root).join("transaction.json")
}
pub(crate) fn is_link(meta: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        // FILE_ATTRIBUTE_REPARSE_POINT also covers junctions and mount points,
        // which `file_type().is_symlink()` does not report on Windows.
        meta.file_type().is_symlink() || meta.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        meta.file_type().is_symlink()
    }
}
// Validate every existing component, including junctions, before any tree mutation.
pub(crate) fn boundary(root: &Path, path: &Path) -> Result<()> {
    let relative = path.strip_prefix(root).map_err(|_| "模组路径越界")?;
    let mut current = root.to_path_buf();
    for part in relative.components() {
        let Component::Normal(part) = part else {
            return Err("模组路径无效".into());
        };
        current.push(part);
        match fs::symlink_metadata(&current) {
            Ok(meta) if is_link(&meta) => return Err("模组目录包含链接，请先检查文件位置".into()),
            Ok(_) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(err(e)),
        }
    }
    Ok(())
}
fn mkdir(root: &Path, path: &Path) -> Result<()> {
    boundary(root, path)?;
    fs::create_dir_all(path).map_err(err)
}
fn files(root: &Path) -> Result<Vec<PathBuf>> {
    fn visit(root: &Path, output: &mut Vec<PathBuf>, depth: usize) -> Result<()> {
        if depth > 32 {
            return Err("模组目录层级过深".into());
        }
        for entry in fs::read_dir(root).map_err(err)? {
            let entry = entry.map_err(err)?;
            let meta = fs::symlink_metadata(entry.path()).map_err(err)?;
            if is_link(&meta) {
                return Err("模组包包含链接，无法安全管理".into());
            }
            if meta.is_dir() {
                visit(&entry.path(), output, depth + 1)?;
            } else if meta.is_file() {
                output.push(entry.path());
            }
            if output.len() > MAX_FILES + 1 {
                return Err("模组包文件过多".into());
            }
        }
        Ok(())
    }
    let mut result = vec![];
    visit(root, &mut result, 0)?;
    Ok(result)
}
fn delete_stage(root: &Path, path: &Path) -> Result<()> {
    boundary(root, path)?;
    if path.exists() {
        files(path)?;
        fs::remove_dir_all(path).map_err(err)?;
    }
    Ok(())
}
/// Cross-process lock for a single launcher-managed file (game root, profile store).
///
/// The lock is advisory: it is released when the process exits, and the file
/// itself is deliberately left in place so that a crashed launcher never leaves
/// a state another launcher cannot recover from.
pub(crate) fn lock_path(path: &Path) -> Result<File> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(err)?;
        let meta = fs::symlink_metadata(parent).map_err(err)?;
        if is_link(&meta) {
            return Err("锁定目录包含链接，请检查文件位置".into());
        }
    }
    match fs::symlink_metadata(path) {
        Ok(meta) if is_link(&meta) => return Err("锁定文件包含链接，请检查文件位置".into()),
        Ok(_) => {}
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => return Err(err(e)),
    }
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(path)
        .map_err(err)?;
    file.try_lock().map_err(|e| match e {
        fs::TryLockError::WouldBlock => "另一个启动器正在管理此游戏，请稍后重试".into(),
        fs::TryLockError::Error(e) => err(e),
    })?;
    Ok(file)
}
/// Cross-launcher lock at the game root. Deliberately *not* inside `BepInEx` so a
/// vanilla launch never creates loader directories as a side effect.
pub(crate) fn lock(root: &Path) -> Result<File> {
    let path = root.join(LOCK_FILE);
    boundary(root, &path)?;
    lock_path(&path)
}
fn pending_name(path: &Path) -> Result<PathBuf> {
    let name = path.file_name().ok_or("清单路径无效")?.to_string_lossy();
    Ok(path.with_file_name(format!("{name}.pending")))
}
fn write_json(path: &Path, value: &impl Serialize) -> Result<()> {
    let temporary = pending_name(path)?;
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(err)?;
    let result = (|| {
        file.write_all(&serde_json::to_vec_pretty(value).map_err(err)?)
            .map_err(err)?;
        file.sync_all().map_err(err)?;
        drop(file);
        if path.exists() {
            return Err("模组清单已存在，请先恢复未完成操作".into());
        }
        fs::rename(&temporary, path).map_err(err)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}
/// Atomically replace a file (`fs::rename` replaces on Windows and POSIX).
fn replace_json(path: &Path, value: &impl Serialize) -> Result<()> {
    let temporary = pending_name(path)?;
    let _ = fs::remove_file(&temporary);
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(err)?;
    let result = (|| {
        file.write_all(&serde_json::to_vec_pretty(value).map_err(err)?)
            .map_err(err)?;
        file.sync_all().map_err(err)?;
        drop(file);
        fs::rename(&temporary, path).map_err(err)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}
fn read_manifest(path: &Path) -> Result<Manifest> {
    let path = path.join(MARKER);
    let meta = fs::symlink_metadata(&path).map_err(err)?;
    if is_link(&meta) || meta.len() > 1024 * 1024 {
        return Err("模组归属清单无效".into());
    }
    let value: Manifest = serde_json::from_slice(&fs::read(path).map_err(err)?).map_err(err)?;
    valid_token(&value.id)?;
    valid_token(&value.revision)?;
    if let Some(id) = &value.previous_revision {
        valid_token(id)?;
    }
    if value.schema != 1 || value.files.is_empty() || value.files.len() > MAX_FILES {
        return Err("模组归属清单无效".into());
    }
    let mut names = HashSet::new();
    for file in &value.files {
        safe_path(&file.path)?;
        if !names.insert(normalized(&file.path))
            || file.sha256.len() != 64
            || !file.sha256.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
        {
            return Err("模组文件清单无效".into());
        }
    }
    Ok(value)
}
fn safe_path(name: &str) -> Result<PathBuf> {
    if name.is_empty() || name.contains(['\\', ':']) || name.starts_with('/') {
        return Err("模组文件路径无效".into());
    }
    let path = PathBuf::from(name);
    if path.components().any(|c| !matches!(c, Component::Normal(_)))
        || name
            .split('/')
            .any(|v| v.is_empty() || v == "." || v == "..")
    {
        return Err("模组文件路径无效".into());
    }
    Ok(path)
}
fn normalized(name: &str) -> String {
    let lower = name.to_lowercase();
    if lower.ends_with(".dll.disabled") {
        lower[..lower.len() - 9].into()
    } else {
        lower
    }
}
fn digest(path: &Path) -> Result<(u64, String)> {
    let mut file = File::open(path).map_err(err)?;
    let mut hash = Sha256::new();
    let mut size = 0;
    let mut buffer = [0u8; 65536];
    loop {
        let n = file.read(&mut buffer).map_err(err)?;
        if n == 0 {
            break;
        }
        size += n as u64;
        if size > MAX_PACKAGE_BYTES {
            return Err("模组文件过大".into());
        }
        hash.update(&buffer[..n]);
    }
    Ok((size, format!("{:x}", hash.finalize())))
}
fn inventory(path: &Path) -> Result<Vec<OwnedFile>> {
    let mut result = vec![];
    let mut total = 0;
    for file in files(path)? {
        let name = file
            .strip_prefix(path)
            .map_err(err)?
            .to_string_lossy()
            .replace('\\', "/");
        if name == MARKER {
            continue;
        }
        safe_path(&name)?;
        let (size, sha256) = digest(&file)?;
        total += size;
        if total > MAX_PACKAGE_BYTES {
            return Err("模组包超过 128 MB".into());
        }
        result.push(OwnedFile {
            path: name,
            size,
            sha256,
        });
    }
    if result.len() > MAX_FILES {
        return Err("模组包文件过多".into());
    }
    result.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(result)
}
fn verify(path: &Path, manifest: &Manifest) -> Result<()> {
    let actual = inventory(path)?;
    let expected: HashMap<_, _> = manifest
        .files
        .iter()
        .map(|f| (normalized(&f.path), f))
        .collect();
    if actual.len() != expected.len() {
        return Err("包内文件已被外部增删，请先检查文件位置；未覆盖或移除文件".into());
    }
    for file in actual {
        let old = expected
            .get(&normalized(&file.path))
            .ok_or("模组包存在清单外文件，请先检查文件位置")?;
        if old.size != file.size || old.sha256 != file.sha256 {
            return Err(format!("{} 已被外部修改，请先保留你的修改", file.path));
        }
    }
    Ok(())
}
fn package(root: &Path, manifest: &Manifest) -> Package {
    let dlls: Vec<bool> = manifest
        .files
        .iter()
        .filter(|f| {
            let lower = f.path.to_ascii_lowercase();
            lower.ends_with(".dll") || lower.ends_with(".dll.disabled")
        })
        .map(|f| {
            // Profiles can rename managed DLLs without rewriting package identity.
            // Reflect the actual live name, not a stale manifest suffix.
            let enabled_name = if f.path.to_ascii_lowercase().ends_with(".dll.disabled") {
                &f.path[..f.path.len() - 9]
            } else { f.path.as_str() };
            live(root, &manifest.id).is_ok_and(|dir| dir.join(enabled_name).is_file())
        })
        .collect();
    let enabled = !dlls.is_empty() && dlls.iter().all(|v| *v);
    let mixed = dlls.iter().any(|v| *v) && dlls.iter().any(|v| !*v);
    Package {
        id: manifest.id.clone(),
        name: manifest.name.clone(),
        files: manifest.files.len(),
        can_rollback: manifest.previous_revision.as_ref().is_some_and(|r| {
            history(root, &manifest.id, r).is_ok_and(|p| p.join(MARKER).is_file())
        }),
        nexus_mod_id: manifest.origin.nexus_mod_id,
        nexus_file_id: manifest.origin.nexus_file_id,
        enabled,
        mixed,
    }
}
pub(crate) fn annotate(root: &Path, mods: &mut [ModEntry]) -> Result<Vec<Package>> {
    let mut packages = HashMap::new();
    for entry in mods {
        let Some(folder) = Path::new(&entry.file).components().next().and_then(|c| {
            if let Component::Normal(v) = c {
                v.to_str()
            } else {
                None
            }
        }) else {
            continue;
        };
        let Some(id) = folder.strip_prefix("flotsam-") else {
            continue;
        };
        if valid_token(id).is_err() {
            continue;
        }
        if !packages.contains_key(id) {
            let path = live(root, id)?;
            boundary(root, &path)?;
            if !path.join(MARKER).exists() {
                continue;
            }
            let manifest = read_manifest(&path)?;
            if manifest.id != id {
                return Err("模组目录与清单不一致".into());
            }
            packages.insert(id.to_string(), package(root, &manifest));
        }
        entry.package = packages.get(id).cloned();
    }
    let trash = store(root).join("removed");
    let mut result = vec![];
    if trash.is_dir() {
        boundary(root, &trash)?;
        for entry in fs::read_dir(trash).map_err(err)? {
            let entry = entry.map_err(err)?;
            let id = entry.file_name().to_string_lossy().into_owned();
            valid_token(&id)?;
            boundary(root, &entry.path())?;
            let manifest = read_manifest(&entry.path())?;
            if manifest.id != id {
                return Err("已移除模组清单不一致".into());
            }
            result.push(package(root, &manifest));
        }
    }
    result.sort_by(|a, b| a.name.cmp(&b.name));
    Ok(result)
}
fn error_set(entries: &[ModEntry]) -> HashSet<(String, String, String)> {
    entries
        .iter()
        .flat_map(|m| {
            m.issues
                .iter()
                .filter(|i| i.severity == "error")
                .map(|i| (m.file.clone(), i.code.clone(), i.message.clone()))
        })
        .collect()
}
fn preflight(root: &Path, id: &str, incoming: Option<&Path>) -> Result<()> {
    let mut cache = MetadataCache::default();
    let before = mods::scan(root, &mut cache)?;
    let old = error_set(&before);
    let prefix = format!("flotsam-{id}");
    let mut after: Vec<_> = before
        .into_iter()
        .filter(|m| !Path::new(&m.file).starts_with(&prefix))
        .collect();
    if let Some(incoming) = incoming {
        crate::online::require_no_duplicate_plugins_except(
            &root.join("BepInEx/plugins"),
            incoming,
            Some(&live(root, id)?),
        )?;
        for mut entry in mods::scan_directory(incoming, true, &mut MetadataCache::default())? {
            entry.file = Path::new(&prefix)
                .join(&entry.file)
                .to_string_lossy()
                .into_owned();
            after.push(entry);
        }
    }
    mods::diagnose(&mut after, root.join("BepInEx/core/BepInEx.dll").is_file());
    for entry in &after {
        for issue in &entry.issues {
            if issue.severity == "error"
                && !old.contains(&(entry.file.clone(), issue.code.clone(), issue.message.clone()))
            {
                return Err(format!("无法更改模组：{} · {}", entry.name, issue.message));
            }
        }
    }
    Ok(())
}
/// Simulate enabling/disabling the whole package; reject newly introduced errors
/// (for example a dependant losing its required plugin).
fn preflight_toggle(root: &Path, id: &str, enabled: bool) -> Result<()> {
    let mut cache = MetadataCache::default();
    let mut entries = mods::scan(root, &mut cache)?;
    let old = error_set(&entries);
    let prefix = format!("flotsam-{id}/");
    let mut touched = false;
    for entry in entries.iter_mut() {
        if entry.file.replace('\\', "/").starts_with(&prefix) {
            entry.enabled = enabled;
            touched = true;
        }
    }
    if !touched {
        return Err("模组包内没有可切换的插件文件".into());
    }
    mods::diagnose(&mut entries, root.join("BepInEx/core/BepInEx.dll").is_file());
    for entry in &entries {
        for issue in &entry.issues {
            if issue.severity == "error"
                && !old.contains(&(entry.file.clone(), issue.code.clone(), issue.message.clone()))
            {
                return Err(format!(
                    "无法{}：{} · {}",
                    if enabled { "启用" } else { "停用" },
                    entry.name,
                    issue.message
                ));
            }
        }
    }
    Ok(())
}
fn copy_tree(root: &Path, source: &Path, destination: &Path) -> Result<()> {
    boundary(root, source)?;
    mkdir(root, destination)?;
    for file in files(source)? {
        let target = destination.join(file.strip_prefix(source).map_err(err)?);
        mkdir(root, target.parent().ok_or("模组路径无效")?)?;
        fs::copy(file, &target).map_err(err)?;
        OpenOptions::new()
            .write(true)
            .open(target)
            .map_err(err)?
            .sync_all()
            .map_err(err)?;
    }
    Ok(())
}
fn commit(
    root: &Path,
    manifest: &Manifest,
    stage: &Path,
    old: Option<&Manifest>,
    check: &impl Fn() -> Result<()>,
) -> Result<()> {
    let target = live(root, &manifest.id)?;
    boundary(root, &target)?;
    mkdir(root, target.parent().ok_or("模组目录无效")?)?;
    let token = stage
        .file_name()
        .and_then(|v| v.to_str())
        .ok_or("暂存目录无效")?;
    let journal = Journal {
        schema: SCHEMA,
        kind: if old.is_some() {
            JournalKind::Revise
        } else {
            JournalKind::Install
        },
        id: manifest.id.clone(),
        token: token.into(),
        old_revision: old.map(|m| m.revision.clone()),
        new_revision: Some(manifest.revision.clone()),
        enabled: None,
    };
    check()?;
    write_json(&journal_path(root), &journal)?;
    let apply = (|| {
        if let Some(old) = old {
            let backup = history(root, &old.id, &old.revision)?;
            mkdir(root, backup.parent().ok_or("备份目录无效")?)?;
            boundary(root, &backup)?;
            if backup.exists() {
                return Err("回退备份已存在，请刷新后重试".into());
            }
            fs::rename(&target, &backup).map_err(err)?;
        }
        fs::rename(stage, &target).map_err(err)?;
        fs::remove_file(journal_path(root)).map_err(err)
    })();
    if let Err(e) = apply {
        if let Err(recovery) = recover(root) {
            return Err(format!("{e}；恢复待处理：{recovery}"));
        }
        return Err(e);
    }
    Ok(())
}
/// Complete every pending launcher transaction for this game, including a
/// half-applied profile switch. Callers must hold [`lock`].
#[allow(dead_code)]
pub(crate) fn recover_all(root: &Path) -> Result<()> {
    recover(root)
}
/// Journal is written before the first rename. Recovery keeps a fully published
/// revision or restores the old directory; an incomplete stage is never loadable.
pub(crate) fn recover(root: &Path) -> Result<()> {
    let path = journal_path(root);
    boundary(root, &path)?;
    if path.exists() {
        if fs::metadata(&path).map_err(err)?.len() > MAX_JOURNAL_BYTES {
            return Err("模组恢复记录无效".into());
        }
        let journal: Journal =
            serde_json::from_slice(&fs::read(&path).map_err(err)?).map_err(err)?;
        if journal.schema > SCHEMA {
            return Err("模组恢复记录版本不受支持".into());
        }
        valid_token(&journal.id)?;
        valid_token(&journal.token)?;
        if let Some(r) = &journal.old_revision {
            valid_token(r)?;
        }
        if let Some(r) = &journal.new_revision {
            valid_token(r)?;
        }
        match journal.resolved_kind()? {
            JournalKind::Install | JournalKind::Revise | JournalKind::Legacy => {
                recover_revision(root, &journal)?
            }
            JournalKind::Remove => recover_remove(root, &journal)?,
            JournalKind::Toggle => recover_toggle(root, &journal)?,
        }
        delete_stage(root, &staging(root, &journal.token)?)?;
        if path.exists() {
            fs::remove_file(&path).map_err(err)?;
        }
    }
    let pending = pending_name(&path)?;
    boundary(root, &pending)?;
    if pending.exists() {
        fs::remove_file(pending).map_err(err)?;
    }
    // Orphan stages are only ours and always outside BepInEx/plugins.
    let stages = store(root).join("staging");
    boundary(root, &stages)?;
    if stages.is_dir() {
        for entry in fs::read_dir(stages).map_err(err)? {
            let entry = entry.map_err(err)?;
            valid_token(&entry.file_name().to_string_lossy())?;
            delete_stage(root, &entry.path())?;
        }
    }
    // Profile transactions live in the same store; finish them in the same call
    // so an interrupted configuration switch is never left half applied.
    crate::profiles::recover_configuration(root)
}
fn recover_revision(root: &Path, journal: &Journal) -> Result<()> {
    let target = live(root, &journal.id)?;
    boundary(root, &target)?;
    let new = journal
        .new_revision
        .as_ref()
        .ok_or("更新记录缺少目标版本")?;
    let published = if target.exists() {
        let manifest = read_manifest(&target)?;
        if manifest.id != journal.id {
            return Err("恢复时发现模组目录与记录不一致".into());
        }
        manifest.revision == *new
    } else {
        false
    };
    if !published {
        let Some(old) = &journal.old_revision else {
            // A new install that never published is simply abandoned.
            return Ok(());
        };
        let backup = history(root, &journal.id, old)?;
        boundary(root, &backup)?;
        if target.exists() {
            let manifest = read_manifest(&target)?;
            if manifest.revision != *old {
                return Err("恢复时发现外部版本，请检查模组目录".into());
            }
        } else if backup.exists() {
            let manifest = read_manifest(&backup)?;
            if manifest.id != journal.id || manifest.revision != *old {
                return Err("恢复备份不匹配".into());
            }
            verify(&backup, &manifest)?;
            fs::rename(&backup, &target).map_err(err)?;
        } else {
            return Err("恢复备份缺失，请检查模组目录".into());
        }
    }
    Ok(())
}
fn recover_remove(root: &Path, journal: &Journal) -> Result<()> {
    let target = live(root, &journal.id)?;
    boundary(root, &target)?;
    let trash = removed(root, &journal.id)?;
    boundary(root, &trash)?;
    if !target.exists() && !trash.exists() {
        return Err("移除记录缺少恢复文件".into());
    }
    if target.exists() && trash.exists() {
        return Err("移除记录与目录冲突，请检查文件位置".into());
    }
    if trash.exists() {
        let manifest = read_manifest(&trash)?;
        if manifest.id != journal.id {
            return Err("移除备份不匹配".into());
        }
        if let Some(old) = &journal.old_revision {
            if manifest.revision != *old {
                return Err("移除备份版本不匹配".into());
            }
        }
        fs::rename(&trash, &target).map_err(err)?;
    }
    Ok(())
}
/// Rename declared files of `manifest` into the requested enabled state.
///
/// Idempotent: already renamed files are accepted, both names present is a
/// conflict (never delete a user file), neither present is an error.
fn reconcile_toggle(root: &Path, target: &Path, manifest: &Manifest, enabled: bool) -> Result<Manifest> {
    let mut owned = Vec::with_capacity(manifest.files.len());
    for file in &manifest.files {
        let Some(new_path) = toggled_name(&file.path, enabled) else {
            owned.push(file.clone());
            continue;
        };
        let from = target.join(safe_path(&file.path)?);
        let to = target.join(safe_path(&new_path)?);
        boundary(root, &from)?;
        boundary(root, &to)?;
        let from_exists = fs::symlink_metadata(&from).is_ok();
        let to_exists = fs::symlink_metadata(&to).is_ok();
        match (from_exists, to_exists) {
            (true, false) => fs::rename(&from, &to).map_err(err)?,
            (false, true) => {}
            (true, true) => return Err("切换模组时发现同名文件冲突，未覆盖任何文件".into()),
            (false, false) => return Err(format!("{} 缺失，请检查模组目录", file.path)),
        }
        owned.push(OwnedFile {
            path: new_path,
            ..file.clone()
        });
    }
    owned.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(Manifest {
        files: owned,
        ..manifest.clone()
    })
}
fn recover_toggle(root: &Path, journal: &Journal) -> Result<()> {
    let target = live(root, &journal.id)?;
    boundary(root, &target)?;
    if !target.exists() {
        return Err("切换记录对应的模组目录缺失".into());
    }
    let manifest = read_manifest(&target)?;
    if manifest.id != journal.id {
        return Err("切换记录与模组目录不一致".into());
    }
    let enabled = journal
        .enabled
        .ok_or("模组切换记录无效".to_string())?;
    let new_revision = journal
        .new_revision
        .as_ref()
        .ok_or("切换记录缺少目标版本")?;
    if manifest.revision == *new_revision {
        // Renames and the manifest write completed; only cleanup was missing.
        return Ok(());
    }
    if Some(manifest.revision.as_str()) != journal.old_revision.as_deref() {
        return Err("切换记录版本与模组目录不一致".into());
    }
    let mut updated = reconcile_toggle(root, &target, &manifest, enabled)?;
    updated.revision = new_revision.clone();
    replace_json(&target.join(MARKER), &updated)
}
fn toggled_name(path: &str, enabled: bool) -> Option<String> {
    let lower = path.to_ascii_lowercase();
    if enabled {
        lower.ends_with(".dll.disabled").then(|| path[..path.len() - 9].to_string())
    } else if lower.ends_with(".dll") {
        Some(format!("{path}.disabled"))
    } else {
        None
    }
}
fn preserve_disabled(root: &Path, old: &Path, incoming: &Path) -> Result<()> {
    let a = mods::scan_directory(old, true, &mut MetadataCache::default())?;
    let b = mods::scan_directory(incoming, true, &mut MetadataCache::default())?;
    let disabled: HashSet<_> = a
        .iter()
        .filter(|e| !e.enabled)
        .flat_map(|e| e.metadata.plugins.iter().map(|p| p.id.as_str()))
        .collect();
    let all_disabled = !a.is_empty() && a.iter().all(|e| !e.enabled);
    for entry in b {
        if entry.enabled
            && (all_disabled
                || entry
                    .metadata
                    .plugins
                    .iter()
                    .any(|p| disabled.contains(p.id.as_str()))
                || a.iter()
                    .any(|e| !e.enabled && normalized(&e.file) == normalized(&entry.file)))
        {
            let from = incoming.join(&entry.file);
            let to = from.with_file_name(format!(
                "{}.disabled",
                from.file_name().unwrap_or_default().to_string_lossy()
            ));
            boundary(root, &from)?;
            boundary(root, &to)?;
            fs::rename(from, to).map_err(err)?;
        }
    }
    Ok(())
}
pub(crate) fn install(
    root: &Path,
    source: &Path,
    name: &str,
    origin: Origin,
    replacing: Option<&str>,
    check: impl Fn() -> Result<()>,
) -> Result<usize> {
    let _lock = lock(root)?;
    recover(root)?;
    check()?;
    let id = match replacing {
        Some(id) => {
            valid_token(id)?;
            id.to_string()
        }
        None => token()?,
    };
    let target = live(root, &id)?;
    boundary(root, &target)?;
    let old = if replacing.is_some() {
        if !target.is_dir() {
            return Err("要更新的模组包不存在，请刷新".into());
        }
        let m = read_manifest(&target)?;
        if m.id != id {
            return Err("模组包归属不一致".into());
        }
        verify(&target, &m)?;
        Some(m)
    } else {
        None
    };
    let stage = staging(root, &token()?)?;
    mkdir(root, &stage)?;
    let result = (|| {
        let count = crate::online::unpack_package(source, name, &stage)?;
        if stage.join(MARKER).exists() {
            return Err("模组包包含保留的管理清单文件".into());
        }
        if let Some(old) = &old {
            let before = mods::scan_directory(&target, true, &mut MetadataCache::default())?;
            let after = mods::scan_directory(&stage, true, &mut MetadataCache::default())?;
            let ids: HashSet<_> = before
                .iter()
                .flat_map(|m| m.metadata.plugins.iter().map(|p| p.id.as_str()))
                .collect();
            let same = after.iter().any(|m| {
                m.metadata
                    .plugins
                    .iter()
                    .any(|p| ids.contains(p.id.as_str()))
            });
            let names: HashSet<_> = old.files.iter().map(|f| normalized(&f.path)).collect();
            if !same
                && !inventory(&stage)?.iter().any(|f| {
                    f.path.to_ascii_lowercase().ends_with(".dll")
                        && names.contains(&normalized(&f.path))
                })
            {
                return Err("所选文件与此模组包没有相同插件，请作为新模组导入".into());
            }
            preserve_disabled(root, &target, &stage)?;
        }
        preflight(root, &id, Some(&stage))?;
        let manifest = Manifest {
            schema: 1,
            id: id.clone(),
            revision: token()?,
            previous_revision: old.as_ref().map(|m| m.revision.clone()),
            name: name.into(),
            installed_at: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_err(err)?
                .as_secs(),
            origin,
            files: inventory(&stage)?,
        };
        write_json(&stage.join(MARKER), &manifest)?;
        commit(root, &manifest, &stage, old.as_ref(), &check)?;
        Ok(count)
    })();
    if result.is_err() && !journal_path(root).exists() {
        let _ = delete_stage(root, &stage);
    }
    result
}
pub(crate) fn remove(root: &Path, id: &str, check: impl Fn() -> Result<()>) -> Result<()> {
    let _lock = lock(root)?;
    recover(root)?;
    check()?;
    let target = live(root, id)?;
    boundary(root, &target)?;
    if !target.exists() {
        return Err("模组包不存在，请刷新".into());
    }
    let manifest = read_manifest(&target)?;
    if manifest.id != id {
        return Err("模组归属不匹配".into());
    }
    verify(&target, &manifest)?;
    preflight(root, id, None)?;
    let trash = removed(root, id)?;
    mkdir(root, trash.parent().ok_or("恢复目录无效")?)?;
    boundary(root, &trash)?;
    if trash.exists() {
        return Err("此模组已存在移除记录，请先恢复".into());
    }
    let journal = Journal {
        schema: SCHEMA,
        kind: JournalKind::Remove,
        id: id.into(),
        token: token()?,
        old_revision: Some(manifest.revision),
        new_revision: None,
        enabled: None,
    };
    check()?;
    write_json(&journal_path(root), &journal)?;
    if let Err(e) = fs::rename(&target, &trash) {
        recover(root)?;
        return Err(err(e));
    }
    fs::remove_file(journal_path(root)).map_err(err)
}
pub(crate) fn restore(root: &Path, id: &str, check: impl Fn() -> Result<()>) -> Result<()> {
    let _lock = lock(root)?;
    recover(root)?;
    check()?;
    let trash = removed(root, id)?;
    boundary(root, &trash)?;
    if !trash.exists() {
        return Err("没有可恢复的模组包".into());
    }
    let manifest = read_manifest(&trash)?;
    if manifest.id != id {
        return Err("模组归属不匹配".into());
    }
    verify(&trash, &manifest)?;
    let target = live(root, id)?;
    boundary(root, &target)?;
    if target.exists() {
        return Err("模组包已存在，未覆盖当前文件".into());
    }
    preflight(root, id, Some(&trash))?;
    mkdir(root, target.parent().ok_or("模组目录无效")?)?;
    check()?;
    fs::rename(trash, target).map_err(err)
}
pub(crate) fn rollback(root: &Path, id: &str, check: impl Fn() -> Result<()>) -> Result<()> {
    let _lock = lock(root)?;
    recover(root)?;
    check()?;
    let target = live(root, id)?;
    boundary(root, &target)?;
    let current = read_manifest(&target)?;
    if current.id != id {
        return Err("模组归属不匹配".into());
    }
    verify(&target, &current)?;
    let backup = history(
        root,
        id,
        current
            .previous_revision
            .as_deref()
            .ok_or("没有可回退版本")?,
    )?;
    boundary(root, &backup)?;
    let old = read_manifest(&backup)?;
    if old.id != id || Some(old.revision.as_str()) != current.previous_revision.as_deref() {
        return Err("回退清单不匹配".into());
    }
    verify(&backup, &old)?;
    let stage = staging(root, &token()?)?;
    let result = (|| {
        copy_tree(root, &backup, &stage)?;
        fs::remove_file(stage.join(MARKER)).map_err(err)?;
        preserve_disabled(root, &target, &stage)?;
        preflight(root, id, Some(&stage))?;
        let manifest = Manifest {
            revision: token()?,
            previous_revision: Some(current.revision.clone()),
            files: inventory(&stage)?,
            ..old
        };
        write_json(&stage.join(MARKER), &manifest)?;
        commit(root, &manifest, &stage, Some(&current), &check)
    })();
    if result.is_err() && !journal_path(root).exists() {
        let _ = delete_stage(root, &stage);
    }
    result
}
/// Enable or disable every DLL in a package atomically. The manifest revision is
/// advanced and recorded in a journal, so an interrupted toggle is completed by
/// `recover` instead of leaving a half-toggled package.
///
/// Returns the number of renamed files (0 when the package is already in the
/// requested state).
pub(crate) fn set_enabled(
    root: &Path,
    id: &str,
    enabled: bool,
    check: impl Fn() -> Result<()>,
) -> Result<usize> {
    let _lock = lock(root)?;
    recover(root)?;
    check()?;
    let target = live(root, id)?;
    boundary(root, &target)?;
    if !target.is_dir() {
        return Err("模组包不存在，请刷新".into());
    }
    let manifest = read_manifest(&target)?;
    if manifest.id != id {
        return Err("模组归属不匹配".into());
    }
    verify(&target, &manifest)?;
    // A profile may have changed only the physical DLL suffix. Record the actual
    // pre-toggle names so rollback/recovery do not assume stale manifest state.
    let manifest = Manifest { files: inventory(&target)?, ..manifest };
    let renames: Vec<(String, String)> = manifest
        .files
        .iter()
        .filter_map(|f| toggled_name(&f.path, enabled).map(|n| (f.path.clone(), n)))
        .collect();
    if renames.is_empty() {
        return Ok(0);
    }
    preflight_toggle(root, id, enabled)?;
    check()?;
    replace_json(&target.join(MARKER), &manifest)?;
    let revision = token()?;
    let journal = Journal {
        schema: SCHEMA,
        kind: JournalKind::Toggle,
        id: id.into(),
        token: token()?,
        old_revision: Some(manifest.revision.clone()),
        new_revision: Some(revision.clone()),
        enabled: Some(enabled),
    };
    write_json(&journal_path(root), &journal)?;
    let mut done: Vec<(PathBuf, PathBuf)> = vec![];
    let apply = (|| -> Result<()> {
        for (from, to) in &renames {
            let from = target.join(safe_path(from)?);
            let to = target.join(safe_path(to)?);
            boundary(root, &from)?;
            boundary(root, &to)?;
            if fs::symlink_metadata(&to).is_ok() {
                return Err(format!(
                    "目标文件已存在：{}",
                    to.file_name().unwrap_or_default().to_string_lossy()
                ));
            }
            fs::rename(&from, &to).map_err(err)?;
            done.push((from, to));
        }
        let mut updated = manifest.clone();
        for file in &mut updated.files {
            if let Some(name) = toggled_name(&file.path, enabled) {
                file.path = name;
            }
        }
        updated.files.sort_by(|a, b| a.path.cmp(&b.path));
        updated.revision = revision.clone();
        updated.installed_at = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(err)?
            .as_secs();
        replace_json(&target.join(MARKER), &updated)
    })();
    if let Err(error) = apply {
        let mut failures = vec![];
        for (from, to) in done.iter().rev() {
            if let Err(e) = fs::rename(to, from) {
                failures.push(err(e));
            }
        }
        if failures.is_empty() {
            let _ = fs::remove_file(journal_path(root));
            return Err(error);
        }
        // Keep the journal: `recover` can still finish the toggle from it.
        return Err(format!(
            "{error}；还原失败：{}；已保留恢复记录，重启启动器后完成或恢复切换",
            failures.join("；")
        ));
    }
    fs::remove_file(journal_path(root)).map_err(err)?;
    Ok(renames.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Sandbox {
        root: PathBuf,
    }
    impl Sandbox {
        fn new(name: &str) -> Self {
            let root = std::env::temp_dir().join(format!(
                "flotsam-pkg-{name}-{}-{}",
                std::process::id(),
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            fs::create_dir_all(root.join("BepInEx/plugins")).unwrap();
            fs::create_dir_all(root.join("BepInEx/core")).unwrap();
            fs::create_dir_all(root.join("BepInEx/.flotsam-manager")).unwrap();
            fs::write(root.join("BepInEx/core/BepInEx.dll"), b"loader").unwrap();
            Self { root }
        }
        fn plugins(&self) -> PathBuf {
            self.root.join("BepInEx/plugins")
        }
        fn source(&self, name: &str, bytes: &[u8]) -> PathBuf {
            let path = self.root.join("downloads").join(name);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, bytes).unwrap();
            path
        }
        fn install(&self, name: &str, bytes: &[u8]) -> Result<String> {
            let source = self.source(name, bytes);
            let before: HashSet<String> = fs::read_dir(self.plugins())
                .unwrap()
                .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
                .collect();
            let existing = before
                .iter()
                .find(|n| self.plugins().join(n.as_str()).join(MARKER).is_file())
                .and_then(|n| n.strip_prefix("flotsam-").map(str::to_string));
            install(
                &self.root,
                &source,
                name,
                Origin {
                    nexus_mod_id: None,
                    nexus_file_id: None,
                },
                existing.as_deref(),
                || Ok(()),
            )?;
            if let Some(id) = existing {
                return Ok(id);
            }
            for entry in fs::read_dir(self.plugins()).unwrap() {
                let entry = entry.unwrap();
                let name = entry.file_name().to_string_lossy().into_owned();
                if !before.contains(&name) {
                    if let Some(id) = name.strip_prefix("flotsam-") {
                        return Ok(id.to_string());
                    }
                }
            }
            Err("missing package directory".into())
        }
    }
    impl Drop for Sandbox {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }
    fn ok() -> Result<()> {
        Ok(())
    }
    fn dll(marker: &str) -> Vec<u8> {
        let mut bytes = vec![b'M', b'Z'];
        bytes.extend_from_slice(b"launcher-package-test");
        bytes.extend_from_slice(marker.as_bytes());
        bytes
    }
    fn read(path: &Path) -> Vec<u8> {
        fs::read(path).unwrap()
    }
    fn package_dir(root: &Path, id: &str) -> PathBuf {
        root.join("BepInEx/plugins").join(format!("flotsam-{id}"))
    }

    #[test]
    fn install_update_rollback_remove_and_restore_keep_ownership() {
        let sandbox = Sandbox::new("lifecycle");
        let root = &sandbox.root;
        let first = sandbox.install("first.dll", &dll("one")).unwrap();
        let dir = package_dir(root, &first);
        assert_eq!(read(&dir.join("first.dll")), dll("one"));
        let manifest = read_manifest(&dir).unwrap();
        assert_eq!(manifest.files.len(), 1);
        assert_eq!(manifest.id, first);
        assert!(manifest.previous_revision.is_none());

        // Update: old revision is preserved as a backup, manifest chains.
        sandbox.install("first.dll", &dll("two")).unwrap();
        assert_eq!(read(&dir.join("first.dll")), dll("two"));
        let updated = read_manifest(&dir).unwrap();
        assert_ne!(updated.revision, manifest.revision);
        let previous = updated.previous_revision.clone().unwrap();
        assert_eq!(previous, manifest.revision);
        assert!(history(root, &first, &previous)
            .unwrap()
            .join(MARKER)
            .is_file());

        // Rollback restores bytes but records the newer revision as previous.
        rollback(root, &first, ok).unwrap();
        assert_eq!(read(&dir.join("first.dll")), dll("one"));
        let rolled = read_manifest(&dir).unwrap();
        assert_eq!(rolled.previous_revision.as_deref(), Some(updated.revision.as_str()));

        // Toggle round trip keeps checksums and file ownership.
        assert_eq!(set_enabled(root, &first, false, ok).unwrap(), 1);
        assert!(!dir.join("first.dll").exists());
        assert_eq!(read(&dir.join("first.dll.disabled")), dll("one"));
        let disabled = read_manifest(&dir).unwrap();
        assert_eq!(disabled.files[0].path, "first.dll.disabled");
        assert_eq!(set_enabled(root, &first, true, ok).unwrap(), 1);
        assert_eq!(read(&dir.join("first.dll")), dll("one"));

        // Remove and restore.
        remove(root, &first, ok).unwrap();
        assert!(!dir.exists());
        let trash = removed(root, &first).unwrap();
        assert!(trash.join(MARKER).is_file());
        restore(root, &first, ok).unwrap();
        assert_eq!(read(&dir.join("first.dll")), dll("one"));
    }

    #[test]
    fn package_toggle_follows_profile_changed_physical_suffix() {
        let sandbox = Sandbox::new("profile-toggle");
        let root = &sandbox.root;
        let id = sandbox.install("mod.dll", &dll("one")).unwrap();
        let dir = package_dir(root, &id);
        fs::rename(dir.join("mod.dll"), dir.join("mod.dll.disabled")).unwrap();
        assert!(!package(root, &read_manifest(&dir).unwrap()).enabled);
        assert_eq!(set_enabled(root, &id, true, ok).unwrap(), 1);
        assert!(package(root, &read_manifest(&dir).unwrap()).enabled);
        assert_eq!(read(&dir.join("mod.dll")), dll("one"));
    }
    #[test]
    fn external_edits_block_replacement_and_removal_without_touching_files() {
        let sandbox = Sandbox::new("verify");
        let root = &sandbox.root;
        let id = sandbox.install("mod.dll", &dll("one")).unwrap();
        let dir = package_dir(root, &id);
        let edited = dir.join("mod.dll");
        fs::write(&edited, b"user edit").unwrap();
        assert!(set_enabled(root, &id, false, ok).is_err());
        assert!(remove(root, &id, ok).is_err());
        assert_eq!(read(&edited), b"user edit");
        let manifest = read_manifest(&dir).unwrap();
        assert!(manifest.files.iter().any(|f| f.path == "mod.dll"));
        // A different file appearing inside the package is also reported.
        fs::write(dir.join("stray.txt"), b"x").unwrap();
        assert!(rollback(root, &id, ok).is_err());
    }

    #[test]
    fn manifest_paths_cannot_escape_or_embed_windows_syntax() {
        for bad in [
            "../evil.dll",
            "a/../../evil.dll",
            "C:/evil.dll",
            "sub\\evil.dll",
            "/abs.dll",
            "a//b.dll",
            "./a.dll",
        ] {
            assert!(safe_path(bad).is_err(), "{bad}");
        }
        assert!(safe_path("sub/ok.dll").is_ok());
        assert!(toggled_name("a/b.DLL.disabled", true).as_deref() == Some("a/b.DLL"));
        assert!(toggled_name("a/b.dll", true).is_none());
        assert_eq!(toggled_name("a/b.dll", false).unwrap(), "a/b.dll.disabled");
    }

    #[test]
    fn interrupted_toggle_is_completed_by_recovery() {
        let sandbox = Sandbox::new("toggle-recover");
        let root = &sandbox.root;
        let id = sandbox.install("mod.dll", &dll("one")).unwrap();
        let dir = package_dir(root, &id);
        let manifest = read_manifest(&dir).unwrap();
        // Simulate a crash after the journal was written and the single rename
        // happened, but before the manifest was replaced.
        let revision = token().unwrap();
        let journal = Journal {
            schema: SCHEMA,
            kind: JournalKind::Toggle,
            id: id.clone(),
            token: token().unwrap(),
            old_revision: Some(manifest.revision.clone()),
            new_revision: Some(revision.clone()),
            enabled: Some(false),
        };
        write_json(&journal_path(root), &journal).unwrap();
        fs::rename(dir.join("mod.dll"), dir.join("mod.dll.disabled")).unwrap();
        recover(root).unwrap();
        assert!(dir.join("mod.dll.disabled").is_file());
        assert!(!journal_path(root).exists());
        let recovered = read_manifest(&dir).unwrap();
        assert_eq!(recovered.revision, revision);
        assert_eq!(recovered.files[0].path, "mod.dll.disabled");
    }

    #[test]
    fn interrupted_update_restores_previous_revision() {
        let sandbox = Sandbox::new("update-recover");
        let root = &sandbox.root;
        let id = sandbox.install("mod.dll", &dll("one")).unwrap();
        let dir = package_dir(root, &id);
        let manifest = read_manifest(&dir).unwrap();
        let backup = history(root, &id, &manifest.revision).unwrap();
        fs::create_dir_all(backup.parent().unwrap()).unwrap();
        // Simulate: old tree already moved into history, new tree not published.
        fs::rename(&dir, &backup).unwrap();
        write_json(
            &journal_path(root),
            &Journal {
                schema: SCHEMA,
                kind: JournalKind::Revise,
                id: id.clone(),
                token: token().unwrap(),
                old_revision: Some(manifest.revision.clone()),
                new_revision: Some(token().unwrap()),
                enabled: None,
            },
        )
        .unwrap();
        recover(root).unwrap();
        assert_eq!(read(&dir.join("mod.dll")), dll("one"));
        assert!(!journal_path(root).exists());
    }

    #[test]
    fn a_second_launcher_cannot_take_the_game_lock() {
        let sandbox = Sandbox::new("lock");
        let _first = lock(&sandbox.root).unwrap();
        let error = lock(&sandbox.root).unwrap_err();
        assert!(error.contains("另一个启动器"), "{error}");
        assert!(sandbox.root.join(LOCK_FILE).is_file());
        // The lock deliberately lives at the game root, not inside BepInEx.
        fs::remove_dir_all(sandbox.root.join("BepInEx")).unwrap();
        drop(_first);
        let _second = lock(&sandbox.root).unwrap();
    }
}
