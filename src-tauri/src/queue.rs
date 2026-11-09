//! Bounded download queue: deduplicates identical tasks, limits concurrency,
//! supports cancel/retry, and reports real progress. Each task owns one real
//! HTTP download and one filesystem identity (`nexus-{mod}-{file}`), so asking
//! for the same file twice reuses the running job instead of writing it twice.
//!
//! The queue state is persisted to the launcher data directory so an
//! interrupted session can explain what happened after a restart. Persistence
//! deliberately stores only identity and progress fields: the Nexus API key and
//! the signed NXM link both contain credentials and are never written to disk.
use crate::{
    online,
    service::Result,
};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex, MutexGuard,
    },
    time::{SystemTime, UNIX_EPOCH},
};
use tauri::{AppHandle, Emitter};
use tokio::sync::Semaphore;

pub const MAX_CONCURRENT: usize = 2;
const MAX_PENDING: usize = 32;
const MAX_RECORDS: usize = 64;
/// Relative to the launcher data directory, next to the download cache.
const STATE_FILE: &str = "downloads/queue.json";
const MAX_STATE_BYTES: u64 = 256 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskState {
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}
impl TaskState {
    fn terminal(self) -> bool {
        matches!(self, TaskState::Done | TaskState::Failed | TaskState::Cancelled)
    }
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskView {
    pub id: String,
    pub mod_id: u64,
    pub file_id: u64,
    pub name: String,
    pub state: TaskState,
    pub received: u64,
    pub total: u64,
    pub attempts: u32,
    pub error: String,
    pub created_at: u64,
}
struct Task {
    view: TaskView,
    /// API key captured at enqueue time. Empty for a task restored after a
    /// restart; retry refills it from the in-memory account state.
    key: String,
    /// Signed download link. Never persisted (it embeds a per-account key).
    nxm: Option<String>,
    cancel: Arc<online::Cancel>,
}
#[derive(Default)]
struct Inner {
    tasks: Vec<Arc<Mutex<Task>>>,
    dispatching: bool,
}
pub struct DownloadQueue {
    inner: Mutex<Inner>,
    slots: Arc<Semaphore>,
    /// Directory of the persisted queue file; set on first use with an app.
    state_path: Mutex<Option<PathBuf>>,
    persistence: Mutex<()>,
    restored: AtomicBool,
    restoration: Mutex<()>,

}
impl Default for DownloadQueue {
    fn default() -> Self {
        Self {
            inner: Mutex::new(Inner::default()),
            slots: Arc::new(Semaphore::new(MAX_CONCURRENT)),
            state_path: Mutex::new(None),
            persistence: Mutex::new(()),
            restored: AtomicBool::new(false),
            restoration: Mutex::new(()),
        }
    }
}
fn lock(inner: &Mutex<Inner>) -> MutexGuard<'_, Inner> {
    inner.lock().unwrap_or_else(|e| e.into_inner())
}
fn task_of(task: &Arc<Mutex<Task>>) -> MutexGuard<'_, Task> {
    task.lock().unwrap_or_else(|e| e.into_inner())
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
pub fn task_id(mod_id: u64, file_id: u64) -> String {
    format!("nexus-{mod_id}-{file_id}")
}
fn views(inner: &Inner) -> Vec<TaskView> {
    inner
        .tasks
        .iter()
        .map(|t| task_of(t).view.clone())
        .collect()
}
fn emit(app: &AppHandle, tasks: Vec<TaskView>) {
    let _ = app.emit("download-queue", tasks);
}
/// On-disk representation. Contains no credentials and no download link.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SavedTask {
    id: String,
    mod_id: u64,
    file_id: u64,
    name: String,
    state: TaskState,
    received: u64,
    total: u64,
    attempts: u32,
    error: String,
    created_at: u64,
}
impl DownloadQueue {
    fn set_state_path(&self, path: PathBuf) {
        if let Ok(mut guard) = self.state_path.lock() {
            *guard = Some(path);
        }
    }
    /// Restore the persisted queue once per process. Uses the launcher data
    /// directory; failing to read it only loses history, never correctness.
    fn ensure_restored(&self, app: &AppHandle) -> bool {
        // A second IPC must not persist an empty queue while the first is loading
        // the previous session's file. Hold only this initialization gate, not inner.
        let _restore = self.restoration.lock().unwrap_or_else(|e| e.into_inner());
        if self.restored.load(Ordering::SeqCst) {
            return false;
        }
        if let Ok(root) = crate::service::app_root(app) {
            let path = root.join(STATE_FILE);
            self.set_state_path(path.clone());
            self.restore_from(&path);
            self.restored.store(true, Ordering::SeqCst);
            return true;
        }
        false
    }
    fn restore_from(&self, path: &Path) -> usize {
        let Ok(meta) = fs::metadata(path) else {
            return 0;
        };
        if !meta.is_file() || meta.len() > MAX_STATE_BYTES {
            return 0;
        }
        let Ok(bytes) = fs::read(path) else {
            return 0;
        };
        let Ok(saved) = serde_json::from_slice::<Vec<SavedTask>>(&bytes) else {
            return 0;
        };
        let downloads = path.parent();
        let start = saved.len().saturating_sub(MAX_RECORDS);
        let mut restored = 0;
        let mut inner = lock(&self.inner);
        for item in saved.into_iter().skip(start) {
            if online::parse_id(&item.id).is_err() || item.id != task_id(item.mod_id, item.file_id) {
                continue;
            }
            let package_present = downloads.is_some_and(|dir| {
                crate::online::parse_id(&item.id).is_ok()
                    && dir.join(format!("{}.package", item.id)).is_file()
            });
            if inner.tasks.iter().any(|task| task_of(task).view.id == item.id) { continue; }
            let (state, error) = match item.state {
                TaskState::Done if package_present => (TaskState::Done, String::new()),
                TaskState::Done => (
                    TaskState::Failed,
                    "缓存文件已丢失，需要重新下载".to_string(),
                ),
                TaskState::Running | TaskState::Queued => (
                    TaskState::Failed,
                    "上次退出时任务中断；重试会从已保留的部分继续或重新下载".to_string(),
                ),
                other => (other, item.error),
            };
            let view = TaskView {
                id: item.id,
                mod_id: item.mod_id,
                file_id: item.file_id,
                name: item.name.chars().filter(|c| !c.is_control()).take(256).collect(),
                state,
                received: item.received.min(64 * 1024 * 1024),
                total: item.total.min(64 * 1024 * 1024),
                attempts: item.attempts,
                error: error.chars().filter(|c| !c.is_control()).take(1024).collect(),
                created_at: item.created_at,
            };
            restored += 1;
            inner.tasks.push(Arc::new(Mutex::new(Task {
                view,
                key: String::new(),
                nxm: None,
                cancel: Arc::new(online::Cancel::new()),
            })));
        }
        restored
    }
    fn persist(&self) {
        // Two download completions/cancel actions must never write the same pending
        // file concurrently or publish an older snapshot after a newer terminal one.
        let _writer = self.persistence.lock().unwrap_or_else(|e| e.into_inner());
        let path = match self.state_path.lock() {
            Ok(guard) => guard.clone(),
            Err(_) => None,
        };
        let Some(path) = path else {
            return;
        };
        let saved: Vec<SavedTask> = lock(&self.inner)
            .tasks
            .iter()
            .map(|task| {
                let view = task_of(task).view.clone();
                SavedTask {
                    id: view.id,
                    mod_id: view.mod_id,
                    file_id: view.file_id,
                    name: view.name,
                    state: view.state,
                    received: view.received,
                    total: view.total,
                    attempts: view.attempts,
                    error: view.error,
                    created_at: view.created_at,
                }
            })
            .collect();
        let Some(parent) = path.parent() else {
            return;
        };
        if fs::create_dir_all(parent).is_err() {
            return;
        }
        let Ok(bytes) = serde_json::to_vec_pretty(&saved) else {
            return;
        };
        let temporary = path.with_extension("pending");
        use std::io::Write;
        let Ok(mut file) = fs::File::create(&temporary) else { return; };
        if file.write_all(&bytes).and_then(|_| file.sync_all()).is_err() { return; }
        drop(file);
        let _ = fs::rename(&temporary, path);
    }
    pub fn list(&self) -> Vec<TaskView> {
        views(&lock(&self.inner))
    }
    /// Insert a queued task. Returns the new view and whether a dispatcher must
    /// be started. Reuses any non-terminal task with the same identity.
    fn add(
        &self,
        key: String,
        mod_id: u64,
        file_id: u64,
        name: String,
        nxm: Option<String>,
    ) -> Result<(TaskView, bool)> {
        if mod_id == 0 || file_id == 0 {
            return Err("文件编号无效".into());
        }
        let id = task_id(mod_id, file_id);
        let mut start = false;
        let mut inner = lock(&self.inner);
        if let Some(existing) = inner.tasks.iter().find(|t| {
            let task = task_of(t);
            task.view.id == id && !task.view.state.terminal()
        }) {
            return Ok((task_of(existing).view.clone(), false));
        }
        inner.tasks.retain(|t| task_of(t).view.id != id);
        let pending = inner
            .tasks
            .iter()
            .filter(|t| !task_of(t).view.state.terminal())
            .count();
        if pending >= MAX_PENDING {
            return Err("下载队列已满，请等待现有任务完成".into());
        }
        if inner.tasks.len() >= MAX_RECORDS {
            let mut excess = inner.tasks.len() + 1 - MAX_RECORDS;
            inner.tasks.retain(|t| {
                if excess > 0 && task_of(t).view.state.terminal() {
                    excess -= 1;
                    false
                } else {
                    true
                }
            });
            if inner.tasks.len() >= MAX_RECORDS {
                return Err("下载记录已满，请先清理已结束的任务".into());
            }
        }
        let view = TaskView {
            id,
            mod_id,
            file_id,
            name,
            state: TaskState::Queued,
            received: 0,
            total: 0,
            attempts: 0,
            error: String::new(),
            created_at: now(),
        };
        inner.tasks.push(Arc::new(Mutex::new(Task {
            view: view.clone(),
            key,
            nxm,
            cancel: Arc::new(online::Cancel::new()),
        })));
        if !inner.dispatching {
            inner.dispatching = true;
            start = true;
        }
        Ok((view, start))
    }
    /// Queue a download. A non-terminal task with the same identity is reused.
    pub fn enqueue(
        self: &Arc<Self>,
        app: &AppHandle,
        key: String,
        mod_id: u64,
        file_id: u64,
        name: String,
        nxm: Option<String>,
    ) -> Result<TaskView> {
        let restored = self.ensure_restored(app);
        let (view, start) = self.add(key, mod_id, file_id, name, nxm)?;
        self.persist();
        if restored || start {
            emit(app, self.list());
        }
        if start {
            let queue = self.clone();
            let app = app.clone();
            tauri::async_runtime::spawn(async move { queue.dispatch(app).await });
        }
        Ok(view)
    }
    async fn dispatch(self: Arc<Self>, app: AppHandle) {
        loop {
            let permit = match self.slots.clone().acquire_owned().await {
                Ok(permit) => permit,
                Err(_) => break,
            };
            // Claim the next queued task while holding the queue lock so two
            // dispatchers can never start the same file.
            let next = {
                let inner = lock(&self.inner);
                inner
                    .tasks
                    .iter()
                    .find(|t| task_of(t).view.state == TaskState::Queued)
                    .cloned()
            };
            let Some(task) = next else {
                drop(permit);
                let mut inner = lock(&self.inner);
                // A task can be queued between the scan and clearing the flag;
                // re-check under the same lock before stopping this dispatcher.
                if inner
                    .tasks
                    .iter()
                    .any(|t| task_of(t).view.state == TaskState::Queued)
                {
                    continue;
                }
                inner.dispatching = false;
                return;
            };
            {
                let mut guard = task_of(&task);
                guard.view.state = TaskState::Running;
                guard.view.attempts += 1;
                guard.view.error.clear();
                guard.cancel = Arc::new(online::Cancel::new());
            }
            self.persist();
            emit(&app, self.list());
            let queue = self.clone();
            let app_child = app.clone();
            tauri::async_runtime::spawn(async move {
                queue.execute(app_child, task).await;
                drop(permit);
            });
        }
        lock(&self.inner).dispatching = false;
    }
    async fn execute(self: Arc<Self>, app: AppHandle, task: Arc<Mutex<Task>>) {
        let (mod_id, file_id, key, nxm, cancel, name) = {
            let guard = task_of(&task);
            (
                guard.view.mod_id,
                guard.view.file_id,
                guard.key.clone(),
                guard.nxm.clone(),
                guard.cancel.clone(),
                guard.view.name.clone(),
            )
        };
        let progress_task = task.clone();
        let progress_app = app.clone();
        let download = online::download_task(
            app.clone(),
            key,
            mod_id,
            file_id,
            nxm,
            cancel.clone(),
            &name,
            move |received, total| {
                {
                    let mut guard = task_of(&progress_task);
                    guard.view.received = received;
                    guard.view.total = total;
                }
                let guard = task_of(&progress_task);
                let _ = progress_app.emit(
                    "download-progress",
                    serde_json::json!({
                        "fileId": guard.view.file_id,
                        "received": guard.view.received,
                        "total": guard.view.total,
                    }),
                );
            },
        );
        // Race the download against cancellation so "cancel" ends the task and
        // releases its queue slot immediately instead of after the request
        // timeout. Dropping the download future closes the socket.
        let result = tokio::select! {
            biased;
            _ = cancel.wait() => Err("已取消".to_string()),
            result = download => result,
        };
        let cancelled = cancel.is_cancelled();
        {
            let mut guard = task_of(&task);
            match result {
                Ok(record) => {
                    guard.view.state = TaskState::Done;
                    guard.view.name = record.name;
                    guard.view.received = record.size;
                    guard.view.total = record.size;
                    guard.view.error.clear();
                }
                Err(error) if cancelled || error == "已取消" => {
                    guard.view.state = TaskState::Cancelled;
                    guard.view.error = "已取消".into();
                }
                Err(error) => {
                    guard.view.state = TaskState::Failed;
                    guard.view.error = error;
                }
            }
            guard.nxm = None;
        }
        if cancelled {
            // Dropping the download future releases the socket but not the
            // partial file; remove it so a cancelled task leaves no residue.
            online::discard_partial(&app, mod_id, file_id);
        }
        self.persist();
        emit(&app, self.list());
    }
    fn cancel_task(&self, id: &str) -> Result<(TaskView, bool)> {
        let inner = lock(&self.inner);
        let task = inner
            .tasks
            .iter()
            .find(|t| task_of(t).view.id == id)
            .cloned()
            .ok_or("下载任务不存在")?;
        let mut guard = task_of(&task);
        match guard.view.state {
            TaskState::Queued => {
                guard.view.state = TaskState::Cancelled;
                guard.view.error = "已取消".into();
                Ok((guard.view.clone(), false))
            }
            TaskState::Running => {
                if guard.cancel.is_cancelled() {
                    return Err("任务正在取消，请稍候".into());
                }
                guard.cancel.cancel();
                guard.view.error = "正在取消".into();
                Ok((guard.view.clone(), true))
            }
            _ => Err("任务已经结束".into()),
        }
    }
    pub fn cancel(self: &Arc<Self>, app: &AppHandle, id: &str) -> Result<TaskView> {
        let (view, running) = self.cancel_task(id)?;
        self.persist();
        emit(app, self.list());
        if running {
            // The dispatcher marks the terminal state when the download future
            // observes cancellation; the returned view already says so.
            return Ok(view);
        }
        Ok(view)
    }
    fn retry_task(&self, id: &str, fresh_key: Option<String>) -> Result<(TaskView, bool)> {
        let task = {
            let inner = lock(&self.inner);
            inner
                .tasks
                .iter()
                .find(|t| task_of(t).view.id == id)
                .cloned()
                .ok_or("下载任务不存在")?
        };
        let mut guard = task_of(&task);
        if !guard.view.state.terminal() {
            return Err("任务尚在进行中".into());
        }
        if let Some(key) = fresh_key {
            guard.key = key;
        }
        if guard.key.is_empty() {
            return Err("请先连接 Nexus 账号，再重试该下载".into());
        }
        guard.view.state = TaskState::Queued;
        guard.view.received = 0;
        guard.view.total = 0;
        guard.view.error.clear();
        guard.cancel = Arc::new(online::Cancel::new());
        drop(guard);
        let mut inner = lock(&self.inner);
        let start = if inner.dispatching {
            false
        } else {
            inner.dispatching = true;
            true
        };
        let view = task_of(&task).view.clone();
        Ok((view, start))
    }
    /// Re-queue a failed or cancelled task under the same identity.
    pub fn retry(
        self: &Arc<Self>,
        app: &AppHandle,
        id: &str,
        fresh_key: Option<String>,
    ) -> Result<TaskView> {
        self.ensure_restored(app);
        let (view, start) = self.retry_task(id, fresh_key)?;
        self.persist();
        emit(app, self.list());
        if start {
            let queue = self.clone();
            let app = app.clone();
            tauri::async_runtime::spawn(async move { queue.dispatch(app).await });
        }
        Ok(view)
    }
    pub fn clear_finished(&self) {
        lock(&self.inner)
            .tasks
            .retain(|t| !task_of(t).view.state.terminal());
        self.persist();
    }
    /// True when the task has no usable key in memory (for example after a
    /// restart, because keys are never persisted).
    pub fn needs_key(&self, id: &str) -> Option<bool> {
        let inner = lock(&self.inner);
        inner
            .tasks
            .iter()
            .find(|t| task_of(t).view.id == id)
            .map(|t| task_of(t).key.is_empty())
    }
    /// Queue state is already in memory; the cache view is separate.
    pub fn counts(&self) -> (usize, usize) {        let inner = lock(&self.inner);
        let running = inner
            .tasks
            .iter()
            .filter(|t| task_of(t).view.state == TaskState::Running)
            .count();
        let pending = inner
            .tasks
            .iter()
            .filter(|t| task_of(t).view.state == TaskState::Queued)
            .count();
        (pending, running)
    }
}

