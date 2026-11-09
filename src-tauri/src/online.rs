use crate::service::{
    app_root, err, game_root, load_settings, require_stopped, safe_name, LauncherState, Result,
};
use reqwest::{Client, Url};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs,
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex, OnceLock,
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tokio::sync::watch;

const MAX_DOWNLOAD: u64 = 64 * 1024 * 1024;
const MAX_CACHE_ENTRIES: usize = 256;
const MAX_RECORD_BYTES: u64 = 8192;
/// A resolved download URL stays usable for this long. It is kept in memory
/// only: Nexus download links carry a per-account key in the query string, so
/// neither the link nor the API key is ever written to disk.
const RESUME_TTL: Duration = Duration::from_secs(600);
const MAX_RESUME_ENTRIES: usize = 32;

#[derive(Default)]
pub struct OnlineState {
    key: Mutex<Option<String>>,
    account: Mutex<Option<Value>>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OnlineMod {
    id: u64,
    name: String,
    author: String,
    summary: String,
    version: String,
}
#[derive(Serialize)]
pub struct Catalog {
    mods: Vec<OnlineMod>,
    live: bool,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OnlineFile {
    id: u64,
    name: String,
    version: String,
    file_name: String,
    size: u64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Downloaded {
    pub id: String,
    pub mod_id: u64,
    pub file_id: u64,
    pub name: String,
    pub size: u64,
    pub sha256: String,
}
impl OnlineState {
    /// The account key currently held in memory. Returning a clone lets the
    /// download queue start a task after the command returns.
    pub(crate) fn download_key(&self) -> Result<String> {
        key(self)
    }
}
/// Cooperative cancellation used by the download queue. A watch channel is
/// used instead of a bare flag so an in-flight HTTP body read is woken up
/// immediately instead of waiting for the next chunk (or the 180 s timeout).
#[derive(Debug)]
pub(crate) struct Cancel {
    sender: watch::Sender<bool>,
}
impl Cancel {
    pub(crate) fn new() -> Self {
        let (sender, _receiver) = watch::channel(false);
        Self { sender }
    }
    pub(crate) fn cancel(&self) {
        // `send` fails when no receiver is currently subscribed (for example a
        // task cancelled before its download started), so the value must be
        // replaced unconditionally.
        self.sender.send_replace(true);
    }
    pub(crate) fn is_cancelled(&self) -> bool {
        *self.sender.borrow()
    }
    /// Resolves as soon as cancellation is requested, including when it was
    /// requested before this future was created.
    pub(crate) async fn wait(&self) {
        let mut receiver = self.sender.subscribe();
        let _ = receiver.wait_for(|value| *value).await;
    }
}
/// In-memory resume data for interrupted downloads. Nothing here is persisted:
/// the URL carries the account key and must not survive the process.
struct Resume {
    url: String,
    total: u64,
    etag: String,
    last_modified: String,
    saved: Instant,
}
fn resumes() -> &'static Mutex<HashMap<String, Resume>> {
    static RESUMES: OnceLock<Mutex<HashMap<String, Resume>>> = OnceLock::new();
    RESUMES.get_or_init(|| Mutex::new(HashMap::new()))
}
fn remember_resume(id: &str, url: &str, total: u64, etag: &str, last_modified: &str) {
    let Ok(mut map) = resumes().lock() else {
        return;
    };
    map.retain(|_, value| value.saved.elapsed() < RESUME_TTL);
    if map.len() >= MAX_RESUME_ENTRIES && !map.contains_key(id) {
        if let Some(oldest) = map
            .iter()
            .min_by_key(|(_, value)| value.saved.elapsed())
            .map(|(key, _)| key.clone())
        {
            map.remove(&oldest);
        }
    }
    map.insert(
        id.to_string(),
        Resume {
            url: url.to_string(),
            total,
            etag: etag.to_string(),
            last_modified: last_modified.to_string(),
            saved: Instant::now(),
        },
    );
}
fn take_resume(id: &str, url: &str) -> Option<(u64, String, String)> {
    let mut map = resumes().lock().ok()?;
    let value = map.get(id)?;
    if value.saved.elapsed() >= RESUME_TTL || value.url != url {
        map.remove(id);
        return None;
    }
    Some((value.total, value.etag.clone(), value.last_modified.clone()))
}
fn forget_resume(id: &str) {
    if let Ok(mut map) = resumes().lock() {
        map.remove(id);
    }
}
fn client_with(https_only: bool) -> Result<Client> {
    Client::builder()
        .https_only(https_only)
        .user_agent("FlotsamLauncher/0.1.0")
        .connect_timeout(Duration::from_secs(12))
        .timeout(Duration::from_secs(180))
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            if attempt.previous().len() > 5 || !download_host(attempt.url()) {
                attempt.error("下载地址不在 Nexus 官方域名内")
            } else {
                attempt.follow()
            }
        }))
        .build()
        .map_err(err)
}
fn client() -> Result<Client> {
    client_with(true)
}
/// The only place plain HTTP is tolerated is a unit test against 127.0.0.1.
fn download_client(url: &Url) -> Result<Client> {
    let plain_http_allowed = cfg!(test) && url.scheme() == "http";
    client_with(!plain_http_allowed)
}
fn download_host(url: &Url) -> bool {
    url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.port_or_known_default() == Some(443)
        && url.host_str().is_some_and(|h| {
            h == "nexusmods.com"
                || h.ends_with(".nexusmods.com")
                || h == "nexus-cdn.com"
                || h.ends_with(".nexus-cdn.com")
        })
}
fn key(state: &OnlineState) -> Result<String> {
    state
        .key
        .lock()
        .map_err(err)?
        .clone()
        .ok_or_else(|| "请先连接 Nexus 账号，或在作者页面下载后导入".into())
}
fn status_error(status: u16) -> String {
    match status {
        401 => "Nexus API Key 无效，请重新连接".into(),
        403 => "Nexus 未授权直链下载；普通账号请打开作者页面手动下载后导入".into(),
        404 => "Nexus 上找不到该文件，可能已被作者删除".into(),
        429 => "Nexus 请求额度已用完，请稍后重试".into(),
        _ => format!("Nexus 请求失败（{status}）"),
    }
}
async fn api(path: &str, key: &str) -> Result<Value> {
    let response = client()?
        .get(format!("https://api.nexusmods.com/v1/{path}"))
        .header("apikey", key)
        .header("Application-Name", "FlotsamLauncher")
        .header("Application-Version", "0.1.0")
        .send()
        .await
        .map_err(|_| "无法连接 Nexus，请检查网络后重试")?;
    let status = response.status().as_u16();
    if status != 200 {
        return Err(status_error(status));
    }
    response
        .json()
        .await
        .map_err(|_| "Nexus 返回的数据无法读取".into())
}
fn curated() -> Vec<OnlineMod> {
    serde_json::from_str(include_str!("../../src/data/mod-catalog.json")).expect("bundled catalog")
}
#[tauri::command]
pub async fn connect_nexus(state: tauri::State<'_, OnlineState>, api_key: String) -> Result<Value> {
    let api_key = api_key.trim();
    if api_key.len() < 20 || api_key.len() > 1024 || !api_key.is_ascii() {
        return Err("请输入有效的 Nexus 个人 API Key".into());
    }
    if api_key.chars().any(char::is_control) {
        return Err("请输入有效的 Nexus 个人 API Key".into());
    }
    let user = api("users/validate.json", api_key).await?;
    *state.key.lock().map_err(err)? = Some(api_key.into());
    let account = serde_json::json!({"name":user["name"],"premium":user["is_premium"]==true});
    *state.account.lock().map_err(err)? = Some(account.clone());
    Ok(account)
}
#[tauri::command]
pub fn nexus_account(state: tauri::State<OnlineState>) -> Result<Option<Value>> {
    Ok(state.account.lock().map_err(err)?.clone())
}
#[tauri::command]
pub fn disconnect_nexus(state: tauri::State<OnlineState>) -> Result<()> {
    state.key.lock().map_err(err)?.take();
    state.account.lock().map_err(err)?.take();
    if let Ok(mut map) = resumes().lock() {
        map.clear();
    }
    Ok(())
}
#[tauri::command]
pub async fn online_catalog(state: tauri::State<'_, OnlineState>) -> Result<Catalog> {
    let Ok(key) = key(&state) else {
        return Ok(Catalog {
            mods: curated(),
            live: false,
        });
    };
    let value = api("games/flotsam/mods/latest_added.json", &key).await?;
    let array = value.as_array().ok_or("Nexus 模组列表格式无效")?;
    let mods = array
        .iter()
        .filter_map(|v| {
            Some(OnlineMod {
                id: v["mod_id"].as_u64()?,
                name: v["name"].as_str()?.into(),
                author: v["author"].as_str().unwrap_or("").into(),
                summary: v["summary"].as_str().unwrap_or("").into(),
                version: v["version"].as_str().unwrap_or("").into(),
            })
        })
        .collect();
    Ok(Catalog { mods, live: true })
}
#[tauri::command]
pub async fn online_files(
    state: tauri::State<'_, OnlineState>,
    mod_id: u64,
) -> Result<Vec<OnlineFile>> {
    if mod_id == 0 {
        return Err("模组编号无效".into());
    }
    let value = api(
        &format!("games/flotsam/mods/{mod_id}/files.json"),
        &key(&state)?,
    )
    .await?;
    let files = value["files"].as_array().ok_or("Nexus 文件列表格式无效")?;
    Ok(files
        .iter()
        .filter(|v| matches!(v["category_id"].as_u64(), Some(1..=4)))
        .filter_map(|v| {
            Some(OnlineFile {
                id: v["file_id"].as_u64()?,
                name: v["name"].as_str()?.into(),
                version: v["version"].as_str().unwrap_or("").into(),
                file_name: v["file_name"].as_str()?.into(),
                size: v["size_in_bytes"].as_u64().unwrap_or(0),
            })
        })
        .collect())
}
fn download_root(app: &tauri::AppHandle) -> Result<PathBuf> {
    Ok(app_root(app)?.join("downloads"))
}
fn nxm_path(link: &str, mod_id: u64, file_id: u64) -> Result<String> {
    let url = Url::parse(link.trim()).map_err(|_| "下载管理器链接无效")?;
    if url.scheme() != "nxm"
        || url.host_str() != Some("flotsam")
        || url.path() != format!("/mods/{mod_id}/files/{file_id}")
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("链接必须指向当前浮游世界模组文件".into());
    }
    let query: std::collections::HashMap<_, _> = url.query_pairs().into_owned().collect();
    let key = query
        .get("key")
        .filter(|s| !s.is_empty() && s.len() < 512 && s.bytes().all(|b| b.is_ascii_graphic()))
        .ok_or("链接缺少下载授权 key")?;
    let expires: u64 = query
        .get("expires")
        .and_then(|s| s.parse().ok())
        .ok_or("链接缺少有效期")?;
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(err)?
        .as_secs();
    if expires <= now {
        return Err("下载链接已过期，请从作者页面重新获取".into());
    }
    let query = Url::parse_with_params(
        "https://api.nexusmods.com/",
        [("key", key.as_str()), ("expires", &expires.to_string())],
    )
    .map_err(err)?;
    Ok(format!(
        "games/flotsam/mods/{mod_id}/files/{file_id}/download_link.json?{}",
        query.query().unwrap_or("")
    ))
}
/// Strictly validate a cache identity. Only the exact `nexus-{mod}-{file}`
/// spelling produced by the queue is accepted, so a crafted record name cannot
/// escape the download directory or alias another entry.
pub(crate) fn parse_id(id: &str) -> Result<(u64, u64)> {
    let rest = id
        .strip_prefix("nexus-")
        .ok_or("下载记录无效")?;
    let (mod_part, file_part) = rest.split_once('-').ok_or("下载记录无效")?;
    let digits = |value: &str| {
        !value.is_empty()
            && value.len() <= 20
            && value.bytes().all(|b| b.is_ascii_digit())
            && !value.starts_with('0')
    };
    if !digits(mod_part) || !digits(file_part) {
        return Err("下载记录无效".into());
    }
    let mod_id: u64 = mod_part.parse().map_err(|_| "下载记录无效")?;
    let file_id: u64 = file_part.parse().map_err(|_| "下载记录无效")?;
    if mod_id == 0 || file_id == 0 {
        return Err("下载记录无效".into());
    }
    Ok((mod_id, file_id))
}
fn record_path(root: &Path, id: &str) -> Result<PathBuf> {
    parse_id(id)?;
    safe_name(id)?;
    Ok(root.join(format!("{id}.json")))
}
fn package_path(root: &Path, id: &str) -> Result<PathBuf> {
    parse_id(id)?;
    safe_name(id)?;
    Ok(root.join(format!("{id}.package")))
}
fn partial_path(root: &Path, id: &str) -> Result<PathBuf> {
    parse_id(id)?;
    safe_name(id)?;
    Ok(root.join(format!("{id}.partial")))
}
fn read_record(path: &Path) -> Option<Downloaded> {
    let meta = fs::metadata(path).ok()?;
    if !meta.is_file() || meta.len() > MAX_RECORD_BYTES {
        return None;
    }
    serde_json::from_slice::<Downloaded>(&fs::read(path).ok()?).ok()
}
/// A cached package is only trusted when the record, its size and its digest
/// all agree with the file on disk. Anything else is treated as absent.
fn valid_cache(root: &Path, id: &str, report: &mut impl FnMut(u64, u64)) -> Option<Downloaded> {
    let record = read_record(&record_path(root, id).ok()?)?;
    if record.id != id {
        return None;
    }
    let package = package_path(root, id).ok()?;
    let size = fs::metadata(&package).ok()?.len();
    if record.size != size || size == 0 || size > MAX_DOWNLOAD {
        return None;
    }
    let bytes = fs::read(&package).ok()?;
    if format!("{:x}", Sha256::digest(&bytes)) != record.sha256 {
        return None;
    }
    report(size, size);
    Some(record)
}
fn write_record_atomic(root: &Path, record: &Downloaded) -> Result<()> {
    let target = record_path(root, &record.id)?;
    let temporary = root.join(format!("{}.json.pending", record.id));
    fs::write(&temporary, serde_json::to_vec_pretty(record).map_err(err)?).map_err(err)?;
    fs::rename(&temporary, target).map_err(err)
}
#[tauri::command]
pub fn list_downloads(app: tauri::AppHandle) -> Result<Vec<Downloaded>> {
    let root = download_root(&app)?;
    if !root.exists() {
        return Ok(vec![]);
    }
    let mut result = Vec::new();
    for entry in fs::read_dir(&root).map_err(err)? {
        if result.len() >= MAX_CACHE_ENTRIES {
            break;
        }
        let entry = entry.map_err(err)?;
        if entry.path().extension().is_none_or(|e| e != "json") {
            continue;
        }
        let id = entry
            .path()
            .file_stem()
            .and_then(|stem| stem.to_str())
            .map(str::to_string)
            .unwrap_or_default();
        if parse_id(&id).is_err() {
            continue;
        }
        let Some(record) = read_record(&entry.path()) else {
            continue;
        };
        if record.id != id || record.size > MAX_DOWNLOAD || record.sha256.len() != 64 {
            continue;
        }
        if package_path(&root, &id)
            .ok()
            .is_some_and(|path| path.is_file())
        {
            result.push(record);
        }
    }
    result.sort_by(|a, b| a.id.cmp(&b.id));
    Ok(result)
}
/// One entry of the local download cache, including interrupted downloads that
/// never produced a usable package. Read-only: the caller can retry or discard.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CacheEntry {
    pub id: String,
    pub mod_id: u64,
    pub file_id: u64,
    pub name: String,
    pub package_bytes: u64,
    pub partial_bytes: u64,
    /// `ready` | `partial` | `damaged`
    pub state: String,
    pub message: String,
}
/// Inspect the download cache without downloading anything. This is the
/// post-crash view: a finished package is verifiable, an interrupted one is
/// reported with its partial size, and a damaged record is never claimed valid.
#[tauri::command]
pub fn download_cache(app: tauri::AppHandle) -> Result<Vec<CacheEntry>> {
    let root = download_root(&app)?;
    if !root.exists() {
        return Ok(vec![]);
    }
    let mut ids: Vec<String> = Vec::new();
    for entry in fs::read_dir(&root).map_err(err)? {
        if ids.len() >= MAX_CACHE_ENTRIES {
            break;
        }
        let entry = entry.map_err(err)?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if let Some(id) = name
            .strip_suffix(".package")
            .or_else(|| name.strip_suffix(".partial"))
            .or_else(|| name.strip_suffix(".json"))
        {
            if parse_id(id).is_ok() && !ids.iter().any(|existing| existing == id) {
                ids.push(id.to_string());
            }
        }
    }
    let mut output = Vec::new();
    for id in ids {
        let (mod_id, file_id) = parse_id(&id)?;
        let package = package_path(&root, &id)?;
        let partial = partial_path(&root, &id)?;
        let package_bytes = fs::metadata(&package).map(|m| m.len()).unwrap_or(0);
        let partial_bytes = fs::metadata(&partial).map(|m| m.len()).unwrap_or(0);
        let record = read_record(&record_path(&root, &id)?);
        let (state, message) = match &record {
            Some(record) if package_bytes > 0 => {
                if record.size != package_bytes {
                    ("damaged", "缓存大小与记录不符，需重新下载")
                } else {
                    let mut progress = |_: u64, _: u64| {};
                    if valid_cache(&root, &id, &mut progress).is_some() {
                        ("ready", "已完成并校验通过，可直接安装")
                    } else {
                        ("damaged", "缓存内容与校验值不符，需重新下载")
                    }
                }
            }
            _ if partial_bytes > 0 => (
                "partial",
                "上次下载中断；本次启动器运行期间重试可从断点继续，重启后需从头下载",
            ),
            None if package_bytes > 0 => ("damaged", "缺少下载记录，无法确认内容"),
            _ => ("damaged", "缓存不完整"),
        };
        output.push(CacheEntry {
            id,
            mod_id,
            file_id,
            name: record.map(|r| r.name).unwrap_or_default(),
            package_bytes,
            partial_bytes,
            state: state.into(),
            message: message.into(),
        });
    }
    output.sort_by(|a, b| a.id.cmp(&b.id));
    Ok(output)
}
/// Delete the cache entry (record, package and partial) for one identity.
#[tauri::command]
pub fn discard_download_cache(app: tauri::AppHandle, id: String) -> Result<()> {
    let root = download_root(&app)?;
    for path in [
        record_path(&root, &id)?,
        package_path(&root, &id)?,
        partial_path(&root, &id)?,
    ] {
        if path.exists() {
            fs::remove_file(&path).map_err(err)?;
        }
    }
    forget_resume(&id);
    Ok(())
}
/// Remove the retained partial of a cancelled task. A cancelled download drops
/// its future (closing socket and file) without running the normal failure
/// cleanup, so the queue calls this to release the temporary file as well.
pub(crate) fn discard_partial(app: &tauri::AppHandle, mod_id: u64, file_id: u64) {
    let id = format!("nexus-{mod_id}-{file_id}");
    if let Ok(root) = download_root(app) {
        if let Ok(path) = partial_path(&root, &id) {
            let _ = fs::remove_file(path);
        }
    }
    forget_resume(&id);
}
fn content_range_start(value: &str) -> Option<(u64, u64)> {
    let value = value.trim();
    let rest = value.strip_prefix("bytes ")?;
    let (range, total) = rest.split_once('/')?;
    let (start, _end) = range.split_once('-')?;
    Some((start.parse().ok()?, total.parse().ok()?))
}
struct StreamResult {
    received: u64,
    sha256: String,
}
/// Stream one HTTP body to `output`, hashing and reporting progress. `offset`
/// is the number of bytes already present (and already hashed). Cancellation is
/// raced against every body read so the socket and the file are released
/// promptly instead of at the next chunk boundary.
async fn stream_body(
    mut response: reqwest::Response,
    output: &mut fs::File,
    offset: u64,
    total_hint: u64,
    cancel: &Cancel,
    mut digest: Sha256,
    mut report: impl FnMut(u64, u64),
) -> Result<StreamResult> {
    if !response.status().is_success() {
        return Err(status_error(response.status().as_u16()));
    }
    let total = response.content_length().unwrap_or(total_hint) + offset;
    if total > MAX_DOWNLOAD {
        return Err("文件超过 64 MB 下载限制".into());
    }
    let mut received = offset;
    let mut reported = Instant::now() - Duration::from_secs(1);
    loop {
        let chunk = tokio::select! {
            biased;
            _ = cancel.wait() => {
                let _ = output.sync_all();
                return Err("已取消".into());
            }
            chunk = response.chunk() => chunk,
        };
        let Some(chunk) = chunk.map_err(|_| "下载中断，请重试")? else {
            break;
        };
        received += chunk.len() as u64;
        if received > MAX_DOWNLOAD {
            return Err("文件超过 64 MB 下载限制".into());
        }
        output.write_all(&chunk).map_err(err)?;
        digest.update(&chunk);
        if reported.elapsed() > Duration::from_millis(150) {
            report(received, total);
            reported = Instant::now();
        }
    }
    if cancel.is_cancelled() {
        let _ = output.sync_all();
        return Err("已取消".into());
    }
    if received == 0 || (total_hint > 0 && received < total_hint) || (total > 0 && received < total)
    {
        let _ = output.sync_all();
        return Err("下载文件不完整".into());
    }
    output.sync_all().map_err(err)?;
    report(received, received);
    Ok(StreamResult {
        received,
        sha256: format!("{:x}", digest.finalize()),
    })
}
/// Download into the launcher cache. Split out from [`download_task`] so the
/// real-HTTP tests can use a temporary directory without an `AppHandle`. The
/// parameters mirror the queue identity plus the resolved request, so grouping
/// them would only hide which value is which.
#[allow(clippy::too_many_arguments)]
pub(crate) async fn fetch_to_cache(
    root: &Path,
    mod_id: u64,
    file_id: u64,
    name: &str,
    url: &Url,
    expected_size: u64,
    cancel: &Cancel,
    mut report: impl FnMut(u64, u64),
) -> Result<Downloaded> {
    if mod_id == 0 || file_id == 0 {
        return Err("文件编号无效".into());
    }
    safe_name(name)?;
    let extension = Path::new(name)
        .extension()
        .and_then(|s| s.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    if extension != "zip" && extension != "dll" {
        return Err("目前支持 ZIP 和 DLL，其他格式请在作者页面下载并手动安装".into());
    }
    if expected_size > MAX_DOWNLOAD {
        return Err("文件超过 64 MB 下载限制".into());
    }
    if cancel.is_cancelled() {
        return Err("已取消".into());
    }
    fs::create_dir_all(root).map_err(err)?;
    let id = format!("nexus-{mod_id}-{file_id}");
    if let Some(record) = valid_cache(root, &id, &mut report) {
        return Ok(record);
    }
    let partial = partial_path(root, &id)?;
    let target = package_path(root, &id)?;
    let resume = take_resume(&id, url.as_str());
    let mut offset = fs::metadata(&partial).map(|m| m.len()).unwrap_or(0);
    if let Some((known_total, _, _)) = &resume {
        if *known_total == 0 || offset > *known_total {
            offset = 0;
        }
    } else {
        // Without in-memory resume data the URL is unknown after a restart, so
        // any leftover bytes cannot be validated against a Range request.
        offset = 0;
    }
    if expected_size > 0 && offset > expected_size {
        offset = 0;
    }
    let mut output = if offset > 0 {
        let mut file = fs::OpenOptions::new().write(true).open(&partial).map_err(err)?;
        // Appending after the retained prefix: the cursor must be at `offset`,
        // otherwise resumed bytes would overwrite the hashed prefix.
        file.seek(SeekFrom::Start(offset)).map_err(err)?;
        file
    } else {
        fs::OpenOptions::new()
            .create(true)
            .truncate(true)
            .write(true)
            .open(&partial)
            .map_err(err)?
    };
    if offset == 0 {
        output.set_len(0).map_err(err)?;
        output.seek(SeekFrom::Start(0)).map_err(err)?;
    }
    let mut digest = Sha256::new();
    if offset > 0 {
        // Hash the retained prefix so a resumed download still produces the
        // digest of the whole file.
        let mut existing = fs::File::open(&partial).map_err(err)?;
        let mut buffer = [0u8; 65536];
        let mut remaining = offset;
        while remaining > 0 {
            let want = remaining.min(buffer.len() as u64) as usize;
            existing.read_exact(&mut buffer[..want]).map_err(err)?;
            digest.update(&buffer[..want]);
            remaining -= want as u64;
        }
    }
    let mut request = download_client(url)?.get(url.clone());
    if offset > 0 {
        request = request.header("Range", format!("bytes={offset}-"));
        if let Some((_, etag, modified)) = &resume {
            if !etag.is_empty() {
                request = request.header("If-Range", etag);
            } else if !modified.is_empty() {
                request = request.header("If-Range", modified);
            }
        }
    }
    let response = request.send().await.map_err(|_| "下载连接失败")?;
    let status = response.status().as_u16();
    if status == 206 {
        let start = response
            .headers()
            .get("content-range")
            .and_then(|value| value.to_str().ok())
            .and_then(content_range_start)
            .map(|(start, _)| start);
        if start != Some(offset) {
            drop(output);
            let _ = fs::remove_file(&partial);
            forget_resume(&id);
            return Err("服务器返回的断点位置不一致，请重试".into());
        }
    } else if status == 200 {
        // The server ignored the Range request (or the validator changed).
        // Restart from zero rather than appending mismatched bytes.
        if offset > 0 {
            output.set_len(0).map_err(err)?;
            output.seek(SeekFrom::Start(0)).map_err(err)?;
            digest = Sha256::new();
            offset = 0;
        }
    } else {
        drop(output);
        let _ = fs::remove_file(&partial);
        forget_resume(&id);
        return Err(status_error(status));
    }
    let etag = response
        .headers()
        .get("etag")
        .and_then(|value| value.to_str().ok())
        .unwrap_or("")
        .to_string();
    let modified = response
        .headers()
        .get("last-modified")
        .and_then(|value| value.to_str().ok())
        .unwrap_or("")
        .to_string();
    let total_hint = if status == 206 {
        response
            .headers()
            .get("content-range")
            .and_then(|value| value.to_str().ok())
            .and_then(content_range_start)
            .map(|(_, total)| total)
            .unwrap_or(0)
    } else {
        response.content_length().unwrap_or(0)
    };
    let streamed = stream_body(
        response,
        &mut output,
        offset,
        total_hint,
        cancel,
        digest,
        &mut report,
    )
    .await;
    drop(output);
    match streamed {
        Ok(result) => {
            if cancel.is_cancelled() {
                let _ = fs::remove_file(&partial);
                forget_resume(&id);
                return Err("已取消".into());
            }
            let record = Downloaded {
                id: id.clone(),
                mod_id,
                file_id,
                name: name.to_string(),
                size: result.received,
                sha256: result.sha256,
            };
            fs::rename(&partial, &target).map_err(err)?;
            write_record_atomic(root, &record)?;
            forget_resume(&id);
            report(record.size, record.size);
            Ok(record)
        }
        Err(error) => {
            let kept = fs::metadata(&partial).map(|m| m.len()).unwrap_or(0);
            if kept > 0 && !cancel.is_cancelled() {
                // Keep the partial and enough non-secret metadata to resume in
                // this process; a retry continues instead of restarting.
                remember_resume(&id, url.as_str(), total_hint, &etag, &modified);
            } else {
                let _ = fs::remove_file(&partial);
                forget_resume(&id);
            }
            Err(error)
        }
    }
}
/// Download one Nexus file into the launcher cache. Runs inside the bounded
/// queue; the caller passes the identity key so cancellation and records stay
/// consistent with the queue entry. The API key and the resolved download URL
/// stay in memory only. The argument list is the queue identity plus the
/// cancellation/progress callbacks; a struct would only rename the same data.
#[allow(clippy::too_many_arguments)]
pub(crate) async fn download_task(
    app: tauri::AppHandle,
    key: String,
    mod_id: u64,
    file_id: u64,
    nxm: Option<String>,
    cancel: Arc<Cancel>,
    declared_name: &str,
    report: impl FnMut(u64, u64),
) -> Result<Downloaded> {
    if mod_id == 0 || file_id == 0 {
        return Err("文件编号无效".into());
    }
    if cancel.is_cancelled() {
        return Err("已取消".into());
    }
    let file = api(
        &format!("games/flotsam/mods/{mod_id}/files/{file_id}.json"),
        &key,
    )
    .await?;
    let remote = file["file_name"].as_str().unwrap_or("");
    let name = if remote.is_empty() {
        declared_name.to_string()
    } else {
        remote.to_string()
    };
    let expected_size = file["size_in_bytes"].as_u64().unwrap_or(0);
    let link_path = match nxm {
        Some(link) => nxm_path(&link, mod_id, file_id)?,
        None => format!("games/flotsam/mods/{mod_id}/files/{file_id}/download_link.json"),
    };
    let links = api(&link_path, &key).await?;
    let url = links[0]["URI"].as_str().ok_or("Nexus 未提供下载链接")?;
    let url = Url::parse(url).map_err(|_| "下载地址无效")?;
    if !download_host(&url) {
        return Err("下载地址不在 Nexus 官方域名内".into());
    }
    let root = download_root(&app)?;
    fetch_to_cache(
        &root,
        mod_id,
        file_id,
        &name,
        &url,
        expected_size,
        &cancel,
        report,
    )
    .await
}
fn safe_relative(name: &str) -> Result<PathBuf> {
    let normalized = name.replace('\\', "/");
    let mut result = PathBuf::new();
    if normalized.starts_with('/') || normalized.ends_with('/') {
        return Err("压缩包路径无效".into());
    }
    for part in normalized.split('/') {
        safe_name(part)?;
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        if part.ends_with(['.', ' '])
            || part.chars().any(char::is_control)
            || matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || (stem.len() == 4
                && (stem.starts_with("COM") || stem.starts_with("LPT"))
                && stem.as_bytes()[3].is_ascii_digit())
        {
            return Err("压缩包包含无效的 Windows 文件名".into());
        }
        result.push(part);
    }
    Ok(result)
}
pub(crate) fn unpack_package(source: &Path, name: &str, destination: &Path) -> Result<usize> {
    if name.to_ascii_lowercase().ends_with(".dll") {
        let mut file = fs::File::open(source).map_err(err)?;
        let mut magic = [0; 2];
        file.read_exact(&mut magic).map_err(err)?;
        if magic != *b"MZ" {
            return Err("下载内容不是有效 DLL".into());
        }
        fs::copy(source, destination.join(name)).map_err(err)?;
        return Ok(1);
    }
    let mut archive = zip::ZipArchive::new(fs::File::open(source).map_err(err)?)
        .map_err(|_| "无法读取 ZIP 文件")?;
    if archive.len() > 1024 {
        return Err("压缩包文件过多".into());
    }
    let mut total = 0u64;
    let mut dlls = 0;
    let mut paths = std::collections::HashSet::new();
    for i in 0..archive.len() {
        let mut file = archive.by_index(i).map_err(err)?;
        if file.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000) {
            return Err("不支持包含符号链接的模组".into());
        }
        if file.is_dir() {
            continue;
        }
        let name = file.name().replace('\\', "/");
        safe_relative(&name)?;
        let lower = name.to_ascii_lowercase();
        let relative = if let Some(index) = lower.find("bepinex/plugins/") {
            &name[index + 16..]
        } else {
            if lower.contains("bepinex/") || lower.ends_with("doorstop_config.ini") {
                return Err("该压缩包包含加载器或前置补丁，请按作者说明手动安装".into());
            }
            name.as_str()
        };
        let relative = safe_relative(relative)?;
        let extension = relative
            .extension()
            .and_then(|s| s.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        if ["exe", "bat", "cmd", "ps1", "vbs", "msi", "lnk"].contains(&extension.as_str()) {
            return Err("模组包含安装脚本或程序，请按作者说明手动处理".into());
        }
        total = total.checked_add(file.size()).ok_or("压缩包过大")?;
        if total > 128 * 1024 * 1024 {
            return Err("模组解压后超过 128 MB".into());
        }
        if !paths.insert(relative.to_string_lossy().to_lowercase()) {
            return Err("压缩包包含重名文件".into());
        }
        let output = destination.join(&relative);
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent).map_err(err)?
        }
        let mut out = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&output)
            .map_err(err)?;
        let copied =
            std::io::copy(&mut (&mut file).take(128 * 1024 * 1024 + 1), &mut out).map_err(err)?;
        if copied != file.size() {
            return Err("压缩包文件大小异常".into());
        }
        if extension == "dll" {
            let mut magic = [0; 2];
            fs::File::open(&output)
                .map_err(err)?
                .read_exact(&mut magic)
                .map_err(err)?;
            if magic != *b"MZ" {
                return Err("压缩包包含无效 DLL".into());
            }
            dlls += 1
        }
        out.sync_all().map_err(err)?;
    }
    if dlls == 0 {
        return Err("压缩包中没有 BepInEx 插件 DLL；汉化资源包等类型请按作者说明安装".into());
    }
    Ok(dlls)
}
pub(crate) fn require_no_duplicate_plugins_except(
    plugins: &Path,
    pending: &Path,
    except: Option<&Path>,
) -> Result<()> {
    crate::mods::require_unique_ids_except(plugins, pending, except)?;
    let mut installed = Vec::new();
    crate::service::walk_files(plugins, &mut installed)?;
    let mut names: std::collections::HashSet<_> = installed
        .iter()
        .filter(|p| !except.is_some_and(|e| p.starts_with(e)))
        .filter_map(|p| p.file_name())
        .map(|s| s.to_string_lossy().to_lowercase())
        .filter_map(|s| {
            if s.ends_with(".dll.disabled") {
                Some(s.trim_end_matches(".disabled").to_string())
            } else if s.ends_with(".dll") {
                Some(s)
            } else {
                None
            }
        })
        .collect();
    let mut incoming = Vec::new();
    crate::service::walk_files(pending, &mut incoming)?;
    for file in incoming {
        let name = file
            .file_name()
            .ok_or("文件名无效")?
            .to_string_lossy()
            .to_lowercase();
        if name.ends_with(".dll") && !names.insert(name.clone()) {
            return Err(format!("已有同名插件 {name}，请先处理旧版本"));
        }
    }
    Ok(())
}
/// Install one verified cache entry. The mutation lock is only held for the
/// install itself: downloads never take it, so a slow download cannot block
/// mod management and a download can run while the game is open.
#[tauri::command]
pub async fn install_download(
    app: tauri::AppHandle,
    state: tauri::State<'_, LauncherState>,
    network: tauri::State<'_, crate::network::NetworkState>,
    id: String,
    package_id: Option<String>,
) -> Result<usize> {
    let state = state.inner().clone();
    let network = network.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        install_download_sync(app, &state, &network, id, package_id)
    })
    .await
    .map_err(err)?
}
fn install_download_sync(
    app: tauri::AppHandle,
    state: &LauncherState,
    network: &crate::network::NetworkState,
    id: String,
    package_id: Option<String>,
) -> Result<usize> {
    parse_id(&id)?;
    let downloads = download_root(&app)?;
    let record = read_record(&record_path(&downloads, &id)?).ok_or("找不到下载记录，请重新下载")?;
    if record.id != id {
        return Err("下载记录不一致".into());
    }
    safe_name(&record.name)?;
    let source = package_path(&downloads, &id)?;
    let bytes = fs::read(&source).map_err(err)?;
    if bytes.len() as u64 != record.size || format!("{:x}", Sha256::digest(&bytes)) != record.sha256
    {
        return Err("缓存文件已损坏，请重新下载".into());
    }
    let _lock = state.mutation.lock().map_err(err)?;
    crate::network::require_disconnected(network)?;
    require_stopped(state)?;
    let root = game_root(&load_settings(&app)?)?;
    if !root.join("BepInEx/core").is_dir() {
        return Err("请先安装与当前游戏兼容的 BepInEx".into());
    }
    crate::packages::install(
        &root,
        &source,
        &record.name,
        crate::packages::Origin {
            nexus_mod_id: Some(record.mod_id),
            nexus_file_id: Some(record.file_id),
        },
        package_id.as_deref(),
        || require_stopped(state),
    )
}
#[tauri::command]
pub fn open_mod_source(mod_id: Option<u64>, account: Option<bool>) -> Result<()> {
    let url = if account == Some(true) {
        "https://www.nexusmods.com/users/myaccount?tab=api".into()
    } else {
        match mod_id {
            Some(id) if id > 0 => format!("https://www.nexusmods.com/flotsam/mods/{id}?tab=files"),
            _ => "https://www.nexusmods.com/flotsam/mods/".into(),
        }
    };
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        std::process::Command::new("rundll32.exe")
            .args(["url.dll,FileProtocolHandler", &url])
            .creation_flags(0x08000000)
            .spawn()
            .map_err(err)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    fn sandbox() -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "flotsam-http-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }
    /// Minimal HTTP/1.1 server: reads one request, records its headers and
    /// replies with the supplied bytes. Uses a real TCP socket, no mock client.
    async fn serve_once(
        reply: Vec<u8>,
    ) -> (std::net::SocketAddr, tokio::task::JoinHandle<String>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .unwrap();
        let address = listener.local_addr().unwrap();
        let handle = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = Vec::new();
            let mut buffer = [0u8; 4096];
            loop {
                let read = socket.read(&mut buffer).await.unwrap_or(0);
                if read == 0 {
                    break;
                }
                request.extend_from_slice(&buffer[..read]);
                if request.windows(4).any(|w| w == b"\r\n\r\n") {
                    break;
                }
            }
            socket.write_all(&reply).await.unwrap_or(());
            socket.flush().await.ok();
            String::from_utf8_lossy(&request).into_owned()
        });
        (address, handle)
    }
    fn plain(reply_body: &str) -> Vec<u8> {
        format!(
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n{}",
            reply_body.len(),
            reply_body
        )
        .into_bytes()
    }
    #[tokio::test]
    async fn downloads_real_http_bytes_with_progress_and_hash() {
        let body = "MZreal-plugin-bytes";
        let (address, server) = serve_once(plain(body)).await;
        let root = sandbox();
        let cancel = Cancel::new();
        let url = Url::parse(&format!("http://{address}/plugin.dll")).unwrap();
        let mut progress = Vec::new();
        let record = fetch_to_cache(
            &root,
            8,
            123,
            "plugin.dll",
            &url,
            0,
            &cancel,
            |received, total| progress.push((received, total)),
        )
        .await
        .unwrap();
        assert_eq!(record.size, body.len() as u64);
        assert_eq!(record.sha256, format!("{:x}", Sha256::digest(body.as_bytes())));
        assert_eq!(progress.last(), Some(&(record.size, record.size)));
        assert_eq!(
            fs::read(root.join("nexus-8-123.package")).unwrap(),
            body.as_bytes()
        );
        assert!(root.join("nexus-8-123.json").is_file());
        server.await.unwrap();
        // A second request reuses the verified cache instead of the network.
        let (unused, _server) = serve_once(plain("different")).await;
        let mut cached_progress = Vec::new();
        let again = fetch_to_cache(
            &root,
            8,
            123,
            "plugin.dll",
            &Url::parse(&format!("http://{unused}/x")).unwrap(),
            0,
            &cancel,
            |received, total| cached_progress.push((received, total)),
        )
        .await
        .unwrap();
        assert_eq!(again.sha256, record.sha256);
        assert_eq!(cached_progress.last(), Some(&(record.size, record.size)));
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn resumes_from_retained_partial_with_range_header() {
        let body = b"MZ0123456789abcdef";
        let (address, server) = serve_once(
            [
                format!(
                    "HTTP/1.1 206 Partial Content\r\nContent-Length: {}\r\nContent-Range: bytes 2-{}/{}\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n",
                    body.len() - 2,
                    body.len() - 1,
                    body.len()
                )
                .into_bytes(),
                body[2..].to_vec(),
            ]
            .concat(),
        )
        .await;
        let root = sandbox();
        let cancel = Cancel::new();
        let url = Url::parse(&format!("http://{address}/plugin.dll")).unwrap();
        // Simulate a first attempt that kept 2 bytes and remember the resume
        // metadata as `fetch_to_cache` does after a stream failure.
        fs::write(root.join("nexus-8-9.partial"), &body[..2]).unwrap();
        remember_resume("nexus-8-9", url.as_str(), body.len() as u64, "\"v1\"", "");
        let record = fetch_to_cache(&root, 8, 9, "plugin.dll", &url, 0, &cancel, |_, _| {})
            .await
            .unwrap();
        assert_eq!(
            fs::read(root.join("nexus-8-9.package")).unwrap(),
            body.to_vec()
        );
        assert_eq!(
            record.sha256,
            format!("{:x}", Sha256::digest(body)),
            "resumed digest must cover the whole file"
        );
        let request = server.await.unwrap().to_ascii_lowercase();
        assert!(request.contains("range: bytes=2-"), "{request}");
        assert!(request.contains("if-range: \"v1\""), "{request}");
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn truncated_stream_fails_and_keeps_partial_for_retry() {
        let (address, server) = serve_once(
            b"HTTP/1.1 200 OK\r\nContent-Length: 999\r\nETag: \"v1\"\r\nConnection: close\r\n\r\nshort"
                .to_vec(),
        )
        .await;
        let root = sandbox();
        let cancel = Cancel::new();
        let url = Url::parse(&format!("http://{address}/plugin.dll")).unwrap();
        let error = fetch_to_cache(&root, 8, 10, "plugin.dll", &url, 0, &cancel, |_, _| {})
            .await
            .unwrap_err();
        // A body shorter than Content-Length surfaces either as a transport
        // error or as an explicit completeness check; both must be a failure.
        assert!(
            error.contains("不完整") || error.contains("中断"),
            "{error}"
        );
        assert_eq!(
            fs::metadata(root.join("nexus-8-10.partial")).unwrap().len(),
            5
        );
        assert!(!root.join("nexus-8-10.package").exists());
        assert!(!root.join("nexus-8-10.json").exists());
        server.await.unwrap();
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn cancellation_wakes_a_stalled_body_and_removes_partial() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut buffer = [0u8; 1024];
            let _ = socket.read(&mut buffer).await;
            // Send a header and one chunk, then stall without closing, so only
            // cooperative cancellation can end the read.
            let _ = socket
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4096\r\nConnection: close\r\n\r\nMZpart")
                .await;
            let _ = socket.flush().await;
            tokio::time::sleep(Duration::from_secs(10)).await;
        });
        let root = sandbox();
        let cancel = Arc::new(Cancel::new());
        let trigger = cancel.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(250)).await;
            trigger.cancel();
        });
        let url = Url::parse(&format!("http://{address}/plugin.dll")).unwrap();
        let started = Instant::now();
        let error = fetch_to_cache(&root, 8, 11, "plugin.dll", &url, 0, &cancel, |_, _| {})
            .await
            .unwrap_err();
        assert_eq!(error, "已取消");
        assert!(
            started.elapsed() < Duration::from_secs(3),
            "cancel took {:?}",
            started.elapsed()
        );
        assert!(!root.join("nexus-8-11.partial").exists());
        server.abort();
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn rejects_oversized_and_error_responses() {
        let root = sandbox();
        let cancel = Cancel::new();
        let (address, server) = serve_once(
            format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", MAX_DOWNLOAD + 1)
                .into_bytes(),
        )
        .await;
        let url = Url::parse(&format!("http://{address}/plugin.zip")).unwrap();
        assert!(fetch_to_cache(&root, 8, 12, "plugin.zip", &url, 0, &cancel, |_, _| {})
            .await
            .unwrap_err()
            .contains("64 MB"));
        server.await.unwrap();
        let (address, server) = serve_once(
            b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_vec(),
        )
        .await;
        let url = Url::parse(&format!("http://{address}/plugin.zip")).unwrap();
        assert!(fetch_to_cache(&root, 8, 13, "plugin.zip", &url, 0, &cancel, |_, _| {})
            .await
            .unwrap_err()
            .contains("未授权"));
        server.await.unwrap();
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn cache_identities_are_strict_and_cannot_escape() {
        assert_eq!(parse_id("nexus-8-123").unwrap(), (8, 123));
        for id in [
            "nexus-8-0123",
            "nexus-08-123",
            "nexus-0-1",
            "nexus-8",
            "nexus-8-9-10",
            "nexus8-9",
            "../nexus-8-9",
            "nexus--9",
            "nexus-8-",
        ] {
            assert!(parse_id(id).is_err(), "{id}");
        }
        assert!(record_path(Path::new("C:/root"), "../nexus-1-1").is_err());
        assert!(package_path(Path::new("C:/root"), "nexus-1-1").is_ok());
    }
    #[test]
    fn archive_validation_rejects_escaping_scripts_and_duplicate_payloads() {
        use std::io::Write;
        fn archive(root: &Path, entries: &[(&str, &[u8])]) -> PathBuf {
            let path = root.join("source.zip");
            let mut zip = zip::ZipWriter::new(fs::File::create(&path).unwrap());
            for (name, bytes) in entries {
                zip.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(bytes).unwrap();
            }
            zip.finish().unwrap();
            path
        }
        for entries in [
            vec![("../escape.dll", b"MZpayload".as_slice())],
            vec![("BepInEx/plugins/mod.dll", b"MZplugin".as_slice()), ("run.ps1", b"unsafe".as_slice())],
            vec![("A.dll", b"MZone".as_slice()), ("a.dll", b"MZtwo".as_slice())],
            vec![("BepInEx/patchers/mod.dll", b"MZplugin".as_slice())],
            vec![("CON.dll", b"MZplugin".as_slice())],
        ] {
            let root = sandbox();
            let output = root.join("stage");
            fs::create_dir(&output).unwrap();
            let source = archive(&root, &entries);
            assert!(unpack_package(&source, "source.zip", &output).is_err(), "{entries:?}");
            assert!(!root.join("escape.dll").exists());
            fs::remove_dir_all(root).unwrap();
        }
        let root = sandbox();
        let output = root.join("stage");
        fs::create_dir(&output).unwrap();
        let source = archive(&root, &[("BepInEx/plugins/workshop/mod.dll", b"MZplugin"), ("BepInEx/plugins/workshop/data.json", b"{}")]);
        assert_eq!(unpack_package(&source, "source.zip", &output).unwrap(), 1);
        assert_eq!(fs::read(output.join("workshop/mod.dll")).unwrap(), b"MZplugin");
        assert_eq!(fs::read(output.join("workshop/data.json")).unwrap(), b"{}");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn incomplete_or_tampered_cache_is_not_reported_as_ready() {
        let root = sandbox();
        let id = "nexus-3-4";
        fs::write(root.join(format!("{id}.package")), b"MZplugin").unwrap();
        let record = Downloaded {
            id: id.into(),
            mod_id: 3,
            file_id: 4,
            name: "plugin.dll".into(),
            size: 8,
            sha256: format!("{:x}", Sha256::digest(b"MZplugin")),
        };
        write_record_atomic(&root, &record).unwrap();
        let mut reported = 0;
        assert!(valid_cache(&root, id, &mut |_, _| reported += 1).is_some());
        assert_eq!(reported, 1);
        // A size mismatch must invalidate the cache even if a record exists.
        let mut progress = |_: u64, _: u64| {};
        fs::write(root.join(format!("{id}.package")), b"MZplugi").unwrap();
        assert!(valid_cache(&root, id, &mut progress).is_none());
        fs::remove_dir_all(root).unwrap();
    }
}
