use serde::{Deserialize, Serialize};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::{
    fs,
    path::{Path, PathBuf},
    process::{Child, Command},
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};
use tauri::Manager;

pub(crate) type Result<T> = std::result::Result<T, String>;
#[derive(Default, Clone)]
pub struct LauncherState {
    pub child: Arc<Mutex<Option<Child>>>,
    pub mutation: Arc<Mutex<()>>,
    pub metadata: Arc<Mutex<crate::mods::MetadataCache>>,
    // Retain the OS lock throughout the game lifetime, including Epic's bootstrap gap.
    pub game_lock: Arc<Mutex<Option<fs::File>>>,
    pub launched_at: Arc<Mutex<Option<std::time::Instant>>>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub game_path: String,
    pub backup_before_launch: bool,
    pub reduced_motion: bool,
    #[serde(default = "default_language")]
    pub language: String,
    #[serde(default = "default_display_mode")]
    pub display_mode: String,
}
pub(crate) fn default_language() -> String {
    "zh".into()
}
pub(crate) fn default_display_mode() -> String {
    "fullscreen".into()
}
pub(crate) fn normalize_display_mode(raw: &str) -> &'static str {
    match raw.trim() {
        "windowed" => "windowed",
        "game" => "game",
        _ => "fullscreen",
    }
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            game_path: "D:\\Epic Games\\Flotsam".into(),
            backup_before_launch: true,
            reduced_motion: false,
            language: default_language(),
            display_mode: default_display_mode(),
        }
    }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveSlot {
    pub slot_name: String,
    pub file_name: String,
    pub save_type: String,
    pub day: Option<u32>,
    pub drifters: Option<u32>,
    pub play_time_seconds: Option<u64>,
    pub modified: u64,
    pub size: u64,
    pub game_version: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveEntry {
    name: String,
    folder: String,
    modified: u64,
    size: u64,
    files: u64,
    community_name: Option<String>,
    latest_slot: Option<String>,
    save_type: Option<String>,
    day: Option<u32>,
    drifters: Option<u32>,
    play_time_seconds: Option<u64>,
    game_version: Option<String>,
    slots: Vec<SaveSlot>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    settings: Settings,
    installed: bool,
    loader_installed: bool,
    mods: Vec<crate::mods::ModEntry>,
    saves: Vec<SaveEntry>,
    backups: usize,
    game_version: Option<String>,
    game_running: bool,
    removed_packages: Vec<crate::packages::Package>,
    package_recovery: bool,
}

pub(crate) fn err(e: impl std::fmt::Display) -> String {
    e.to_string()
}
pub(crate) fn app_root(app: &tauri::AppHandle) -> Result<PathBuf> {
    app.path().app_local_data_dir().map_err(err)
}
fn settings_path(app: &tauri::AppHandle) -> Result<PathBuf> {
    Ok(app_root(app)?.join("settings.json"))
}
pub(crate) fn load_settings(app: &tauri::AppHandle) -> Result<Settings> {
    let p = settings_path(app)?;
    if !p.exists() {
        return Ok(Settings::default());
    }
    serde_json::from_slice(&fs::read(p).map_err(err)?).map_err(|e| format!("设置文件无法读取：{e}"))
}
pub(crate) fn game_root(settings: &Settings) -> Result<PathBuf> {
    let root = fs::canonicalize(&settings.game_path).map_err(|_| "游戏目录不存在".to_string())?;
    if !root.join("Flotsam.exe").is_file()
        || !root
            .join("Flotsam_Data/Managed/Assembly-CSharp.dll")
            .is_file()
    {
        return Err("请选择包含 Flotsam.exe 的游戏目录".into());
    }
    Ok(root)
}
pub(crate) fn flotsam_data_root() -> Result<PathBuf> {
    Ok(
        PathBuf::from(std::env::var_os("USERPROFILE").ok_or("无法读取用户目录")?)
            .join("AppData")
            .join("LocalLow")
            .join("Pajama Llama Games")
            .join("Flotsam"),
    )
}

pub(crate) fn saves_root() -> Result<PathBuf> {
    Ok(flotsam_data_root()?.join("Saves"))
}

#[cfg(windows)]
fn native_screen_resolution() -> (u32, u32) {
    use windows_sys::Win32::Graphics::Gdi::{DEVMODEW, ENUM_CURRENT_SETTINGS, EnumDisplaySettingsW};
    // SAFETY: zero-initialized POD struct with dmSize set before calling Win32 API.
    unsafe {
        let mut dm: DEVMODEW = std::mem::zeroed();
        dm.dmSize = std::mem::size_of::<DEVMODEW>() as u16;
        if EnumDisplaySettingsW(std::ptr::null(), ENUM_CURRENT_SETTINGS, &mut dm) != 0
            && dm.dmPelsWidth >= 1024
            && dm.dmPelsHeight >= 600
        {
            return (dm.dmPelsWidth, dm.dmPelsHeight);
        }
    }
    (1920, 1080)
}

pub(crate) fn apply_display_mode(mode: &str) -> Result<()> {
    let normalized = normalize_display_mode(mode);
    #[cfg(windows)]
    {
        use winreg::{enums::HKEY_CURRENT_USER, RegKey};
        let (native_w, native_h) = native_screen_resolution();
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        if let Ok((key, _)) = hkcu.create_subkey("Software\\Pajama Llama Games\\Flotsam") {
            const K_MODE: &str = "Screenmanager Fullscreen mode_h3630240806";
            const K_NATIVE: &str = "Screenmanager Resolution Use Native_h1405027254";
            const K_W: &str = "Screenmanager Resolution Width_h182942802";
            const K_H: &str = "Screenmanager Resolution Height_h2627697771";
            const K_WIN_W: &str = "Screenmanager Resolution Window Width_h2524650974";
            const K_WIN_H: &str = "Screenmanager Resolution Window Height_h1684712807";
            match normalized {
                "fullscreen" => {
                    let _ = key.set_value(K_MODE, &1u32);
                    let _ = key.set_value(K_NATIVE, &1u32);
                    let _ = key.set_value(K_W, &native_w);
                    let _ = key.set_value(K_H, &native_h);
                }
                "windowed" => {
                    let win_w = (native_w * 4 / 5).clamp(1280, 1600);
                    let win_h = (native_h * 4 / 5).clamp(720, 960);
                    let _ = key.set_value(K_MODE, &3u32);
                    let _ = key.set_value(K_NATIVE, &0u32);
                    let _ = key.set_value(K_W, &win_w);
                    let _ = key.set_value(K_H, &win_h);
                    let _ = key.set_value(K_WIN_W, &win_w);
                    let _ = key.set_value(K_WIN_H, &win_h);
                }
                _ => {
                    // Heal legacy 960x540 lab window if present; otherwise preserve in-game choice.
                    let cur_mode: u32 = key.get_value(K_MODE).unwrap_or(1);
                    let cur_w: u32 = key.get_value(K_W).unwrap_or(native_w);
                    let cur_h: u32 = key.get_value(K_H).unwrap_or(native_h);
                    if cur_mode == 3 && cur_w <= 1024 && cur_h <= 600 {
                        let _ = key.set_value(K_MODE, &1u32);
                        let _ = key.set_value(K_NATIVE, &1u32);
                        let _ = key.set_value(K_W, &native_w);
                        let _ = key.set_value(K_H, &native_h);
                    }
                }
            }
        }
    }
    #[cfg(not(windows))]
    {
        let _ = normalized;
    }
    Ok(())
}

pub(crate) fn normalize_windows_path(raw: &str) -> Result<String> {
    let unified = raw.trim().replace('/', "\\");
    let stripped = if let Some(rest) = unified.strip_prefix("\\\\?\\UNC\\") {
        format!("\\\\{rest}")
    } else if let Some(rest) = unified.strip_prefix("\\\\?\\") {
        rest.to_string()
    } else {
        unified
    };
    if stripped.is_empty() || stripped.contains('"') {
        return Err("目标路径无效".into());
    }
    Ok(stripped)
}

pub(crate) fn windows_shell_path(path: &Path) -> Result<String> {
    let canonical = fs::canonicalize(path).map_err(|_| "目标路径不存在".to_string())?;
    normalize_windows_path(&canonical.to_string_lossy())
}

#[derive(Deserialize)]
struct RawGameVersion {
    #[serde(rename = "Major")]
    major: Option<u32>,
    #[serde(rename = "Minor")]
    minor: Option<u32>,
    #[serde(rename = "Patch")]
    patch: Option<u32>,
    #[serde(rename = "AdditionalModifiers")]
    modifiers: Option<String>,
}

#[derive(Deserialize)]
struct RawSaveInfo {
    #[serde(rename = "_communityName")]
    community_name: Option<String>,
    #[serde(rename = "_name")]
    name: Option<String>,
    #[serde(rename = "_day")]
    day: Option<i64>,
    #[serde(rename = "_drifterCount")]
    drifter_count: Option<i64>,
    #[serde(rename = "_playTime")]
    play_time: Option<f64>,
    #[serde(rename = "_timestamp")]
    timestamp: Option<String>,
    #[serde(rename = "_type")]
    save_type: Option<i32>,
    #[serde(rename = "_version")]
    version: Option<RawGameVersion>,
}

#[derive(Deserialize)]
struct RawSaveEnvelope {
    #[serde(rename = "_data")]
    _data: Option<serde::de::IgnoredAny>,
    #[serde(rename = "_info")]
    info: Option<RawSaveInfo>,
}

fn parse_dotnet_date_seconds(raw: &str) -> Option<u64> {
    let start = raw.find("/Date(")? + 6;
    let rest = &raw[start..];
    let digits_len = rest
        .bytes()
        .take_while(|b| b.is_ascii_digit())
        .count();
    if digits_len == 0 {
        return None;
    }
    let ms: u64 = rest[..digits_len].parse().ok()?;
    Some(ms / 1000)
}

pub(crate) fn parse_flotsam_save_bytes(
    bytes: &[u8],
    file_name: &str,
    fallback_modified: u64,
    fallback_size: u64,
) -> Option<(Option<String>, SaveSlot)> {
    use std::io::Read;
    if bytes.len() < 2 || bytes.len() > 64 * 1024 * 1024 {
        return None;
    }
    let mut text = String::new();
    if bytes[0] == 0x1F && bytes[1] == 0x8B {
        let decoder = flate2::read::GzDecoder::new(bytes);
        decoder
            .take(64 * 1024 * 1024)
            .read_to_string(&mut text)
            .ok()?;
    } else {
        text = std::str::from_utf8(bytes).ok()?.to_string();
    }

    let info: RawSaveInfo = if let Some(idx) = text.rfind("\"_info\":") {
        let json_slice = text[idx + 8..].trim_end();
        let trimmed = json_slice.strip_suffix('}').unwrap_or(json_slice);
        serde_json::from_str(trimmed)
            .ok()
            .or_else(|| {
                serde_json::from_str::<RawSaveEnvelope>(&text)
                    .ok()
                    .and_then(|e| e.info)
            })?
    } else {
        serde_json::from_str::<RawSaveEnvelope>(&text).ok()?.info?
    };

    let stem = Path::new(file_name)
        .file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| file_name.to_string());
    let slot_name = info
        .name
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(stem);
    let save_type = match info.save_type.unwrap_or(0) {
        1 => "quicksave",
        2 => "autosave",
        16 => "snapshot",
        _ => "manual",
    }
    .to_string();
    let modified = info
        .timestamp
        .as_deref()
        .and_then(parse_dotnet_date_seconds)
        .filter(|&ts| ts > 946_684_800)
        .unwrap_or(fallback_modified);
    let game_version = info.version.and_then(|v| {
        let major = v.major?;
        let minor = v.minor.unwrap_or(0);
        let patch = v.patch.unwrap_or(0);
        let suffix = v.modifiers.unwrap_or_default();
        Some(format!("{major}.{minor}.{patch}{suffix}"))
    });
    let community = info.community_name.filter(|s| !s.trim().is_empty());

    Some((
        community,
        SaveSlot {
            slot_name,
            file_name: file_name.to_string(),
            save_type,
            day: info.day.and_then(|d| u32::try_from(d.max(0)).ok()),
            drifters: info.drifter_count.and_then(|d| u32::try_from(d.max(0)).ok()),
            play_time_seconds: info
                .play_time
                .filter(|t| t.is_finite() && *t >= 0.0)
                .map(|t| t.round() as u64),
            modified,
            size: fallback_size,
            game_version,
        },
    ))
}
pub(crate) fn safe_name(name: &str) -> Result<()> {
    if name.is_empty()
        || name == "."
        || name == ".."
        || name.chars().any(|c| "\\/:<>\"|?*".contains(c))
    {
        return Err("文件名无效".into());
    }
    Ok(())
}
pub(crate) fn walk_files(root: &Path, output: &mut Vec<PathBuf>) -> Result<()> {
    fn visit(root: &Path, output: &mut Vec<PathBuf>, depth: usize) -> Result<()> {
        if depth > 32 { return Err("目录层级超过安全上限（32）".into()); }
        let metadata = match fs::symlink_metadata(root) {
            Ok(value) => value,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(e) => return Err(err(e)),
        };
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if metadata.file_attributes() & 0x400 != 0 { return Err("管理目录不能包含链接或重解析点".into()); }
        }
        if metadata.file_type().is_symlink() { return Err("管理目录不能包含符号链接".into()); }
        if metadata.is_file() {
            if output.len() >= 32768 { return Err("文件数量超过安全上限（32768）".into()); }
            output.push(root.to_path_buf());
            return Ok(());
        }
        for entry in fs::read_dir(root).map_err(err)? {
            visit(&entry.map_err(err)?.path(), output, depth + 1)?;
        }
        Ok(())
    }
    visit(root, output, 0)
}
fn contained(root: &Path, relative: &str) -> Result<PathBuf> {
    let joined = fs::canonicalize(root.join(relative)).map_err(err)?;
    let root = fs::canonicalize(root).map_err(err)?;
    if !joined.starts_with(&root) {
        return Err("文件超出管理目录".into());
    }
    Ok(joined)
}
fn game_running(state: &LauncherState) -> Result<bool> {
    let mut child = state.child.lock().map_err(err)?;
    if let Some(process) = child.as_mut() {
        if process.try_wait().map_err(err)?.is_none() {
            return Ok(true);
        }
        *child = None;
    }
    #[cfg(windows)]
    {
        let out = Command::new("tasklist.exe")
            .args(["/FI", "IMAGENAME eq Flotsam.exe", "/FO", "CSV", "/NH"])
            .creation_flags(0x08000000)
            .output()
            .map_err(err)?;
        if !out.status.success() {
            return Err("无法确认游戏运行状态".into());
        }
        let running = String::from_utf8_lossy(&out.stdout)
            .to_ascii_lowercase()
            .contains("flotsam.exe");
        if running { return Ok(true); }
        // EOSBootstrapper can exit before the actual game process appears.
        let mut launched = state.launched_at.lock().map_err(err)?;
        if launched.is_some_and(|time| time.elapsed() < std::time::Duration::from_secs(30)) {
            return Ok(true);
        }
        *launched = None;
        *state.game_lock.lock().map_err(err)? = None;
        Ok(false)
    }
    #[cfg(not(windows))]
    {
        Ok(false)
    }
}
pub(crate) fn require_stopped(state: &LauncherState) -> Result<()> {
    if game_running(state)? {
        Err("请先退出游戏".into())
    } else {
        Ok(())
    }
}

