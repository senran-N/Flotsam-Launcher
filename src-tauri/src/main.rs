#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod compat;
mod mods;
mod multiplayer;
mod network;
mod online;
mod packages;
mod profiles;
mod queue;
mod save_restore;
mod service;

use compat::{compatibility_preflight, compatibility_report};
use multiplayer::*;
use network::*;
use online::*;
use profiles::*;
use queue::*;
use save_restore::{list_save_backups, restore_save_backup};
use service::*;

fn main() {
    tauri::Builder::default()
        .manage(LauncherState::default())
        .manage(NetworkState::default())
        .manage(OnlineState::default())
        .manage(std::sync::Arc::new(queue::DownloadQueue::default()))
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
            network_interfaces,
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
