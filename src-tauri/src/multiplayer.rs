use crate::service::{
    app_root, err, game_root, load_settings, require_stopped, LauncherState, Result,
};
use serde::Serialize;
use std::{
    fs,
    io::{Cursor, Read, Write},
    path::{Path, PathBuf},
    process::Command,
};

const PLUGIN: &[u8] = include_bytes!("../../multiplayer/payload/Flotsam.Multiplayer.dll");
const LOADER: &[u8] = include_bytes!("../../multiplayer/payload/BepInEx.zip");
const PLUGIN_PATH: &str = "BepInEx/plugins/Flotsam.Multiplayer/Flotsam.Multiplayer.dll";
const DISABLED_PLUGIN_PATH: &str =
    "BepInEx/plugins/Flotsam.Multiplayer/Flotsam.Multiplayer.dll.disabled";
const LOADER_LICENSE: &str = include_str!("../../THIRD-PARTY/BepInEx-LICENSE.txt");

fn supported(root: &Path) -> Result<()> {
    if !root.join("Flotsam_Data/Managed/Assembly-CSharp.dll").is_file() {
        return Err("未找到游戏核心程序集 Flotsam_Data/Managed/Assembly-CSharp.dll".into());
    }
    Ok(())
}

pub(crate) fn loader_ready(root: &Path) -> bool {
    root.join("BepInEx/core/BepInEx.dll").is_file()
        && root.join("winhttp.dll").is_file()
        && root.join("doorstop_config.ini").is_file()
}

pub(crate) fn installed(root: &Path) -> bool {
    !root.join(DISABLED_PLUGIN_PATH).exists()
        && fs::read(root.join(PLUGIN_PATH)).is_ok_and(|bytes| bytes == PLUGIN)
        && loader_ready(root)
}

pub(crate) fn ensure_loader_under_lock(
    app: &tauri::AppHandle,
    state: &LauncherState,
    root: &Path,
) -> Result<()> {
    if loader_ready(root) {
        return Ok(());
    }
    require_stopped(state)?;
    crate::packages::recover(root)?;
    recover_loader(root)?;
    supported(root)?;
    let entries = loader_entries(root)?;
    let licenses = app_root(app)?.join("licenses");
    fs::create_dir_all(&licenses).map_err(err)?;
    fs::write(licenses.join("BepInEx-LICENSE.txt"), LOADER_LICENSE).map_err(err)?;
    publish_loader(root, entries, || require_stopped(state))
}

pub(crate) fn ensure_multiplayer_under_lock(
    app: &tauri::AppHandle,
    state: &LauncherState,
    root: &Path,
) -> Result<()> {
    ensure_loader_under_lock(app, state, root)?;
    supported(root)?;
    publish_plugin(root, || require_stopped(state))
}

pub(crate) fn ensure_installed(
    app: &tauri::AppHandle,
    state: &LauncherState,
    root: &Path,
) -> Result<()> {
    if installed(root) {
        return Ok(());
    }
    require_stopped(state)?;
    let _directory_lock = crate::packages::lock(root)?;
    ensure_multiplayer_under_lock(app, state, root)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MultiplayerStatus {
    installed: bool,
    version: &'static str,
    runtime: Option<serde_json::Value>,
}

#[tauri::command]
pub fn multiplayer_status(app: tauri::AppHandle) -> Result<MultiplayerStatus> {
    let root = game_root(&load_settings(&app)?)?;
    let path = app_root(&app)?.join("multiplayer/session/probe.json");
    let runtime = fs::metadata(&path)
        .ok()
        .filter(|m| m.len() < 65536)
        .and_then(|m| m.modified().ok())
        .filter(|time| time.elapsed().is_ok_and(|age| age.as_secs() < 12))
        .and_then(|_| fs::read(path).ok())
        .and_then(|bytes| serde_json::from_slice(&bytes).ok());
    Ok(MultiplayerStatus {
        installed: installed(&root),
        version: "0.0.1-pre",
        runtime,
    })
}

fn loader_entries(root: &Path) -> Result<Vec<(PathBuf, Vec<u8>)>> {
    let mut archive = zip::ZipArchive::new(Cursor::new(LOADER)).map_err(err)?;
    let mut entries = Vec::new();
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(err)?;
        if entry.is_dir() {
            continue;
        }
        let relative = entry.enclosed_name().ok_or("加载器路径无效")?;
        if entry.size() > 16 * 1024 * 1024 || entries.len() >= 256 {
            return Err("加载器文件过大".into());
        }
        let mut bytes = Vec::new();
        entry.read_to_end(&mut bytes).map_err(err)?;
        let target = root.join(relative);
        crate::packages::boundary(root, &target)?;
        entries.push((target, bytes));
    }
    Ok(entries)
}

