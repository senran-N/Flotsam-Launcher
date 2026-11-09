//! Every test uses its own random OS temp directory. No AppHandle, USERPROFILE,
//! game installation, real launcher backup, or production saves_root is used.
use super::*;

struct TempDir { path: PathBuf }
impl TempDir {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!("flotsam-save-restore-test-{}", token().unwrap()));
        fs::create_dir(&path).unwrap();
        Self { path }
    }
    fn layout(&self) -> Layout {
        Layout::new(&self.path.join("app"), &self.path.join("game-data/Saves")).unwrap()
    }
}
impl Drop for TempDir {
    fn drop(&mut self) {
        // Only this test's own freshly-created random directory is ever removed.
        // Tests unlink their explicitly-created junctions/links before this drop.
        let _ = fs::remove_dir_all(&self.path);
    }
}
fn put(path: impl AsRef<Path>, bytes: &[u8]) {
    let path = path.as_ref();
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(path, bytes).unwrap();
}
fn backup(layout: &Layout) -> PathBuf {
    let path = layout.backups.join("存档-1700000000000");
    put(path.join("Town/Autosaves/save.wpd"), b"new-save-bytes\0\xff");
    put(path.join(ANCILLARY), br#"{"game_path":"ancillary, not save data"}"#);
    path
}
fn live(layout: &Layout) {
    put(layout.live.join("Town/Autosaves/save.wpd"), b"original-save");
    put(layout.live.join("OnlyOld/save.wpd"), b"do-not-delete");
    fs::create_dir_all(layout.live.join("EmptyOriginalDirectory")).unwrap();
}
fn assert_original(layout: &Layout) {
    assert_eq!(fs::read(layout.live.join("Town/Autosaves/save.wpd")).unwrap(), b"original-save");
    assert_eq!(fs::read(layout.live.join("OnlyOld/save.wpd")).unwrap(), b"do-not-delete");
    assert!(layout.live.join("EmptyOriginalDirectory").is_dir());
}

#[test]
fn replaces_entire_saves_and_retains_exact_original_generation() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    let before = manifest(&layout.live, false, true).unwrap();
    let source = backup(&layout);
    let source_before = manifest(&source, false, true).unwrap();
    let report = restore_at(&layout, "存档-1700000000000").unwrap();
    assert_eq!(report.files, 1);
    assert_eq!(report.bytes, b"new-save-bytes\0\xff".len() as u64);
    assert_eq!(fs::read(layout.live.join("Town/Autosaves/save.wpd")).unwrap(), b"new-save-bytes\0\xff");
    assert!(!layout.live.join("OnlyOld").exists());
    assert!(!layout.live.join(ANCILLARY).exists());
    let rollback = PathBuf::from(report.rollback_generation.unwrap());
    assert!(rollback.starts_with(&layout.store));
    assert_eq!(manifest(&rollback, false, true).unwrap(), before);
    assert_eq!(manifest(&source, false, true).unwrap(), source_before);
    assert!(!layout.journal().exists());
    recover(&layout).unwrap();
    assert_eq!(manifest(&rollback, false, true).unwrap(), before);
}
#[test]
fn accepts_actual_absolute_backup_path_including_windows_extended_prefix() {
    let temp = TempDir::new();
    let layout = temp.layout();
    let source = fs::canonicalize(backup(&layout)).unwrap();
    restore_at(&layout, source.to_str().unwrap()).unwrap();
    assert!(layout.live.join("Town/Autosaves/save.wpd").is_file());
}
#[test]
fn missing_saves_is_supported_without_canonicalizing_missing_directory() {
    let temp = TempDir::new();
    let layout = temp.layout();
    backup(&layout);
    assert!(!layout.live.exists());
    let result = restore_at(&layout, "存档-1700000000000").unwrap();
    assert!(result.rollback_generation.is_none());
    assert!(layout.live.is_dir());
}
#[test]
fn rejects_traversal_foreign_backups_pending_and_ancillary_only_without_touching_live() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let foreign = temp.path.join("foreign/存档-1700000000000");
    put(foreign.join("stolen.wpd"), b"not-launcher-backup");
    for bad in ["../存档-1700000000000", ".pending-1700000000000", "存档-nope", "backups/存档-1700000000000", foreign.to_str().unwrap()] {
        assert!(restore_at(&layout, bad).is_err(), "accepted {bad}");
        assert_original(&layout);
    }
    let empty = layout.backups.join("存档-1700000000001");
    put(empty.join(ANCILLARY), b"{}");
    assert!(restore_at(&layout, "存档-1700000000001").is_err());
    assert_original(&layout);
}
#[test]
fn recovery_before_first_rename_keeps_original_and_stage() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let (_, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    recover(&layout).unwrap();
    assert_original(&layout);
    assert!(stage.is_dir());
    assert!(!layout.journal().exists());
    recover(&layout).unwrap();
}
#[test]
fn recovery_between_renames_returns_original_whole_directory() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let (journal, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    let rollback = layout.generation("rollback", &journal.token).unwrap();
    rename_checked(&layout, &layout.live, &rollback).unwrap();
    assert!(!layout.live.exists());
    recover(&layout).unwrap();
    assert_original(&layout);
    assert!(stage.is_dir());
    assert!(!rollback.exists());
}
#[test]
fn recovery_after_install_before_commit_retains_both_generations() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let (journal, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    rename_checked(&layout, &layout.live, &layout.generation("rollback", &journal.token).unwrap()).unwrap();
    rename_checked(&layout, &stage, &layout.live).unwrap();
    recover(&layout).unwrap();
    assert_original(&layout);
    let aborted = layout.generation("aborted", &journal.token).unwrap();
    assert_eq!(manifest(&aborted, false, true).unwrap(), journal.manifest);
    recover(&layout).unwrap();
}
#[test]
fn recovery_itself_can_be_interrupted_between_its_renames() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let (journal, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    rename_checked(&layout, &layout.live, &layout.generation("rollback", &journal.token).unwrap()).unwrap();
    rename_checked(&layout, &stage, &layout.live).unwrap();
    rename_checked(&layout, &layout.live, &layout.generation("aborted", &journal.token).unwrap()).unwrap();
    recover(&layout).unwrap();
    assert_original(&layout);
}
#[test]
fn committed_recovery_keeps_restored_saves_and_old_generation() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let (journal, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    let rollback = layout.generation("rollback", &journal.token).unwrap();
    rename_checked(&layout, &layout.live, &rollback).unwrap();
    rename_checked(&layout, &stage, &layout.live).unwrap();
    write_new(&layout.generation("commit", &journal.token).unwrap(), journal.token.as_bytes()).unwrap();
    recover(&layout).unwrap();
    assert_eq!(manifest(&layout.live, false, true).unwrap(), journal.manifest);
    assert_eq!(fs::read(rollback.join("OnlyOld/save.wpd")).unwrap(), b"do-not-delete");
}
#[test]
fn no_previous_saves_crash_restores_absence_but_keeps_aborted_bytes() {
    let temp = TempDir::new();
    let layout = temp.layout();
    backup(&layout);
    let (journal, stage) = prepare(&layout, "存档-1700000000000").unwrap();
    rename_checked(&layout, &stage, &layout.live).unwrap();
    recover(&layout).unwrap();
    assert!(!layout.live.exists());
    assert_eq!(manifest(&layout.generation("aborted", &journal.token).unwrap(), false, true).unwrap(), journal.manifest);
}
#[test]
fn malformed_journal_and_ambiguous_states_fail_closed() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    write_new(&layout.journal(), b"{truncated").unwrap();
    assert!(recover(&layout).is_err());
    assert_original(&layout);
    assert!(layout.journal().is_file());
    fs::remove_file(layout.journal()).unwrap();
    backup(&layout);
    let (journal, _) = prepare(&layout, "存档-1700000000000").unwrap();
    // A rollback directory appearing before any rename is not an expected state.
    fs::create_dir(layout.generation("rollback", &journal.token).unwrap()).unwrap();
    assert!(recover(&layout).is_err());
    assert_original(&layout);
}
#[test]
fn source_changes_after_manifest_are_rejected_before_renames() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    let source = backup(&layout);
    let expected = manifest(&source, true, true).unwrap();
    put(source.join("Town/Autosaves/save.wpd"), b"different-save-bytes");
    let stage = layout.generation("stage", &token().unwrap()).unwrap();
    assert!(stage_snapshot(&source, &stage, &expected).is_err());
    assert_original(&layout);
    assert!(!layout.journal().exists());
}
#[test]
fn late_preflight_failure_never_renames_original_saves() {
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    assert!(restore_at_with_preflight(&layout, "存档-1700000000000", || {
        Err("game started during staging".into())
    }).is_err());
    assert_original(&layout);
    assert!(!layout.journal().exists());
}
#[test]
fn windows_unsafe_names_and_oversized_manifests_are_rejected() {
    for name in ["../save", "a/../b", "a//b", "NUL.wpd", "COM1.wpd", "town/save:stream", "a/trailing.", "a/trailing ", "a\\b", "/absolute"] {
        assert!(valid_relative(name).is_err(), "accepted {name}");
    }
    let duplicate = Manifest { directories: vec!["Town".into(), "town".into()], files: vec![] };
    assert!(validate_manifest(&duplicate, true).is_err());
    let oversized = Manifest { directories: vec![], files: vec![SaveFile { path: "save".into(), bytes: MAX_FILE_BYTES + 1, sha256: "0".repeat(64) }] };
    assert!(validate_manifest(&oversized, true).is_err());
}
#[cfg(unix)]
#[test]
fn rejects_linked_backup_files_and_linked_current_saves_without_touching_targets() {
    use std::os::unix::fs::symlink;
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    let source = backup(&layout);
    let outside = temp.path.join("outside");
    put(outside.join("keep"), b"outside-original");
    symlink(outside.join("keep"), source.join("link.wpd")).unwrap();
    assert!(restore_at(&layout, "存档-1700000000000").is_err());
    assert_original(&layout);
    fs::remove_file(source.join("link.wpd")).unwrap();
    fs::rename(&layout.live, temp.path.join("parked-original")).unwrap();
    symlink(&outside, &layout.live).unwrap();
    assert!(restore_at(&layout, "存档-1700000000000").is_err());
    assert_eq!(fs::read(outside.join("keep")).unwrap(), b"outside-original");
    fs::remove_file(&layout.live).unwrap();
}
#[cfg(windows)]
#[test]
fn rejects_windows_junction_for_backup_and_current_saves() {
    use std::os::windows::process::CommandExt;
    let temp = TempDir::new();
    let layout = temp.layout();
    live(&layout);
    backup(&layout);
    let outside = temp.path.join("outside");
    put(outside.join("keep"), b"outside-original");
    // Junction creation does not require Developer Mode. Both ends are tempdir.
    let junction = |link: &Path, target: &Path| {
        let status = std::process::Command::new("cmd.exe")
            .args(["/D", "/C", "mklink", "/J"])
            .arg(link).arg(target).creation_flags(0x08000000).status().unwrap();
        assert!(status.success(), "could not create temporary test junction");
    };
    let linked_backup = layout.backups.join("存档-1700000000001");
    junction(&linked_backup, &outside);
    assert!(restore_at(&layout, "存档-1700000000001").is_err());
    assert_original(&layout);
    fs::remove_dir(&linked_backup).unwrap();
    fs::rename(&layout.live, temp.path.join("parked-original")).unwrap();
    junction(&layout.live, &outside);
    assert!(restore_at(&layout, "存档-1700000000000").is_err());
    assert_eq!(fs::read(outside.join("keep")).unwrap(), b"outside-original");
    fs::remove_dir(&layout.live).unwrap();
}
#[test]
fn backup_store_lock_is_cross_process_compatible_and_exclusive() {
    let temp = TempDir::new();
    let layout = temp.layout();
    let path = layout.app.join("save-backup.lock");
    let first = lock_path(&path).unwrap();
    assert!(lock_path(&path).is_err());
    drop(first);
    let _next = lock_path(&path).unwrap();
}
