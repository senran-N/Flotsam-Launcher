//! Compatibility manifest exchanged before joining a room. The launcher sends
//! the real game, loader (BepInEx + Doorstop), enabled plugins, enabled patchers
//! and the plugin configuration files that exist locally, so a join failure
//! names the exact difference, whether it blocks the join, and the action that
//! fixes it instead of "版本不一致".
//!
//! Local blockers can be listed *before* any handshake through
//! [`preflight`] / the `compatibility_preflight` command: a missing loader,
//! Doorstop proxy, dependency or a broken plugin is reported without contacting
//! the host. Remote differences are computed with [`diff`] once the peer
//! manifest arrives.
use crate::{
    mods::{self, MetadataCache},
    service::{err, walk_files, Result},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    fs::File,
    io::Read,
    path::{Path, PathBuf},
    sync::{Mutex, OnceLock},
    time::SystemTime,
};

/// Upper bound for any component list received from a peer.
pub const MAX_COMPONENTS: usize = 1024;
const MAX_CONFIGS: usize = 64;
const MAX_CONFIG_BYTES: u64 = 256 * 1024;
const MAX_COMPONENT_NAME: usize = 128;
const MAX_HASH_CACHE: usize = 512;

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Component {
    /// File name without extension, lower-cased. Stable across machines.
    pub name: String,
    #[serde(default)]
    pub plugin_id: String,
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub sha256: String,
    #[serde(default)]
    pub enabled: bool,
}

/// One plugin configuration file. Only files that exist locally are listed;
/// a presence difference therefore means "the other side has not generated it
/// yet or has deleted it".
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ConfigEntry {
    /// File name inside `BepInEx/config`, e.g. `local.flotsam.multiplayer.cfg`.
    pub file: String,
    #[serde(default)]
    pub plugin_id: String,
    /// Hash of the normalised text (BOM/CRLF/blank-line insensitive).
    #[serde(default)]
    pub sha256: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    #[serde(default)]
    pub game: String,
    /// Hash of `BepInEx/core/BepInEx.dll`; empty when the loader is absent.
    #[serde(default)]
    pub loader: String,
    /// Human-readable loader version (assembly version), for messages only.
    #[serde(default)]
    pub loader_version: String,
    /// Hash of the Doorstop proxy (`winhttp.dll`); empty when absent.
    #[serde(default)]
    pub doorstop: String,
    /// Enabled plugins only; disabled DLLs never load.
    #[serde(default)]
    pub plugins: Vec<Component>,
    #[serde(default)]
    pub patchers: Vec<Component>,
    /// Installed but disabled; used to suggest "enable this file".
    #[serde(default)]
    pub disabled_plugins: Vec<Component>,
    #[serde(default)]
    pub disabled_patchers: Vec<Component>,
    #[serde(default)]
    pub configs: Vec<ConfigEntry>,
    #[serde(default)]
    pub fingerprint: String,
    /// Present when the launcher could not inspect part of the install.
    #[serde(default)]
    pub warning: String,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Difference {
    /// game | loader | doorstop | missing | extra | version | patcher | config | inspect
    pub kind: String,
    pub detail: String,
    pub fix: String,
    /// True when the join must be refused until this is resolved.
    #[serde(default)]
    pub blocking: bool,
    /// Machine-readable follow-up the UI can map to a launcher action.
    #[serde(default)]
    pub action: String,
}
impl Difference {
    fn blocking(kind: &str, detail: String, fix: &str, action: &str) -> Self {
        Self {
            kind: kind.into(),
            detail,
            fix: fix.into(),
            blocking: true,
            action: action.into(),
        }
    }
    fn warning(kind: &str, detail: String, fix: &str, action: &str) -> Self {
        Self {
            kind: kind.into(),
            detail,
            fix: fix.into(),
            blocking: false,
            action: action.into(),
        }
    }
    pub fn blocks(&self) -> bool {
        self.blocking
    }
}

/// `(size, modified, digest)` for one path.
type HashStamps = HashMap<PathBuf, (u64, Option<SystemTime>, String)>;
fn hash_cache() -> &'static Mutex<HashStamps> {
    static CACHE: OnceLock<Mutex<HashStamps>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}