#[tauri::command]
pub async fn get_snapshot(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
) -> Result<Snapshot> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || get_snapshot_sync(app, &state))
        .await
        .map_err(err)?
}
pub(crate) fn get_snapshot_sync(app: tauri::AppHandle, state: &LauncherState) -> Result<Snapshot> {
    let settings = load_settings(&app)?;
    let valid = game_root(&settings).ok();
    let mut mods = if let Some(root) = &valid {
        crate::mods::scan(root, &mut *state.metadata.lock().map_err(err)?)?
    } else {
        Vec::new()
    };
    let removed_packages = if let Some(root) = &valid {
        crate::packages::annotate(root, &mut mods)?
    } else {
        vec![]
    };
    let mut saves = Vec::new();
    let mut detected_game_version: Option<String> = None;
    let save_root = saves_root()?;
    if save_root.is_dir() {
        for entry in fs::read_dir(&save_root).map_err(err)? {
            let entry = entry.map_err(err)?;
            if !entry.file_type().map_err(err)?.is_dir() {
                continue;
            }
            let dir_path = entry.path();
            let mut files = Vec::new();
            walk_files(&dir_path, &mut files)?;
            let mut size = 0;
            let mut modified = 0;
            let mut slots = Vec::new();
            let mut latest_community: Option<(u64, String)> = None;
            for file in &files {
                let meta = fs::metadata(file).map_err(err)?;
                let file_size = meta.len();
                let file_modified = meta
                    .modified()
                    .map_err(err)?
                    .duration_since(UNIX_EPOCH)
                    .map_err(err)?
                    .as_secs();
                size += file_size;
                modified = modified.max(file_modified);

                let is_fs = file
                    .extension()
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("fs"));
                if is_fs {
                    let rel_name = file
                        .strip_prefix(&dir_path)
                        .unwrap_or(file)
                        .to_string_lossy()
                        .replace('\\', "/");
                    if let Ok(raw) = fs::read(file) {
                        if let Some((community, slot)) =
                            parse_flotsam_save_bytes(&raw, &rel_name, file_modified, file_size)
                        {
                            modified = modified.max(slot.modified);
                            if let Some(c) = community {
                                if latest_community
                                    .as_ref()
                                    .is_none_or(|(ts, _)| slot.modified >= *ts)
                                {
                                    latest_community = Some((slot.modified, c));
                                }
                            }
                            slots.push(slot);
                        }
                    }
                }
            }
            slots.sort_by_key(|s| std::cmp::Reverse(s.modified));
            let latest = slots.first().cloned();
            if detected_game_version.is_none() {
                detected_game_version = latest.as_ref().and_then(|s| s.game_version.clone());
            }
            let folder = entry.file_name().to_string_lossy().into_owned();
            let community_name = latest_community.map(|(_, c)| c);
            saves.push(SaveEntry {
                name: community_name.clone().unwrap_or_else(|| folder.clone()),
                folder,
                modified,
                size,
                files: files.len() as u64,
                community_name,
                latest_slot: latest.as_ref().map(|s| s.slot_name.clone()),
                save_type: latest.as_ref().map(|s| s.save_type.clone()),
                day: latest.as_ref().and_then(|s| s.day),
                drifters: latest.as_ref().and_then(|s| s.drifters),
                play_time_seconds: latest.as_ref().and_then(|s| s.play_time_seconds),
                game_version: latest.and_then(|s| s.game_version),
                slots,
            });
        }
    }
    saves.sort_by_key(|b| std::cmp::Reverse(b.modified));
    let backups_dir = app_root(&app)?.join("backups");
    let backups = if backups_dir.is_dir() {
        fs::read_dir(backups_dir)
            .map_err(err)?
            .filter_map(|e| e.ok())
            .filter(|e| e.path().is_dir() && !e.file_name().to_string_lossy().starts_with('.'))
            .count()
    } else {
        0
    };
    let loader_installed = valid
        .as_ref()
        .is_some_and(|p| crate::multiplayer::loader_ready(p));
    Ok(Snapshot {
        settings,
        installed: valid.is_some(),
        loader_installed,
        mods,
        saves,
        backups,
        game_version: detected_game_version,
        game_running: game_running(state)?,
        removed_packages,
        package_recovery: valid.as_ref().is_some_and(|root| {
            ["BepInEx/.flotsam-manager/transaction.json", "BepInEx/.flotsam-manager/profile-transaction.json", ".flotsam-loader-transaction.json"]
                .iter().any(|path| root.join(path).exists())
        }),
    })
}

