use crate::{
    crypto::*,
    groups::*,
    network::*,
    pairing::Session,
    platform,
    transfers::{self, Decision, Job, Offer},
};
use anyhow::{Result, ensure};
use futures::{FutureExt, StreamExt, future::BoxFuture, stream::FuturesUnordered};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::PathBuf,
    sync::Arc,
    time::{Duration, Instant},
};
use tokio::sync::mpsc;
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub paused: bool,
    pub auto_images: bool,
    pub text_interval: u64,
    pub attachment_interval: u64,
    pub receive_directory: PathBuf,
    pub hotkey_modifiers: u32,
    pub hotkey_key: u32,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            paused: false,
            auto_images: true,
            text_interval: 2,
            attachment_interval: 3,
            receive_directory: PathBuf::from(std::env::var("USERPROFILE").unwrap_or_default())
                .join("Downloads/ClipBridge"),
            hotkey_modifiers: 0,
            hotkey_key: 0,
        }
    }
}
#[derive(Clone, Serialize, Deserialize, Default)]
pub struct RouteState {
    pub clock: i64,
    pub received: i64,
    #[serde(default)]
    pub winner: String,
    pub last_sync: i64,
    pub status: String,
    pub last_id: String,
}
#[derive(Serialize, Deserialize)]
pub struct State {
    pub identity: Identity,
    pub groups: Vec<Group>,
    pub settings: Settings,
    pub routes: BTreeMap<String, RouteState>,
    pub seen: BTreeMap<String, i64>,
    pub clock: i64,
    #[serde(default)]
    pub clipboard: Option<Text>,
    pub outgoing: Vec<Job>,
    pub incoming: Vec<Job>,
    #[serde(skip)]
    channels_cache: std::sync::Mutex<(String, Vec<Route>)>,
}
impl State {
    pub fn load(path: &std::path::Path) -> Result<Self> {
        if path.exists() {
            let data = zeroize::Zeroizing::new(platform::protect(&std::fs::read(path)?, false)?);
            let mut s: Self = serde_json::from_slice(&data)?;
            for job in &mut s.incoming {
                if matches!(job.phase.as_str(), "receiving" | "saving") {
                    job.phase = "accepted".into();
                }
            }
            for job in &mut s.outgoing {
                if job.phase == "sending" {
                    job.phase = if job.uploaded { "ready" } else { "waiting" }.into();
                }
            }
            s.identity.member.validate()?;
            ensure!(
                s.identity
                    .sign(b"state-check")
                    .and_then(|sign| s.identity.member.verify(b"state-check", &sign))
                    .is_ok(),
                "本机身份损坏"
            );
            for g in &mut s.groups {
                g.refresh_removed();
                g.validate()?
            }
            Ok(s)
        } else {
            Ok(Self {
                identity: Identity::new(
                    &std::env::var("COMPUTERNAME").unwrap_or("Windows".into()),
                )?,
                groups: vec![],
                settings: Settings::default(),
                routes: BTreeMap::new(),
                seen: BTreeMap::new(),
                clock: 0,
                clipboard: None,
                outgoing: vec![],
                incoming: vec![],
                channels_cache: Default::default(),
            })
        }
    }
    pub fn save(&self, path: &std::path::Path) -> Result<()> {
        let plain = zeroize::Zeroizing::new(serde_json::to_vec(self)?);
        platform::write_private(path, &plain)
    }
    pub fn receive(&mut self, route: &Route, e: &Envelope) -> Result<Option<Text>> {
        let state = self.routes.entry(route.config.pair.clone()).or_default();
        if e.id == state.last_id
            || e.clock < state.clock
            || (e.clock == state.clock && e.from <= state.winner)
        {
            return Ok(None);
        }
        let text: Text = e.open(&route.config)?;
        text.validate()?;
        ensure!(text.origin == route.peer.id, "剪贴项来源不是已配对设备");
        state.received = e.clock;
        state.clock = state.clock.max(e.clock);
        state.winner = e.from.clone();
        state.last_id = e.id.clone();
        let event = format!("{}:{}", text.origin, text.event_id);
        let trusted = hash(&route.peer.signing_key);
        if self.seen.contains_key(&event)
            || self.seen.get(&trusted).copied().unwrap_or(0) >= text.event_clock
        {
            return Ok(None);
        }
        self.seen.insert(event, text.event_clock);
        self.seen.insert(trusted, text.event_clock);
        while self.seen.len() > 1024 {
            let key = self.seen.keys().find(|k| k.contains(':')).cloned();
            if let Some(key) = key {
                self.seen.remove(&key);
            } else {
                break;
            }
        }
        self.clipboard = Some(text.clone());
        Ok(Some(text))
    }
    pub fn reroute_jobs(&mut self, routes: &[Route]) -> bool {
        let mut changed = false;
        for job in &mut self.outgoing {
            if matches!(job.phase.as_str(), "done" | "rejected" | "cancelled") {
                continue;
            }
            if routes.iter().any(|route| route.config.pair == job.pair) {
                continue;
            }
            if let Some(route) = routes
                .iter()
                .find(|route| hash(&route.peer.signing_key) == job.peer)
            {
                job.pair = route.config.pair.clone();
                job.uploaded = false;
                job.offer.status = "offered".into();
                job.phase = "waiting".into();
                changed = true;
            }
        }
        changed
    }
    fn job_views(&self) -> Vec<JobView> {
        let mut jobs: Vec<_> = self
            .incoming
            .iter()
            .map(|job| (job, true))
            .chain(self.outgoing.iter().map(|job| (job, false)))
            .collect();
        jobs.sort_by(|(a, ai), (b, bi)| {
            let ap = *ai && a.phase == "waiting";
            let bp = *bi && b.phase == "waiting";
            bp.cmp(&ap)
                .then_with(|| b.offer.expires.cmp(&a.offer.expires))
        });
        jobs.into_iter()
            .take(50)
            .map(|(job, incoming)| JobView {
                id: job.offer.id.clone(),
                name: job.offer.name.clone(),
                size: job.offer.size,
                kind: job.offer.kind.clone(),
                phase: job.phase.clone(),
                incoming,
                destination: job.destination.to_string_lossy().into_owned(),
            })
            .collect()
    }
    pub fn channels(&self) -> Vec<Route> {
        if self.settings.paused {
            return vec![];
        }
        let generation = hash(
            serde_json::to_vec(&(
                &self.identity.agreement_private,
                self.groups
                    .iter()
                    .map(|g| {
                        (
                            &g.id,
                            g.enabled,
                            g.storage_version,
                            &g.connection,
                            &g.access,
                            &g.members,
                            &g.removed,
                            &g.suppressed_peers,
                        )
                    })
                    .collect::<Vec<_>>(),
            ))
            .unwrap_or_default(),
        );
        let mut cache = self.channels_cache.lock().unwrap();
        if cache.0 == generation {
            return cache.1.clone();
        }
        let mut out = vec![];
        for g in &self.groups {
            if !g.enabled || g.removed.contains(&self.identity.member.id) {
                continue;
            }
            for cert in g.members.values() {
                let peer = &cert.member;
                if peer.id == self.identity.member.id
                    || g.removed.contains(&peer.id)
                    || g.suppressed_peers.contains(&peer.id)
                {
                    continue;
                }
                if let Ok(config) = g.channel(&self.identity, peer) {
                    out.push(Route {
                        config,
                        peer: peer.clone(),
                        group: g.id.clone(),
                    })
                }
            }
        }
        *cache = (generation, out.clone());
        out
    }
}
#[derive(Clone)]
pub struct Route {
    pub config: Config,
    pub peer: Member,
    pub group: String,
}
#[derive(Clone, Serialize, PartialEq)]
pub struct MemberView {
    pub id: String,
    pub name: String,
    pub own: bool,
    pub suppressed: bool,
}
#[derive(Clone, Serialize, PartialEq)]
pub struct GroupView {
    pub id: String,
    pub name: String,
    pub transport: String,
    pub server: String,
    pub enabled: bool,
    pub left: bool,
    pub status: String,
    pub last_sync: i64,
    pub members: Vec<MemberView>,
}
#[derive(Clone, Serialize, PartialEq)]
pub struct Target {
    pub id: String,
    pub name: String,
}
#[derive(Clone, Serialize, PartialEq)]
pub struct JobView {
    pub id: String,
    pub name: String,
    pub size: u64,
    pub kind: String,
    pub phase: String,
    pub incoming: bool,
    pub destination: String,
}
#[derive(Clone, Serialize, Default, PartialEq)]
pub struct PairView {
    pub code: String,
    pub link: String,
    pub safety: String,
    pub peer: String,
    pub status: String,
    pub ready: bool,
    pub approved: bool,
    pub done: bool,
    pub busy: bool,
    pub host: bool,
}
#[derive(Clone, Serialize)]
pub struct Snapshot {
    pub own_name: String,
    pub settings: Settings,
    pub startup: bool,
    pub shell: bool,
    pub groups: Vec<GroupView>,
    pub jobs: Vec<JobView>,
    pub targets: Vec<Target>,
    pub last_sync: i64,
    pub paired: Option<PairView>,
    pub notice: String,
}
pub enum Event {
    Snapshot(Arc<Snapshot>),
    Open,
    SendFiles(Vec<PathBuf>),
    Quit,
    Fatal(String),
}
#[derive(Clone)]
pub enum Command {
    Open,
    Quit,
    TogglePause,
    ChooseFiles,
    Clipboard(bool),
    Notice(String),
    Create {
        name: String,
        connection: Connection,
        setup: String,
    },
    Join {
        connection: Connection,
        code: String,
    },
    Invite(String),
    CancelPairing,
    Approve,
    ToggleGroup(String, bool),
    RemoveMember(String, String),
    DeleteGroup(String),
    Refresh(String),
    Settings(Settings),
    HotkeyApplied(u32, u32, bool),
    Startup(bool),
    Shell(bool),
    ChooseDirectory,
    StageFiles {
        paths: Vec<PathBuf>,
        peer: String,
    },
    Decide(String, bool),
    OpenFile(String, bool),
    Arguments(Vec<String>),
}
enum Work {
    Pair(u64, Result<Session>),
    PairStep(u64, Session, Result<()>),
    Group(String, Result<Group>),
    Text(Route, Result<Option<Envelope>>),
    Sent(Route, String, Result<()>),
    Index(Route, Result<Option<transfers::Index>>),
    Upload(Route, String, Result<()>),
    Download(Route, Offer, Result<PathBuf>),
    Stage(Result<Job>),
    Clean(String, Result<()>),
    Saved(String, Result<PathBuf>),
}
type Works = FuturesUnordered<BoxFuture<'static, Work>>;
pub struct Engine {
    state: State,
    path: PathBuf,
    cache: PathBuf,
    rx: mpsc::UnboundedReceiver<Command>,
    tx: mpsc::UnboundedSender<Command>,
    events: async_channel::Sender<Event>,
    work: Works,
    pair: Option<Session>,
    pair_view: Option<PairView>,
    pair_generation: u64,
    pair_busy: bool,
    pair_halted: bool,
    last_pair: Instant,
    approve_pending: bool,
    groups_busy: BTreeSet<String>,
    route_busy: BTreeSet<String>,
    attachments_busy: BTreeSet<String>,
    transfers_busy: BTreeSet<String>,
    last_poll: BTreeMap<String, Instant>,
    last_attachment: BTreeMap<String, Instant>,
    last_group: BTreeMap<String, Instant>,
    remote: BTreeMap<String, transfers::Index>,
    notice: String,
    last_snapshot: String,
    last_local_hash: String,
    pending: BTreeMap<String, (Text, i64)>,
    index_versions: BTreeMap<String, (String, Instant)>,
    retry_after: BTreeMap<String, Instant>,
    stages: usize,
    startup: bool,
    shell: bool,
    pending_shell_files: Vec<PathBuf>,
    shell_deadline: Option<Instant>,
    quitting: bool,
}
pub fn launch(
    events: async_channel::Sender<Event>,
    data_dir: Option<PathBuf>,
) -> mpsc::UnboundedSender<Command> {
    let (tx, rx) = mpsc::unbounded_channel();
    let commands = tx.clone();
    std::thread::spawn(move || {
        let init: Result<Engine> = (|| {
            let dir = data_dir.map(Ok).unwrap_or_else(platform::directory)?;
            std::fs::create_dir_all(&dir)?;
            let path = dir.join("desktop-rust-v1.bin");
            let cache = dir.join("attachments");
            std::fs::create_dir_all(&cache)?;
            let state = State::load(&path)?;
            state.save(&path)?;
            Ok(Engine {
                state,
                path,
                cache,
                rx,
                tx: commands,
                events: events.clone(),
                work: Works::new(),
                pair: None,
                pair_view: None,
                pair_generation: 0,
                pair_busy: false,
                pair_halted: false,
                last_pair: Instant::now() - Duration::from_secs(2),
                approve_pending: false,
                groups_busy: BTreeSet::new(),
                route_busy: BTreeSet::new(),
                attachments_busy: BTreeSet::new(),
                transfers_busy: BTreeSet::new(),
                last_poll: BTreeMap::new(),
                last_attachment: BTreeMap::new(),
                last_group: BTreeMap::new(),
                remote: BTreeMap::new(),
                notice: String::new(),
                last_snapshot: String::new(),
                last_local_hash: String::new(),
                pending: BTreeMap::new(),
                index_versions: BTreeMap::new(),
                retry_after: BTreeMap::new(),
                stages: 0,
                startup: platform::startup(),
                shell: platform::shell(),
                pending_shell_files: vec![],
                shell_deadline: None,
                quitting: false,
            })
        })();
        match init {
            Ok(mut engine) => {
                let runtime = tokio::runtime::Builder::new_multi_thread()
                    .worker_threads(2)
                    .enable_all()
                    .build()
                    .unwrap();
                runtime.block_on(engine.run());
            }
            Err(e) => {
                let _ = events.try_send(Event::Fatal(format!("本地数据读取失败，已停止同步：{e}")));
            }
        }
    });
    tx
}
impl Engine {
    fn save(&mut self) -> bool {
        if let Err(e) = self.state.save(&self.path) {
            self.state.settings.paused = true;
            self.notice = format!("本地保存失败，已暂停同步：{e}");
            return false;
        }
        true
    }
    fn notice(&mut self, s: impl Into<String>) {
        self.notice = s.into();
    }
    fn snapshot(&mut self) {
        let mut targets = BTreeMap::new();
        let channels = self.state.channels();
        for route in &channels {
            targets
                .entry(hash(&route.peer.signing_key))
                .or_insert(Target {
                    id: hash(&route.peer.signing_key),
                    name: route.peer.name.clone(),
                });
        }
        let own = &self.state.identity.member.id;
        let groups = self
            .state
            .groups
            .iter()
            .map(|g| GroupView {
                id: g.id.clone(),
                name: g.name.clone(),
                transport: g.connection.transport.clone(),
                server: g.connection.server.clone(),
                enabled: g.enabled,
                left: g.removed.contains(own),
                status: if self.state.settings.paused {
                    "已暂停".into()
                } else {
                    g.status.clone()
                },
                last_sync: g.last_sync,
                members: g
                    .members
                    .values()
                    .filter(|c| !g.removed.contains(&c.member.id))
                    .map(|c| MemberView {
                        id: c.member.id.clone(),
                        name: c.member.name.clone(),
                        own: c.member.id == *own,
                        suppressed: g.suppressed_peers.contains(&c.member.id),
                    })
                    .collect(),
            })
            .collect();
        let jobs = self.state.job_views();
        let snapshot = Snapshot {
            own_name: self.state.identity.member.name.clone(),
            settings: self.state.settings.clone(),
            startup: self.startup,
            shell: self.shell,
            groups,
            jobs,
            targets: targets.into_values().collect(),
            last_sync: self
                .state
                .groups
                .iter()
                .map(|g| g.last_sync)
                .max()
                .unwrap_or(0),
            paired: self.pair_view.clone(),
            notice: self.notice.clone(),
        };
        let encoded = serde_json::to_string(&snapshot).unwrap_or_default();
        if encoded != self.last_snapshot {
            self.last_snapshot = encoded;
            let _ = self.events.try_send(Event::Snapshot(Arc::new(snapshot)));
        }
    }
    async fn run(&mut self) {
        let mut tick = tokio::time::interval(Duration::from_millis(250));
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        loop {
            tokio::select! {biased;command=self.rx.recv()=>{if let Some(command)=command{if let Err(e)=self.command(command).await{self.notice(e.to_string());}}else{break}},Some(work)=self.work.next(),if !self.work.is_empty()=>{if let Err(e)=self.complete(work).await{self.notice(e.to_string());}},_=tick.tick()=>{self.schedule();}}
            self.snapshot();
            if self.quitting {
                break;
            }
        }
        self.save();
        let _ = self.events.try_send(Event::Quit);
    }
    fn update_pair(&mut self) {
        if let Some(p) = &self.pair {
            self.pair_view = Some(PairView {
                code: crate::pake::display(&p.code),
                link: p.link.clone(),
                safety: p.sas.clone(),
                peer: p.peer.as_ref().map(|m| m.name.clone()).unwrap_or_default(),
                status: p.status.clone(),
                ready: p.ready() && !self.pair_halted,
                approved: p.approved,
                done: p.done,
                busy: false,
                host: p.host(),
            })
        }
    }
    fn active(&self, route: &Route) -> bool {
        self.state
            .channels()
            .iter()
            .any(|r| r.config.pair == route.config.pair && r.config.key == route.config.key)
    }
    async fn command(&mut self, c: Command) -> Result<()> {
        if !matches!(
            &c,
            Command::Clipboard(_)
                | Command::Notice(_)
                | Command::Arguments(_)
                | Command::HotkeyApplied(_, _, _)
        ) {
            self.notice.clear();
        }
        match c {
            Command::Open => {
                let _ = self.events.try_send(Event::Open);
            }
            Command::Quit => self.quitting = true,
            Command::Notice(s) => self.notice(s),
            Command::TogglePause => {
                self.state.settings.paused = !self.state.settings.paused;
                self.pending.clear();
                self.state.clipboard = None;
                self.state.clipboard = None;
                self.save();
            }
            Command::ChooseFiles => {
                let tx = self.tx.clone();
                std::thread::spawn(move || {
                    if let Some(paths) = rfd::FileDialog::new()
                        .set_title("通过 ClipBridge 发送文件")
                        .pick_files()
                    {
                        let _ = tx.send(Command::Arguments(
                            std::iter::once("--send".into())
                                .chain(paths.iter().map(|p| p.to_string_lossy().into_owned()))
                                .collect(),
                        ));
                    }
                });
            }
            Command::Arguments(args) => {
                // Protocol activation cannot turn trailing URL text into CLI commands.
                if args.first().is_some_and(|a| a == "--notification") {
                    if let Some(uri) = args.get(1) {
                        self.notification_action(uri)?;
                    }
                    return Ok(());
                }

                if args.first().is_some_and(|a| a == "--quit") {
                    self.quitting = true;
                    return Ok(());
                }
                if let Some(index) = args.iter().position(|a| a == "--send") {
                    for path in args[index + 1..].iter().take(20).map(PathBuf::from) {
                        if !self.pending_shell_files.contains(&path)
                            && self.pending_shell_files.len() < 20
                        {
                            self.pending_shell_files.push(path);
                        }
                    }
                    self.shell_deadline = Some(Instant::now() + Duration::from_millis(500));
                } else if let Some(index) = args.iter().position(|a| a == "--notification") {
                    if let Some(uri) = args.get(index + 1) {
                        self.notification_action(uri)?
                    }
                } else if !args.iter().any(|a| a == "--startup") {
                    let _ = self.events.try_send(Event::Open);
                }
            }
            Command::Settings(mut s) => {
                ensure!(
                    (1..=300).contains(&s.text_interval)
                        && (1..=300).contains(&s.attachment_interval)
                        && s.receive_directory.is_absolute(),
                    "间隔应为 1–300 秒，目录需为绝对路径"
                );
                let changed = s.hotkey_key != self.state.settings.hotkey_key
                    || s.hotkey_modifiers != self.state.settings.hotkey_modifiers;
                if changed {
                    platform::set_hotkey(s.hotkey_modifiers, s.hotkey_key)?;
                    s.hotkey_modifiers = self.state.settings.hotkey_modifiers;
                    s.hotkey_key = self.state.settings.hotkey_key;
                }
                self.state.settings = s;
                self.last_poll.clear();
                self.last_attachment.clear();
                self.save();
            }
            Command::HotkeyApplied(modifiers, key, success) => {
                if success {
                    self.state.settings.hotkey_modifiers = modifiers;
                    self.state.settings.hotkey_key = key;
                    self.save();
                } else {
                    self.notice("快捷键已被其他程序占用，请选择其他组合");
                }
            }
            Command::Startup(value) => {
                platform::set_startup(value)?;
                self.startup = value;
            }
            Command::Shell(value) => {
                platform::set_shell(value)?;
                self.shell = value;
            }
            Command::ChooseDirectory => {
                let tx = self.tx.clone();
                let mut settings = self.state.settings.clone();
                std::thread::spawn(move || {
                    if let Some(path) = rfd::FileDialog::new()
                        .set_directory(&settings.receive_directory)
                        .pick_folder()
                    {
                        settings.receive_directory = path;
                        let _ = tx.send(Command::Settings(settings));
                    }
                });
            }
            Command::ToggleGroup(id, value) => {
                let g = self
                    .state
                    .groups
                    .iter_mut()
                    .find(|g| g.id == id)
                    .ok_or_else(|| anyhow::anyhow!("同步组不存在"))?;
                ensure!(
                    !g.removed.contains(&self.state.identity.member.id),
                    "本机已退出"
                );
                g.enabled = value;
                self.pending.clear();
                self.state.clipboard = None;
                self.save();
            }
            Command::RemoveMember(id, member) => {
                let own = self.state.identity.clone();
                let g = self
                    .state
                    .groups
                    .iter_mut()
                    .find(|g| g.id == id)
                    .ok_or_else(|| anyhow::anyhow!("同步组不存在"))?;
                g.remove(&own, &member)?;
                self.last_group.remove(&id);
                self.pending.clear();
                self.state.clipboard = None;
                self.save();
            }
            Command::DeleteGroup(id) => {
                self.state.groups.retain(|g| g.id != id);
                self.pending.clear();
                self.state.clipboard = None;
                self.save();
            }
            Command::Refresh(id) => {
                self.last_group.remove(&id);
            }
            Command::Create {
                name,
                connection,
                setup,
            } => {
                ensure!(self.state.groups.len() < 4, "最多保存 4 个同步组");
                self.cancel_pair();
                self.pair_busy = true;
                self.pair_view = Some(PairView {
                    busy: true,
                    status: "正在创建同步组…".into(),
                    host: true,
                    ..Default::default()
                });
                let generation = self.pair_generation;
                let own = self.state.identity.clone();
                self.work.push(
                    async move {
                        let result = async {
                            let group = Group::new(&own, &name, connection)?;
                            provision(&group, &setup).await?;
                            publish(&group, &own).await?;
                            Session::invite(group, own).await
                        }
                        .await;
                        Work::Pair(generation, result)
                    }
                    .boxed(),
                );
            }
            Command::Invite(id) => {
                let group = self
                    .state
                    .groups
                    .iter()
                    .find(|g| g.id == id)
                    .cloned()
                    .ok_or_else(|| anyhow::anyhow!("同步组不存在"))?;
                self.cancel_pair();
                self.pair_busy = true;
                self.pair_view = Some(PairView {
                    busy: true,
                    status: "正在生成邀请…".into(),
                    host: true,
                    ..Default::default()
                });
                let generation = self.pair_generation;
                let own = self.state.identity.clone();
                self.work.push(
                    async move { Work::Pair(generation, Session::invite(group, own).await) }
                        .boxed(),
                );
            }
            Command::Join {
                mut connection,
                mut code,
            } => {
                ensure!(self.state.groups.len() < 4, "最多保存 4 个同步组");
                if code.starts_with("clipbridge://group#") {
                    let parsed = crate::pairing::parse(&code)?;
                    connection = parsed.connection;
                    code = parsed.code
                }
                self.cancel_pair();
                self.pair_busy = true;
                self.pair_view = Some(PairView {
                    busy: true,
                    status: "正在连接邀请…".into(),
                    ..Default::default()
                });
                let generation = self.pair_generation;
                let own = self.state.identity.clone();
                self.work.push(async move{Work::Pair(generation,Session::join(connection,&code,own).await)}.boxed());
            }
            Command::CancelPairing => self.cancel_pair(),
            Command::Approve => {
                self.approve_pending = true;
                if let Some(pair) = &mut self.pair {
                    pair.approve()?;
                    self.approve_pending = false
                }
                self.update_pair();
            }
            Command::Clipboard(force) => {
                if self.state.settings.paused {
                    if force {
                        self.notice("同步已暂停，请先恢复");
                    }
                    return Ok(());
                }
                if self.state.channels().is_empty() {
                    if force {
                        self.notice("请先开启一个包含接收设备的同步组");
                    }
                    return Ok(());
                }

                if !force
                    && platform::sequence()
                        == platform::CLIP_SEQUENCE.load(std::sync::atomic::Ordering::SeqCst)
                {
                    return Ok(());
                }
                if !self.state.settings.paused {
                    self.state.clipboard = None;
                    let clip = platform::clipboard(force || self.state.settings.auto_images)?;
                    match clip {
                        Some(platform::Clip::Text(text)) => {
                            if !force
                                && self
                                    .pair_view
                                    .as_ref()
                                    .is_some_and(|p| !p.code.is_empty() && p.code == text.trim())
                            {
                                return Ok(());
                            }
                            ensure!(text.len() <= 65536, "文本超过 64 KiB");
                            let digest = hash(&text);
                            if force || digest != self.last_local_hash {
                                self.last_local_hash = digest;
                                self.state.clock += 1;
                                let payload = Text {
                                    kind: "text".into(),
                                    text,
                                    created: now(),
                                    expires: now() + 600,
                                    event_id: id(),
                                    origin: self.state.identity.member.id.clone(),
                                    event_clock: self.state.clock,
                                };
                                let mut unique = BTreeMap::new();
                                for route in self.state.channels() {
                                    unique.entry(hash(&route.peer.signing_key)).or_insert(route);
                                }
                                for (peer, _) in unique {
                                    self.pending.insert(peer, (payload.clone(), 0));
                                }
                                self.save();
                            }
                        }
                        Some(platform::Clip::Image(bytes)) => {
                            let digest = hash(&bytes);
                            if force || digest != self.last_local_hash {
                                self.last_local_hash = digest;
                                let mut unique = BTreeMap::new();
                                for route in self.state.channels() {
                                    unique.entry(hash(&route.peer.signing_key)).or_insert(route);
                                }
                                for (_, route) in unique {
                                    let path = self.cache.join(format!("clipboard-{}.png", id()));
                                    tokio::fs::write(&path, &bytes).await?;
                                    self.stage(
                                        vec![path],
                                        &hash(&route.peer.signing_key),
                                        "image",
                                    )?;
                                }
                            }
                        }
                        None => {}
                    }
                }
            }
            Command::StageFiles { paths, peer } => self.stage(paths, &peer, "file")?,
            Command::Decide(id, accept) => {
                let job = self
                    .state
                    .incoming
                    .iter_mut()
                    .find(|j| j.offer.id == id)
                    .ok_or_else(|| anyhow::anyhow!("接收请求不存在"))?;
                ensure!(
                    job.phase == "waiting" && job.offer.expires > now(),
                    "请求已处理或过期"
                );
                job.phase = if accept { "accepted" } else { "rejected" }.into();
                self.last_attachment.clear();
                self.save();
            }
            Command::OpenFile(id, folder) => {
                let job = self
                    .state
                    .incoming
                    .iter()
                    .find(|j| j.offer.id == id && j.phase == "done")
                    .ok_or_else(|| anyhow::anyhow!("文件尚未接收完成"))?;
                platform::open_file(&job.destination, folder)?;
            }
        }
        Ok(())
    }
    fn cancel_pair(&mut self) {
        self.pair_generation += 1;
        self.pair = None;
        self.pair_view = None;
        self.pair_busy = false;
        self.pair_halted = false;
        self.approve_pending = false;
    }
    fn stage(&mut self, paths: Vec<PathBuf>, peer: &str, kind: &str) -> Result<()> {
        ensure!(!self.state.settings.paused, "同步已暂停");
        ensure!(
            !paths.is_empty()
                && paths.len() <= 20
                && self.stages
                    + self
                        .state
                        .outgoing
                        .iter()
                        .filter(|j| matches!(j.phase.as_str(), "waiting" | "ready" | "sending"))
                        .count()
                    + paths.len()
                    <= 20,
            "最多 20 个待发送文件"
        );
        let route = self
            .state
            .channels()
            .into_iter()
            .find(|r| hash(&r.peer.signing_key) == peer)
            .ok_or_else(|| anyhow::anyhow!("目标设备没有已开启的连接"))?;
        let cached = std::fs::read_dir(&self.cache)?
            .filter_map(|f| f.ok().and_then(|f| f.metadata().ok()).map(|m| m.len()))
            .sum::<u64>();
        let requested = paths
            .iter()
            .map(|p| std::fs::metadata(p).map(|m| m.len()))
            .collect::<std::io::Result<Vec<_>>>()?
            .into_iter()
            .sum::<u64>();
        ensure!(
            cached + requested <= 512 * 1024 * 1024,
            "附件暂存已接近 512 MB"
        );
        for path in paths {
            let cache = self.cache.clone();
            let own = self.state.identity.member.id.clone();
            let peer = peer.to_owned();
            let pair = route.config.pair.clone();
            let kind = kind.to_owned();
            self.stages += 1;
            self.work.push(
                async move {
                    let result = transfers::stage(&path, &cache, &own, &kind, &peer, &pair).await;
                    if kind == "image" {
                        let _ = tokio::fs::remove_file(&path).await;
                    }
                    Work::Stage(result)
                }
                .boxed(),
            );
        }
        Ok(())
    }
    fn notification_action(&mut self, uri: &str) -> Result<()> {
        let u = reqwest::Url::parse(uri)?;
        ensure!(
            u.scheme() == "clipbridge-rust" && u.query().is_none() && u.fragment().is_none(),
            "通知操作无效"
        );
        let parts: Vec<_> = u.path().trim_matches('/').split('/').collect();
        ensure!(parts.len() == 2, "通知操作无效");
        let id = parts[0];
        let job = self
            .state
            .incoming
            .iter()
            .find(|j| j.offer.id == id)
            .ok_or_else(|| anyhow::anyhow!("通知已过期"))?;
        ensure!(
            equal(job.token.as_bytes(), parts[1].as_bytes()),
            "通知操作无效"
        );
        let command = match u.host_str() {
            Some("accept") => Command::Decide(id.into(), true),
            Some("reject") => Command::Decide(id.into(), false),
            Some("open") => Command::OpenFile(id.into(), false),
            Some("folder") => Command::OpenFile(id.into(), true),
            _ => anyhow::bail!("未知通知操作"),
        };
        let _ = self.tx.send(command);
        Ok(())
    }
    fn deliver(&mut self) {
        if self.state.settings.paused {
            return;
        }
        if let Some(text) = self.state.clipboard.clone() {
            if text.expires <= now() {
                self.state.clipboard = None;
                self.save();
                return;
            }
            let existing = arboard::Clipboard::new()
                .and_then(|mut c| c.get_text())
                .ok();
            if existing.as_deref() == Some(&text.text) || platform::set_text(&text.text).is_ok() {
                self.state.clipboard = None;
                self.save();
            }
        }
    }
    fn schedule(&mut self) {
        if self
            .shell_deadline
            .map(|t| t <= Instant::now())
            .unwrap_or(false)
        {
            self.shell_deadline = None;
            let paths = std::mem::take(&mut self.pending_shell_files);
            let _ = self.events.try_send(Event::SendFiles(paths));
            let _ = self.events.try_send(Event::Open);
        }

        self.deliver();
        for job in self
            .state
            .outgoing
            .iter_mut()
            .chain(self.state.incoming.iter_mut())
        {
            if job.offer.expires <= now()
                && !matches!(job.phase.as_str(), "done" | "rejected" | "cancelled")
            {
                job.phase = "cancelled".into();
                let _ = std::fs::remove_file(&job.path);
            }
        }
        self.state
            .outgoing
            .retain(|j| j.offer.expires > now() - 86400);
        self.state
            .incoming
            .retain(|j| j.offer.expires > now() - 7 * 86400);
        if !self.pair_busy
            && !self.pair_halted
            && self.last_pair.elapsed() >= Duration::from_secs(1)
        {
            self.last_pair = Instant::now();
            if let Some(mut pair) = self.pair.take() {
                if !pair.done {
                    if self.approve_pending {
                        if pair.approve().is_ok() {
                            self.approve_pending = false
                        }
                    }
                    let generation = self.pair_generation;
                    self.pair_busy = true;
                    self.work.push(
                        async move {
                            let result = pair.step().await;
                            Work::PairStep(generation, pair, result)
                        }
                        .boxed(),
                    );
                } else {
                    self.pair = Some(pair)
                }
            }
        }
        for g in &self.state.groups {
            let due = self
                .last_group
                .get(&g.id)
                .map(|t| t.elapsed() >= Duration::from_secs(60))
                .unwrap_or(true);
            if due && !self.groups_busy.contains(&g.id) {
                self.groups_busy.insert(g.id.clone());
                self.last_group.insert(g.id.clone(), Instant::now());
                let mut group = g.clone();
                let id = g.id.clone();
                let own = self.state.identity.clone();
                self.work.push(
                    async move {
                        let result = async {
                            publish(&group, &own).await?;
                            if group.enabled && !group.removed.contains(&own.member.id) {
                                refresh(&mut group, &own).await?
                            }
                            Ok(group)
                        }
                        .await;
                        Work::Group(id, result)
                    }
                    .boxed(),
                );
            }
        }
        let routes = self.state.channels();
        if self.state.reroute_jobs(&routes) {
            self.last_attachment.clear();
            self.index_versions.clear();
            self.save();
        }
        let mut preferred = BTreeMap::new();
        for route in &routes {
            preferred
                .entry(hash(&route.peer.signing_key))
                .or_insert(route.clone());
        }
        for route in routes {
            let pair = route.config.pair.clone();
            let due = self
                .last_poll
                .get(&pair)
                .map(|t| t.elapsed() >= Duration::from_secs(self.state.settings.text_interval))
                .unwrap_or(true);
            if due && !self.route_busy.contains(&pair) {
                self.route_busy.insert(pair.clone());
                self.last_poll.insert(pair.clone(), Instant::now());
                let r = route.clone();
                self.work.push(
                    async move {
                        let result = async {
                            channel_http(&r.config)?
                                .get::<Envelope>(&clip_path(&r.config, false, "clip"))
                                .await
                        }
                        .await;
                        Work::Text(r, result)
                    }
                    .boxed(),
                );
            }
            let due = self
                .last_attachment
                .get(&pair)
                .map(|t| {
                    t.elapsed() >= Duration::from_secs(self.state.settings.attachment_interval)
                })
                .unwrap_or(true);
            if due && !self.attachments_busy.contains(&pair) {
                self.attachments_busy.insert(pair.clone());
                self.last_attachment.insert(pair.clone(), Instant::now());
                let r = route.clone();
                let offers: Vec<Offer> = self
                    .state
                    .outgoing
                    .iter()
                    .filter(|j| {
                        j.peer == hash(&route.peer.signing_key)
                            && j.pair == pair
                            && j.offer.expires > now()
                            && !matches!(j.phase.as_str(), "done" | "rejected")
                    })
                    .map(|j| j.offer.clone())
                    .take(32)
                    .collect();
                let decisions: Vec<Decision> = self
                    .state
                    .incoming
                    .iter()
                    .filter(|j| {
                        j.peer == hash(&route.peer.signing_key)
                            && j.offer.expires > now()
                            && j.phase != "waiting"
                    })
                    .map(|j| Decision {
                        id: j.offer.id.clone(),
                        status: if matches!(j.phase.as_str(), "done" | "rejected") {
                            j.phase.clone()
                        } else {
                            "accepted".into()
                        },
                    })
                    .take(128)
                    .collect();
                let version = hash(serde_json::to_vec(&(&offers, &decisions)).unwrap_or_default());
                let publish = self
                    .index_versions
                    .get(&pair)
                    .map(|(v, t)| v != &version || t.elapsed() >= Duration::from_secs(900))
                    .unwrap_or(true);
                if publish {
                    self.index_versions
                        .insert(pair.clone(), (version, Instant::now()));
                }
                self.work.push(
                    async move {
                        let result = async {
                            let mut http = channel_http(&r.config)?;
                            let remote = transfers::get_index(&http, &r.config).await?;
                            if publish {
                                transfers::put_index(&mut http, &r.config, offers, decisions)
                                    .await?;
                            }
                            Ok(remote)
                        }
                        .await;
                        Work::Index(r, result)
                    }
                    .boxed(),
                );
            }
        }
        for (peer, (payload, attempt)) in self.pending.clone() {
            if self
                .retry_after
                .get(&format!("text:{peer}"))
                .map(|t| *t > Instant::now())
                .unwrap_or(false)
            {
                continue;
            }
            if payload.expires <= now() {
                self.pending.remove(&peer);
                continue;
            }
            let choices: Vec<_> = self
                .state
                .channels()
                .into_iter()
                .filter(|r| hash(&r.peer.signing_key) == peer)
                .collect();
            if choices.is_empty() {
                continue;
            }
            let route = choices[attempt as usize % choices.len()].clone();
            let busy = format!("text:{peer}");
            if self.transfers_busy.contains(&busy) {
                continue;
            }
            let state = self
                .state
                .routes
                .entry(route.config.pair.clone())
                .or_default();
            state.clock += 1;
            state.winner = route.config.device.clone();
            let clock = state.clock;
            if !self.save() {
                break;
            }
            self.transfers_busy.insert(busy);
            let r = route.clone();
            self.work.push(
                async move {
                    let result = async {
                        let e = Envelope::seal(&r.config, clock, &payload)?;
                        put_clip(&mut channel_http(&r.config)?, &r.config, "clip", &e).await
                    }
                    .await;
                    Work::Sent(r, payload.event_id.clone(), result)
                }
                .boxed(),
            );
        }
        for job in self.state.outgoing.clone() {
            if self
                .retry_after
                .get(&job.offer.id)
                .map(|t| *t > Instant::now())
                .unwrap_or(false)
            {
                continue;
            }
            if !job.uploaded || !matches!(job.phase.as_str(), "done" | "rejected" | "cancelled") {
                continue;
            }
            let key = format!("clean:{}", job.offer.id);
            if self.transfers_busy.contains(&key) {
                continue;
            }
            if let Some(route) = self
                .state
                .channels()
                .into_iter()
                .find(|r| r.config.pair == job.pair)
            {
                self.transfers_busy.insert(key);
                self.work.push(
                    async move {
                        let result = async {
                            channel_http(&route.config)?
                                .delete(&transfers::path(&route.config, true, &job.offer.id))
                                .await
                        }
                        .await;
                        Work::Clean(job.offer.id, result)
                    }
                    .boxed(),
                );
            }
        }
        for job in self.state.outgoing.clone() {
            if self
                .retry_after
                .get(&job.offer.id)
                .map(|t| *t > Instant::now())
                .unwrap_or(false)
            {
                continue;
            }
            if job.offer.expires <= now()
                || matches!(job.phase.as_str(), "done" | "rejected" | "cancelled")
            {
                continue;
            }
            if self.transfers_busy.contains(&job.offer.id) {
                continue;
            }
            let choices: Vec<_> = self
                .state
                .channels()
                .into_iter()
                .filter(|r| hash(&r.peer.signing_key) == job.peer)
                .collect();
            let route = choices
                .iter()
                .find(|r| r.config.pair == job.pair)
                .or(choices.first());
            let Some(route) = route.cloned() else {
                continue;
            };
            let remote = self.remote.get(&route.config.pair);
            let decision = remote.and_then(|i| i.decisions.iter().find(|d| d.id == job.offer.id));
            if let Some(d) = decision {
                if matches!(d.status.as_str(), "done" | "rejected") {
                    if let Some(j) = self
                        .state
                        .outgoing
                        .iter_mut()
                        .find(|j| j.offer.id == job.offer.id)
                    {
                        j.phase = d.status.clone();
                        let _ = std::fs::remove_file(&j.path);
                    }
                    self.save();
                    continue;
                }
            }
            if !job.uploaded
                && (job.offer.kind == "image"
                    || decision.map(|d| d.status == "accepted").unwrap_or(false))
            {
                if let Some(current) = self
                    .state
                    .outgoing
                    .iter_mut()
                    .find(|j| j.offer.id == job.offer.id)
                {
                    current.phase = "sending".into();
                }
                self.transfers_busy.insert(job.offer.id.clone());
                let id = job.offer.id.clone();
                self.work.push(
                    async move {
                        let result = async {
                            transfers::upload(
                                &mut channel_http(&route.config)?,
                                &route.config,
                                &job,
                            )
                            .await
                        }
                        .await;
                        Work::Upload(route, id, result)
                    }
                    .boxed(),
                );
            }
        }
        for job in self.state.incoming.clone() {
            if self
                .retry_after
                .get(&job.offer.id)
                .map(|t| *t > Instant::now())
                .unwrap_or(false)
            {
                continue;
            }
            if job.phase != "accepted"
                || job.offer.expires <= now()
                || self.transfers_busy.contains(&job.offer.id)
            {
                continue;
            }
            let choices = self.state.channels();
            let route = choices
                .iter()
                .find(|r| {
                    hash(&r.peer.signing_key) == job.peer
                        && self
                            .remote
                            .get(&r.config.pair)
                            .map(|i| {
                                i.offers
                                    .iter()
                                    .any(|o| o.id == job.offer.id && o.status == "ready")
                            })
                            .unwrap_or(false)
                })
                .cloned();
            if let Some(route) = route {
                if let Some(current) = self
                    .state
                    .incoming
                    .iter_mut()
                    .find(|j| j.offer.id == job.offer.id)
                {
                    current.phase = "receiving".into();
                }
                self.transfers_busy.insert(job.offer.id.clone());
                let cache = self.cache.clone();
                self.work.push(
                    async move {
                        let result = async {
                            transfers::download(
                                &channel_http(&route.config)?,
                                &route.config,
                                &job.offer,
                                &cache,
                            )
                            .await
                        }
                        .await;
                        Work::Download(route, job.offer, result)
                    }
                    .boxed(),
                );
            }
        }
    }
    async fn complete(&mut self, w: Work) -> Result<()> {
        match w {
            Work::Pair(generation, result) => {
                if generation != self.pair_generation {
                    return Ok(());
                }
                self.pair_busy = false;
                if let Err(e) = &result {
                    if let Some(p) = &mut self.pair_view {
                        p.busy = false;
                        p.status = e.to_string();
                    }
                    self.pair_halted = true;
                }
                let pair = result?;
                if pair.host() {
                    if let Some(group) = &pair.group {
                        if !self.state.groups.iter().any(|g| g.id == group.id) {
                            self.state.groups.push(group.clone());
                            self.save();
                        }
                    }
                }
                self.pair = Some(pair);
                self.update_pair();
            }
            Work::PairStep(generation, pair, result) => {
                if generation != self.pair_generation {
                    return Ok(());
                }
                self.pair_busy = false;
                if pair.host() {
                    if let Some(group) = &pair.group {
                        if let Some(current) =
                            self.state.groups.iter_mut().find(|g| g.id == group.id)
                        {
                            if group.sequence > current.sequence {
                                *current = group.clone();
                                self.save();
                            }
                        }
                    }
                }
                if pair.done && !pair.host() {
                    let received = pair.group.clone().unwrap();
                    let old = self.state.groups.iter().find(|g| g.id == received.id);
                    let installed = received.install(old, &self.state.identity)?;
                    self.state.groups.retain(|g| g.id != installed.id);
                    self.last_group.remove(&installed.id);
                    self.state.groups.push(installed);
                    self.save();
                }
                if let Err(e) = &result {
                    self.notice(e.to_string());
                    if e.downcast_ref::<reqwest::Error>().is_none() {
                        self.pair_halted = true
                    }
                }
                self.pair = Some(pair);
                self.update_pair();
            }
            Work::Group(id, result) => {
                self.groups_busy.remove(&id);
                match result {
                    Ok(group) => {
                        if let Some(current) = self.state.groups.iter_mut().find(|g| g.id == id) {
                            for _ in 0..8 {
                                for feed in group.feeds.values() {
                                    let _ = current.accept(feed.clone());
                                }
                            }
                            current.status = group.status;
                            self.save();
                        }
                    }
                    Err(e) => {
                        if let Some(g) = self.state.groups.iter_mut().find(|g| g.id == id) {
                            g.status = e.to_string();
                        }
                    }
                }
            }
            Work::Text(route, result) => {
                self.route_busy.remove(&route.config.pair);
                if !self.active(&route) {
                    return Ok(());
                }
                match result {
                    Ok(Some(e)) => {
                        let received = self.state.receive(&route, &e)?;
                        if let Some(text) = received {
                            self.pending.clear();
                            self.last_local_hash = hash(&text.text);
                            if self.save() {
                                self.deliver();
                                self.synced(&route);
                            }
                        } else {
                            self.save();
                        }
                    }
                    Ok(None) => {
                        self.state
                            .routes
                            .entry(route.config.pair.clone())
                            .or_default()
                            .status = "连接正常".into();
                    }
                    Err(e) => {
                        let message = e.to_string();
                        self.state
                            .routes
                            .entry(route.config.pair.clone())
                            .or_default()
                            .status = message.clone();
                        if let Some(g) = self.state.groups.iter_mut().find(|g| g.id == route.group)
                        {
                            g.status = message;
                        }
                    }
                }
            }
            Work::Sent(route, event_id, result) => {
                let peer = hash(&route.peer.signing_key);
                self.transfers_busy.remove(&format!("text:{peer}"));
                if !self.active(&route) {
                    return Ok(());
                }
                if result.is_ok() {
                    if self.pending.get(&peer).map(|(p, _)| p.event_id.as_str())
                        == Some(event_id.as_str())
                    {
                        self.pending.remove(&peer);
                    }
                    self.synced(&route);
                } else if let Some((_, attempt)) = self.pending.get_mut(&peer) {
                    self.retry_after.insert(
                        format!("text:{peer}"),
                        Instant::now() + Duration::from_secs(3),
                    );
                    *attempt += 1;
                }
            }
            Work::Index(route, result) => {
                self.attachments_busy.remove(&route.config.pair);
                if !self.active(&route) {
                    return Ok(());
                }
                match result {
                    Ok(index) => {
                        if let Some(index) = index {
                            for offer in &index.offers {
                                if offer.sender != route.peer.id || offer.expires <= now() {
                                    continue;
                                }
                                if let Some(job) = self.state.incoming.iter_mut().find(|j| {
                                    j.offer.id == offer.id && j.offer.sender == offer.sender
                                }) {
                                    if offer.status == "cancelled"
                                        && !matches!(job.phase.as_str(), "done" | "rejected")
                                    {
                                        job.phase = "rejected".into()
                                    }
                                    continue;
                                }
                                if self
                                    .state
                                    .incoming
                                    .iter()
                                    .filter(|j| !matches!(j.phase.as_str(), "done" | "rejected"))
                                    .count()
                                    >= 32
                                {
                                    continue;
                                }
                                let job = Job {
                                    offer: offer.clone(),
                                    path: PathBuf::new(),
                                    peer: hash(&route.peer.signing_key),
                                    pair: route.config.pair.clone(),
                                    phase: if offer.kind == "image" {
                                        "accepted"
                                    } else {
                                        "waiting"
                                    }
                                    .into(),
                                    uploaded: false,
                                    destination: PathBuf::new(),
                                    token: id(),
                                };
                                if offer.kind == "file" {
                                    let _ = platform::notify(
                                        &offer.id,
                                        "收到文件请求",
                                        &format!(
                                            "{} · {:.1} MB",
                                            offer.name,
                                            offer.size as f64 / 1048576.0
                                        ),
                                        &[
                                            (
                                                "接收",
                                                format!(
                                                    "clipbridge-rust://accept/{}/{}",
                                                    offer.id, job.token
                                                ),
                                            ),
                                            (
                                                "拒绝",
                                                format!(
                                                    "clipbridge-rust://reject/{}/{}",
                                                    offer.id, job.token
                                                ),
                                            ),
                                        ],
                                    );
                                }
                                self.state.incoming.push(job);
                            }
                            self.remote.insert(route.config.pair.clone(), index);
                            self.save();
                        }
                    }
                    Err(e) => {
                        self.index_versions.remove(&route.config.pair);
                        self.notice(format!("附件连接失败：{e}"));
                        let alternate = self
                            .state
                            .channels()
                            .iter()
                            .find(|r| {
                                r.peer.id == route.peer.id && r.config.pair != route.config.pair
                            })
                            .map(|r| r.config.pair.clone());
                        if let Some(pair) = alternate {
                            for j in &mut self.state.outgoing {
                                if j.pair == route.config.pair && !j.uploaded {
                                    j.pair = pair.clone();
                                }
                            }
                        }
                    }
                }
            }
            Work::Upload(route, id, result) => {
                if result.is_err() {
                    self.retry_after
                        .insert(id.clone(), Instant::now() + Duration::from_secs(30));
                }
                self.transfers_busy.remove(&id);
                if !self.active(&route) {
                    return Ok(());
                }
                if let Some(job) = self.state.outgoing.iter_mut().find(|j| j.offer.id == id) {
                    if result.is_ok() {
                        job.uploaded = true;
                        job.offer.status = "ready".into();
                        job.phase = "ready".into();
                        job.pair = route.config.pair.clone();
                    } else {
                        job.phase = "waiting".into();
                        let choices = self.state.channels();
                        let peers: Vec<_> = choices
                            .iter()
                            .filter(|r| hash(&r.peer.signing_key) == hash(&route.peer.signing_key))
                            .collect();
                        if let Some(next) =
                            peers.iter().find(|r| r.config.pair != route.config.pair)
                        {
                            if let Some(job) =
                                self.state.outgoing.iter_mut().find(|j| j.offer.id == id)
                            {
                                job.pair = next.config.pair.clone();
                            }
                        }
                    }
                }
                self.index_versions.remove(&route.config.pair);
                self.last_attachment.remove(&route.config.pair);
                self.save();
            }
            Work::Download(route, offer, result) => {
                if result.is_err() {
                    self.retry_after
                        .insert(offer.id.clone(), Instant::now() + Duration::from_secs(30));
                    if let Some(job) = self
                        .state
                        .incoming
                        .iter_mut()
                        .find(|j| j.offer.id == offer.id)
                    {
                        job.phase = "accepted".into();
                    }
                }
                self.transfers_busy.remove(&offer.id);
                if !self.active(&route) {
                    if let Some(job) = self
                        .state
                        .incoming
                        .iter_mut()
                        .find(|j| j.offer.id == offer.id)
                    {
                        job.phase = "accepted".into();
                    }
                    if let Ok(path) = result {
                        let _ = tokio::fs::remove_file(path).await;
                    }
                    return Ok(());
                }
                let path = result?;
                let Some(job) = self.state.incoming.iter_mut().find(|j| {
                    j.offer.id == offer.id && matches!(j.phase.as_str(), "accepted" | "receiving")
                }) else {
                    let _ = tokio::fs::remove_file(path).await;
                    return Ok(());
                };
                job.path = path.clone();
                if offer.kind == "image" {
                    if let Err(error) = platform::set_image(&path) {
                        job.phase = "rejected".into();
                        let _ = tokio::fs::remove_file(&path).await;
                        self.last_attachment.clear();
                        self.save();
                        self.notice(format!("图片写入剪贴板失败，请重新发送：{error}"));
                        let _ = platform::notify(
                            &offer.id,
                            "图片同步失败",
                            "无法写入剪贴板，请重新发送",
                            &[],
                        );
                        return Ok(());
                    }
                    job.phase = "done".into();
                    let _ = tokio::fs::remove_file(path).await;
                    let _ = platform::notify(&offer.id, "图片已同步", "已写入剪贴板", &[]);
                } else {
                    job.phase = "saving".into();
                    let copy = job.clone();
                    let directory = self.state.settings.receive_directory.clone();
                    self.transfers_busy.insert(offer.id.clone());
                    self.work.push(
                        async move {
                            Work::Saved(
                                copy.offer.id.clone(),
                                transfers::save_download(&copy, &directory).await,
                            )
                        }
                        .boxed(),
                    );
                }
                self.synced(&route);
                self.last_attachment.clear();
                self.save();
            }
            Work::Saved(id, result) => {
                self.transfers_busy.remove(&id);
                let path = match result {
                    Ok(path) => path,
                    Err(error) => {
                        if let Some(job) = self.state.incoming.iter_mut().find(|j| j.offer.id == id)
                        {
                            job.phase = "rejected".into();
                            let _ = std::fs::remove_file(&job.path);
                        }
                        self.save();
                        self.notice(format!("保存失败，请检查接收目录后重新发送：{error}"));
                        return Ok(());
                    }
                };
                if let Some(job) = self.state.incoming.iter_mut().find(|j| j.offer.id == id) {
                    job.phase = "done".into();
                    job.destination = path;
                    let _ = std::fs::remove_file(&job.path);
                    let _ = platform::notify(
                        &id,
                        "文件接收完成",
                        &job.offer.name,
                        &[
                            (
                                "打开文件",
                                format!("clipbridge-rust://open/{id}/{}", job.token),
                            ),
                            (
                                "打开文件所在位置",
                                format!("clipbridge-rust://folder/{id}/{}", job.token),
                            ),
                        ],
                    );
                }
                self.last_attachment.clear();
                self.save();
            }
            Work::Clean(id, result) => {
                self.transfers_busy.remove(&format!("clean:{id}"));
                if result.is_ok() {
                    if let Some(job) = self.state.outgoing.iter_mut().find(|j| j.offer.id == id) {
                        job.uploaded = false;
                    }
                    self.save();
                }
            }
            Work::Stage(result) => {
                self.stages = self.stages.saturating_sub(1);
                self.state.outgoing.push(result?);
                self.last_attachment.clear();
                self.save();
            }
        }
        Ok(())
    }
    fn synced(&mut self, r: &Route) {
        let time = millis();
        if let Some(group) = self.state.groups.iter_mut().find(|g| g.id == r.group) {
            group.last_sync = time;
            group.status = "连接正常".into()
        }
        self.state
            .routes
            .entry(r.config.pair.clone())
            .or_default()
            .last_sync = time;
        self.save();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn state() -> State {
        State {
            identity: Identity::new("Rust test").unwrap(),
            groups: vec![],
            settings: Settings::default(),
            routes: BTreeMap::new(),
            seen: BTreeMap::new(),
            clock: 0,
            clipboard: None,
            outgoing: vec![],
            incoming: vec![],
            channels_cache: Default::default(),
        }
    }
    #[test]
    fn three_device_text_is_not_received_twice_across_groups() {
        let sender = Identity::new("Phone").unwrap();
        let mut b = state();
        let mut c = state();
        let connection = Connection {
            transport: "webdav".into(),
            server: "https://example.invalid/dav/".into(),
            username: "test".into(),
            password: "test".into(),
        };
        let mut group = Group::new(&sender, "Mesh", connection.clone()).unwrap();
        group.add(&sender, &b.identity.member).unwrap();
        group.add(&sender, &c.identity.member).unwrap();
        b.groups.push(group.clone());
        c.groups.push(group.clone());
        let payload = Text {
            kind: "text".into(),
            text: "Three device text".into(),
            created: now(),
            expires: now() + 600,
            event_id: id(),
            origin: sender.member.id.clone(),
            event_clock: 1,
        };
        for receiver in [&mut b, &mut c] {
            let send = group.channel(&sender, &receiver.identity.member).unwrap();
            let route = receiver
                .channels()
                .into_iter()
                .find(|r| r.peer.id == sender.member.id)
                .unwrap();
            let envelope = Envelope::seal(&send, 1, &payload).unwrap();
            assert!(receiver.receive(&route, &envelope).unwrap().is_some());
            assert!(receiver.receive(&route, &envelope).unwrap().is_none());
            let mut other = Group::new(&sender, "Other route", connection.clone()).unwrap();
            other.add(&sender, &receiver.identity.member).unwrap();
            receiver.groups.push(other.clone());
            let route = receiver
                .channels()
                .into_iter()
                .find(|r| r.group == other.id)
                .unwrap();
            let config = other.channel(&sender, &receiver.identity.member).unwrap();
            assert!(
                receiver
                    .receive(&route, &Envelope::seal(&config, 1, &payload).unwrap())
                    .unwrap()
                    .is_none()
            );
            assert_eq!(
                receiver.clipboard.as_ref().unwrap().event_id,
                payload.event_id
            );
            assert_eq!(receiver.routes[&send.pair].clock, 1);
        }
    }
    #[test]
    fn file_tasks_switch_only_to_the_same_confirmed_device() {
        let mut state = state();
        let peer = Identity::new("Peer").unwrap();
        for name in ["first", "backup"] {
            let mut group = Group::new(
                &state.identity,
                name,
                Connection {
                    transport: "webdav".into(),
                    server: "https://example.invalid/dav/".into(),
                    username: "test".into(),
                    password: "test".into(),
                },
            )
            .unwrap();
            group.add(&state.identity, &peer.member).unwrap();
            state.groups.push(group);
        }
        let routes = state.channels();
        let first = routes[0].config.pair.clone();
        let backup = routes[1].config.pair.clone();
        let peer_key = hash(&peer.member.signing_key);
        state.outgoing.push(Job {
            offer: Offer {
                id: id(),
                name: "file.bin".into(),
                mime: "application/octet-stream".into(),
                kind: "file".into(),
                size: 0,
                hash: hash([]),
                chunks: 1,
                expires: now() + 3600,
                status: "ready".into(),
                sender: state.identity.member.id.clone(),
            },
            path: PathBuf::new(),
            peer: peer_key.clone(),
            pair: first,
            phase: "ready".into(),
            uploaded: true,
            destination: PathBuf::new(),
            token: id(),
        });
        state.groups.remove(0);
        let routes = state.channels();
        assert!(state.reroute_jobs(&routes));
        let job = &state.outgoing[0];
        assert_eq!(job.pair, backup);
        assert_eq!(job.peer, peer_key);
        assert!(!job.uploaded);
        assert_eq!(job.offer.status, "offered");
        assert!(!state.reroute_jobs(&routes));
        state.groups.clear();
        let stranger = Identity::new("Other device").unwrap();
        let mut group = Group::new(
            &state.identity,
            "other",
            Connection {
                transport: "webdav".into(),
                server: "https://example.invalid/dav/".into(),
                username: "test".into(),
                password: "test".into(),
            },
        )
        .unwrap();
        group.add(&state.identity, &stranger.member).unwrap();
        state.groups.push(group);
        assert!(!state.reroute_jobs(&state.channels()));
        assert_eq!(state.outgoing[0].peer, peer_key);
    }
    #[test]
    fn pending_receipts_are_visible_before_completed_history() {
        let mut state = state();
        let mut base = Job {
            offer: Offer {
                id: id(),
                name: "file.bin".into(),
                mime: "application/octet-stream".into(),
                kind: "file".into(),
                size: 0,
                hash: hash([]),
                chunks: 1,
                expires: now() + 3600,
                status: "offered".into(),
                sender: state.identity.member.id.clone(),
            },
            path: PathBuf::new(),
            peer: String::new(),
            pair: String::new(),
            phase: "waiting".into(),
            uploaded: false,
            destination: PathBuf::new(),
            token: id(),
        };
        let pending = base.offer.id.clone();
        state.incoming.push(base.clone());
        base.phase = "done".into();
        for i in 0..60 {
            base.offer.id = id();
            base.offer.expires += i;
            state.outgoing.push(base.clone());
        }
        let view = state.job_views();
        assert_eq!(view.len(), 50);
        assert_eq!(view[0].id, pending);
        assert!(view[0].incoming);
        assert_eq!(view[0].phase, "waiting");
    }
    #[test]
    fn route_cache_and_protected_state_fail_closed() {
        let mut s = state();
        let peer = Identity::new("Peer").unwrap();
        let mut g = Group::new(
            &s.identity,
            "Test",
            Connection {
                transport: "webdav".into(),
                server: "https://example.invalid/dav/".into(),
                username: "test".into(),
                password: "test".into(),
            },
        )
        .unwrap();
        g.add(&s.identity, &peer.member).unwrap();
        s.groups.push(g);
        let key = s.channels()[0].config.key.clone();
        for _ in 0..100 {
            assert_eq!(s.channels()[0].config.key, key)
        }
        s.groups[0].enabled = false;
        assert!(s.channels().is_empty());
        s.groups[0].enabled = true;
        assert_eq!(s.channels()[0].config.key, key);
        let dir = std::env::temp_dir().join(format!("clipbridge-rust-{}", id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state.bin");
        s.save(&path).unwrap();
        let raw = std::fs::read(&path).unwrap();
        assert!(!String::from_utf8_lossy(&raw).contains(&s.identity.signing_private));
        let loaded = State::load(&path).unwrap();
        assert_eq!(loaded.channels()[0].config.key, key);
        std::fs::write(&path, b"invalid protected state").unwrap();
        assert!(State::load(&path).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), b"invalid protected state");
        std::fs::remove_dir_all(dir).unwrap();
    }
}
