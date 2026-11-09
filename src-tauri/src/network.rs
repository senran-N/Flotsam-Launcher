//! Launcher room protocol (separate from the in-game plugin protocol).
//!
//! Design constraints:
//! * every frame is length-prefixed and bounded (`MAX_FRAME`), every read has a
//!   timeout, every collection has a ceiling, and the handshake is limited in
//!   frames as well as time, so a hostile peer cannot make the launcher allocate
//!   or wait without bound;
//! * admission is decided by the structured compatibility manifest, so a
//!   rejected join names the blocking difference and the fix (config-only or
//!   version-text differences are warnings and do not block);
//! * members are managed explicitly: the host can kick, every member has its
//!   address and measured latency, and each disconnect carries a reason code.
use crate::service::{err, game_root, load_settings, Result};
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use std::{
    collections::BTreeMap,
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    net::{TcpListener, TcpStream},
    sync::{mpsc, oneshot, watch},
    task::JoinSet,
    time::{timeout, Duration},
};

/// Bumped whenever the launcher wire format changes. A peer with a different
/// value is rejected with an explicit "update the launcher" message instead of
/// failing later in an unclear way.
const PROTOCOL: u32 = 2;
const MAX_FRAME: usize = 32768;
const LIMIT: usize = 8;
/// Nothing useful is sent after this many handshake frames.
const MAX_HANDSHAKE_FRAMES: usize = 32;
const HEARTBEAT: Duration = Duration::from_secs(3);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(8);
const FRAME_TIMEOUT: Duration = Duration::from_secs(12);
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const MAX_MESSAGES: usize = 20;
const MAX_NOTICE: usize = 200;
const MAX_NICK: usize = 24;
const CHAT_RATE: Duration = Duration::from_millis(350);
/// Peers that have not completed the handshake yet are capped separately.
const MAX_PENDING_PEERS: usize = 16;

#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Member {
    pub id: u64,
    pub name: String,
    #[serde(default)]
    pub host: bool,
    /// Peer address as seen by the host; "local" for the host itself.
    #[serde(default)]
    pub address: String,
    /// Last measured round-trip time in milliseconds, 0 when unknown.
    #[serde(default)]
    pub latency: u32,
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Message {
    id: u64,
    name: String,
    text: String,
    time: u64,
}
#[derive(Clone, Default, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct RoomStatus {
    pub connected: bool,
    pub host: bool,
    pub mode: String,
    pub address: String,
    pub invite: String,
    pub members: Vec<Member>,
    pub messages: Vec<Message>,
    /// Human-readable failure text.
    pub error: String,
    /// Stable machine-readable reason code, e.g. `host_closed` or `timeout`.
    #[serde(default)]
    pub reason: String,
    /// Host announcement shown to every member (bounded).
    #[serde(default)]
    pub notice: String,
    /// Result of the last pre-join comparison; blocking entries explain a
    /// rejected join, non-blocking ones are configuration hints.
    #[serde(default)]
    pub differences: Vec<crate::compat::Difference>,
    /// Latency of the last heartbeat round trip in milliseconds, 0 when unknown.
    #[serde(default)]
    pub latency: u32,
    /// Room capacity including the host.
    #[serde(default)]
    pub capacity: usize,
    /// Active room interaction policy for independent multi-ship mode.
    #[serde(default)]
    pub rules: Rules,
}