pub(crate) fn recover_loader(root: &Path) -> Result<()> {
    for legacy in [
        ".flotsam-loader-stage",
        ".flotsam-loader-transaction.pending",
        ".flotsam-loader-transaction.json",
    ] {
        let path = root.join(legacy);
        crate::packages::boundary(root, &path)?;
        if path.is_file() {
            fs::remove_file(path).map_err(err)?;
        }
    }
    Ok(())
}

fn write_atomic(root: &Path, target: &Path, bytes: &[u8]) -> Result<()> {
    crate::packages::boundary(root, target)?;
    let parent = target.parent().ok_or("安装目标路径无效")?;
    fs::create_dir_all(parent).map_err(err)?;
    let stage = target.with_extension("installing");
    crate::packages::boundary(root, &stage)?;
    if stage.exists() {
        fs::remove_file(&stage).map_err(err)?;
    }
    let mut file = fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&stage)
        .map_err(err)?;
    file.write_all(bytes).map_err(err)?;
    file.sync_all().map_err(err)?;
    drop(file);
    fs::rename(&stage, target).map_err(err)
}

fn publish_loader(
    root: &Path,
    entries: Vec<(PathBuf, Vec<u8>)>,
    check: impl Fn() -> Result<()>,
) -> Result<()> {
    for (path, bytes) in entries {
        if path.exists() {
            continue;
        }
        check()?;
        write_atomic(root, &path, &bytes)?;
    }
    Ok(())
}

pub(crate) fn refresh_existing_plugin(root: &Path) {
    let dir = root.join("BepInEx/plugins/Flotsam.Multiplayer");
    if dir.is_dir() && !installed(root) {
        let _ = publish_plugin(root, || Ok(()));
    }
}

fn publish_plugin(root: &Path, check: impl Fn() -> Result<()>) -> Result<()> {
    let disabled = root.join(DISABLED_PLUGIN_PATH);
    crate::packages::boundary(root, &disabled)?;
    if disabled.is_file() {
        check()?;
        fs::remove_file(&disabled).map_err(err)?;
    }
    let target = root.join(PLUGIN_PATH);
    if fs::read(&target).is_ok_and(|old| old == PLUGIN) {
        return Ok(());
    }
    check()?;
    write_atomic(root, &target, PLUGIN)
}

#[tauri::command]
pub async fn install_loader(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
) -> Result<()> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = state.mutation.lock().map_err(err)?;
        require_stopped(&state)?;
        let root = game_root(&load_settings(&app)?)?;
        let _directory_lock = crate::packages::lock(&root)?;
        ensure_loader_under_lock(&app, &state, &root)
    })
    .await
    .map_err(err)?
}

#[tauri::command]
pub async fn install_multiplayer(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
) -> Result<()> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || install_multiplayer_sync(app, &state))
        .await
        .map_err(err)?
}

fn install_multiplayer_sync(app: tauri::AppHandle, state: &LauncherState) -> Result<()> {
    let _guard = state.mutation.lock().map_err(err)?;
    require_stopped(state)?;
    let root = game_root(&load_settings(&app)?)?;
    let _directory_lock = crate::packages::lock(&root)?;
    ensure_multiplayer_under_lock(&app, state, &root)
}

#[tauri::command]
pub async fn launch_multiplayer(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    auto_start: Option<bool>,
) -> Result<()> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        launch_multiplayer_sync(app, &state, &network, auto_start.unwrap_or(false))
    })
    .await
    .map_err(err)?
}