#[tauri::command]
pub fn save_settings(
    app: tauri::AppHandle,
    state: tauri::State<LauncherState>,
    network: tauri::State<crate::network::NetworkState>,
    mut settings: Settings,
) -> Result<()> {
    let _guard = state.mutation.lock().map_err(err)?;
    if load_settings(&app)?.game_path != settings.game_path {
        crate::network::require_disconnected(&network)?;
    }
    settings.display_mode = normalize_display_mode(&settings.display_mode).into();
    game_root(&settings)?;
    let path = settings_path(&app)?;
    fs::create_dir_all(path.parent().ok_or("设置路径无效")?).map_err(err)?;
    let temp = path.with_extension("tmp");
    fs::write(&temp, serde_json::to_vec_pretty(&settings).map_err(err)?).map_err(err)?;
    fs::rename(temp, path).map_err(err)
}
#[tauri::command]
pub async fn select_game_directory() -> Result<Option<String>> {
    Ok(rfd::AsyncFileDialog::new()
        .set_title("选择浮游世界安装目录")
        .pick_folder()
        .await
        .map(|f| f.path().to_string_lossy().into_owned()))
}

fn copy_snapshot(source: &Path, dest: &Path) -> Result<()> {
    let mut files = Vec::new();
    walk_files(source, &mut files)?;
    for path in files {
        let target = dest.join(path.strip_prefix(source).map_err(err)?);
        fs::create_dir_all(target.parent().ok_or("备份路径无效")?).map_err(err)?;
        fs::copy(path, target).map_err(err)?;
    }
    Ok(())
}
pub(crate) fn create_backup(app: &tauri::AppHandle) -> Result<String> {
    let source = saves_root()?;
    let mut files = Vec::new();
    walk_files(&source, &mut files)?;
    if files.is_empty() {
        return Err("没有可备份的存档".into());
    }
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(err)?
        .as_millis();
    let root = app_root(app)?.join("backups");
    fs::create_dir_all(&root).map_err(err)?;
    let pending = root.join(format!(".pending-{stamp}"));
    let dest = root.join(format!("存档-{stamp}"));
    fs::create_dir(&pending).map_err(err)?;
    copy_snapshot(&source, &pending)?;
    fs::write(
        pending.join("launcher-settings.json"),
        serde_json::to_vec_pretty(&load_settings(app)?).map_err(err)?,
    )
    .map_err(err)?;
    fs::rename(&pending, &dest).map_err(err)?;
    Ok(dest.to_string_lossy().into_owned())
}
#[tauri::command]
pub async fn backup_saves(app: tauri::AppHandle, state: tauri::State<'_, LauncherState>) -> Result<String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = state.mutation.lock().map_err(err)?;
        require_stopped(&state)?;
        let root = game_root(&load_settings(&app)?)?;
        let _directory_lock = crate::packages::lock(&root)?;
        crate::save_restore::recover_before_launch(&app)?;
        let _backup_lock = crate::packages::lock_path(&app_root(&app)?.join("save-backup.lock"))?;
        require_stopped(&state)?;
        create_backup(&app)
    }).await.map_err(err)?
}
fn doorstop_allows_disable(config: &str) -> bool {
    let mut general = false;
    let mut seen = false;
    for raw in config.lines() {
        let line = raw.trim();
        if line.starts_with(['#', ';']) || line.is_empty() { continue; }
        if line.starts_with('[') { general = line.eq_ignore_ascii_case("[General]"); continue; }
        if !general { continue; }
        if let Some((key, value)) = line.split_once('=') {
            if key.trim().eq_ignore_ascii_case("ignore_disable_switch") {
                if seen || !value.trim().eq_ignore_ascii_case("false") { return false; }
                seen = true;
            }
        }
    }
    seen
}
#[tauri::command]
pub async fn launch_game(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    mode: String,
) -> Result<()> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || launch_game_sync(app, &state, mode))
        .await
        .map_err(err)?
}
fn launch_game_sync(app: tauri::AppHandle, state: &LauncherState, mode: String) -> Result<()> {
    let _guard = state.mutation.lock().map_err(err)?;
    require_stopped(state)?;
    let settings = load_settings(&app)?;
    let root = game_root(&settings)?;
    let directory_lock = crate::packages::lock(&root)?;
    require_stopped(state)?;
    crate::packages::recover(&root)?;
    crate::multiplayer::recover_loader(&root)?;
    crate::save_restore::recover_before_launch(&app)?;
    if mode != "vanilla" && mode != "modded" {
        return Err("启动模式无效".into());
    }
    if mode == "modded" {
        if !crate::multiplayer::loader_ready(&root) {
            crate::multiplayer::ensure_loader_under_lock(&app, state, &root)?;
        }
        let preflight = crate::compat::preflight(&root)?;
        if !preflight.ok {
            return Err(preflight.blockers.iter().map(|item| format!("{}（{}）", item.detail, item.fix)).collect::<Vec<_>>().join("；"));
        }
        crate::mods::require_compatible(&crate::mods::scan(
            &root,
            &mut *state.metadata.lock().map_err(err)?,
        )?)?;
    }
    if settings.backup_before_launch {
        let _backup_lock = crate::packages::lock_path(&app_root(&app)?.join("save-backup.lock"))?;
        let mut files = Vec::new();
        walk_files(&saves_root()?, &mut files)?;
        if !files.is_empty() {
            create_backup(&app)?;
        }
    }
    apply_display_mode(&settings.display_mode)?;
    // Preserve Epic's bootstrap path when present. No authentication arguments are manufactured.
    let exe = if root.join("EOSBootstrapper.exe").exists() {
        root.join("EOSBootstrapper.exe")
    } else {
        root.join("Flotsam.exe")
    };
    let mut command = Command::new(exe);
    command.current_dir(&root).env_remove("FLOTSAM_MULTIPLAYER_CONFIG");
    // Doorstop's documented process-local switch leaves the user's installed
    // loader and plugin files unchanged. Never inherit a disabled loader in modded mode.
    if mode == "vanilla" {
        if root.join("winhttp.dll").exists() {
            let config = fs::read_to_string(root.join("doorstop_config.ini")).map_err(err)?;
            if !doorstop_allows_disable(&config) {
                return Err("Doorstop 配置忽略停用开关，无法保证本次原版启动不加载插件；请先检查 ignore_disable_switch".into());
            }
        }
        command.env("DOORSTOP_DISABLE", "1");
    } else {
        command.env_remove("DOORSTOP_DISABLE");
    }
    let child = command.spawn().map_err(err)?;
    *state.child.lock().map_err(err)? = Some(child);
    *state.game_lock.lock().map_err(err)? = Some(directory_lock);
    *state.launched_at.lock().map_err(err)? = Some(std::time::Instant::now());
    Ok(())
}
#[cfg(windows)]
fn open_in_explorer(dir: &Path) -> Result<()> {
    let clean = windows_shell_path(dir)?;
    Command::new("explorer.exe")
        .arg(clean)
        .spawn()
        .map_err(err)?;
    Ok(())
}