fn nexus_key(state: &crate::online::OnlineState) -> crate::service::Result<String> {
    state.download_key()
}

/// Queue a Nexus file download. Returns the existing task when the same file is
/// already pending, so a double click cannot download twice.
#[tauri::command]
pub async fn queue_download(
    app: AppHandle,
    online: tauri::State<'_, crate::online::OnlineState>,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
    mod_id: u64,
    file_id: u64,
    nxm: Option<String>,
) -> Result<TaskView> {
    let key = nexus_key(&online)?;
    queue.enqueue(&app, key, mod_id, file_id, String::new(), nxm)
}
#[tauri::command]
pub fn download_tasks(
    app: AppHandle,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
) -> Vec<TaskView> {
    // Restores an interrupted previous session once, then returns the live list.
    if queue.ensure_restored(&app) {
        emit(&app, queue.list());
    }
    queue.list()
}
/// Explicit recovery entry point for the UI; idempotent.
#[tauri::command]
pub fn recover_download_queue(
    app: AppHandle,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
) -> Vec<TaskView> {
    queue.ensure_restored(&app);
    queue.list()
}
#[tauri::command]
pub fn download_queue_counts(
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
) -> (usize, usize) {
    queue.counts()
}
#[tauri::command]
pub fn cancel_download(
    app: AppHandle,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
    id: String,
) -> Result<TaskView> {
    queue.cancel(&app, &id)
}
#[tauri::command]
pub fn retry_download(
    app: AppHandle,
    online: tauri::State<'_, crate::online::OnlineState>,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
    id: String,
) -> Result<TaskView> {
    // A restored task has no key in memory (it is never persisted). Refill it
    // from the connected account so retry works after a restart.
    let needs_key = queue.needs_key(&id).unwrap_or(true);
    let key = if needs_key {
        Some(nexus_key(&online)?)
    } else {
        None
    };
    queue.retry(&app, &id, key)
}
#[tauri::command]
pub fn clear_downloads(
    app: AppHandle,
    queue: tauri::State<'_, std::sync::Arc<DownloadQueue>>,
) {
    queue.ensure_restored(&app);
    queue.clear_finished();
    emit(&app, queue.list());
}