/// Hash a file, reusing the digest while size and modification time are stable.
/// The cache is bounded so a long session with many files cannot grow without
/// limit.
fn hash_file(path: &Path) -> Result<String> {
    let meta = std::fs::metadata(path).map_err(err)?;
    let stamp = (meta.len(), meta.modified().ok());
    if let Some((size, modified, value)) = hash_cache()
        .lock()
        .map_err(err)?
        .get(path)
    {
        if (*size, *modified) == stamp {
            return Ok(value.clone());
        }
    }
    let mut file = File::open(path).map_err(err)?;
    let mut hash = Sha256::new();
    let mut buffer = [0u8; 65536];
    loop {
        let n = file.read(&mut buffer).map_err(err)?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
    }
    let value = format!("{:x}", hash.finalize());
    let mut cache = hash_cache().lock().map_err(err)?;
    if cache.len() >= MAX_HASH_CACHE {
        cache.clear();
    }
    cache.insert(path.to_path_buf(), (stamp.0, stamp.1, value.clone()));
    Ok(value)
}
fn version_of(path: &Path) -> Option<String> {
    let bytes = std::fs::read(path).ok()?;
    let meta = mods::read_metadata(&bytes).ok()?;
    meta.assembly_version
        .map(|v| v.trim_end_matches(".0").to_string())
}
fn push_warning(warnings: &mut Vec<String>, message: String) {
    if warnings.len() < 8 && !warnings.iter().any(|w| w == &message) {
        warnings.push(message);
    }
}
/// Read enabled and disabled DLL components from one BepInEx directory.
/// Unreadable files degrade to a warning instead of aborting the whole scan.
fn components(
    root: &Path,
    relative: &str,
    plugins: bool,
    warnings: &mut Vec<String>,
) -> (Vec<Component>, Vec<Component>) {
    let directory = root.join(relative);
    let mut files = Vec::new();
    if let Err(error) = walk_files(&directory, &mut files) {
        push_warning(warnings, format!("无法读取 {relative}：{error}"));
        return (Vec::new(), Vec::new());
    }
    let mut enabled = Vec::new();
    let mut disabled = Vec::new();
    for file in files {
        let file_name = file
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_ascii_lowercase();
        let (base, is_enabled) = if let Some(stem) = file_name.strip_suffix(".dll.disabled") {
            (stem.to_string(), false)
        } else if let Some(stem) = file_name.strip_suffix(".dll") {
            (stem.to_string(), true)
        } else {
            continue;
        };
        if base.is_empty() || base.len() > MAX_COMPONENT_NAME {
            continue;
        }
        if enabled.len() + disabled.len() >= MAX_COMPONENTS {
            push_warning(
                warnings,
                format!("{relative} 中的 DLL 超过 {MAX_COMPONENTS} 个，只比较前 {MAX_COMPONENTS} 个"),
            );
            break;
        }
        let bytes = match std::fs::read(&file) {
            Ok(bytes) if bytes.len() <= 32 * 1024 * 1024 => bytes,
            Ok(_) => {
                push_warning(warnings, format!("{base} 超过 32 MB，未参与比较"));
                continue;
            }
            Err(error) => {
                push_warning(warnings, format!("无法读取 {base}：{error}"));
                continue;
            }
        };
        let meta = if plugins {
            mods::read_metadata(&bytes).ok()
        } else {
            None
        };
        if plugins && meta.is_none() {
            push_warning(
                warnings,
                format!("{base} 的插件声明无法读取，只能按文件名比较"),
            );
        }
        let plugin = meta.as_ref().and_then(|m| m.plugins.first());
        let sha256 = match hash_file(&file) {
            Ok(hash) => hash,
            Err(error) => {
                push_warning(warnings, format!("无法校验 {base}：{error}"));
                continue;
            }
        };
        let component = Component {
            name: base,
            plugin_id: plugin.map(|p| p.id.clone()).unwrap_or_default(),
            version: plugin
                .map(|p| p.version.clone())
                .or_else(|| version_of(&file))
                .unwrap_or_default(),
            sha256,
            enabled: is_enabled,
        };
        if is_enabled {
            enabled.push(component);
        } else {
            disabled.push(component);
        }
    }
    enabled.sort_by(|a, b| a.name.cmp(&b.name).then(a.plugin_id.cmp(&b.plugin_id)));
    disabled.sort_by(|a, b| a.name.cmp(&b.name).then(a.plugin_id.cmp(&b.plugin_id)));
    (enabled, disabled)
}
fn normalise_config(bytes: &[u8]) -> Vec<u8> {
    let text = String::from_utf8_lossy(bytes);
    let mut output = String::new();
    for line in text.lines() {
        let line = line.trim_end().replace('\u{feff}', "");
        if line.is_empty() {
            continue;
        }
        output.push_str(&line);
        output.push('\n');
    }
    output.into_bytes()
}
/// Configuration files that belong to an enabled plugin. Comparing the whole
/// text is intentionally a warning, never an admission blocker: players may
/// legitimately keep different keybinds or language settings.
fn configs_of(root: &Path, plugins: &[Component]) -> (Vec<ConfigEntry>, Vec<String>) {
    let ids: HashSet<String> = plugins
        .iter()
        .map(|p| {
            let id = if p.plugin_id.is_empty() {
                p.name.clone()
            } else {
                p.plugin_id.clone()
            };
            id.to_ascii_lowercase()
        })
        .collect();
    let directory = root.join("BepInEx/config");
    let mut entries = Vec::new();
    let mut warnings = Vec::new();
    let read_dir = match std::fs::read_dir(&directory) {
        Ok(read_dir) => read_dir,
        Err(_) => return (entries, warnings),
    };
    for entry in read_dir.flatten() {
        let path = entry.path();
        let file_name = entry.file_name().to_string_lossy().into_owned();
        let lower = file_name.to_ascii_lowercase();
        let Some(stem) = lower.strip_suffix(".cfg") else {
            continue;
        };
        if stem.is_empty() || !ids.contains(stem) || entries.len() >= MAX_CONFIGS {
            continue;
        }
        let meta = match std::fs::metadata(&path) {
            Ok(meta) if meta.is_file() => meta,
            _ => continue,
        };
        if meta.len() > MAX_CONFIG_BYTES {
            push_warning(&mut warnings, format!("配置 {file_name} 超过 256 KB，未参与比较"));
            continue;
        }
        let bytes = match std::fs::read(&path) {
            Ok(bytes) => bytes,
            Err(error) => {
                push_warning(&mut warnings, format!("无法读取配置 {file_name}：{error}"));
                continue;
            }
        };
        entries.push(ConfigEntry {
            file: file_name,
            plugin_id: stem.to_string(),
            sha256: format!("{:x}", Sha256::digest(normalise_config(&bytes))),
        });
    }
    entries.sort_by_key(|entry| entry.file.to_ascii_lowercase());
    (entries, warnings)
}
fn doorstop_hash(root: &Path) -> String {
    let proxy = root.join("winhttp.dll");
    if !proxy.is_file() {
        return String::new();
    }
    hash_file(&proxy).unwrap_or_default()
}
pub fn build(root: &Path) -> Result<Manifest> {
    let game = root.join("Flotsam_Data/Managed/Assembly-CSharp.dll");
    if !game.is_file() {
        return Err("未找到 Assembly-CSharp.dll，无法核对游戏版本".into());
    }
    let game_hash = hash_file(&game).map_err(|error| format!("无法读取游戏程序集：{error}"))?;
    let mut warnings = vec![];
    let (plugins, disabled_plugins) = components(root, "BepInEx/plugins", true, &mut warnings);
    let (patchers, disabled_patchers) = components(root, "BepInEx/patchers", false, &mut warnings);
    let core = root.join("BepInEx/core/BepInEx.dll");
    let (loader, loader_version) = if core.is_file() {
        (
            hash_file(&core).map_err(|error| format!("无法读取 BepInEx 加载器：{error}"))?,
            version_of(&core).unwrap_or_default(),
        )
    } else {
        (String::new(), String::new())
    };
    let doorstop = doorstop_hash(root);
    let (configs, config_warnings) = configs_of(root, &plugins);
    for warning in config_warnings {
        push_warning(&mut warnings, warning);
    }
    let warning = if warnings.len() > 4 {
        let extra = warnings.len() - 4;
        format!("{}；另有 {extra} 项无法检查", warnings[..4].join("；"))
    } else {
        warnings.join("；")
    };
    let mut manifest = Manifest {
        game: game_hash,
        loader,
        loader_version,
        doorstop,
        plugins,
        patchers,
        disabled_plugins,
        disabled_patchers,
        configs,
        fingerprint: String::new(),
        warning,
    };
    manifest.fingerprint = fingerprint_of(&manifest);
    Ok(manifest)
}
/// The fingerprint intentionally matches the older launcher digest: sorted
/// `name + bytes` over the same set of loaded DLLs, so two builds that only
/// differ in manifest fields still compare equal at the loaded-code layer.
/// Configuration files are deliberately excluded: they are reported as
/// warnings and must not silently reject a join.
fn fingerprint_of(manifest: &Manifest) -> String {
    let mut entries: Vec<(String, String)> = manifest
        .plugins
        .iter()
        .chain(manifest.patchers.iter())
        .map(|c| (c.name.clone(), c.sha256.clone()))
        .collect();
    entries.push(("assembly-csharp".into(), manifest.game.clone()));
    entries.push(("bepinex".into(), manifest.loader.clone()));
    entries.sort();
    let mut hasher = Sha256::new();
    for (name, hash) in entries {
        hasher.update((name.len() as u64).to_be_bytes());
        hasher.update(name.as_bytes());
        hasher.update(hash.as_bytes());
    }
    format!("{:x}", hasher.finalize())
}
fn component_key(component: &Component) -> String {
    if component.plugin_id.is_empty() {
        component.name.to_ascii_lowercase()
    } else {
        component.plugin_id.to_ascii_lowercase()
    }
}
fn find<'a>(list: &'a [Component], item: &Component) -> Option<&'a Component> {
    let key = component_key(item);
    list.iter()
        .find(|other| component_key(other) == key)
        .or_else(|| {
            list.iter()
                .find(|other| other.name.eq_ignore_ascii_case(&item.name))
        })
}
fn describe_enable_hint(disabled: &[Component], item: &Component) -> Option<String> {
    find(disabled, item).map(|local| format!("本机已安装但被停用：{}，请先启用", local.name))
}
fn diff_components(
    kind: &str,
    label: &str,
    local: &[Component],
    remote: &[Component],
    local_disabled: &[Component],
    output: &mut Vec<Difference>,
) {
    for item in local {
        match find(remote, item) {
            None => {
                let (fix, action) = match describe_enable_hint(local_disabled, item) {
                    Some(hint) => (
                        format!("{hint}；若房主确实没有，请在「模组」页停用该组件"),
                        "enable_plugin",
                    ),
                    None => (
                        "在「模组」页停用该组件，或在房主一侧安装同一版本".to_string(),
                        "disable_mod",
                    ),
                };
                output.push(Difference::blocking(
                    kind,
                    format!("本机{label}了房主没有的组件：{}", item.name),
                    &fix,
                    action,
                ));
            }
            Some(other) if other.sha256 != item.sha256 => {
                output.push(Difference::blocking(
                    "version",
                    format!(
                        "{label} {} 文件不同（本机 {} / 房主 {}）",
                        item.name,
                        if item.version.is_empty() { "未知" } else { &item.version },
                        if other.version.is_empty() { "未知" } else { &other.version }
                    ),
                    "双方安装同一作者发布的同一文件，或由一方在「模组」页停用",
                    "match_version",
                ));
            }
            Some(other) if !other.version.is_empty() && other.version != item.version => {
                output.push(Difference::warning(
                    "version",
                    format!(
                        "{label} {} 声明版本不同：本机 {} / 房主 {}",
                        item.name, item.version, other.version
                    ),
                    "核对双方的模组来源；文件一致时通常不影响加载",
                    "match_version",
                ));
            }
            Some(_) => {}
        }
    }
    for item in remote {
        if find(local, item).is_none() {
            let (fix, action) = match describe_enable_hint(local_disabled, item) {
                Some(hint) => (format!("{hint}后重试"), "enable_plugin"),
                None => (
                    "从「模组 → 发现模组」下载同一文件，或向房主要同一版本".to_string(),
                    "install_mod",
                ),
            };
            output.push(Difference::blocking(
                kind,
                format!("缺少房主启用的{label}：{}", item.name),
                &fix,
                action,
            ));
        }
    }
}
fn dedupe(differences: Vec<Difference>) -> Vec<Difference> {
    let mut seen = HashSet::new();
    let mut output = Vec::new();
    for item in differences {
        if seen.insert((item.kind.clone(), item.detail.clone())) {
            output.push(item);
        }
    }
    output.sort_by(|a, b| {
        b.blocking
            .cmp(&a.blocking)
            .then(a.kind.cmp(&b.kind))
            .then(a.detail.cmp(&b.detail))
    });
    output
}
/// Compare two manifests and return actionable differences. An empty list means
/// the two installs load the same code and configuration.
pub fn diff(local: &Manifest, remote: &Manifest) -> Vec<Difference> {
    let mut output = vec![];
    if local.game != remote.game {
        output.push(Difference::blocking(
            "game",
            "游戏核心程序集不同".into(),
            "双方更新到同一游戏版本",
            "update_game",
        ));
    }
    if local.loader != remote.loader {
        let detail = if local.loader.is_empty() {
            "本机未安装 BepInEx 加载器".to_string()
        } else if remote.loader.is_empty() {
            "房主未安装 BepInEx 加载器".to_string()
        } else {
            format!(
                "BepInEx 加载器不同（本机 {} / 房主 {}）",
                if local.loader_version.is_empty() {
                    "未知"
                } else {
                    &local.loader_version
                },
                if remote.loader_version.is_empty() {
                    "未知"
                } else {
                    &remote.loader_version
                }
            )
        };
        output.push(Difference::blocking(
            "loader",
            detail,
            "在「联机」页安装内置的 BepInEx 5.4.23.6",
            "install_loader",
        ));
    }
    if local.doorstop != remote.doorstop && !local.loader.is_empty() && !remote.loader.is_empty()
    {
        if local.doorstop.is_empty() || remote.doorstop.is_empty() {
            output.push(Difference::blocking(
                "doorstop",
                if local.doorstop.is_empty() {
                    "本机缺少 winhttp.dll（Doorstop 代理），加载器不会生效"
                } else {
                    "房主缺少 winhttp.dll（Doorstop 代理），加载器不会生效"
                }
                .into(),
                "在「联机」页安装内置加载器，补齐 winhttp.dll",
                "install_loader",
            ));
        } else {
            output.push(Difference::warning(
                "doorstop",
                "winhttp.dll（Doorstop 代理）版本不同".into(),
                "双方使用启动器内置的加载器；若仍无法加载，重新安装联机组件",
                "install_loader",
            ));
        }
    }
    diff_components(
        "missing",
        "启用的插件",
        &local.plugins,
        &remote.plugins,
        &local.disabled_plugins,
        &mut output,
    );
    diff_components(
        "patcher",
        "启用的前置补丁",
        &local.patchers,
        &remote.patchers,
        &local.disabled_patchers,
        &mut output,
    );
    // Configurations are informative only; a join is never rejected for them.
    for item in &local.configs {
        match remote
            .configs
            .iter()
            .find(|other| other.file.eq_ignore_ascii_case(&item.file))
        {
            None => output.push(Difference::warning(
                "config",
                format!("房主没有配置 {}（可能尚未运行过该模组）", item.file),
                "双方各运行一次模组生成配置，或按 `BepInEx/config` 中的同名文件核对",
                "compare_config",
            )),
            Some(other) if other.sha256 != item.sha256 => output.push(Difference::warning(
                "config",
                format!("配置 {} 与房主不同", item.file),
                "若该配置影响玩法（资源、规则、同步），请双方核对后复制房主的同名文件",
                "compare_config",
            )),
            Some(_) => {}
        }
    }
    for item in &remote.configs {
        if !local
            .configs
            .iter()
            .any(|other| other.file.eq_ignore_ascii_case(&item.file))
        {
            output.push(Difference::warning(
                "config",
                format!("本机没有房主已有的配置 {}", item.file),
                "先运行一次该模组生成默认配置，再按房主配置核对",
                "compare_config",
            ));
        }
    }
    // Inspection problems only matter when the two sides differ; identical
    // unreadable files are the same limitation on both machines.
    if local.warning != remote.warning {
        if !local.warning.is_empty() {
            output.push(Difference::warning(
                "inspect",
                format!("本机有文件未能完整检查：{}", local.warning),
                "按提示重新扫描；无法读取的 DLL 可能被游戏占用",
                "rescan",
            ));
        }
        if !remote.warning.is_empty() {
            output.push(Difference::warning(
                "inspect",
                format!("房主有文件未能完整检查：{}", remote.warning),
                "请房主重新扫描后再加入",
                "rescan",
            ));
        }
    }
    dedupe(output)
}
/// Build the manifest used for room admission. Errors carry the real reason so
/// a join can explain why the local install could not be inspected.
pub(crate) fn for_room(root: &Path) -> Result<Manifest> {
    build(root).map_err(|error| format!("无法读取本机游戏信息：{error}"))
}
/// Local, pre-handshake checks. Every blocker can be fixed in the launcher
/// without contacting a host.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Preflight {
    pub ok: bool,
    pub blockers: Vec<Difference>,
    pub warnings: Vec<Difference>,
    pub manifest: ManifestSummary,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestSummary {
    pub game: String,
    pub loader: bool,
    pub loader_version: String,
    pub doorstop: bool,
    pub plugins: usize,
    pub patchers: usize,
    pub disabled: usize,
    pub configs: usize,
    pub fingerprint: String,
}
fn summary(manifest: &Manifest) -> ManifestSummary {
    ManifestSummary {
        game: manifest.game.clone(),
        loader: !manifest.loader.is_empty(),
        loader_version: manifest.loader_version.clone(),
        doorstop: !manifest.doorstop.is_empty(),
        plugins: manifest.plugins.len(),
        patchers: manifest.patchers.len(),
        disabled: manifest.disabled_plugins.len() + manifest.disabled_patchers.len(),
        configs: manifest.configs.len(),
        fingerprint: manifest.fingerprint.clone(),
    }
}
fn issue_difference(issue: &mods::Issue, name: &str) -> Difference {
    let (action, fix) = match issue.code.as_str() {
        "missing" => (
            "install_dependency",
            "在「模组 → 发现模组」安装缺失的前置插件，或向作者页面手动下载后导入",
        ),
        "dependencyVersion" => (
            "update_dependency",
            "安装满足最低版本的前置插件；更新前先停用旧版本",
        ),
        "duplicate" => (
            "resolve_duplicate",
            "同一插件 ID 只能保留一个版本；在「模组」页停用或移除重复文件",
        ),
        "incompatible" => (
            "resolve_conflict",
            "两个插件声明互斥；停用其中之一",
        ),
        "cycle" => (
            "resolve_dependency_cycle",
            "循环前置无法加载；停用一个插件并核对作者说明",
        ),
        "process" => (
            "check_process",
            "该插件未声明适用于 Flotsam.exe，可能不会加载；核对作者说明",
        ),
        "version" => (
            "fix_version",
            "BepInEx 5 要求点分数字版本；改用作者发布的兼容版本",
        ),
        "loader" => ("install_loader", "在「联机」页安装内置 BepInEx 加载器"),
        _ => ("read_mod", "查看「模组」页的说明并处理该文件"),
    };
    Difference::blocking(
        "plugin",
        format!("{name}：{}", issue.message),
        fix,
        action,
    )
}
/// Result of a conservative `doorstop_config.ini` inspection.
///
/// Doorstop is what loads BepInEx into the game: with the proxy present but the
/// config disabled (or pointing at another assembly) the loader silently does
/// nothing, so a room would open without mods. The parser reads only the
/// `[General]` keys and never rewrites the file: an existing third-party
/// configuration is reported for manual repair, only a *missing* file is
/// something the installer may create.
#[derive(Debug, Clone, PartialEq, Eq)]
enum DoorstopConfig {
    /// File absent: the bundled loader install may create it.
    Missing,
    /// Present and plausibly enabled with the BepInEx core as target.
    Ready,
    /// Present but definitely not loading BepInEx; needs a manual fix.
    Misconfigured(String),
    /// Present but not understandable; treat as manual review.
    Unreadable(String),
}
/// The `[General] target_assembly` the bundled Doorstop/BepInEx 5.4.23.6
/// expects, normalised. This is the preloader entry point, not BepInEx.dll.
const DOORSTOP_TARGET: &str = "bepinex\\core\\bepinex.preloader.dll";
fn doorstop_config(root: &Path) -> DoorstopConfig {
    let path = root.join("doorstop_config.ini");
    let meta = match std::fs::metadata(&path) {
        Ok(meta) if meta.is_file() => meta,
        Ok(_) => return DoorstopConfig::Unreadable("doorstop_config.ini 不是文件".into()),
        // Only a genuinely absent file may be created by the installer; a
        // permission or sharing error needs the user, never an overwrite.
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return DoorstopConfig::Missing
        }
        Err(error) => {
            return DoorstopConfig::Unreadable(format!(
                "无法访问 doorstop_config.ini：{error}"
            ))
        }
    };
    if meta.len() > 64 * 1024 {
        return DoorstopConfig::Unreadable("doorstop_config.ini 过大，请手动核对".into());
    }
    let bytes = match std::fs::read(&path) {
        Ok(bytes) => bytes,
        Err(error) => {
            return DoorstopConfig::Unreadable(format!(
                "无法读取 doorstop_config.ini：{error}"
            ))
        }
    };
    let text = String::from_utf8_lossy(&bytes);
    let mut section = String::new();
    let mut enabled: Option<String> = None;
    let mut target: Option<String> = None;
    let mut duplicates: Vec<String> = Vec::new();
    for line in text.lines() {
        let line = line.trim().trim_start_matches('\u{feff}');
        if line.is_empty() || line.starts_with(';') || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') {
            section = line[1..line.len() - 1].trim().to_ascii_lowercase();
            continue;
        }
        if section != "general" {
            continue;
        }
        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        let key = key.trim().to_ascii_lowercase();
        let value = value.trim().trim_matches('"').to_string();
        let slot = match key.as_str() {
            "enabled" => Some(&mut enabled),
            "target_assembly" => Some(&mut target),
            _ => None,
        };
        if let Some(slot) = slot {
            if slot.is_some() && !duplicates.iter().any(|value| value == &key) {
                duplicates.push(key.clone());
            }
            *slot = Some(value);
        }
    }
    if !duplicates.is_empty() {
        // Doorstop's own precedence for repeated keys is not worth predicting;
        // an ambiguous config is reported for manual review.
        return DoorstopConfig::Unreadable(format!(
            "doorstop_config.ini 的 [General] 重复定义了 {}，请手动核对",
            duplicates.join("、")
        ));
    }
    match enabled.as_deref() {
        Some("false") | Some("0") | Some("off") => {
            return DoorstopConfig::Misconfigured(
                format!(
                    "doorstop_config.ini 中 [General] enabled={}，加载器不会加载任何模组",
                    enabled.unwrap_or_default()
                ),
            )
        }
        // Only an explicit true may be treated as enabled; an unrecognised
        // value must not be silently accepted.
        Some("true") | Some("1") | Some("on") => {}
        Some(other) => {
            return DoorstopConfig::Unreadable(format!(
                "doorstop_config.ini 的 [General] enabled={other} 无法识别，请改为 true 或 false"
            ))
        }
        None => {
            return DoorstopConfig::Unreadable(
                "doorstop_config.ini 缺少 [General] enabled，无法确认加载器是否启用".into(),
            )
        }
    }
    let Some(target) = target else {
        return DoorstopConfig::Unreadable(
            "doorstop_config.ini 缺少 [General] target_assembly，无法确认加载目标".into(),
        );
    };
    let normalised = target
        .replace('/', "\\")
        .trim_start_matches(".\\")
        .trim_end_matches('\\')
        .to_ascii_lowercase();
    // Exact canonical relative path: a suffix match would accept
    // `Evil\BepInEx\core\BepInEx.Preloader.dll`, which loads other code.
    if normalised != DOORSTOP_TARGET {
        return DoorstopConfig::Misconfigured(format!(
            "doorstop_config.ini 的 target_assembly 指向 {target}，不是 BepInEx 预加载器"
        ));
    }
    DoorstopConfig::Ready
}
/// Inspect the local install without a network connection. Used by
/// `join_room`/`host_room` before any handshake and by the settings UI.
pub fn preflight(root: &Path) -> Result<Preflight> {
    crate::multiplayer::refresh_existing_plugin(root);
    let manifest = build(root)?;
    let mut blockers = Vec::new();
    let mut warnings = Vec::new();
    if manifest.loader.is_empty() {
        blockers.push(Difference::blocking(
            "loader",
            "本机未安装 BepInEx 加载器".into(),
            "在「联机」页安装内置的 BepInEx 5.4.23.6",
            "install_loader",
        ));
    } else if manifest.doorstop.is_empty() {
        blockers.push(Difference::blocking(
            "doorstop",
            "本机缺少 winhttp.dll（Doorstop 代理），加载器不会生效".into(),
            "在「联机」页重新安装联机组件以补齐 winhttp.dll",
            "install_loader",
        ));
    }
    // The Doorstop config only matters when the loader is actually installed;
    // a missing loader is already the single actionable blocker.
    if !manifest.loader.is_empty() {
        match doorstop_config(root) {
            DoorstopConfig::Ready => {}
            DoorstopConfig::Missing => blockers.push(Difference::blocking(
                "doorstop",
                "本机缺少 doorstop_config.ini，加载器不会生效".into(),
                "在「联机」页安装内置加载器以生成默认配置；已有自定义配置时请自行保留",
                "install_loader",
            )),
            DoorstopConfig::Misconfigured(detail) => blockers.push(Difference::blocking(
                "doorstop",
                detail,
                "这是已有的第三方配置，启动器不会覆盖：请把 [General] 的 enabled 改为 true，target_assembly 改为 BepInEx\\core\\BepInEx.Preloader.dll",
                "read_mod",
            )),
            DoorstopConfig::Unreadable(detail) => blockers.push(Difference::blocking(
                "doorstop",
                detail,
                "请核对 doorstop_config.ini 的 [General] 段；启动器不自动改写第三方配置",
                "read_mod",
            )),
        }
    }
    if !manifest.warning.is_empty() {
        warnings.push(Difference::warning(
            "inspect",
            format!("有文件未能完整检查：{}", manifest.warning),
            "按提示重新扫描；被游戏占用的 DLL 需要先退出游戏",
            "rescan",
        ));
    }
    let mut cache = MetadataCache::default();
    match mods::scan(root, &mut cache) {
        Ok(entries) => {
            for entry in entries.iter().filter(|entry| entry.enabled) {
                for issue in entry
                    .issues
                    .iter()
                    .filter(|issue| issue.severity == "error" && issue.code != "loader")
                {
                    blockers.push(issue_difference(issue, &entry.name));
                }
            }
        }
        Err(error) => warnings.push(Difference::warning(
            "inspect",
            format!("无法扫描模组目录：{error}"),
            "检查 BepInEx/plugins 的读取权限后重试",
            "rescan",
        )),
    }
    let blockers = dedupe(blockers);
    let warnings = dedupe(warnings);
    Ok(Preflight {
        ok: blockers.is_empty(),
        blockers,
        warnings,
        manifest: summary(&manifest),
    })
}
/// Validate a manifest received from a peer before comparing it. Keeps string
/// sizes and list lengths bounded so a hostile peer cannot force unbounded work.
pub fn sanitize(manifest: &mut Manifest) -> Result<()> {
    fn check_components(list: &mut [Component]) -> Result<()> {
        if list.len() > MAX_COMPONENTS {
            return Err("对方发送的组件清单超出长度限制".into());
        }
        for component in list.iter_mut() {
            if component.name.is_empty()
                || component.name.len() > MAX_COMPONENT_NAME
                || component.name.contains(['/', '\\'])
                || component.plugin_id.len() > 256
                || component.version.len() > 64
                || component.sha256.len() > 64
                || component
                    .name
                    .chars()
                    .chain(component.plugin_id.chars())
                    .chain(component.version.chars())
                    .any(char::is_control)
            {
                return Err("对方发送的组件字段无效".into());
            }
            component.name.make_ascii_lowercase();
            component.sha256.make_ascii_lowercase();
        }
        Ok(())
    }
    check_components(&mut manifest.plugins)?;
    check_components(&mut manifest.patchers)?;
    check_components(&mut manifest.disabled_plugins)?;
    check_components(&mut manifest.disabled_patchers)?;
    if manifest.configs.len() > MAX_CONFIGS {
        return Err("对方发送的配置清单超出长度限制".into());
    }
    for config in manifest.configs.iter_mut() {
        if config.file.is_empty()
            || config.file.len() > MAX_COMPONENT_NAME + 8
            || config.file.contains(['/', '\\'])
            || config.file.chars().any(char::is_control)
            || config.sha256.len() > 64
        {
            return Err("对方发送的配置字段无效".into());
        }
        config.file.make_ascii_lowercase();
        config.sha256.make_ascii_lowercase();
    }
    for (label, value, limit) in [
        ("游戏版本", &mut manifest.game, 64usize),
        ("加载器", &mut manifest.loader, 64),
        ("Doorstop", &mut manifest.doorstop, 64),
        ("指纹", &mut manifest.fingerprint, 64),
    ] {
        if value.len() > limit {
            return Err(format!("对方发送的{label}字段过长"));
        }
        if !value.is_empty() && !value.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(format!("对方发送的{label}字段无效"));
        }
        value.make_ascii_lowercase();
    }
    if manifest.loader_version.len() > 64 || manifest.warning.len() > 512 {
        return Err("对方发送的加载器版本或提示过长".into());
    }
    if manifest.loader_version.chars().any(char::is_control)
        || manifest.warning.chars().any(char::is_control)
    {
        return Err("对方发送的提示包含控制字符".into());
    }
    Ok(())
}