#[cfg(windows)]
fn select_in_explorer(target: &Path) -> Result<()> {
    let clean = windows_shell_path(target)?;
    Command::new("explorer.exe")
        .raw_arg(format!("/select,\"{clean}\""))
        .spawn()
        .map_err(err)?;
    Ok(())
}

#[tauri::command]
pub fn open_directory(
    app: tauri::AppHandle,
    kind: String,
    subpath: Option<String>,
) -> Result<()> {
    let root = match kind.as_str() {
        "game" => game_root(&load_settings(&app)?)?,
        "mods" => {
            let plugins = game_root(&load_settings(&app)?)?
                .join("BepInEx")
                .join("plugins");
            if !plugins.exists() {
                fs::create_dir_all(&plugins).map_err(err)?;
            }
            plugins
        }
        "saves" => {
            let base = saves_root()?;
            if !base.exists() {
                if let Some(parent) = base.parent() {
                    if parent.is_dir() {
                        fs::create_dir_all(&base).map_err(err)?;
                    }
                }
            }
            if let Some(sub) = subpath.filter(|s| !s.trim().is_empty()) {
                safe_name(&sub)?;
                contained(&base, &sub)?
            } else {
                base
            }
        }
        "backups" => {
            let base = app_root(&app)?.join("backups");
            if !base.exists() {
                fs::create_dir_all(&base).map_err(err)?;
            }
            if let Some(sub) = subpath.filter(|s| !s.trim().is_empty()) {
                safe_name(&sub)?;
                contained(&base, &sub)?
            } else {
                base
            }
        }
        "logs" => saves_root()?
            .parent()
            .ok_or("日志目录不存在")?
            .to_path_buf(),
        _ => return Err("目录类型无效".into()),
    };
    if !root.is_dir() {
        return Err("目录尚不存在".into());
    }
    #[cfg(windows)]
    {
        open_in_explorer(&root)
    }
    #[cfg(not(windows))]
    {
        let _ = root;
        Err("当前仅支持 Windows".into())
    }
}
#[tauri::command]
pub async fn import_mod(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
) -> Result<Option<String>> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    let preflight = state.clone();
    tauri::async_runtime::spawn_blocking(move || require_stopped(&preflight))
        .await
        .map_err(err)??;
    let picked = rfd::AsyncFileDialog::new()
        .set_title("导入本地模组")
        .add_filter("BepInEx 插件", &["dll", "zip"])
        .pick_file()
        .await;
    let Some(picked) = picked else {
        return Ok(None);
    };
    let source = picked.path().to_path_buf();
    tauri::async_runtime::spawn_blocking(move || import_mod_path(app, &state, &network, &source))
        .await
        .map_err(err)?
}
fn import_mod_path(
    app: tauri::AppHandle,
    state: &LauncherState,
    network: &crate::network::NetworkState,
    source: &Path,
) -> Result<Option<String>> {
    let _guard = state.mutation.lock().map_err(err)?;
    crate::network::require_disconnected(network)?;
    require_stopped(state)?;
    let root = game_root(&load_settings(&app)?)?;
    if !crate::multiplayer::loader_ready(&root) {
        let _lock = crate::packages::lock(&root)?;
        crate::multiplayer::ensure_loader_under_lock(&app, state, &root)?;
    }
    let name = source.file_name().ok_or("文件名无效")?.to_string_lossy();
    safe_name(&name)?;
    if ![".dll", ".zip"]
        .iter()
        .any(|e| name.to_ascii_lowercase().ends_with(e))
    {
        return Err("请选择 DLL 或 ZIP 文件".into());
    }
    crate::packages::install(
        &root,
        source,
        &name,
        crate::packages::Origin {
            nexus_mod_id: None,
            nexus_file_id: None,
        },
        None,
        || require_stopped(state),
    )?;
    Ok(Some(name.into_owned()))
}

