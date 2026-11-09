#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod compat;
mod mods;
mod multiplayer;
mod network;
mod online;
mod packages;
mod profiles;
mod queue;
mod service;
mod save_restore;
use multiplayer::*;
use network::*;
use online::*;
use profiles::*;
use compat::{compatibility_report, compatibility_preflight};
use queue::*;
use service::*;
use save_restore::{list_save_backups, restore_save_backup};
use tauri::Manager;

fn main() {
    tauri::Builder::default()
        .manage(LauncherState::default())
        .manage(NetworkState::default())
        .manage(OnlineState::default())
        .manage(std::sync::Arc::new(queue::DownloadQueue::default()))
        .setup(|app| {
            if std::env::args().any(|arg| arg == "--diagnose") {
                let result = get_snapshot_sync(app.handle().clone(), &app.state::<LauncherState>());
                let report = match result {
                    Ok(snapshot) => serde_json::to_string_pretty(&snapshot)?,
                    Err(error) => {
                        serde_json::to_string_pretty(&serde_json::json!({"error":error}))?
                    }
                };
                std::fs::write("diagnostics.json", report)?;
                app.handle().exit(0);
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_snapshot,
            save_settings,
            select_game_directory,
            launch_game,
            open_directory,
            import_mod,
            toggle_mod,
            open_mod_file,
            update_mod_package,
            change_mod_package,
            backup_saves,
            list_save_backups,
            restore_save_backup,
            room_status,
            host_room,
            join_room,
            leave_room,
            room_chat,
            multiplayer_status,
            install_loader,
            install_multiplayer,
            launch_multiplayer,
            online_catalog,
            online_files,
            connect_nexus,
            disconnect_nexus,
            nexus_account,
            list_downloads,
            install_download,
            open_mod_source,
            queue_download,
            download_tasks,
            cancel_download,
            retry_download,
            clear_downloads,
            last_join_differences,
            clear_join_differences,
            last_join_failure,
            room_kick,
            room_notice,
            download_cache,
            discard_download_cache,
            recover_download_queue,
            download_queue_counts,
            compatibility_preflight,
            compatibility_report,
            list_mod_profiles,
            save_mod_profile,
            apply_mod_profile,
            delete_mod_profile,
            export_mod_profile,
            import_mod_profile
        ])
        .run(tauri::generate_context!())
        .expect("启动器无法运行");
}