/// Read the local install for the settings screen. Never invents components.
#[tauri::command]
pub fn compatibility_report(app: tauri::AppHandle) -> Result<Compatibility> {
    let settings = crate::service::load_settings(&app)?;
    let root = crate::service::game_root(&settings)?;
    let manifest = build(&root)?;
    let preflight = preflight(&root)?;
    Ok(Compatibility {
        game: manifest.game.clone(),
        loader: !manifest.loader.is_empty(),
        loader_version: manifest.loader_version.clone(),
        doorstop: !manifest.doorstop.is_empty(),
        plugins: manifest.plugins.iter().map(|c| c.name.clone()).collect(),
        patchers: manifest.patchers.iter().map(|c| c.name.clone()).collect(),
        disabled_plugins: manifest
            .disabled_plugins
            .iter()
            .map(|c| c.name.clone())
            .collect(),
        configs: manifest.configs.iter().map(|c| c.file.clone()).collect(),
        fingerprint: manifest.fingerprint.clone(),
        warning: manifest.warning.clone(),
        blockers: preflight.blockers,
        warnings: preflight.warnings,
    })
}
/// Local pre-handshake report: usable before joining any room.
#[tauri::command]
pub fn compatibility_preflight(app: tauri::AppHandle) -> Result<Preflight> {
    let settings = crate::service::load_settings(&app)?;
    let root = crate::service::game_root(&settings)?;
    preflight(&root)
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Compatibility {
    game: String,
    loader: bool,
    loader_version: String,
    doorstop: bool,
    plugins: Vec<String>,
    patchers: Vec<String>,
    disabled_plugins: Vec<String>,
    configs: Vec<String>,
    fingerprint: String,
    warning: String,
    blockers: Vec<Difference>,
    warnings: Vec<Difference>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        path::PathBuf,
        time::{SystemTime, UNIX_EPOCH},
    };

    fn sandbox() -> PathBuf {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "flotsam-compat-{}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(path.join("Flotsam_Data/Managed")).unwrap();
        std::fs::create_dir_all(path.join("BepInEx/plugins")).unwrap();
        std::fs::create_dir_all(path.join("BepInEx/core")).unwrap();
        std::fs::create_dir_all(path.join("BepInEx/config")).unwrap();
        std::fs::write(path.join("Flotsam_Data/Managed/Assembly-CSharp.dll"), b"game").unwrap();
        std::fs::write(path.join("BepInEx/core/BepInEx.dll"), b"loader").unwrap();
        std::fs::write(path.join("winhttp.dll"), b"doorstop").unwrap();
        // A realistic Doorstop configuration (matches the bundled BepInEx
        // 5.4.23.6 vendor file): without it the loader is inert.
        std::fs::write(
            path.join("doorstop_config.ini"),
            "[General]\r\nenabled = true\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        path
    }
    #[test]
    fn identical_installs_have_no_differences() {
        let root = sandbox();
        std::fs::write(root.join("BepInEx/plugins/Tools.dll"), b"mod").unwrap();
        std::fs::write(root.join("BepInEx/config/Tools.cfg"), "[A]\r\nkey = 1\r\n").unwrap();
        let a = build(&root).unwrap();
        let b = build(&root).unwrap();
        assert_eq!(a.fingerprint, b.fingerprint);
        assert!(diff(&a, &b).is_empty());
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn diff_names_missing_extra_and_changed_files() {
        let root = sandbox();
        std::fs::write(root.join("BepInEx/plugins/Tools.dll"), b"mod").unwrap();
        let local = build(&root).unwrap();
        std::fs::write(root.join("BepInEx/plugins/Other.dll"), b"other").unwrap();
        let remote = build(&root).unwrap();
        let differences = diff(&local, &remote);
        assert!(differences
            .iter()
            .any(|d| d.kind == "missing" && d.blocking));
        assert!(differences.iter().all(|d| !d.fix.is_empty() && !d.action.is_empty()));
        std::fs::write(root.join("BepInEx/plugins/Tools.dll"), b"changed").unwrap();
        let changed = build(&root).unwrap();
        assert!(diff(&local, &changed)
            .iter()
            .any(|d| d.kind == "version" && d.blocking));
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn disabled_plugins_are_not_part_of_the_loaded_set_but_are_listed() {
        let root = sandbox();
        std::fs::write(root.join("BepInEx/plugins/Tools.dll.disabled"), b"mod").unwrap();
        let manifest = build(&root).unwrap();
        assert!(manifest.plugins.is_empty());
        assert_eq!(manifest.disabled_plugins.len(), 1);
        // A disabled copy that the host has enabled is reported with an enabling hint.
        let mut remote = manifest.clone();
        remote.disabled_plugins.clear();
        remote.plugins = manifest.disabled_plugins.clone();
        let differences = diff(&manifest, &remote);
        let missing = differences
            .iter()
            .find(|d| d.kind == "missing")
            .expect("missing difference");
        assert!(missing.blocking);
        assert_eq!(missing.action, "enable_plugin");
        assert!(missing.fix.contains("停用"), "{}", missing.fix);
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn loader_and_game_differences_are_reported() {
        let root = sandbox();
        let local = build(&root).unwrap();
        let mut remote = local.clone();
        remote.loader = "different".into();
        remote.game = "different".into();
        let differences = diff(&local, &remote);
        assert!(differences.iter().any(|d| d.kind == "loader" && d.blocking));
        assert!(differences.iter().any(|d| d.kind == "game" && d.blocking));
    }
    #[test]
    fn doorstop_and_config_differences_are_actionable_but_config_is_not_blocking() {
        let root = sandbox();
        std::fs::write(root.join("BepInEx/plugins/Tools.dll"), b"mod").unwrap();
        std::fs::write(root.join("BepInEx/config/Tools.cfg"), "key = 1\n").unwrap();
        let local = build(&root).unwrap();
        let mut remote = local.clone();
        remote.doorstop = String::new();
        remote.configs.clear();
        let differences = diff(&local, &remote);
        assert!(differences
            .iter()
            .any(|d| d.kind == "doorstop" && d.blocking));
        let config = differences
            .iter()
            .find(|d| d.kind == "config")
            .expect("config difference");
        assert!(!config.blocking);
        assert!(config.fix.contains("配置"));
    }
    #[test]
    fn preflight_reports_missing_loader_and_install_issues_before_any_handshake() {
        let root = sandbox();
        std::fs::remove_file(root.join("BepInEx/core/BepInEx.dll")).unwrap();
        let report = preflight(&root).unwrap();
        assert!(!report.ok);
        assert!(report
            .blockers
            .iter()
            .any(|d| d.kind == "loader" && d.action == "install_loader"));
        // With the loader restored the install can be admitted locally.
        std::fs::write(root.join("BepInEx/core/BepInEx.dll"), b"loader").unwrap();
        assert!(preflight(&root).unwrap().ok);
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn preflight_checks_the_real_doorstop_configuration() {
        let root = sandbox();
        // Missing config: installable, because no third-party file would be
        // overwritten.
        std::fs::remove_file(root.join("doorstop_config.ini")).unwrap();
        let missing = preflight(&root).unwrap();
        assert!(!missing.ok);
        assert!(missing
            .blockers
            .iter()
            .any(|d| d.kind == "doorstop" && d.action == "install_loader"));
        // Disabled: an existing configuration must be repaired by hand.
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled=false\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        let disabled = preflight(&root).unwrap();
        let blocker = disabled
            .blockers
            .iter()
            .find(|d| d.kind == "doorstop")
            .expect("disabled doorstop blocker");
        assert!(blocker.blocking);
        assert_eq!(blocker.action, "read_mod");
        assert!(blocker.detail.contains("enabled=false"), "{}", blocker.detail);
        // Wrong target assembly: also a manual repair, never auto-overwritten.
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled=true\r\ntarget_assembly=Other\\Loader.dll\r\n",
        )
        .unwrap();
        let wrong = preflight(&root).unwrap();
        let blocker = wrong
            .blockers
            .iter()
            .find(|d| d.kind == "doorstop")
            .expect("wrong target blocker");
        assert_eq!(blocker.action, "read_mod");
        assert!(blocker.detail.contains("target_assembly"), "{}", blocker.detail);
        // A suffix match would accept a hostile prefix that loads other code.
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled=true\r\ntarget_assembly=Evil\\BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        assert!(preflight(&root)
            .unwrap()
            .blockers
            .iter()
            .any(|d| d.kind == "doorstop" && d.action == "read_mod"));
        // Unrecognised or duplicate keys are ambiguous, so they need review.
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled=maybe\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        assert!(doorstop_config(&root) != DoorstopConfig::Ready);
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled=true\r\nenabled=false\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        assert!(matches!(
            doorstop_config(&root),
            DoorstopConfig::Unreadable(_)
        ));
        // An enabled config with the canonical target is accepted.
        std::fs::write(
            root.join("doorstop_config.ini"),
            "[General]\r\nenabled = true\r\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n",
        )
        .unwrap();
        assert_eq!(doorstop_config(&root), DoorstopConfig::Ready);
        // The original file is untouched by inspection.
        assert!(std::fs::read_to_string(root.join("doorstop_config.ini"))
            .unwrap()
            .contains("BepInEx.Preloader.dll"));
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn received_manifest_is_bounded_and_normalised() {
        let mut manifest = Manifest {
            game: "AA".into(),
            loader: "bb".into(),
            ..Default::default()
        };
        manifest.plugins.push(Component {
            name: "Tools.DLL".into(),
            sha256: "CC".into(),
            ..Default::default()
        });
        sanitize(&mut manifest).unwrap();
        assert_eq!(manifest.game, "aa");
        assert_eq!(manifest.plugins[0].name, "tools.dll");
        let mut hostile = Manifest {
            plugins: (0..=MAX_COMPONENTS)
                .map(|index| Component {
                    name: format!("p{index}"),
                    ..Default::default()
                })
                .collect(),
            ..Default::default()
        };
        assert!(sanitize(&mut hostile).is_err());
        let mut traversal = Manifest {
            configs: vec![ConfigEntry {
                file: "../evil.cfg".into(),
                ..Default::default()
            }],
            ..Default::default()
        };
        assert!(sanitize(&mut traversal).is_err());
    }
    #[test]
    fn config_hash_ignores_crlf_and_blank_lines() {
        let root = sandbox();
        std::fs::write(root.join("BepInEx/plugins/Tools.dll"), b"mod").unwrap();
        std::fs::write(root.join("BepInEx/config/Tools.cfg"), "a = 1\r\n\r\nb = 2\r\n").unwrap();
        let first = build(&root).unwrap();
        std::fs::write(root.join("BepInEx/config/Tools.cfg"), "a = 1\nb = 2\n").unwrap();
        let second = build(&root).unwrap();
        assert_eq!(first.configs, second.configs);
        std::fs::remove_dir_all(root).unwrap();
    }
}