#[tauri::command]
pub async fn update_mod_package(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    id: String,
) -> Result<Option<String>> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    let preflight = state.clone();
    tauri::async_runtime::spawn_blocking(move || require_stopped(&preflight))
        .await
        .map_err(err)??;
    let Some(picked) = rfd::AsyncFileDialog::new()
        .set_title("选择模组的新版本")
        .add_filter("BepInEx 插件", &["dll", "zip"])
        .pick_file()
        .await
    else {
        return Ok(None);
    };
    let source = picked.path().to_path_buf();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = state.mutation.lock().map_err(err)?;
        crate::network::require_disconnected(&network)?;
        require_stopped(&state)?;
        let root = game_root(&load_settings(&app)?)?;
        let name = source
            .file_name()
            .ok_or("文件名无效")?
            .to_string_lossy()
            .into_owned();
        safe_name(&name)?;
        crate::packages::install(
            &root,
            &source,
            &name,
            crate::packages::Origin {
                nexus_mod_id: None,
                nexus_file_id: None,
            },
            Some(&id),
            || require_stopped(&state),
        )?;
        Ok(Some(name))
    })
    .await
    .map_err(err)?
}
#[tauri::command]
pub async fn change_mod_package(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    id: String,
    action: String,
) -> Result<()> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = state.mutation.lock().map_err(err)?;
        crate::network::require_disconnected(&network)?;
        require_stopped(&state)?;
        let root = game_root(&load_settings(&app)?)?;
        match action.as_str() {
            "remove" => crate::packages::remove(&root, &id, || require_stopped(&state)),
            "restore" => crate::packages::restore(&root, &id, || require_stopped(&state)),
            "rollback" => crate::packages::rollback(&root, &id, || require_stopped(&state)),
            "enable" => crate::packages::set_enabled(&root, &id, true, || require_stopped(&state)).map(|_| ()),
            "disable" => crate::packages::set_enabled(&root, &id, false, || require_stopped(&state)).map(|_| ()),
            "recover" => {
                let _lock = crate::packages::lock(&root)?;
                require_stopped(&state)?;
                crate::packages::recover(&root)?;
                crate::multiplayer::recover_loader(&root)
            }
            _ => Err("模组操作无效".into()),
        }
    })
    .await
    .map_err(err)?
}
#[tauri::command]
pub async fn toggle_mod(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    file: String,
    enabled: bool,
) -> Result<()> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        toggle_mod_sync(app, &state, &network, file, enabled)
    })
    .await
    .map_err(err)?
}
fn toggle_mod_sync(
    app: tauri::AppHandle,
    state: &LauncherState,
    network: &crate::network::NetworkState,
    file: String,
    enabled: bool,
) -> Result<()> {
    let _guard = state.mutation.lock().map_err(err)?;
    crate::network::require_disconnected(network)?;
    require_stopped(state)?;
    let root = game_root(&load_settings(&app)?)?;
    let plugins = root.join("BepInEx/plugins");
    let entries = crate::mods::scan(&root, &mut *state.metadata.lock().map_err(err)?)?;
    let requested = file.replace('\\', "/");
    if let Some(package) = entries.iter().find(|entry| entry.file.replace('\\', "/") == requested).and_then(|entry| entry.package.as_ref()) {
        return crate::packages::set_enabled(&root, &package.id, enabled, || require_stopped(state)).map(|_| ());
    }
    let _directory_lock = crate::packages::lock(&root)?;
    require_stopped(state)?;
    crate::packages::recover(&root)?;
    let source = contained(&plugins, &file)?;
    let name = source.file_name().ok_or("文件无效")?.to_string_lossy();
    let lower = name.to_ascii_lowercase();
    let dest = if enabled {
        if !lower.ends_with(".dll.disabled") {
            return Err("模组状态已变化，请刷新".into());
        }
        source.with_file_name(&name[..name.len() - 9])
    } else {
        if !lower.ends_with(".dll") {
            return Err("模组状态已变化，请刷新".into());
        }
        source.with_file_name(format!("{name}.disabled"))
    };
    if dest.exists() {
        return Err("目标文件已存在".into());
    }
    let normalized = source
        .strip_prefix(fs::canonicalize(&plugins).map_err(err)?)
        .map_err(err)?
        .to_string_lossy()
        .into_owned();
    let mut mods = crate::mods::scan(&root, &mut *state.metadata.lock().map_err(err)?)?;
    crate::mods::preflight_toggle(
        &mut mods,
        &normalized,
        enabled,
        root.join("BepInEx/core/BepInEx.dll").is_file(),
    )?;
    fs::rename(source, dest).map_err(err)
}

