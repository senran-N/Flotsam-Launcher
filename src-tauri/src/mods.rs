use crate::service::{err, walk_files, Result};
use dotnetdll::prelude::*;
use serde::Serialize;
use std::{
    collections::{HashMap, HashSet},
    fs,
    path::{Path, PathBuf},
    time::SystemTime,
};

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Dependency {
    pub plugin_id: String,
    pub minimum_version: Option<String>,
    pub optional: bool,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginMetadata {
    pub id: String,
    pub name: String,
    pub version: String,
    pub dependencies: Vec<Dependency>,
    pub incompatible: Vec<String>,
    pub processes: Vec<String>,
}
#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Metadata {
    pub assembly_name: Option<String>,
    pub assembly_version: Option<String>,
    pub company: Option<String>,
    pub plugins: Vec<PluginMetadata>,
    pub metadata_error: Option<String>,
}
#[derive(Clone, Debug, Serialize)]
pub struct Issue {
    pub code: String,
    pub severity: String,
    pub message: String,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModEntry {
    pub name: String,
    pub file: String,
    pub enabled: bool,
    pub size: u64,
    #[serde(flatten)]
    pub metadata: Metadata,
    pub issues: Vec<Issue>,
    pub package: Option<crate::packages::Package>,
}
#[derive(Default)]
pub struct MetadataCache {
    files: HashMap<PathBuf, (u64, Option<SystemTime>, Metadata)>,
    #[cfg(test)]
    parses: usize,
}

fn attribute_name(res: &Resolution<'_>, attr: &Attribute<'_>) -> String {
    match attr.constructor {
        UserMethod::Definition(index) => res[index.parent_type()].type_name(),
        UserMethod::Reference(index) => match &res[index].parent {
            MethodReferenceParent::Type(MethodType::Base(ty)) => match ty.as_ref() {
                BaseType::Type {
                    source: TypeSource::User(ty),
                    ..
                } => ty.type_name(res),
                _ => String::new(),
            },
            _ => String::new(),
        },
    }
}
fn string_arg(args: &[FixedArg<'_>], index: usize) -> Result<String> {
    match args.get(index) {
        Some(FixedArg::String(Some(value)))
            if !value.is_empty() && value.len() <= 1024 && !value.chars().any(char::is_control) =>
        {
            Ok(value.to_string())
        }
        _ => Err("插件声明包含无效文本".into()),
    }
}
fn is_string(parameter: &Parameter<MethodType>) -> bool {
    matches!(&parameter.1, ParameterType::Value(MethodType::Base(ty)) if matches!(ty.as_ref(),BaseType::String))
}
pub(crate) fn read_metadata(bytes: &[u8]) -> Result<Metadata> {
    if bytes.len() > 32 * 1024 * 1024 {
        return Err("DLL 超过元数据扫描的 32 MB 上限".into());
    }
    let mut res = Resolution::parse(
        bytes,
        ReadOptions {
            skip_method_bodies: true,
            lazy_attributes: true,
            ..Default::default()
        },
    )
    .map_err(err)?;
    // BepInEx 5 declares DependencyFlags as Int32. Normalise only this known
    // constructor argument: dotnetdll 0.3's enum attribute reader requires an
    // external resolver and rejects the underlying integral value. No DLL is loaded.
    let constructors: Vec<_> = res
        .enumerate_method_references()
        .filter_map(|(index, method)| {
            let attr = Attribute::new(
                UserMethod::Reference(index),
                CustomAttributeData {
                    constructor_args: vec![],
                    named_args: vec![],
                },
            );
            (method.name == ".ctor"
                && attribute_name(&res, &attr) == "BepInEx.BepInDependency"
                && method.signature.parameters.len() == 2
                && !is_string(&method.signature.parameters[1]))
            .then_some(index)
        })
        .collect();
    for index in constructors {
        res[index].signature.parameters[1].1 =
            ParameterType::Value(MethodType::Base(Box::new(BaseType::Int32)));
    }
    let mut output = Metadata::default();
    if let Some(assembly) = &res.assembly {
        output.assembly_name = Some(assembly.name.to_string());
        let v = assembly.version;
        output.assembly_version = Some(format!(
            "{}.{}.{}.{}",
            v.major, v.minor, v.build, v.revision
        ));
    }
    for attr in res.assembly_attributes().map_err(err)? {
        if attribute_name(&res, &attr) == "System.Reflection.AssemblyCompanyAttribute" {
            let data = attr
                .instantiation_data(&AlwaysFailsResolver, &res)
                .map_err(err)?;
            output.company = string_arg(&data.constructor_args, 0).ok();
        }
    }
    for (index, _) in res.enumerate_type_definitions() {
        let attrs = res.type_attributes(index).map_err(err)?;
        let Some(plugin) = attrs
            .iter()
            .find(|attr| attribute_name(&res, attr) == "BepInEx.BepInPlugin")
        else {
            continue;
        };
        let data = plugin
            .instantiation_data(&AlwaysFailsResolver, &res)
            .map_err(err)?;
        let mut plugin = PluginMetadata {
            id: string_arg(&data.constructor_args, 0)?,
            name: string_arg(&data.constructor_args, 1)?,
            version: string_arg(&data.constructor_args, 2)?,
            dependencies: vec![],
            incompatible: vec![],
            processes: vec![],
        };
        for attr in &attrs {
            let name = attribute_name(&res, attr);
            if !matches!(
                name.as_str(),
                "BepInEx.BepInDependency" | "BepInEx.BepInIncompatibility" | "BepInEx.BepInProcess"
            ) {
                continue;
            }
            let data = attr
                .instantiation_data(&AlwaysFailsResolver, &res)
                .map_err(err)?;
            let args = &data.constructor_args;
            let id = string_arg(args, 0)?;
            match name.as_str() {
                "BepInEx.BepInDependency" => {
                    let (minimum_version, optional) = match args.get(1) {
                        Some(FixedArg::String(_)) => (Some(string_arg(args, 1)?), false),
                        Some(FixedArg::Integral(IntegralParam::Int32(flags))) => {
                            (None, flags & 1 == 0 && flags & 2 != 0)
                        }
                        None => (None, false),
                        _ => return Err("无法识别前置依赖声明".into()),
                    };
                    plugin.dependencies.push(Dependency {
                        plugin_id: id,
                        minimum_version,
                        optional,
                    });
                }
                "BepInEx.BepInIncompatibility" => plugin.incompatible.push(id),
                _ => plugin.processes.push(id),
            }
        }
        output.plugins.push(plugin);
    }
    Ok(output)
}
fn cached_metadata(path: &Path, meta: &fs::Metadata, cache: &mut MetadataCache) -> Metadata {
    let stamp = (meta.len(), meta.modified().ok());
    if let Some((size, modified, value)) = cache.files.get(path) {
        if (*size, *modified) == stamp {
            return value.clone();
        }
    }
    #[cfg(test)]
    {
        cache.parses += 1;
    }
    let parsed = if meta.len() > 32 * 1024 * 1024 {
        Err("DLL 超过元数据扫描的 32 MB 上限".into())
    } else {
        fs::read(path).map_err(err).and_then(|bytes| {
            std::panic::catch_unwind(|| read_metadata(&bytes))
                .unwrap_or_else(|_| Err("DLL 元数据格式异常".into()))
        })
    };
    let value = parsed.unwrap_or_else(|error| Metadata {
        metadata_error: Some(error),
        ..Default::default()
    });
    cache
        .files
        .insert(path.to_path_buf(), (stamp.0, stamp.1, value.clone()));
    value
}
pub(crate) fn scan(root: &Path, cache: &mut MetadataCache) -> Result<Vec<ModEntry>> {
    crate::multiplayer::refresh_existing_plugin(root);
    scan_directory(
        &root.join("BepInEx/plugins"),
        root.join("BepInEx/core/BepInEx.dll").is_file(),
        cache,
    )
}
pub(crate) fn scan_directory(
    plugins: &Path,
    loader: bool,
    cache: &mut MetadataCache,
) -> Result<Vec<ModEntry>> {
    let mut paths = vec![];
    walk_files(plugins, &mut paths)?;
    let mut alive = HashSet::new();
    let mut mods = vec![];
    for path in paths {
        let filename = path.file_name().unwrap_or_default().to_string_lossy();
        let lower = filename.to_ascii_lowercase();
        let enabled = lower.ends_with(".dll");
        if !enabled && !lower.ends_with(".dll.disabled") {
            continue;
        }
        let meta = fs::metadata(&path).map_err(err)?;
        let metadata = cached_metadata(&path, &meta, cache);
        alive.insert(path.clone());
        let name = metadata
            .plugins
            .first()
            .map(|p| p.name.clone())
            .unwrap_or_else(|| {
                filename[..filename.len() - if enabled { 4 } else { 13 }].to_string()
            });
        mods.push(ModEntry {
            name,
            file: path
                .strip_prefix(plugins)
                .map_err(err)?
                .to_string_lossy()
                .into(),
            enabled,
            size: meta.len(),
            metadata,
            issues: vec![],
            package: None,
        });
    }
    cache.files.retain(|path, _| alive.contains(path));
    diagnose(&mut mods, loader);
    mods.sort_by_key(|entry| entry.name.to_lowercase());
    Ok(mods)
}
fn version(value: &str) -> Option<[i32; 4]> {
    let parts: Vec<_> = value.split('.').collect();
    if !(2..=4).contains(&parts.len()) {
        return None;
    }
    let mut result = [-1; 4];
    for (i, value) in parts.iter().enumerate() {
        if value.is_empty() || !value.bytes().all(|c| c.is_ascii_digit()) {
            return None;
        }
        result[i] = value.parse().ok()?;
    }
    Some(result)
}
fn issue(code: &str, message: String) -> Issue {
    Issue {
        code: code.into(),
        severity: "error".into(),
        message,
    }
}
pub(crate) fn diagnose(mods: &mut [ModEntry], loader: bool) {
    let mut enabled: HashMap<String, Vec<PluginMetadata>> = HashMap::new();
    for entry in mods.iter().filter(|e| e.enabled) {
        for plugin in &entry.metadata.plugins {
            enabled
                .entry(plugin.id.clone())
                .or_default()
                .push(plugin.clone());
        }
    }
    for entry in mods.iter_mut() {
        entry.issues.clear();
        if !entry.enabled {
            continue;
        }
        if !loader {
            entry
                .issues
                .push(issue("loader", "尚未安装 BepInEx".into()));
        }
        if entry.metadata.metadata_error.is_some() {
            entry.issues.push(Issue {
                code: "metadata".into(),
                severity: "warning".into(),
                message: "无法读取插件声明，请检查文件及作者说明".into(),
            });
        }
        for plugin in &entry.metadata.plugins {
            if version(&plugin.version).is_none() {
                entry.issues.push(issue(
                    "version",
                    format!(
                        "{} 的版本号无法被 BepInEx 5 识别：{}",
                        plugin.name, plugin.version
                    ),
                ));
            }
            if enabled.get(&plugin.id).is_some_and(|items| items.len() > 1) {
                entry
                    .issues
                    .push(issue("duplicate", format!("重复插件 ID：{}", plugin.id)));
            }
            if !plugin.processes.is_empty()
                && !plugin.processes.iter().any(|p| {
                    p.eq_ignore_ascii_case("Flotsam") || p.eq_ignore_ascii_case("Flotsam.exe")
                })
            {
                entry.issues.push(issue(
                    "process",
                    format!("{} 未声明适用于 Flotsam.exe", plugin.name),
                ));
            }
            for dep in &plugin.dependencies {
                match enabled.get(&dep.plugin_id).and_then(|items| items.first()) {
                    None if !dep.optional => entry.issues.push(issue(
                        "missing",
                        format!("缺少已启用前置：{}", dep.plugin_id),
                    )),
                    Some(installed) => {
                        if let Some(minimum) = &dep.minimum_version {
                            match (version(&installed.version), version(minimum)) {
                                (Some(found), Some(required)) if found < required => {
                                    entry.issues.push(issue(
                                        "dependencyVersion",
                                        format!(
                                            "{} 需要 {} 或更新版本，当前为 {}",
                                            dep.plugin_id, minimum, installed.version
                                        ),
                                    ))
                                }
                                (None, _) | (_, None) => entry.issues.push(issue(
                                    "dependencyVersion",
                                    format!("无法判断 {} 的版本要求 {}", dep.plugin_id, minimum),
                                )),
                                _ => {}
                            }
                        }
                    }
                    _ => {}
                }
            }
            for id in &plugin.incompatible {
                if enabled.contains_key(id) {
                    entry.issues.push(issue(
                        "incompatible",
                        format!("{} 与 {} 不兼容", plugin.name, id),
                    ));
                }
            }
            if cycle(&plugin.id, &enabled) {
                entry
                    .issues
                    .push(issue("cycle", format!("{} 存在循环前置依赖", plugin.name)));
            }
        }
    }
}
fn cycle(target: &str, plugins: &HashMap<String, Vec<PluginMetadata>>) -> bool {
    let mut visited = HashSet::new();
    let mut pending = vec![target];
    while let Some(current) = pending.pop() {
        if !visited.insert(current) {
            continue;
        }
        if let Some(items) = plugins.get(current) {
            for p in items {
                for dep in p.dependencies.iter().filter(|d| !d.optional) {
                    if dep.plugin_id == target {
                        return true;
                    }
                    pending.push(&dep.plugin_id);
                }
            }
        }
    }
    false
}
pub(crate) fn require_compatible(mods: &[ModEntry]) -> Result<()> {
    if let Some(entry) = mods
        .iter()
        .find(|entry| entry.issues.iter().any(|issue| issue.severity == "error"))
    {
        let problems = entry
            .issues
            .iter()
            .filter(|issue| issue.severity == "error")
            .map(|issue| issue.message.as_str())
            .collect::<Vec<_>>()
            .join("；");
        return Err(format!("{}：{}", entry.name, problems));
    }
    Ok(())
}
pub(crate) fn preflight_toggle(
    mods: &mut [ModEntry],
    file: &str,
    enabled: bool,
    loader: bool,
) -> Result<()> {
    let existing: HashSet<_> = mods
        .iter()
        .flat_map(|m| {
            m.issues
                .iter()
                .filter(|i| i.severity == "error")
                .map(|i| (m.file.clone(), i.code.clone(), i.message.clone()))
        })
        .collect();
    mods.iter_mut()
        .find(|m| m.file == file)
        .ok_or("模组状态已变化，请刷新")?
        .enabled = enabled;
    diagnose(mods, loader);
    for entry in mods {
        for issue in &entry.issues {
            if issue.severity == "error"
                && !existing.contains(&(
                    entry.file.clone(),
                    issue.code.clone(),
                    issue.message.clone(),
                ))
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

#[cfg(test)]
pub(crate) fn require_unique_ids(installed: &Path, incoming: &Path) -> Result<()> {
    require_unique_ids_except(installed, incoming, None)
}
pub(crate) fn require_unique_ids_except(
    installed: &Path,
    incoming: &Path,
    except: Option<&Path>,
) -> Result<()> {
    fn ids(root: &Path, except: Option<&Path>) -> Result<Vec<String>> {
        let mut paths = vec![];
        if root.is_file() {
            paths.push(root.to_path_buf());
        } else {
            walk_files(root, &mut paths)?;
            paths.retain(|p| {
                p.file_name().is_some_and(|name| {
                    let n = name.to_string_lossy().to_ascii_lowercase();
                    n.ends_with(".dll") || n.ends_with(".dll.disabled")
                })
            });
        }
        let mut ids = vec![];
        let mut cache = MetadataCache::default();
        for path in paths
            .into_iter()
            .filter(|p| !except.is_some_and(|e| p.starts_with(e)))
        {
            let meta = fs::metadata(&path).map_err(err)?;
            ids.extend(
                cached_metadata(&path, &meta, &mut cache)
                    .plugins
                    .into_iter()
                    .map(|p| p.id),
            );
        }
        Ok(ids)
    }
    let mut occupied: HashSet<_> = ids(installed, except)?.into_iter().collect();
    for id in ids(incoming, None)? {
        if !occupied.insert(id.clone()) {
            return Err(format!("插件 ID 已存在：{id}，请先处理旧版本"));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    const FIXTURE: &[u8] = include_bytes!("../../tests/fixtures/metadata.dll");
    fn entry(id: &str, version: &str) -> ModEntry {
        ModEntry {
            name: id.into(),
            file: format!("{id}.dll"),
            enabled: true,
            size: 1,
            metadata: Metadata {
                plugins: vec![PluginMetadata {
                    id: id.into(),
                    name: id.into(),
                    version: version.into(),
                    dependencies: vec![],
                    incompatible: vec![],
                    processes: vec![],
                }],
                ..Default::default()
            },
            issues: vec![],
            package: None,
        }
    }
    fn dependency(id: &str, minimum: Option<&str>, optional: bool) -> Dependency {
        Dependency {
            plugin_id: id.into(),
            minimum_version: minimum.map(str::to_string),
            optional,
        }
    }
    #[test]
    fn reads_real_bepinex_attributes_and_bundled_plugin_without_loading() {
        let meta = read_metadata(FIXTURE).unwrap();
        assert_eq!(meta.company.as_deref(), Some("Metadata fixture company"));
        assert_eq!(meta.assembly_version.as_deref(), Some("3.2.1.0"));
        assert_eq!(meta.plugins.len(), 2);
        let p = &meta.plugins[0];
        assert_eq!(p.id, "fixture.main");
        assert_eq!(p.name, "海上工坊");
        assert_eq!(p.version, "2.1.0");
        assert_eq!(p.dependencies[0].minimum_version.as_deref(), Some("1.2.0"));
        assert!(p.dependencies[1].optional);
        assert!(!p.dependencies[2].optional);
        assert_eq!(p.incompatible, ["fixture.conflict"]);
        assert_eq!(p.processes, ["Flotsam.exe"]);
        let bundled = read_metadata(include_bytes!(
            "../../multiplayer/payload/Flotsam.Multiplayer.dll"
        ))
        .unwrap();
        assert_eq!(bundled.plugins[0].id, "local.flotsam.multiplayer");
        assert!(version(&bundled.plugins[0].version).is_some());
    }
    #[test]
    fn identifies_missing_disabled_and_old_dependencies_but_allows_absent_optional() {
        let mut main = entry("main", "1.0");
        main.metadata.plugins[0].dependencies = vec![
            dependency("required", Some("1.2.0"), false),
            dependency("optional", None, true),
        ];
        let mut mods = vec![main, entry("required", "1.1.99")];
        diagnose(&mut mods, true);
        assert_eq!(mods[0].issues[0].code, "dependencyVersion");
        mods[1].metadata.plugins[0].version = "1.2.0".into();
        diagnose(&mut mods, true);
        assert!(require_compatible(&mods).is_ok());
        mods[1].enabled = false;
        diagnose(&mut mods, true);
        assert_eq!(mods[0].issues[0].code, "missing");
        assert!(require_compatible(&mods).is_err());
    }
    #[test]
    fn detects_duplicate_ids_conflicts_cycles_and_process_mismatch() {
        let mut a = entry("a", "1.0");
        a.metadata.plugins[0]
            .dependencies
            .push(dependency("b", None, false));
        a.metadata.plugins[0].incompatible.push("b".into());
        let mut b = entry("b", "1.0");
        b.metadata.plugins[0]
            .dependencies
            .push(dependency("a", None, false));
        b.metadata.plugins[0]
            .processes
            .push("AnotherGame.exe".into());
        let mut mods = vec![a, b, entry("a", "2.0")];
        diagnose(&mut mods, true);
        for code in ["duplicate", "incompatible", "cycle"] {
            assert!(mods[0].issues.iter().any(|i| i.code == code), "{code}");
        }
        assert!(mods[1].issues.iter().any(|i| i.code == "process"));
    }
    #[test]
    fn system_version_order_preserves_unspecified_build_and_rejects_semver() {
        assert!(version("1.2") < version("1.2.0"));
        assert!(version("1.10.0") > version("1.9.99"));
        for value in ["1", "1.2-beta", "-1.2", "1.2.3.4.5", "2147483648.0", ""] {
            assert!(version(value).is_none(), "{value}");
        }
    }
    #[test]
    fn toggle_preflight_preserves_dependencies_and_allows_repair_of_existing_errors() {
        let mut main = entry("main", "1.0");
        main.metadata.plugins[0]
            .dependencies
            .push(dependency("required", None, false));
        let mut mods = vec![main, entry("required", "1.0")];
        diagnose(&mut mods, true);
        assert!(preflight_toggle(&mut mods, "required.dll", false, true)
            .unwrap_err()
            .contains("缺少已启用前置"));
        diagnose(&mut mods, true);
        assert!(preflight_toggle(&mut mods, "required.dll", true, true).is_ok());
        mods[1].enabled = false;
        diagnose(&mut mods, true);
        assert!(preflight_toggle(&mut mods, "main.dll", false, true).is_ok());
    }
    #[test]
    fn cache_reuses_unchanged_dll_and_invalidates_modified_and_removed_files() {
        let root = std::env::temp_dir().join(format!(
            "flotsam-metadata-{}",
            std::time::UNIX_EPOCH.elapsed().unwrap().as_nanos()
        ));
        let plugins = root.join("BepInEx/plugins");
        fs::create_dir_all(&plugins).unwrap();
        let dll = plugins.join("renamed.dll");
        fs::write(&dll, FIXTURE).unwrap();
        let mut cache = MetadataCache::default();
        for _ in 0..20 {
            let entries = scan(&root, &mut cache).unwrap();
            assert_eq!(entries[0].name, "海上工坊");
        }
        assert_eq!(cache.parses, 1);
        fs::write(&dll, b"broken").unwrap();
        let entries = scan(&root, &mut cache).unwrap();
        assert!(entries[0].metadata.metadata_error.is_some());
        assert_eq!(cache.parses, 2);
        fs::remove_file(dll).unwrap();
        assert!(scan(&root, &mut cache).unwrap().is_empty());
        assert!(cache.files.is_empty());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn package_identity_rejects_renamed_plugin_and_internal_duplicates() {
        let root = std::env::temp_dir().join(format!(
            "flotsam-id-{}",
            std::time::UNIX_EPOCH.elapsed().unwrap().as_nanos()
        ));
        let installed = root.join("installed");
        let incoming = root.join("incoming");
        fs::create_dir_all(&installed).unwrap();
        fs::create_dir_all(&incoming).unwrap();
        fs::write(installed.join("first.dll.disabled"), FIXTURE).unwrap();
        fs::write(incoming.join("renamed.dll"), FIXTURE).unwrap();
        assert!(require_unique_ids(&installed, &incoming)
            .unwrap_err()
            .contains("fixture.main"));
        fs::remove_file(installed.join("first.dll.disabled")).unwrap();
        assert!(require_unique_ids(&installed, &incoming).is_ok());
        fs::write(incoming.join("another.dll"), FIXTURE).unwrap();
        assert!(require_unique_ids(&installed, &incoming).is_err());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    #[ignore = "Explicit performance sample, prints structured timing data"]
    fn catalog_scan_metrics() {
        let root = std::env::temp_dir().join(format!(
            "flotsam-metrics-{}",
            std::time::UNIX_EPOCH.elapsed().unwrap().as_nanos()
        ));
        let plugins = root.join("BepInEx/plugins");
        fs::create_dir_all(&plugins).unwrap();
        for i in 0..200 {
            fs::write(plugins.join(format!("sample-{i}.dll")), FIXTURE).unwrap();
        }
        let mut cache = MetadataCache::default();
        let start = std::time::Instant::now();
        let entries = scan(&root, &mut cache).unwrap();
        let cold = start.elapsed().as_secs_f64() * 1000.0;
        let parsed = cache.parses;
        let mut warm = vec![];
        for _ in 0..20 {
            let start = std::time::Instant::now();
            scan(&root, &mut cache).unwrap();
            warm.push(start.elapsed().as_secs_f64() * 1000.0);
        }
        warm.sort_by(f64::total_cmp);
        assert_eq!(cache.parses, parsed);
        println!(
            "METRICS {}",
            serde_json::json!({"dlls":entries.len(),"bytesPerDll":FIXTURE.len(),"coldMs":cold,"warmMedianMs":warm[10],"warmMaxMs":warm[19],"coldParses":parsed,"warmAdditionalParses":cache.parses-parsed,"samples":20,"profile":"debug","dataset":"200 copies of the real BepInEx metadata fixture; duplicate IDs intentionally retained"})
        );
        fs::remove_dir_all(root).unwrap();
    }
}