fn launch_multiplayer_sync(
    app: tauri::AppHandle,
    state: &LauncherState,
    network: &crate::network::NetworkState,
    auto_start: bool,
) -> Result<()> {
    let _guard = state.mutation.lock().map_err(err)?;
    require_stopped(state)?;
    let settings = load_settings(&app)?;
    let root = game_root(&settings)?;
    let directory_lock = crate::packages::lock(&root)?;
    require_stopped(state)?;
    crate::packages::recover(&root)?;
    recover_loader(&root)?;
    supported(&root)?;
    crate::save_restore::recover_before_launch(&app)?;
    if !installed(&root) {
        ensure_multiplayer_under_lock(&app, state, &root)?;
    }
    let preflight = crate::compat::preflight(&root)?;
    if !preflight.ok {
        return Err(preflight
            .blockers
            .iter()
            .map(|item| format!("{}（{}）", item.detail, item.fix))
            .collect::<Vec<_>>()
            .join("；"));
    }
    crate::mods::require_compatible(&crate::mods::scan(
        &root,
        &mut *state.metadata.lock().map_err(err)?,
    )?)?;
    let connection = crate::network::game_connection(network)?;
    if connection.host && settings.backup_before_launch {
        let _backup_lock = crate::packages::lock_path(&app_root(&app)?.join("save-backup.lock"))?;
        let mut files = Vec::new();
        crate::service::walk_files(&crate::service::saves_root()?, &mut files)?;
        if !files.is_empty() {
            crate::service::create_backup(&app)?;
        }
    }
    crate::service::apply_display_mode(&settings.display_mode)?;
    let base = app_root(&app)?.join("multiplayer");
    let session = base.join("session");
    let real_data_root = crate::service::flotsam_data_root()?;
    let profile = if connection.host {
        real_data_root.clone()
    } else {
        base.join("client-profile")
    };
    fs::create_dir_all(&session).map_err(err)?;
    fs::create_dir_all(&profile).map_err(err)?;
    if !connection.host {
        let src_prefs = real_data_root.join("PlayerSettings.json");
        let dst_prefs = profile.join("PlayerSettings.json");
        if src_prefs.is_file() && !dst_prefs.exists() {
            let _ = fs::copy(&src_prefs, &dst_prefs);
        }
    }
    let stop = session.join("stop");
    if stop.exists() {
        fs::remove_file(stop).map_err(err)?;
    }
    let display_mode = crate::service::normalize_display_mode(&settings.display_mode);
    let config = session.join("network.json");
    fs::write(
        &config,
        serde_json::to_vec_pretty(&serde_json::json!({
            "role": if connection.host { "host" } else { "client" },
            "address": connection.address.ip().to_string(),
            "port": connection.address.port(),
            "secret": connection.secret,
            "profile": profile.to_string_lossy(),
            "mode": connection.mode,
            "nickname": connection.nickname,
            "displayMode": display_mode,
            "rules": {
                "stealing": connection.rules.stealing,
                "sabotage": connection.rules.sabotage,
                "trading": connection.rules.trading,
                "friendlyFire": connection.rules.friendly_fire
            },
            "autoStart": auto_start && connection.host
        }))
        .map_err(err)?,
    )
    .map_err(err)?;
    let executable = root.join(if root.join("EOSBootstrapper.exe").is_file() {
        "EOSBootstrapper.exe"
    } else {
        "Flotsam.exe"
    });
    let child = Command::new(executable)
        .current_dir(root)
        .env("FLOTSAM_MULTIPLAYER_CONFIG", config)
        .env_remove("DOORSTOP_DISABLE")
        .spawn()
        .map_err(err)?;
    *state.child.lock().map_err(err)? = Some(child);
    *state.game_lock.lock().map_err(err)? = Some(directory_lock);
    *state.launched_at.lock().map_err(err)? = Some(std::time::Instant::now());
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn bundled_loader_has_valid_entries_without_hash_lock() {
        let root = std::env::temp_dir().join(format!("flotsam-loader-{}", std::process::id()));
        let entries = loader_entries(&root).unwrap();
        assert!(entries.iter().any(|(path, _)| path.ends_with("winhttp.dll")));
        assert!(entries
            .iter()
            .any(|(path, _)| path.ends_with("BepInEx/core/BepInEx.dll")));
        assert!(entries.iter().all(|(path, _)| path.starts_with(&root)));
    }

    #[test]
    fn loader_and_plugin_install_cleanly_and_preserve_existing_files() {
        let root = std::env::temp_dir().join(format!(
            "flotsam-install-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(root.join("Flotsam_Data/Managed")).unwrap();
        fs::write(
            root.join("Flotsam_Data/Managed/Assembly-CSharp.dll"),
            b"any-game-version",
        )
        .unwrap();
        fs::write(root.join("foreign.txt"), b"keep").unwrap();

        supported(&root).unwrap();
        publish_loader(&root, loader_entries(&root).unwrap(), || Ok(())).unwrap();
        publish_plugin(&root, || Ok(())).unwrap();

        assert!(loader_ready(&root));
        assert!(installed(&root));
        assert_eq!(fs::read(root.join("foreign.txt")).unwrap(), b"keep");
        fs::remove_dir_all(root).unwrap();
    }
}