#[tauri::command]
pub async fn open_mod_file(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    file: String,
    kind: String,
    plugin_id: Option<String>,
) -> Result<()> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let root = game_root(&load_settings(&app)?)?;
        let plugins = root.join("BepInEx").join("plugins");
        let source = contained(&plugins, &file)?;
        if kind == "location" {
            #[cfg(windows)]
            {
                return select_in_explorer(&source);
            }
            #[cfg(not(windows))]
            {
                let _ = source;
                return Err("当前仅支持 Windows".into());
            }
        } else if kind == "config" {
            let id = plugin_id.ok_or("请选择插件配置")?;
            safe_name(&id)?;
            let mods = crate::mods::scan(&root, &mut *state.metadata.lock().map_err(err)?)?;
            if !mods.iter().any(|entry| {
                entry.file == file && entry.metadata.plugins.iter().any(|p| p.id == id)
            }) {
                return Err("插件声明已变化，请刷新".into());
            }
            let config = root.join("BepInEx").join("config");
            let name = format!("{id}.cfg");
            if !config.join(&name).is_file() {
                return Err("配置尚未生成，请先运行一次模组".into());
            }
            let path = contained(&config, &name)?;
            let clean = windows_shell_path(&path)?;
            Command::new("notepad.exe")
                .arg(clean)
                .spawn()
                .map_err(err)?;
            Ok(())
        } else {
            Err("文件操作无效".into())
        }
    })
    .await
    .map_err(err)?
}