#[cfg(test)]
mod tests {
    use super::*;
    fn sandbox() -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "flotsam-queue-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&path).unwrap();
        path
    }
    fn queue_with_state(dir: &Path) -> DownloadQueue {
        let queue = DownloadQueue::default();
        queue.set_state_path(dir.join("downloads/queue.json"));
        queue
    }
    #[test]
    fn task_identity_is_stable_and_unique_per_file() {
        assert_eq!(task_id(8, 123), "nexus-8-123");
        assert_ne!(task_id(8, 123), task_id(8, 124));
    }
    #[test]
    fn only_finished_states_are_terminal() {
        assert!(TaskState::Done.terminal());
        assert!(TaskState::Failed.terminal());
        assert!(TaskState::Cancelled.terminal());
        assert!(!TaskState::Queued.terminal());
        assert!(!TaskState::Running.terminal());
    }
    #[test]
    fn duplicate_enqueue_reuses_the_live_task() {
        let dir = sandbox();
        let queue = queue_with_state(&dir);
        let (first, start) = queue
            .add("key".into(), 1, 2, String::new(), None)
            .unwrap();
        assert!(start);
        let (second, start_again) = queue
            .add("key".into(), 1, 2, String::new(), None)
            .unwrap();
        assert!(!start_again);
        assert_eq!(first.id, second.id);
        assert_eq!(queue.list().len(), 1);
        // The same identity after completion is replaced by a fresh task. A
        // drained dispatcher is simulated by clearing the flag first.
        {
            let mut inner = lock(&queue.inner);
            task_of(&inner.tasks[0]).view.state = TaskState::Done;
            inner.dispatching = false;
        }
        let (third, start) = queue
            .add("key".into(), 1, 2, String::new(), None)
            .unwrap();
        assert!(start);
        assert_eq!(third.state, TaskState::Queued);
        assert_eq!(queue.list().len(), 1);
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn pending_queue_is_bounded() {
        let dir = sandbox();
        let queue = queue_with_state(&dir);
        for file in 1..=MAX_PENDING as u64 {
            queue
                .add("key".into(), 1, file, String::new(), None)
                .unwrap();
        }
        let error = queue
            .add("key".into(), 1, MAX_PENDING as u64 + 1, String::new(), None)
            .unwrap_err();
        assert!(error.contains("队列已满"), "{error}");
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn cancelling_a_queued_task_is_immediate_and_cancels_a_running_one() {
        let dir = sandbox();
        let queue = queue_with_state(&dir);
        queue.add("key".into(), 1, 1, String::new(), None).unwrap();
        queue.add("key".into(), 1, 2, String::new(), None).unwrap();
        {
            let inner = lock(&queue.inner);
            task_of(&inner.tasks[1]).view.state = TaskState::Running;
        }
        let (cancelled, backend) = queue.cancel_task("nexus-1-1").unwrap();
        assert_eq!(cancelled.state, TaskState::Cancelled);
        assert!(!backend);
        let (pending, backend) = queue.cancel_task("nexus-1-2").unwrap();
        assert_eq!(pending.state, TaskState::Running);
        assert!(backend);
        let token = {
            let inner = lock(&queue.inner);
            let task = inner.tasks[1].clone();
            drop(inner);
            let cancel = {
                let guard = task_of(&task);
                guard.cancel.clone()
            };
            cancel
        };
        assert!(token.is_cancelled());
        assert!(queue.cancel_task("nexus-1-2").is_err());
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn concurrent_persistence_serializes_complete_snapshots() {
        let dir = sandbox();
        let queue = Arc::new(queue_with_state(&dir));
        queue.add("key".into(), 5, 1, "tool.dll".into(), None).unwrap();
        let threads: Vec<_> = (0..8).map(|_| {
            let queue = queue.clone();
            std::thread::spawn(move || { for _ in 0..8 { queue.persist(); } })
        }).collect();
        for thread in threads { thread.join().unwrap(); }
        let saved: Vec<SavedTask> = serde_json::from_slice(&fs::read(dir.join("downloads/queue.json")).unwrap()).unwrap();
        assert_eq!(saved.len(), 1);
        assert_eq!(saved[0].id, "nexus-5-1");
        assert!(!dir.join("downloads/queue.pending").exists());
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn persisted_queue_contains_no_credentials_and_restores_interrupted_tasks() {
        let dir = sandbox();
        let downloads = dir.join("downloads");
        fs::create_dir_all(&downloads).unwrap();
        let queue = queue_with_state(&dir);
        queue
            .add(
                "SECRET-API-KEY".into(),
                3,
                4,
                "Tools.zip".into(),
                Some("nxm://flotsam/mods/3/files/4?key=SECRET&expires=4102444800".into()),
            )
            .unwrap();
        queue
            .add("SECRET-API-KEY".into(), 3, 5, "Done.zip".into(), None)
            .unwrap();
        {
            let inner = lock(&queue.inner);
            task_of(&inner.tasks[0]).view.state = TaskState::Running;
            task_of(&inner.tasks[0]).view.received = 1024;
            task_of(&inner.tasks[0]).view.total = 4096;
            task_of(&inner.tasks[1]).view.state = TaskState::Done;
            task_of(&inner.tasks[1]).view.received = 7;
            task_of(&inner.tasks[1]).view.total = 7;
        }
        fs::write(downloads.join("nexus-3-5.package"), b"cache").unwrap();
        queue.persist();
        let raw = fs::read_to_string(downloads.join("queue.json")).unwrap();
        assert!(!raw.contains("SECRET"), "credentials must never be persisted");
        assert!(!raw.contains("nxm://"));
        let restored = DownloadQueue::default();
        restored.restore_from(&downloads.join("queue.json"));
        let list = restored.list();
        assert_eq!(list.len(), 2);
        // Interrupted work is explained, not silently reported as running.
        assert_eq!(list[0].state, TaskState::Failed);
        assert!(list[0].error.contains("中断"), "{}", list[0].error);
        assert_eq!(list[0].received, 1024);
        // A finished entry stays usable only while its package exists.
        assert_eq!(list[1].state, TaskState::Done);
        fs::remove_file(downloads.join("nexus-3-5.package")).unwrap();
        let lost = DownloadQueue::default();
        lost.restore_from(&downloads.join("queue.json"));
        let list = lost.list();
        assert_eq!(list[1].state, TaskState::Failed);
        assert!(list[1].error.contains("缓存文件已丢失"));
        // Restored tasks have no key, so retry must be told to reconnect first.
        assert!(lost.retry_task("nexus-3-4", None).is_err());
        assert_eq!(
            lost.retry_task("nexus-3-4", Some("fresh".into()))
                .unwrap()
                .0
                .state,
            TaskState::Queued
        );
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn restore_rejects_foreign_or_malformed_records() {
        let dir = sandbox();
        let downloads = dir.join("downloads");
        fs::create_dir_all(&downloads).unwrap();
        let saved = vec![
            SavedTask {
                id: "nexus-1-1".into(),
                mod_id: 9,
                file_id: 1,
                name: String::new(),
                state: TaskState::Queued,
                received: 0,
                total: 0,
                attempts: 0,
                error: String::new(),
                created_at: 0,
            },
            SavedTask {
                id: "../escape".into(),
                mod_id: 1,
                file_id: 1,
                name: String::new(),
                state: TaskState::Queued,
                received: 0,
                total: 0,
                attempts: 0,
                error: String::new(),
                created_at: 0,
            },
        ];
        fs::write(
            downloads.join("queue.json"),
            serde_json::to_vec(&saved).unwrap(),
        )
        .unwrap();
        let queue = DownloadQueue::default();
        queue.restore_from(&downloads.join("queue.json"));
        assert!(queue.list().is_empty());
        fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn clear_finished_removes_only_terminal_records() {
        let dir = sandbox();
        let queue = queue_with_state(&dir);
        queue.add("k".into(), 1, 1, String::new(), None).unwrap();
        queue.add("k".into(), 1, 2, String::new(), None).unwrap();
        {
            let inner = lock(&queue.inner);
            task_of(&inner.tasks[0]).view.state = TaskState::Done;
            task_of(&inner.tasks[1]).view.state = TaskState::Running;
        }
        queue.clear_finished();
        let list = queue.list();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].id, "nexus-1-2");
        fs::remove_dir_all(dir).unwrap();
    }
}