/// Independent-mode room settings. Ownership and interaction rules are chosen
/// before hosting. `sabotage` is enforced today by the game plugin for actions
/// aimed at another player's ship; the remaining switches are carried to the
/// plugin as the agreed policy for the cross-ship interactions it implements.
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Rules {
    #[serde(default = "default_true")]
    pub stealing: bool,
    #[serde(default = "default_true")]
    pub sabotage: bool,
    #[serde(default = "default_true")]
    pub trading: bool,
    #[serde(default)]
    pub friendly_fire: bool,
}
fn default_true() -> bool {
    true
}
impl Default for Rules {
    fn default() -> Self {
        Self {
            stealing: true,
            sabotage: true,
            trading: true,
            friendly_fire: false,
        }
    }
}
/// Host-only member management commands, routed to the host loop.
enum Admin {
    Kick(u64),
    Notice(String),
}
struct Session {
    nickname: String,
    status: Arc<Mutex<RoomStatus>>,
    /// Independent-mode interaction policy agreed at room creation and carried
    /// to the game plugin through network.json. The host owns the value; a
    /// guest stores what the host sent in its Welcome frame.
    rules: Mutex<Rules>,
    commands: mpsc::Sender<String>,
    admin: mpsc::Sender<Admin>,
    stop: watch::Sender<bool>,
}
/// Structured result of the last rejected join. Kept outside the room session
/// so the UI can list the differences and the reason after the room failed.
#[derive(Default)]
pub struct JoinFailure {
    pub differences: Mutex<Vec<crate::compat::Difference>>,
    pub reason: Mutex<String>,
    pub message: Mutex<String>,
    pub at: Mutex<u64>,
}
/// The report returned by `last_join_failure`.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JoinReport {
    pub reason: String,
    pub message: String,
    pub at: u64,
    pub differences: Vec<crate::compat::Difference>,
}
fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
fn now_secs() -> u64 {
    now_ms() / 1000
}
fn clip(text: &str, limit: usize) -> String {
    text.chars().take(limit).collect()
}
fn summarize(differences: &[crate::compat::Difference]) -> String {
    let mut text = String::from("游戏或模组不一致：");
    for (index, item) in differences.iter().take(4).enumerate() {
        if index > 0 {
            text.push('；');
        }
        text.push_str(&item.detail);
    }
    if differences.len() > 4 {
        text.push_str(&format!("；另有 {} 项", differences.len() - 4));
    }
    text
}
fn blocking_of(differences: &[crate::compat::Difference]) -> Vec<crate::compat::Difference> {
    differences
        .iter()
        .filter(|item| item.blocks())
        .cloned()
        .collect()
}
fn join_differences(differences: &[crate::compat::Difference]) -> Option<String> {
    let blocking = blocking_of(differences);
    (!blocking.is_empty()).then(|| summarize(&blocking))
}
fn summarize_message(differences: &[crate::compat::Difference], fallback: String) -> String {
    if differences.is_empty() {
        fallback
    } else {
        summarize(differences)
    }
}
#[derive(Default, Clone)]
pub struct NetworkState {
    session: Arc<Mutex<Option<Session>>>,
    failure: Arc<JoinFailure>,
}
impl NetworkState {
    fn record_failure(
        &self,
        reason: &str,
        message: &str,
        differences: &[crate::compat::Difference],
    ) {
        if let Ok(mut values) = self.failure.differences.lock() {
            *values = differences.to_vec();
        }
        if let Ok(mut value) = self.failure.reason.lock() {
            *value = reason.to_string();
        }
        if let Ok(mut value) = self.failure.message.lock() {
            *value = message.to_string();
        }
        if let Ok(mut value) = self.failure.at.lock() {
            *value = now_secs();
        }
    }
}
pub(crate) struct GameConnection {
    pub nickname: String,
    pub host: bool,
    pub address: std::net::SocketAddr,
    pub secret: String,
    pub mode: String,
    pub rules: Rules,
}
pub(crate) fn game_connection(state: &NetworkState) -> Result<GameConnection> {
    let sessions = state.session.lock().map_err(err)?;
    let session = sessions.as_ref().ok_or("请先创建或加入房间")?;
    let status = session.status.lock().map_err(err)?;
    if !status.connected {
        return Err("房间已断开，请重新连接".into());
    }
    if status.mode != "shared" && status.mode != "independent" {
        return Err("房间模式无效".into());
    }
    let (mut address, secret) = parse_invite(&status.invite)?;
    address.set_port(
        address
            .port()
            .checked_add(2)
            .ok_or("联机游戏端口超限，请使用 65533 以下的房间端口")?,
    );
    let rules = session.rules.lock().map_err(err)?.clone();
    Ok(GameConnection {
        nickname: session.nickname.clone(),
        host: status.host,
        address,
        secret,
        mode: status.mode.clone(),
        rules,
    })
}
pub(crate) fn require_disconnected(state: &NetworkState) -> Result<()> {
    let sessions = state.session.lock().map_err(err)?;
    let connected = match sessions.as_ref() {
        None => false,
        Some(session) => session.status.lock().map_err(err)?.connected,
    };
    if connected {
        Err("请先离开房间，再修改游戏目录或模组".into())
    } else {
        Ok(())
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        let _ = self.stop.send(true);
    }
}
#[derive(Serialize, Deserialize, Debug)]
#[serde(tag = "type", rename_all = "camelCase")]
enum Wire {
    Challenge {
        nonce: String,
    },
    Hello {
        protocol: u32,
        name: String,
        fingerprint: String,
        proof: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        manifest: Option<crate::compat::Manifest>,
    },
    Welcome {
        #[serde(default)]
        manifest: Option<crate::compat::Manifest>,
        #[serde(default)]
        rules: Option<Rules>,
        #[serde(default)]
        notice: String,
    },
    State {
        mode: String,
        members: Vec<Member>,
        messages: Vec<Message>,
        #[serde(default)]
        notice: String,
    },
    Chat {
        text: String,
    },
    Ping {
        at: u64,
    },
    Pong {
        at: u64,
    },
    /// The host removed this member.
    Kicked {
        reason: String,
    },
    Error {
        message: String,
    },
    ErrorDetail {
        message: String,
        #[serde(default)]
        differences: Vec<crate::compat::Difference>,
        #[serde(default)]
        reason: String,
    },
}
fn random_hex() -> Result<String> {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).map_err(err)?;
    Ok(hex(&bytes))
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn valid_name(name: &str) -> Result<String> {
    let name = name.trim();
    if name.is_empty() || name.chars().count() > MAX_NICK || name.chars().any(char::is_control) {
        return Err("昵称需为 1–24 个字符".into());
    }
    Ok(name.into())
}
fn valid_chat(text: &str) -> Result<String> {
    let text = text.trim();
    if text.is_empty() || text.len() > 500 || text.chars().any(char::is_control) {
        return Err("消息需为 1–500 字节，且不能包含控制字符".into());
    }
    Ok(text.into())
}
fn valid_notice(text: &str) -> Result<String> {
    let text = text.trim();
    if text.chars().count() > MAX_NOTICE || text.chars().any(|c| c.is_control()) {
        return Err(format!("房间公告最多 {MAX_NOTICE} 个字符，且不能包含换行"));
    }
    Ok(text.into())
}
fn proof(secret: &str, nonce: &str, name: &str, fingerprint: &str) -> Vec<u8> {
    let mut mac = Hmac::<Sha256>::new_from_slice(secret.as_bytes()).unwrap();
    mac.update(&serde_json::to_vec(&(PROTOCOL, nonce, name, fingerprint)).unwrap());
    mac.finalize().into_bytes().to_vec()
}
fn verify(secret: &str, nonce: &str, name: &str, fingerprint: &str, received: &str) -> bool {
    if received.len() != 64 || !received.bytes().all(|b| b.is_ascii_hexdigit()) {
        return false;
    }
    let bytes = (0..64)
        .step_by(2)
        .map(|i| u8::from_str_radix(&received[i..i + 2], 16))
        .collect::<std::result::Result<Vec<_>, _>>();
    let Ok(bytes) = bytes else { return false };
    let mut mac = Hmac::<Sha256>::new_from_slice(secret.as_bytes()).unwrap();
    mac.update(&serde_json::to_vec(&(PROTOCOL, nonce, name, fingerprint)).unwrap());
    mac.verify_slice(&bytes).is_ok()
}
async fn write_wire(writer: &mut (impl AsyncWrite + Unpin), value: &Wire) -> Result<()> {
    let bytes = serde_json::to_vec(value).map_err(err)?;
    if bytes.len() > MAX_FRAME {
        return Err("网络消息过长".into());
    }
    writer.write_u32(bytes.len() as u32).await.map_err(err)?;
    writer.write_all(&bytes).await.map_err(err)
}
async fn read_wire(reader: &mut (impl AsyncRead + Unpin)) -> Result<Wire> {
    timeout(FRAME_TIMEOUT, async {
        let size = reader.read_u32().await.map_err(err)? as usize;
        if size == 0 || size > MAX_FRAME {
            return Err("网络消息格式无效".into());
        }
        let mut bytes = vec![0; size];
        reader.read_exact(&mut bytes).await.map_err(err)?;
        serde_json::from_slice(&bytes).map_err(err)
    })
    .await
    .map_err(|_| "连接超时".to_string())?
}
/// Legacy digest retained for the tests that pin its semantics (package folder
/// names must not affect compatibility). Production admission uses
/// [`crate::compat::Manifest::fingerprint`], which also covers patchers,
/// disabled components and loader/doorstop state.
#[cfg(test)]
fn fingerprint(root: &std::path::Path) -> Result<String> {
    use crate::service::walk_files;
    use sha2::Digest;
    let mut files = Vec::new();
    walk_files(&root.join("BepInEx/plugins"), &mut files)?;
    walk_files(&root.join("BepInEx/patchers"), &mut files)?;
    walk_files(&root.join("BepInEx/core"), &mut files)?;
    files.retain(|p| p.extension().is_some_and(|s| s.eq_ignore_ascii_case("dll")));
    files.push(root.join("Flotsam_Data/Managed/Assembly-CSharp.dll"));
    // Local import folder names differ; compare DLL names and bytes across machines.
    let mut entries = Vec::new();
    for path in files {
        let name = path
            .file_name()
            .ok_or("文件名无效")?
            .to_string_lossy()
            .to_lowercase();
        let mut file = std::fs::File::open(path).map_err(err)?;
        let mut hash = Sha256::new();
        std::io::copy(&mut file, &mut hash).map_err(err)?;
        entries.push((name, hash.finalize().to_vec()));
    }
    entries.sort();
    let mut hasher = Sha256::new();
    for (name, hash) in entries {
        hasher.update((name.len() as u64).to_be_bytes());
        hasher.update(name.as_bytes());
        hasher.update(hash);
    }

    Ok(hex(&hasher.finalize()))
}
fn make_invite(address: &str, secret: &str) -> String {
    format!("flotsam://{address}/{secret}")
}
fn parse_invite(invite: &str) -> Result<(std::net::SocketAddr, String)> {
    let raw = invite
        .trim()
        .strip_prefix("flotsam://")
        .ok_or("请粘贴完整的 flotsam:// 邀请码")?;
    let (address, secret) = raw.rsplit_once('/').ok_or("邀请码格式无效")?;
    let address: std::net::SocketAddr = address.parse().map_err(|_| "邀请码中的 IP 或端口无效")?;
    if address.port() == 0
        || address.ip().is_unspecified()
        || address.ip().is_multicast()
        || secret.len() != 64
        || !secret.bytes().all(|b| b.is_ascii_hexdigit())
    {
        return Err("邀请码格式无效".into());
    }
    Ok((address, secret.into()))
}
fn local_ip() -> String {
    std::net::UdpSocket::bind("0.0.0.0:0")
        .and_then(|s| {
            s.connect("192.0.2.1:80")?;
            s.local_addr()
        })
        .map(|a| a.ip().to_string())
        .unwrap_or_else(|_| "127.0.0.1".into())
}
fn snapshot_wire(status: &RoomStatus) -> Wire {
    Wire::State {
        mode: status.mode.clone(),
        members: status.members.clone(),
        messages: status.messages.clone(),
        notice: status.notice.clone(),
    }
}
enum Event {
    Join(u64, Member, mpsc::Sender<Wire>, oneshot::Sender<bool>),
    Leave(u64),
    Chat(u64, String),
    Latency(u64, u32),
    Kick(u64),
}
fn add_message(status: &mut RoomStatus, id: u64, text: String) {
    let Some(member) = status.members.iter().find(|m| m.id == id) else {
        return;
    };
    let name = member.name.clone();
    status.messages.push(Message {
        id,
        name,
        text,
        time: now_secs(),
    });
    if status.messages.len() > MAX_MESSAGES {
        let excess = status.messages.len() - MAX_MESSAGES;
        status.messages.drain(..excess);
    }
}
async fn serve_peer(
    mut socket: TcpStream,
    id: u64,
    secret: String,
    manifest: crate::compat::Manifest,
    rules: Rules,
    notice: String,
    events: mpsc::Sender<Event>,
) -> Result<()> {
    let peer = socket
        .peer_addr()
        .map(|address| address.ip().to_string())
        .unwrap_or_default();
    let nonce = random_hex()?;
    write_wire(
        &mut socket,
        &Wire::Challenge {
            nonce: nonce.clone(),
        },
    )
    .await?;
    let hello = timeout(HANDSHAKE_TIMEOUT, read_wire(&mut socket))
        .await
        .map_err(|_| "握手超时")??;
    let mut differences = vec![];
    let result: std::result::Result<(String, String), (&'static str, String)> = match hello {
        Wire::Hello {
            protocol,
            name,
            fingerprint,
            proof: received,
            manifest: remote,
        } => {
            if protocol != PROTOCOL {
                Err((
                    "protocol",
                    format!(
                        "启动器网络协议不一致（对方 {protocol}，本机 {PROTOCOL}），请双方更新到同一版本"
                    ),
                ))
            } else if !verify(&secret, &nonce, &name, &fingerprint, &received) {
                Err(("invite", "邀请码已失效或不正确".to_string()))
            } else if let Some(mut remote) = remote {
                if let Err(error) = crate::compat::sanitize(&mut remote) {
                    Err(("manifest", error))
                } else {
                    differences = crate::compat::diff(&manifest, &remote);
                    if differences.iter().any(|item| item.blocks()) {
                        Err((
                            "compatibility",
                            "游戏或模组不一致，请按差异清单补齐".to_string(),
                        ))
                    } else {
                        match valid_name(&name) {
                            Ok(name) => Ok((name, peer)),
                            Err(error) => Err(("nickname", error)),
                        }
                    }
                }
            } else if fingerprint != manifest.fingerprint {
                // Legacy peer without a manifest: compare the digest only.
                Err(("compatibility", "游戏版本或启用的模组不一致".to_string()))
            } else {
                match valid_name(&name) {
                    Ok(name) => Ok((name, peer)),
                    Err(error) => Err(("nickname", error)),
                }
            }
        }
        _ => Err(("handshake", "握手格式无效".to_string())),
    };
    let (name, peer) = match result {
        Ok(value) => value,
        Err((reason, error)) => {
            // The message names the first differences so a client that ignores
            // the structured list still sees the real cause.
            let blocking = blocking_of(&differences);
            let message = clip(&summarize_message(&differences, error), 400);
            let _ = write_wire(
                &mut socket,
                &Wire::ErrorDetail {
                    message: message.clone(),
                    differences: blocking,
                    reason: reason.to_string(),
                },
            )
            .await;
            return Err(message);
        }
    };
    let (tx, mut rx) = mpsc::channel(16);
    let (reply, accepted) = oneshot::channel();
    write_wire(
        &mut socket,
        &Wire::Welcome {
            manifest: Some(manifest.clone()),
            rules: Some(rules.clone()),
            notice: notice.clone(),
        },
    )
    .await?;
    events
        .send(Event::Join(
            id,
            Member {
                id,
                name,
                host: false,
                address: peer,
                latency: 0,
            },
            tx,
            reply,
        ))
        .await
        .map_err(err)?;
    if !accepted.await.map_err(err)? {
        let _ = write_wire(
            &mut socket,
            &Wire::ErrorDetail {
                message: format!("房间已满（上限 {LIMIT} 人）"),
                differences: vec![],
                reason: "full".into(),
            },
        )
        .await;
        return Ok(());
    }
    let (mut reader, mut writer) = socket.into_split();
    let (out, mut out_rx) = mpsc::channel::<Wire>(8);
    let reads = {
        let events = events.clone();
        let out = out.clone();
        async move {
            let mut last = tokio::time::Instant::now() - Duration::from_secs(1);
            let mut frames = 0usize;
            loop {
                frames += 1;
                if frames > 1_000_000 {
                    // Practically unreachable; keeps the connection bounded by
                    // frame count in addition to the per-frame timeout.
                    return Err("连接帧数超限".to_string());
                }
                match read_wire(&mut reader).await? {
                    Wire::Pong { at } => {
                        let now = now_ms();
                        if now >= at && now - at < 60_000 {
                            let _ =
                                events.send(Event::Latency(id, (now - at) as u32)).await;
                        }
                    }
                    Wire::Ping { at } => {
                        let _ = out.send(Wire::Pong { at }).await;
                    }
                    Wire::Chat { text } => {
                        let text = valid_chat(&text)?;
                        if last.elapsed() < CHAT_RATE {
                            continue;
                        }
                        last = tokio::time::Instant::now();
                        events.send(Event::Chat(id, text)).await.map_err(err)?;
                    }
                    _ => return Err("无效的客户端消息".into()),
                }
            }
            #[allow(unreachable_code)]
            Ok::<(), String>(())
        }
    };
    let writes = async {
        let mut heartbeat = tokio::time::interval(HEARTBEAT);
        loop {
            tokio::select! {
                _=heartbeat.tick()=>write_wire(&mut writer,&Wire::Ping{at:now_ms()}).await?,
                Some(message)=out_rx.recv()=>write_wire(&mut writer,&message).await?,
                message=rx.recv()=>{
                    let Some(m)=message else {
                        // Closing the room ends every peer read loop.
                        return Ok::<(),String>(());
                    };
                    write_wire(&mut writer,&m).await?;
                },
            }
        }
    };
    let result = tokio::select! {v=reads=>v,v=writes=>v};
    let _ = events.send(Event::Leave(id)).await;
    result
}
/// Host-side channels grouped into one value: passing four separate receivers
/// through the same call site reads poorly and trips the argument-count lint.
struct HostChannels {
    commands: mpsc::Receiver<String>,
    admin: mpsc::Receiver<Admin>,
    stop: watch::Receiver<bool>,
}
async fn host_loop(
    listener: TcpListener,
    secret: String,
    manifest: crate::compat::Manifest,
    rules: Rules,
    status: Arc<Mutex<RoomStatus>>,
    channels: HostChannels,
) {
    let HostChannels {
        commands,
        admin,
        stop,
    } = channels;
    let (mut commands, mut admin) = (commands, admin);
    let mut stop = stop;
    let (events, mut incoming) = mpsc::channel(64);
    let mut peers: BTreeMap<u64, mpsc::Sender<Wire>> = BTreeMap::new();
    let mut tasks = JoinSet::new();
    let mut next = 1;
    let broadcast = |status: &Arc<Mutex<RoomStatus>>, peers: &BTreeMap<u64, mpsc::Sender<Wire>>| {
        if let Ok(snapshot) = status.lock().map(|s| s.clone()) {
            for tx in peers.values() {
                let _ = tx.try_send(snapshot_wire(&snapshot));
            }
        }
    };
    loop {
        tokio::select! {
            _=stop.changed()=>break,
            Some(_)=tasks.join_next(),if !tasks.is_empty()=>{},
            accepted=listener.accept()=>if let Ok((socket,_))=accepted {
                if tasks.len()<MAX_PENDING_PEERS {
                    let id=next;next+=1;
                    let notice=status.lock().map(|s|s.notice.clone()).unwrap_or_default();
                    tasks.spawn(serve_peer(socket,id,secret.clone(),manifest.clone(),rules.clone(),notice,events.clone()));
                }
            },
            event=incoming.recv()=>{
                let Some(event)=event else{break};
                match event {
                    Event::Join(id,member,tx,reply)=>{
                        let accepted = match status.lock() {
                            Ok(mut s) if s.members.len() < LIMIT => {
                                peers.insert(id,tx);
                                s.members.push(member);
                                reply.send(true).is_ok()
                            }
                            Ok(_) => { let _=reply.send(false); false }
                            Err(_) => false,
                        };
                        if !accepted { continue; }
                    }
                    Event::Leave(id)=>{peers.remove(&id);if let Ok(mut s)=status.lock(){s.members.retain(|m|m.id!=id);}},
                    Event::Chat(id,text)=>{if let Ok(mut s)=status.lock(){add_message(&mut s,id,text);}}
                    Event::Latency(id,ms)=>{if let Ok(mut s)=status.lock(){if let Some(m)=s.members.iter_mut().find(|m|m.id==id){m.latency=ms;}}}
                    Event::Kick(id)=>{
                        if let Some(tx)=peers.remove(&id){
                            let _=tx.try_send(Wire::Kicked{reason:"房主将你移出房间".into()});
                        }
                        if let Ok(mut s)=status.lock(){s.members.retain(|m|m.id!=id);}
                    }
                }
                broadcast(&status,&peers);
            },
            text=commands.recv()=>{let Some(text)=text else{break};if let Ok(mut s)=status.lock(){add_message(&mut s,0,text);}broadcast(&status,&peers);},
            command=admin.recv()=>{
                let Some(command)=command else{break};
                match command {
                    Admin::Kick(id)=>if id!=0&&peers.contains_key(&id){let _=events.send(Event::Kick(id)).await;},
                    Admin::Notice(text)=>if let Ok(mut s)=status.lock(){s.notice=text;} ,
                }
                broadcast(&status,&peers);
            },
        }
    }
    tasks.abort_all();
    if let Ok(mut s) = status.lock() {
        s.connected = false;
        s.members.clear();
        if s.reason.is_empty() {
            s.reason = "closed".into();
        }
    }
}
/// Reason code for a disconnect, from the low-level error text. Kept as a
/// small classifier so the UI can distinguish "host closed the room" from
/// "network timed out" without parsing prose.
fn disconnect_reason(error: &str) -> &'static str {
    if error.contains("协议") || error.contains("无效") || error.contains("格式") {
        "protocol"
    } else if error.contains("超时") {
        "timeout"
    } else if error.contains("房间") || error.contains("房主") {
        "host_closed"
    } else {
        "connection_lost"
    }
}
async fn client_loop(
    socket: TcpStream,
    status: Arc<Mutex<RoomStatus>>,
    mut commands: mpsc::Receiver<String>,
    mut stop: watch::Receiver<bool>,
) {
    let (mut reader, mut writer) = socket.into_split();
    let (pong, mut pongs) = mpsc::channel::<Wire>(8);
    let reads = {
        let status = status.clone();
        async move {
            let mut frames = 0usize;
            loop {
                frames += 1;
                if frames > 1_000_000 {
                    return Err("连接帧数超限".to_string());
                }
                match read_wire(&mut reader).await? {
                    Wire::Ping { at } => {
                        pong.send(Wire::Pong { at }).await.map_err(err)?;
                    }
                    Wire::Pong { at } => {
                        let now = now_ms();
                        if now >= at && now - at < 60_000 {
                            if let Ok(mut s) = status.lock() {
                                s.latency = (now - at) as u32;
                            }
                        }
                    }
                    Wire::State {
                        mode,
                        mut members,
                        messages,
                        notice,
                    } => {
                        if let Ok(mut s) = status.lock() {
                            // The host broadcasts a fresh member list every second, and it has
                            // no per-peer round-trip time of its own. Preserve the latency this
                            // side actually measured so the value does not flicker back to 0.
                            for member in members.iter_mut() {
                                if member.latency == 0 {
                                    if let Some(previous) = s
                                        .members
                                        .iter()
                                        .find(|existing| existing.id == member.id)
                                    {
                                        member.latency = previous.latency;
                                    }
                                }
                            }
                            s.mode = mode;
                            s.members = members;
                            s.messages = messages;
                            s.notice = notice;
                        }
                    }
                    Wire::ErrorDetail {
                        message,
                        differences,
                        reason,
                    } => {
                        if let Ok(mut s) = status.lock() {
                            s.error = clip(&message, 400);
                            s.reason = if reason.is_empty() {
                                "rejected".into()
                            } else {
                                reason
                            };
                            s.differences = differences;
                        }
                        return Err(message);
                    }
                    Wire::Kicked { reason } => {
                        if let Ok(mut s) = status.lock() {
                            s.error = clip(&reason, 200);
                            s.reason = "kicked".into();
                        }
                        return Err(reason);
                    }
                    Wire::Error { message } => return Err(message),
                    _ => return Err("无效的房间消息".into()),
                }
            }
            #[allow(unreachable_code)]
            Ok::<(), String>(())
        }
    };
    let writes = async {
        let mut heartbeat = tokio::time::interval(HEARTBEAT);
        loop {
            tokio::select! {
                _=heartbeat.tick()=>write_wire(&mut writer,&Wire::Ping{at:now_ms()}).await?,
                Some(message)=pongs.recv()=>write_wire(&mut writer,&message).await?,
                text=commands.recv()=>match text{Some(text)=>write_wire(&mut writer,&Wire::Chat{text}).await?,None=>return Ok::<(),String>(())},
            }
        }
    };
    let result = tokio::select! {r=reads=>r,r=writes=>r,_=stop.changed()=>Ok(())};
    if let Ok(mut s) = status.lock() {
        s.connected = false;
        s.members.clear();
        if let Err(error) = &result {
            s.reason = disconnect_reason(error).into();
            if s.error.is_empty() {
                s.error = clip(error, 300);
            }
        } else if s.reason.is_empty() {
            s.reason = "left".into();
        }
    }
}
type SessionChannels = (
    Arc<Mutex<RoomStatus>>,
    mpsc::Receiver<String>,
    mpsc::Receiver<Admin>,
    watch::Receiver<bool>,
);
fn register(
    state: &NetworkState,
    status: RoomStatus,
    nickname: String,
    rules: Rules,
) -> Result<SessionChannels> {
    let mut session = state.session.lock().map_err(err)?;
    let connected = match session.as_ref() {
        None => false,
        Some(existing) => existing.status.lock().map_err(err)?.connected,
    };
    if connected {
        return Err("请先离开当前房间".into());
    }
    let status = Arc::new(Mutex::new(status));
    let (commands, rx) = mpsc::channel(16);
    let (admin_tx, admin_rx) = mpsc::channel(8);
    let (stop, stop_rx) = watch::channel(false);
    *session = Some(Session {
        nickname,
        status: status.clone(),
        rules: Mutex::new(rules),
        commands,
        admin: admin_tx,
        stop,
    });
    Ok((status, rx, admin_rx, stop_rx))
}
#[tauri::command]
pub fn room_status(state: tauri::State<NetworkState>) -> Result<RoomStatus> {
    let sessions = state.session.lock().map_err(err)?;
    match sessions.as_ref() {
        None => Ok(RoomStatus {
            capacity: LIMIT,
            ..Default::default()
        }),
        Some(session) => {
            let mut status = session.status.lock().map_err(err)?.clone();
            status.capacity = LIMIT;
            status.rules = session.rules.lock().map_err(err)?.clone();
            Ok(status)
        }
    }
}
#[tauri::command]
// Tauri command signatures are fixed by the frontend contract (`host_room` is
// called with these eight values); grouping them would change the IPC API.
#[allow(clippy::too_many_arguments)]
pub async fn host_room(
    app: tauri::AppHandle,
    state: tauri::State<'_, NetworkState>,
    launcher: tauri::State<'_, crate::service::LauncherState>,
    name: String,
    mode: String,
    port: u16,
    advertise: String,
    rules: Option<Rules>,
) -> Result<RoomStatus> {
    let name = valid_name(&name)?;
    if mode != "shared" && mode != "independent" {
        return Err("房间模式无效".into());
    }
    if port < 1024 {
        return Err("请选择 1024–65535 之间的端口".into());
    }
    let ip: std::net::IpAddr = if advertise.trim().is_empty() {
        local_ip()
    } else {
        advertise.trim().into()
    }
    .parse()
    .map_err(|_| "请输入本机局域网、虚拟局域网或已转发的公网 IP")?;
    if !ip.is_ipv4() || ip.is_unspecified() || ip.is_multicast() {
        return Err("请输入有效的 IPv4 地址".into());
    }
    let address = format!("{ip}:{port}");
    // Preflight before taking the port: a host with a broken install should be
    // told exactly what to fix instead of learning later that nobody can join.
    // The launcher mutation lock is only held for the synchronous scan; it must
    // not be held across the bind await or this future stops being `Send`.
    let manifest = {
        let _guard = launcher.mutation.lock().map_err(err)?;
        let root = game_root(&load_settings(&app)?)?;
        crate::multiplayer::ensure_installed(&app, &launcher, &root)?;
        let preflight = crate::compat::preflight(&root)?;
        if !preflight.ok {
            let detail = preflight
                .blockers
                .iter()
                .take(3)
                .map(|item| format!("{}（{}）", item.detail, item.fix))
                .collect::<Vec<_>>()
                .join("；");
            return Err(format!("本机还不满足联机条件：{detail}"));
        }
        crate::compat::for_room(&root)?
    };
    let listener = TcpListener::bind(("0.0.0.0", port))
        .await
        .map_err(|_| "无法创建房间，端口可能已被占用")?;
    let secret = random_hex()?;
    let room_rules = rules.unwrap_or_default();
    let result = RoomStatus {
        connected: true,
        host: true,
        mode,
        address: address.clone(),
        invite: make_invite(&address, &secret),
        members: vec![Member {
            id: 0,
            name: name.clone(),
            host: true,
            address: "local".into(),
            latency: 0,
        }],
        capacity: LIMIT,
        rules: room_rules.clone(),
        ..Default::default()
    };
    // Re-take the mutation lock after the bind await and compare the manifest
    // with the snapshot: another launcher action could have changed mods while
    // the socket was being created. Registration happens under the same guard,
    // so the manifest sent to guests is always the one registered.
    let (status, rx, admin, stop) = {
        let _guard = launcher.mutation.lock().map_err(err)?;
        let settings = load_settings(&app)?;
        let current = crate::compat::for_room(&game_root(&settings)?)?;
        if current != manifest {
            let message = "创建房间期间游戏或模组发生变化，请重试".to_string();
            state.record_failure("changed", &message, &[]);
            return Err(message);
        }
        register(
            &state,
            result.clone(),
            result.members[0].name.clone(),
            room_rules.clone(),
        )?
    };
    tauri::async_runtime::spawn(host_loop(
        listener,
        secret,
        manifest,
        room_rules,
        status,
        HostChannels {
            commands: rx,
            admin,
            stop,
        },
    ));
    Ok(result)
}
#[tauri::command]
pub async fn join_room(
    app: tauri::AppHandle,
    state: tauri::State<'_, NetworkState>,
    launcher: tauri::State<'_, crate::service::LauncherState>,
    name: String,
    invite: String,
) -> Result<RoomStatus> {
    let name = valid_name(&name)?;
    let (address, secret) = parse_invite(&invite)?;
    let manifest = {
        let _guard = launcher.mutation.lock().map_err(err)?;
        let root = game_root(&load_settings(&app)?)?;
        crate::multiplayer::ensure_installed(&app, &launcher, &root)?;
        // Local preflight before touching the network: a missing loader,
        // Doorstop proxy, dependency or broken plugin is reported here.
        let preflight = crate::compat::preflight(&root)?;
        if !preflight.ok {
            let detail = preflight
                .blockers
                .iter()
                .take(3)
                .map(|item| format!("{}（{}）", item.detail, item.fix))
                .collect::<Vec<_>>()
                .join("；");
            let message = format!("本机还不满足联机条件：{detail}");
            state.record_failure("compatibility", &message, &preflight.blockers);
            return Err(message);
        }
        crate::compat::for_room(&root)?
    };
    let hash = manifest.fingerprint.clone();
    let mut socket = match timeout(CONNECT_TIMEOUT, TcpStream::connect(address)).await {
        Ok(Ok(socket)) => socket,
        Ok(Err(_)) => {
            let message = "无法连接房主，请检查 IP、端口和房间是否开启".to_string();
            state.record_failure("unreachable", &message, &[]);
            return Err(message);
        }
        Err(_) => {
            let message =
                "连接超时，请检查双方网络、端口和防火墙（公网需房主自行完成端口转发）".to_string();
            state.record_failure("timeout", &message, &[]);
            return Err(message);
        }
    };
    let Wire::Challenge { nonce } = read_wire(&mut socket).await? else {
        state.record_failure("handshake", "目标不是兼容的浮游房间", &[]);
        return Err("目标不是兼容的浮游房间".into());
    };
    let signed = hex(&proof(&secret, &nonce, &name, &hash));
    write_wire(
        &mut socket,
        &Wire::Hello {
            protocol: PROTOCOL,
            name: name.clone(),
            fingerprint: hash,
            proof: signed,
            manifest: Some(manifest.clone()),
        },
    )
    .await?;
    let mut notice = String::new();
    let mut differences = vec![];
    let mut room_rules = Rules::default();
    let host_mode;
    let mut members = vec![];
    let mut messages = vec![];
    let mut frames = 0usize;
    loop {
        frames += 1;
        if frames > MAX_HANDSHAKE_FRAMES {
            state.record_failure("handshake", "房主返回的握手消息过多", &[]);
            return Err("房主返回的握手消息过多，已断开".into());
        }
        match read_wire(&mut socket).await? {
            Wire::Welcome {
                manifest: remote,
                rules: received_rules,
                notice: remote_notice,
            } => {
                if let Some(received) = received_rules {
                    room_rules = received;
                }
                notice = clip(&remote_notice, MAX_NOTICE);
                if let Some(mut remote) = remote {
                    crate::compat::sanitize(&mut remote)?;
                    differences = crate::compat::diff(&manifest, &remote);
                    if differences.iter().any(|item| item.blocks()) {
                        // A join that would desynchronise the world is refused
                        // before any state is exchanged; the guest UI lists each
                        // blocking item with its fix, warnings stay informational.
                        let message = join_differences(&differences)
                            .unwrap_or_else(|| "差异清单不完整".into());
                        state.record_failure("compatibility", &message, &differences);
                        return Err(message);
                    }
                }
                write_wire(&mut socket, &Wire::Pong { at: now_ms() }).await?;
            }
            Wire::State {
                mode,
                members: remote_members,
                messages: remote_messages,
                notice: remote_notice,
            } => {
                host_mode = mode;
                members = remote_members;
                messages = remote_messages;
                if !remote_notice.is_empty() {
                    notice = clip(&remote_notice, MAX_NOTICE);
                }
                break;
            }
            Wire::Ping { at } => write_wire(&mut socket, &Wire::Pong { at }).await?,
            Wire::ErrorDetail {
                message,
                differences: remote,
                reason,
            } => {
                let message = clip(&message, 400);
                state.record_failure(
                    if reason.is_empty() { "rejected" } else { &reason },
                    &message,
                    &remote,
                );
                return Err(join_differences(&remote).unwrap_or(message));
            }
            Wire::Error { message } => {
                let message = clip(&message, 400);
                state.record_failure("rejected", &message, &[]);
                return Err(message);
            }
            _ => {
                state.record_failure("handshake", "目标返回了无效握手", &[]);
                return Err("目标返回了无效握手".into());
            }
        }
    }
    if members.len() > LIMIT {
        state.record_failure("protocol", "房主发送的成员列表超出上限", &[]);
        return Err("房主发送的成员列表超出上限".into());
    }
    let result = RoomStatus {
        connected: true,
        host: false,
        mode: host_mode,
        address: address.to_string(),
        invite,
        members,
        messages,
        error: String::new(),
        reason: String::new(),
        notice,
        differences: differences.clone(),
        latency: 0,
        capacity: LIMIT,
        rules: room_rules.clone(),
    };
    let (status, rx, admin, stop) = {
        // Re-take the mutation lock after the handshake awaits and require the
        // data sent in `Hello` to still be current. Comparing the whole
        // manifest also covers a changed game directory, which the fingerprint
        // alone would not distinguish.
        let _guard = launcher.mutation.lock().map_err(err)?;
        let current = crate::compat::for_room(&game_root(&load_settings(&app)?)?)?;
        if current != manifest {
            let message = "连接期间游戏或模组发生变化，请重新加入".to_string();
            state.record_failure("changed", &message, &[]);
            return Err(message);
        }
        register(&state, result.clone(), name, room_rules)?
    };
    tauri::async_runtime::spawn(client_loop(socket, status, rx, stop));
    let _ = admin;
    Ok(result)
}
#[tauri::command]
pub fn last_join_differences(state: tauri::State<NetworkState>) -> Vec<crate::compat::Difference> {
    state
        .failure
        .differences
        .lock()
        .map(|d| d.clone())
        .unwrap_or_default()
}
/// Structured reason for the last failed join, so the UI can tell a wrong
/// invite from a timeout, a full room or a compatibility mismatch.
#[tauri::command]
pub fn last_join_failure(state: tauri::State<NetworkState>) -> JoinReport {
    JoinReport {
        reason: state
            .failure
            .reason
            .lock()
            .map(|value| value.clone())
            .unwrap_or_default(),
        message: state
            .failure
            .message
            .lock()
            .map(|value| value.clone())
            .unwrap_or_default(),
        at: state.failure.at.lock().map(|value| *value).unwrap_or(0),
        differences: state
            .failure
            .differences
            .lock()
            .map(|value| value.clone())
            .unwrap_or_default(),
    }
}
#[tauri::command]
pub fn clear_join_differences(state: tauri::State<NetworkState>) {
    if let Ok(mut values) = state.failure.differences.lock() {
        values.clear();
    }
    if let Ok(mut value) = state.failure.reason.lock() {
        value.clear();
    }
    if let Ok(mut value) = state.failure.message.lock() {
        value.clear();
    }
}
/// Host-only member management. `id` is the member id from `room_status`
/// (0 is the host and cannot be kicked).
#[tauri::command]
pub fn room_kick(state: tauri::State<NetworkState>, id: u64) -> Result<()> {
    let sessions = state.session.lock().map_err(err)?;
    let session = sessions.as_ref().ok_or("尚未创建房间")?;
    let status = session.status.lock().map_err(err)?;
    if !status.host {
        return Err("只有房主可以移出成员".into());
    }
    if !status.connected {
        return Err("房间已断开".into());
    }
    if id == 0 || !status.members.iter().any(|m| m.id == id) {
        return Err("成员不存在".into());
    }
    drop(status);
    session
        .admin
        .try_send(Admin::Kick(id))
        .map_err(|_| "操作过快，请稍后再试".to_string())
}
/// Set a short host announcement; an empty string clears it. Bounded and
/// broadcast to every member.
#[tauri::command]
pub fn room_notice(state: tauri::State<NetworkState>, text: String) -> Result<()> {
    let text = valid_notice(&text)?;
    let sessions = state.session.lock().map_err(err)?;
    let session = sessions.as_ref().ok_or("尚未创建房间")?;
    let status = session.status.lock().map_err(err)?;
    if !status.host {
        return Err("只有房主可以设置公告".into());
    }
    if !status.connected {
        return Err("房间已断开".into());
    }
    drop(status);
    session
        .admin
        .try_send(Admin::Notice(text))
        .map_err(|_| "操作过快，请稍后再试".to_string())
}
#[tauri::command]
pub fn leave_room(
    app: tauri::AppHandle,
    state: tauri::State<NetworkState>,
    launcher: tauri::State<crate::service::LauncherState>,
) -> Result<()> {
    let _guard = launcher.mutation.lock().map_err(err)?;
    let stop = crate::service::app_root(&app)?.join("multiplayer/session/stop");
    std::fs::create_dir_all(stop.parent().ok_or("联机会话路径无效")?).map_err(err)?;
    std::fs::write(stop, b"stop").map_err(err)?;
    state.session.lock().map_err(err)?.take();
    Ok(())
}
#[tauri::command]
pub fn room_chat(state: tauri::State<NetworkState>, text: String) -> Result<()> {
    let text = valid_chat(&text)?;
    let sessions = state.session.lock().map_err(err)?;
    let session = sessions.as_ref().ok_or("尚未加入房间")?;
    let connected = session.status.lock().map_err(err)?.connected;
    if !connected {
        return Err("房间已断开".into());
    }
    session
        .commands
        .try_send(text)
        .map_err(|_| "消息发送过快，请稍后再试".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fingerprints_ignore_package_folders_but_detect_changed_dlls() {
        let root = std::env::temp_dir().join(format!(
            "flotsam-fingerprint-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        for (name, package) in [("a", "local-111"), ("b", "local-222")] {
            let game = root.join(name);
            std::fs::create_dir_all(game.join("Flotsam_Data/Managed")).unwrap();
            std::fs::create_dir_all(game.join("BepInEx/plugins").join(package)).unwrap();
            std::fs::write(
                game.join("Flotsam_Data/Managed/Assembly-CSharp.dll"),
                b"same-game",
            )
            .unwrap();
            std::fs::write(
                game.join("BepInEx/plugins").join(package).join("Tools.dll"),
                b"same-mod",
            )
            .unwrap();
        }
        assert_eq!(
            fingerprint(&root.join("a")).unwrap(),
            fingerprint(&root.join("b")).unwrap()
        );
        std::fs::write(
            root.join("b/BepInEx/plugins/ignored.dll.disabled"),
            b"disabled",
        )
        .unwrap();
        assert_eq!(
            fingerprint(&root.join("a")).unwrap(),
            fingerprint(&root.join("b")).unwrap()
        );
        std::fs::write(
            root.join("b/BepInEx/plugins/local-222/Tools.dll"),
            b"changed",
        )
        .unwrap();
        assert_ne!(
            fingerprint(&root.join("a")).unwrap(),
            fingerprint(&root.join("b")).unwrap()
        );
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn invites_and_authentication() {
        let secret = "a".repeat(64);
        let invite = make_invite("127.0.0.1:27025", &secret);
        assert_eq!(parse_invite(&invite).unwrap().1, secret);
        assert!(parse_invite("flotsam://0.0.0.0:0/a").is_err());
        let signed = hex(&proof(&secret, "nonce", "Alice", "hash"));
        assert!(verify(&secret, "nonce", "Alice", "hash", &signed));
        assert!(!verify(&secret, "other", "Alice", "hash", &signed));
    }
    #[tokio::test]
    async fn rejects_oversized_frame() {
        let (mut a, mut b) = tokio::io::duplex(64);
        a.write_u32(40000).await.unwrap();
        assert!(read_wire(&mut b).await.is_err());
    }
    fn empty_manifest(game: &str, loader: &str) -> crate::compat::Manifest {
        crate::compat::Manifest {
            game: game.into(),
            loader: loader.into(),
            ..Default::default()
        }
    }
    async fn connect_test(
        address: std::net::SocketAddr,
        secret: &str,
        hash: &str,
        manifest: Option<crate::compat::Manifest>,
    ) -> Result<TcpStream> {
        let mut socket = TcpStream::connect(address).await.map_err(err)?;
        let Wire::Challenge { nonce } = read_wire(&mut socket).await? else {
            panic!()
        };
        write_wire(
            &mut socket,
            &Wire::Hello {
                protocol: PROTOCOL,
                name: "客人".into(),
                fingerprint: hash.into(),
                proof: hex(&proof(secret, &nonce, "客人", hash)),
                manifest,
            },
        )
        .await?;
        loop {
            match read_wire(&mut socket).await? {
                Wire::Ping { at } => write_wire(&mut socket, &Wire::Pong { at }).await?,
                Wire::Welcome { .. } => {}
                Wire::State { .. } => return Ok(socket),
                Wire::Error { message } => return Err(message),
                Wire::ErrorDetail { message, .. } => return Err(message),
                _ => panic!(),
            }
        }
    }
    #[tokio::test]
    async fn real_tcp_join_chat_mismatch_and_shutdown() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let secret = "b".repeat(64);
        let status = Arc::new(Mutex::new(RoomStatus {
            connected: true,
            host: true,
            mode: "shared".into(),
            members: vec![Member {
                id: 0,
                name: "房主".into(),
                host: true,
                address: "local".into(),
                latency: 0,
            }],
            capacity: LIMIT,
            ..Default::default()
        }));
        let (_tx, rx) = mpsc::channel(16);
        let (_admin_tx, admin_rx) = mpsc::channel(8);
        let (stop, stop_rx) = watch::channel(false);
        // Manifests carry hex digests; peers validate them, so the fixtures are
        // hex too instead of arbitrary words.
        let game = "a".repeat(64);
        let loader = "b".repeat(64);
        let other_game = "c".repeat(64);
        let manifest = empty_manifest(&game, &loader);
        let host = tokio::spawn(host_loop(
            listener,
            secret.clone(),
            manifest.clone(),
            Rules::default(),
            status.clone(),
            HostChannels {
                commands: rx,
                admin: admin_rx,
                stop: stop_rx,
            },
        ));
        // A wrong digest from a legacy peer is still reported.
        assert_eq!(
            connect_test(address, &secret, &"d".repeat(64), None)
                .await
                .unwrap_err(),
            "游戏版本或启用的模组不一致"
        );
        assert_eq!(
            connect_test(address, &"c".repeat(64), &game, None)
                .await
                .unwrap_err(),
            "邀请码已失效或不正确"
        );
        // A manifest that differs from the host is reported per component, not
        // as a single opaque message.
        let different = empty_manifest(&other_game, &loader);
        let error = connect_test(
            address,
            &secret,
            &different.fingerprint.clone(),
            Some(different),
        )
        .await
        .unwrap_err();
        assert!(error.contains("游戏核心程序集不同"), "{error}");
        let mut client = connect_test(address, &secret, &manifest.fingerprint.clone(), Some(manifest))
            .await
            .unwrap();
        assert_eq!(status.lock().unwrap().members.len(), 2);
        write_wire(
            &mut client,
            &Wire::Chat {
                text: "一起出海".into(),
            },
        )
        .await
        .unwrap();
        loop {
            match read_wire(&mut client).await.unwrap() {
                Wire::State { messages, .. } if !messages.is_empty() => {
                    assert_eq!(messages[0].text, "一起出海");
                    break;
                }
                Wire::Ping { at } => {
                    write_wire(&mut client, &Wire::Pong { at }).await.unwrap();
                }
                _ => {}
            }
        }
        // The member list carries the peer address and a measured latency. Loopback round
        // trips can be under one millisecond, so delay the reply deliberately: 0 ms is a
        // legitimate measurement and asserting `> 0` would be flaky rather than meaningful.
        let mut saw_latency = false;
        for _ in 0..24 {
            if let Wire::Ping { at } = read_wire(&mut client).await.unwrap() {
                tokio::time::sleep(Duration::from_millis(5)).await;
                write_wire(&mut client, &Wire::Pong { at }).await.unwrap();
            }
            if status
                .lock()
                .unwrap()
                .members
                .iter()
                .any(|m| m.id != 0 && m.latency >= 5)
            {
                saw_latency = true;
                break;
            }
        }
        assert!(saw_latency, "member latency should be measured from pongs");
        assert!(status
            .lock()
            .unwrap()
            .members
            .iter()
            .any(|m| m.id != 0 && m.address == "127.0.0.1"));
        stop.send(true).unwrap();
        host.await.unwrap();
        assert!(!status.lock().unwrap().connected);
        let rebound = TcpListener::bind(address).await.unwrap();
        drop(rebound);
    }
    #[test]
    fn disconnect_reasons_are_distinguishable() {
        assert_eq!(disconnect_reason("连接超时"), "timeout");
        assert_eq!(disconnect_reason("启动器网络协议不一致"), "protocol");
        assert_eq!(disconnect_reason("无效的房间消息"), "protocol");
        assert_eq!(disconnect_reason("房主关闭了房间"), "host_closed");
        assert_eq!(disconnect_reason("connection reset"), "connection_lost");
    }
}