pub const LAUNCHER_VERSION: &str = "0.0.1-pre";
pub const GITHUB_REPO: &str = "senran-N/Flotsam-Launcher";
pub const GITHUB_RELEASES_URL: &str = "https://github.com/senran-N/Flotsam-Launcher/releases";

#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    pub current_version: String,
    pub latest_version: String,
    pub has_update: bool,
    pub release_url: String,
    pub download_url: String,
    pub notes: String,
    pub published_at: String,
}

fn parse_semver_parts(raw: &str) -> ((u64, u64, u64), Option<String>) {
    let trimmed = raw.trim().trim_start_matches(['v', 'V']);
    let (core, pre) = match trimmed.split_once('-') {
        Some((c, p)) => (c, Some(p.to_ascii_lowercase())),
        None => (trimmed, None),
    };
    let mut nums = core
        .split('.')
        .map(|s| s.parse::<u64>().unwrap_or(0));
    let major = nums.next().unwrap_or(0);
    let minor = nums.next().unwrap_or(0);
    let patch = nums.next().unwrap_or(0);
    ((major, minor, patch), pre)
}

pub fn is_newer_version(current: &str, latest: &str) -> bool {
    let c_clean = current.trim().trim_start_matches(['v', 'V']);
    let l_clean = latest.trim().trim_start_matches(['v', 'V']);
    if l_clean.is_empty() || c_clean.eq_ignore_ascii_case(l_clean) {
        return false;
    }
    let (c_ver, c_pre) = parse_semver_parts(c_clean);
    let (l_ver, l_pre) = parse_semver_parts(l_clean);
    if l_ver != c_ver {
        return l_ver > c_ver;
    }
    match (c_pre, l_pre) {
        (Some(_), None) => true,
        (None, Some(_)) => false,
        (Some(cp), Some(lp)) => lp > cp,
        (None, None) => false,
    }
}

#[tauri::command]
pub async fn check_launcher_update() -> Result<UpdateInfo> {
    let api_url = format!("https://api.github.com/repos/{GITHUB_REPO}/releases?per_page=10");
    let client = reqwest::Client::builder()
        .user_agent(format!("Flotsam-Launcher/{LAUNCHER_VERSION}"))
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .map_err(err)?;

    let response = client
        .get(&api_url)
        .header("Accept", "application/vnd.github+json")
        .send()
        .await
        .map_err(|e| format!("无法连接 GitHub 更新源：{e}"))?;

    if response.status().as_u16() == 404 {
        return Ok(UpdateInfo {
            current_version: LAUNCHER_VERSION.into(),
            latest_version: LAUNCHER_VERSION.into(),
            has_update: false,
            release_url: GITHUB_RELEASES_URL.into(),
            download_url: GITHUB_RELEASES_URL.into(),
            notes: String::new(),
            published_at: String::new(),
        });
    }

    if !response.status().is_success() {
        return Err(format!("GitHub 返回状态异常 ({})", response.status()));
    }

    let releases: serde_json::Value = response.json().await.map_err(err)?;
    let Some(list) = releases.as_array() else {
        return Err("GitHub 发布列表格式无效".into());
    };

    let newest = list
        .iter()
        .find(|item| !item.get("draft").and_then(|v| v.as_bool()).unwrap_or(false));

    let Some(rel) = newest else {
        return Ok(UpdateInfo {
            current_version: LAUNCHER_VERSION.into(),
            latest_version: LAUNCHER_VERSION.into(),
            has_update: false,
            release_url: GITHUB_RELEASES_URL.into(),
            download_url: GITHUB_RELEASES_URL.into(),
            notes: String::new(),
            published_at: String::new(),
        });
    };

    let tag = rel
        .get("tag_name")
        .and_then(|v| v.as_str())
        .unwrap_or(LAUNCHER_VERSION)
        .trim_start_matches(['v', 'V'])
        .to_string();
    let html_url = rel
        .get("html_url")
        .and_then(|v| v.as_str())
        .unwrap_or(GITHUB_RELEASES_URL)
        .to_string();
    let notes = rel
        .get("body")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let published_at = rel
        .get("published_at")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();

    let mut download_url = html_url.clone();
    if let Some(assets) = rel.get("assets").and_then(|v| v.as_array()) {
        if let Some(asset) = assets.iter().find(|a| {
            a.get("name")
                .and_then(|n| n.as_str())
                .is_some_and(|n| {
                    let lower = n.to_ascii_lowercase();
                    lower.ends_with(".exe") || lower.ends_with(".zip")
                })
        }) {
            if let Some(dl) = asset.get("browser_download_url").and_then(|u| u.as_str()) {
                download_url = dl.to_string();
            }
        }
    }

    let has_update = is_newer_version(LAUNCHER_VERSION, &tag);
    Ok(UpdateInfo {
        current_version: LAUNCHER_VERSION.into(),
        latest_version: tag,
        has_update,
        release_url: html_url,
        download_url,
        notes,
        published_at,
    })
}

#[tauri::command]
pub fn open_github_url(url: Option<String>) -> Result<()> {
    let target = url
        .map(|u| u.trim().to_string())
        .filter(|u| !u.is_empty())
        .unwrap_or_else(|| GITHUB_RELEASES_URL.to_string());
    if !target.starts_with("https://github.com/senran-N/Flotsam-Launcher")
        && !target.starts_with("https://objects.githubusercontent.com/")
    {
        return Err("仅允许打开官方 GitHub 发布链接".into());
    }
    #[cfg(windows)]
    {
        Command::new("explorer.exe")
            .arg(&target)
            .spawn()
            .map_err(err)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn sandbox() -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "flotsam-test-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&p).unwrap();
        p
    }
    #[test]
    fn vanilla_loader_disable_requires_an_unambiguous_enabled_switch() {
        assert!(doorstop_allows_disable("[General]\nignore_disable_switch = false\n"));
        assert!(!doorstop_allows_disable("[General]\nignore_disable_switch = true\n"));
        assert!(!doorstop_allows_disable("[Other]\nignore_disable_switch = false\n"));
        assert!(!doorstop_allows_disable("[General]\nignore_disable_switch=false\nignore_disable_switch=true\n"));
    }
    #[test]
    fn prevents_directory_escape() {
        let p = sandbox();
        fs::create_dir(p.join("plugins")).unwrap();
        fs::write(p.join("outside.dll"), b"outside").unwrap();
        assert!(contained(&p.join("plugins"), "../outside.dll").is_err());
        fs::remove_dir_all(p).unwrap();
    }
    #[test]
    fn backup_preserves_nested_bytes() {
        let p = sandbox();
        let src = p.join("source");
        fs::create_dir_all(src.join("town/Autosaves")).unwrap();
        fs::write(src.join("town/Autosaves/save.wpd"), [0, 1, 255, 3]).unwrap();
        copy_snapshot(&src, &p.join("backup")).unwrap();
        assert_eq!(
            fs::read(p.join("backup/town/Autosaves/save.wpd")).unwrap(),
            vec![0, 1, 255, 3]
        );
        assert!(src.join("town/Autosaves/save.wpd").exists());
        fs::remove_dir_all(p).unwrap();
    }
    #[test]
    fn rejects_invalid_game_directory() {
        let p = sandbox();
        assert!(game_root(&Settings {
            game_path: p.to_string_lossy().into(),
            ..Default::default()
        })
        .is_err());
        fs::remove_dir_all(p).unwrap();
    }
    #[test]
    fn windows_path_normalizer_strips_verbatim_prefix_and_forward_slashes() {
        assert_eq!(
            normalize_windows_path(r"\\?\D:\Epic Games\Flotsam/BepInEx/plugins").unwrap(),
            r"D:\Epic Games\Flotsam\BepInEx\plugins"
        );
        assert_eq!(
            normalize_windows_path(
                r"C:\Users\21143\AppData/LocalLow/Pajama Llama Games/Flotsam/Saves"
            )
            .unwrap(),
            r"C:\Users\21143\AppData\LocalLow\Pajama Llama Games\Flotsam\Saves"
        );
        assert_eq!(
            normalize_windows_path(r"\\?\UNC\NasServer\Share/Saves").unwrap(),
            r"\\NasServer\Share\Saves"
        );
    }
    #[test]
    fn parses_flotsam_gzip_save_metadata() {
        use flate2::{write::GzEncoder, Compression};
        use std::io::Write;
        let payload = r#"{"_data":[31,139,8,0],"_info":{"_communityName":"海上灯塔镇","_day":14,"_drifterCount":8,"_name":"Autosave 2","_playTime":5432.4,"_size":29126,"_timestamp":"\/Date(1794030898747+0800)\/","_type":2,"_version":{"AdditionalModifiers":"f9","Major":1,"Minor":0,"Patch":1,"Save":9}}}"#;
        let mut gz = GzEncoder::new(Vec::new(), Compression::fast());
        gz.write_all(payload.as_bytes()).unwrap();
        let compressed = gz.finish().unwrap();
        let (community, slot) =
            parse_flotsam_save_bytes(&compressed, "Autosave 2.fs", 100, compressed.len() as u64)
                .unwrap();
        assert_eq!(community.as_deref(), Some("海上灯塔镇"));
        assert_eq!(slot.slot_name, "Autosave 2");
        assert_eq!(slot.save_type, "autosave");
        assert_eq!(slot.day, Some(14));
        assert_eq!(slot.drifters, Some(8));
        assert_eq!(slot.play_time_seconds, Some(5432));
        assert_eq!(slot.modified, 1794030898);
        assert_eq!(slot.game_version.as_deref(), Some("1.0.1f9"));
    }
    #[test]
    fn compares_semver_and_prerelease_versions_accurately() {
        assert!(!is_newer_version("0.0.1-pre", "0.0.1-pre"));
        assert!(!is_newer_version("0.0.1-pre", "v0.0.1-pre"));
        assert!(is_newer_version("0.0.1-pre", "0.0.1"));
        assert!(is_newer_version("0.0.1-pre", "v0.0.2-pre"));
        assert!(is_newer_version("0.0.1-pre", "0.1.0"));
        assert!(!is_newer_version("0.0.1", "0.0.1-pre"));
    }
}
