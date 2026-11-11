use crate::{groups::Connection, platform, runtime::*};
use gpui::{ImageFormat, prelude::*, *};
use gpui_component::{
    Disableable, Icon, IconName, Theme, ThemeMode,
    button::{Button, ButtonVariants},
    input::{Input, InputState},
    switch::Switch,
};
use std::{collections::BTreeMap, path::PathBuf, sync::Arc};
const INK: u32 = 0x19352e;
const MUTED: u32 = 0x70827c;
const GREEN: u32 = 0x008578;
const LINE: u32 = 0xe7eeeb;
const BG: u32 = 0xf4f7f6;
#[derive(Clone)]
enum Overlay {
    None,
    Form(bool),
    Pair,
    Members(String),
    Send(Vec<PathBuf>, String),
    Confirm(String, Command),
}
pub struct Desktop {
    tx: tokio::sync::mpsc::UnboundedSender<Command>,
    snapshot: Option<Arc<Snapshot>>,
    page: usize,
    overlay: Overlay,
    dav: bool,
    inputs: BTreeMap<&'static str, Entity<InputState>>,
    initialized: bool,
    notice: String,
    qr: Option<Arc<Image>>,
    qr_link: String,
    focus: FocusHandle,
    recording: bool,
    logo: Arc<Image>,
}
fn card() -> Div {
    div()
        .flex()
        .flex_col()
        .gap_4()
        .p_6()
        .rounded(px(20.))
        .bg(rgb(0xffffff))
        .border_1()
        .border_color(rgb(LINE))
}
fn muted(text: impl Into<SharedString>) -> Div {
    div().text_sm().text_color(rgb(MUTED)).child(text.into())
}
fn title(text: impl Into<SharedString>) -> Div {
    div()
        .text_xl()
        .font_weight(FontWeight::SEMIBOLD)
        .child(text.into())
}
fn time(value: i64) -> String {
    chrono::DateTime::from_timestamp_millis(value)
        .map(|t| {
            t.with_timezone(&chrono::Local)
                .format("%m-%d %H:%M:%S")
                .to_string()
        })
        .filter(|_| value > 0)
        .unwrap_or("暂无记录".into())
}
fn phase(p: &str) -> &str {
    match p {
        "waiting" => "等待接收",
        "accepted" => "等待发送方上传",
        "ready" => "等待对方下载",
        "sending" => "正在发送",
        "receiving" => "正在接收",
        "saving" => "正在保存",
        "done" => "已完成",
        "rejected" => "已拒绝",
        "cancelled" => "已取消",
        _ => "处理中",
    }
}
impl Desktop {
    pub fn new(
        tx: tokio::sync::mpsc::UnboundedSender<Command>,
        events: async_channel::Receiver<Event>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut inputs = BTreeMap::new();
        for (key, placeholder, secret) in [
            ("name", "我的设备", false),
            ("server", "https://服务器地址:端口", false),
            ("username", "WebDAV账号", false),
            ("password", "WebDAV密码", true),
            ("setup", "从服务器日志获取", true),
            ("code", "16 位配对码或完整邀请", false),
            ("textInterval", "2", false),
            ("attachmentInterval", "3", false),
        ] {
            inputs.insert(
                key,
                cx.new(|cx| {
                    InputState::new(window, cx)
                        .placeholder(placeholder)
                        .masked(secret)
                }),
            );
        }
        cx.spawn_in(window, async move |this, cx| {
            while let Ok(event) = events.recv().await {
                let _ = this.update_in(cx, |view, window, cx| {
                    match event {
                        Event::Snapshot(snapshot) => {
                            platform::set_paused(snapshot.settings.paused);
                            if !view.initialized {
                                view.inputs["textInterval"].update(cx, |input, cx| {
                                    input.set_value(
                                        snapshot.settings.text_interval.to_string(),
                                        window,
                                        cx,
                                    )
                                });
                                view.inputs["attachmentInterval"].update(cx, |input, cx| {
                                    input.set_value(
                                        snapshot.settings.attachment_interval.to_string(),
                                        window,
                                        cx,
                                    )
                                });
                                view.initialized = true;
                                let _ = platform::set_hotkey(
                                    snapshot.settings.hotkey_modifiers,
                                    snapshot.settings.hotkey_key,
                                );
                            }
                            if let Some(pair) = &snapshot.paired {
                                if !pair.link.is_empty() && view.qr_link != pair.link {
                                    view.qr_link = pair.link.clone();
                                    view.qr = Self::make_qr(&pair.link);
                                }
                                if !matches!(view.overlay, Overlay::Pair) {
                                    view.overlay = Overlay::Pair;
                                }
                            }
                            view.snapshot = Some(snapshot);
                        }
                        Event::Open => {
                            platform::show(true);
                            window.activate_window();
                        }
                        Event::SendFiles(paths) => {
                            view.overlay = Overlay::Send(paths, String::new());
                            platform::show(true);
                            window.activate_window();
                        }
                        Event::Quit => {
                            cx.quit();
                        }
                        Event::Fatal(error) => {
                            view.notice = error;
                        }
                    }
                    cx.notify();
                });
            }
        })
        .detach();
        let mut png = std::io::Cursor::new(vec![]);
        image::load_from_memory(include_bytes!("../../ClipBridge.Windows/App.ico"))
            .unwrap()
            .write_to(&mut png, image::ImageFormat::Png)
            .unwrap();
        let logo = Arc::new(Image::from_bytes(ImageFormat::Png, png.into_inner()));
        Self {
            tx,
            snapshot: None,
            page: 0,
            overlay: Overlay::None,
            dav: false,
            inputs,
            initialized: false,
            notice: String::new(),
            qr: None,
            qr_link: String::new(),
            focus: cx.focus_handle(),
            recording: false,
            logo,
        }
    }
    fn make_qr(text: &str) -> Option<Arc<Image>> {
        let code = qrcode::QrCode::new(text).ok()?;
        let png = code
            .render::<image::Luma<u8>>()
            .min_dimensions(240, 240)
            .quiet_zone(true)
            .build();
        let mut bytes = std::io::Cursor::new(vec![]);
        image::DynamicImage::ImageLuma8(png)
            .write_to(&mut bytes, image::ImageFormat::Png)
            .ok()?;
        Some(Arc::new(Image::from_bytes(
            ImageFormat::Png,
            bytes.into_inner(),
        )))
    }
    fn send(&self, command: Command) {
        let _ = self.tx.send(command);
    }
    fn value(&self, key: &str, cx: &App) -> String {
        self.inputs[key].read(cx).value().to_string()
    }
    fn field(&self, label: &str, key: &str) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap_2()
            .child(muted(label.to_owned()))
            .child(Input::new(&self.inputs[key]).h(px(44.)))
            .into_any_element()
    }
    fn action(
        &self,
        id: impl Into<SharedString>,
        label: &str,
        command: Command,
        primary: bool,
    ) -> AnyElement {
        let tx = self.tx.clone();
        let b = Button::new(id.into())
            .label(label.to_owned())
            .h(px(40.))
            .on_click(move |_, _, _| {
                let _ = tx.send(command.clone());
            });
        if primary {
            b.primary().into_any_element()
        } else {
            b.into_any_element()
        }
    }
    fn switch_row(
        &self,
        id: &'static str,
        label: &str,
        description: &str,
        on: bool,
        command: impl Fn(bool) -> Command + 'static,
    ) -> AnyElement {
        let tx = self.tx.clone();
        div()
            .flex()
            .items_center()
            .justify_between()
            .gap_6()
            .py_3()
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .flex_1()
                    .child(label.to_owned())
                    .child(muted(description.to_owned())),
            )
            .child(Switch::new(id).checked(on).on_click(move |value, _, _| {
                let _ = tx.send(command(*value));
            }))
            .into_any_element()
    }
    fn header(&self, heading: &str, subtitle: &str) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap_2()
            .mb_6()
            .child(
                div()
                    .text_3xl()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(heading.to_owned()),
            )
            .child(muted(subtitle.to_owned()))
            .into_any_element()
    }
    fn status(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let mut view = div()
            .flex()
            .flex_col()
            .gap_5()
            .child(self.header("状态", "同步与最近传输"));
        let Some(s) = self.snapshot.clone() else {
            return view.child(card().child("正在启动…")).into_any_element();
        };
        let paused = s.settings.paused;
        view = view.child(
            div()
                .flex()
                .gap_4()
                .child(
                    card()
                        .flex_1()
                        .bg(rgb(if paused { 0xf1ede4 } else { 0xe1f3e9 }))
                        .border_0()
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .gap_3()
                                .child(div().size_3().rounded_full().bg(rgb(if paused {
                                    0xb28a42
                                } else {
                                    GREEN
                                })))
                                .child(title(if paused {
                                    "同步已暂停"
                                } else if s.targets.is_empty() {
                                    "尚无接收设备"
                                } else {
                                    "同步运行中"
                                })),
                        )
                        .child(muted(format!("最后同步  {}", time(s.last_sync))))
                        .child(self.action(
                            "pause",
                            if paused {
                                "启用同步"
                            } else {
                                "暂停同步"
                            },
                            Command::TogglePause,
                            false,
                        )),
                )
                .child(
                    card()
                        .w(px(180.))
                        .child(muted("接收设备"))
                        .child(
                            div()
                                .text_3xl()
                                .font_weight(FontWeight::SEMIBOLD)
                                .child(s.targets.len().to_string()),
                        )
                        .child(muted(format!("{} 个同步组", s.groups.len()))),
                ),
        );
        if s.targets.is_empty() {
            view = view.child(
                card()
                    .child(title("添加你的设备"))
                    .child(muted("创建同步组，或使用已有设备的邀请加入。"))
                    .child(
                        Button::new("start-group")
                            .label("管理同步组")
                            .primary()
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.page = 1;
                                cx.notify()
                            })),
                    ),
            );
        } else {
            let mut devices = div().flex().flex_wrap().gap_3();
            for t in &s.targets {
                devices = devices.child(
                    div()
                        .flex()
                        .items_center()
                        .gap_2()
                        .px_4()
                        .py_3()
                        .rounded_lg()
                        .bg(rgb(0xffffff))
                        .child(Icon::new(IconName::Frame).size_4().text_color(rgb(GREEN)))
                        .child(t.name.clone()),
                );
            }
            view = view.child(devices);
        }
        let mut transfers = card().child(
            div()
                .flex()
                .justify_between()
                .items_center()
                .child(title("最近传输"))
                .child(self.action("send-file", "发送文件", Command::ChooseFiles, true)),
        );
        if s.jobs.is_empty() {
            transfers = transfers.child(div().py_8().child(muted("暂无文件传输")));
        } else {
            for job in s.jobs.iter().take(50) {
                let mut row = div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap_4()
                    .py_3()
                    .border_b_1()
                    .border_color(rgb(LINE))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_3()
                            .flex_1()
                            .min_w_0()
                            .child(
                                Icon::new(if job.kind == "image" {
                                    IconName::GalleryVerticalEnd
                                } else {
                                    IconName::File
                                })
                                .size_5()
                                .text_color(rgb(GREEN)),
                            )
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap_1()
                                    .min_w_0()
                                    .child(div().truncate().child(job.name.clone()))
                                    .child(muted(format!(
                                        "{} · {:.1} MB · {}",
                                        if job.incoming { "接收" } else { "发送" },
                                        job.size as f64 / 1048576.0,
                                        phase(&job.phase)
                                    ))),
                            ),
                    );
                if job.incoming && job.phase == "waiting" {
                    row = row
                        .child(self.action(
                            format!("accept-{}", job.id),
                            "接收",
                            Command::Decide(job.id.clone(), true),
                            true,
                        ))
                        .child(self.action(
                            format!("reject-{}", job.id),
                            "拒绝",
                            Command::Decide(job.id.clone(), false),
                            false,
                        ));
                }
                if job.incoming && job.phase == "done" && !job.destination.is_empty() {
                    row = row
                        .child(self.action(
                            format!("open-{}", job.id),
                            "打开",
                            Command::OpenFile(job.id.clone(), false),
                            false,
                        ))
                        .child(self.action(
                            format!("folder-{}", job.id),
                            "所在位置",
                            Command::OpenFile(job.id.clone(), true),
                            false,
                        ));
                }
                transfers = transfers.child(row);
            }
        }
        view.child(transfers).into_any_element()
    }
    fn groups(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let mut view = div()
            .flex()
            .flex_col()
            .gap_5()
            .child(self.header("同步组", "邀请、成员与连接方式"));
        view = view.child(
            div()
                .flex()
                .gap_3()
                .child(
                    Button::new("create")
                        .label("创建同步组")
                        .icon(IconName::Plus)
                        .primary()
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.overlay = Overlay::Form(false);
                            this.notice.clear();
                            cx.notify()
                        })),
                )
                .child(
                    Button::new("join")
                        .label("加入同步组")
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.overlay = Overlay::Form(true);
                            this.notice.clear();
                            cx.notify()
                        })),
                ),
        );
        if let Some(s) = &self.snapshot {
            for group in &s.groups {
                let id = group.id.clone();
                let tx = self.tx.clone();
                let mut c = card()
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .gap_4()
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap_2()
                                    .flex_1()
                                    .min_w_0()
                                    .child(title(group.name.clone()))
                                    .child(muted(format!(
                                        "{} · {} 台设备",
                                        if group.transport == "webdav" {
                                            "WebDAV"
                                        } else {
                                            "专用中转"
                                        },
                                        group.members.len()
                                    ))),
                            )
                            .child(
                                Switch::new(SharedString::from(format!("group-{}", group.id)))
                                    .checked(group.enabled && !group.left)
                                    .disabled(group.left)
                                    .on_click(move |on, _, _| {
                                        let _ = tx.send(Command::ToggleGroup(id.clone(), *on));
                                    }),
                            ),
                    )
                    .child(muted(group.server.clone()))
                    .child(div().text_sm().child(if group.left {
                        "本机已退出".to_owned()
                    } else if !group.enabled {
                        "已关闭".to_owned()
                    } else {
                        group.status.clone()
                    }));
                let members = group.id.clone();
                let mut actions = div().flex().gap_3().child(
                    Button::new(SharedString::from(format!("members-{}", group.id)))
                        .label("管理成员")
                        .on_click(cx.listener(move |this, _, _, cx| {
                            this.overlay = Overlay::Members(members.clone());
                            cx.notify()
                        })),
                );
                if !group.left {
                    actions = actions.child(self.action(
                        format!("invite-{}", group.id),
                        "邀请设备",
                        Command::Invite(group.id.clone()),
                        true,
                    ));
                } else {
                    let id = group.id.clone();
                    actions = actions.child(
                        Button::new(SharedString::from(format!("delete-{}", id)))
                            .label("删除本机记录")
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.overlay = Overlay::Confirm(
                                    "仅删除本机记录？".into(),
                                    Command::DeleteGroup(id.clone()),
                                );
                                cx.notify()
                            })),
                    );
                }
                c = c.child(actions);
                view = view.child(c);
            }
            if s.groups.is_empty() {
                view = view.child(card().child(muted("尚无同步组")));
            }
        }
        view.into_any_element()
    }
    fn settings(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let mut view = div()
            .flex()
            .flex_col()
            .gap_5()
            .child(self.header("设置", "同步行为与系统集成"));
        let Some(s) = self.snapshot.clone() else {
            return view.into_any_element();
        };
        let mut settings = s.settings.clone();
        settings.auto_images = !settings.auto_images;
        let auto = s.settings.clone();
        view = view.child(
            card()
                .child(title("剪贴板"))
                .child(self.switch_row(
                    "auto-images",
                    "复制图片自动发送",
                    if s.settings.auto_images {
                        "复制图片后同步到已开启设备"
                    } else {
                        "自动发送已关闭，可用下方快捷键手动同步图片"
                    },
                    s.settings.auto_images,
                    move |value| {
                        let mut set = auto.clone();
                        set.auto_images = value;
                        Command::Settings(set)
                    },
                ))
                .child(
                    div()
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_3()
                        .child(
                            div()
                                .flex()
                                .flex_col()
                                .gap_2()
                                .child("手动上传快捷键")
                                .child(muted(hotkey(&s.settings))),
                        )
                        .child(
                            Button::new("record-hotkey")
                                .label(if self.recording {
                                    "请按组合键…"
                                } else {
                                    "设置快捷键"
                                })
                                .on_click(cx.listener(|this, _, window, cx| {
                                    this.recording = true;
                                    this.focus.focus(window);
                                    cx.notify()
                                })),
                        )
                        .child({
                            let mut settings = s.settings.clone();
                            settings.hotkey_key = 0;
                            settings.hotkey_modifiers = 0;
                            self.action("clear-hotkey", "清除", Command::Settings(settings), false)
                        }),
                ),
        );
        view = view.child(
            card()
                .child(title("检查间隔"))
                .child(muted("较短间隔会增加请求，WebDAV 请留意服务商限频。"))
                .child(
                    div()
                        .flex()
                        .gap_5()
                        .child(
                            div()
                                .flex_1()
                                .child(self.field("文本轮询 · 秒", "textInterval")),
                        )
                        .child(
                            div()
                                .flex_1()
                                .child(self.field("附件检查 · 秒", "attachmentInterval")),
                        ),
                )
                .child(
                    Button::new("save-intervals")
                        .label("保存间隔")
                        .primary()
                        .on_click(cx.listener(|this, _, _, cx| {
                            let a = this.value("textInterval", cx).parse::<u64>();
                            let b = this.value("attachmentInterval", cx).parse::<u64>();
                            if let (Ok(a), Ok(b), Some(s)) = (a, b, &this.snapshot) {
                                if (1..=300).contains(&a) && (1..=300).contains(&b) {
                                    let mut settings = s.settings.clone();
                                    settings.text_interval = a;
                                    settings.attachment_interval = b;
                                    this.send(Command::Settings(settings));
                                    this.notice = "已保存检查间隔".into();
                                } else {
                                    this.notice = "请输入 1–300 秒".into()
                                }
                            } else {
                                this.notice = "请输入整数秒数".into()
                            }
                            cx.notify()
                        })),
                ),
        );
        view = view.child(
            card()
                .child(title("系统集成"))
                .child(self.switch_row(
                    "startup",
                    "开机自启动",
                    "登录 Windows 后在托盘运行",
                    s.startup,
                    Command::Startup,
                ))
                .child(self.switch_row(
                    "shell",
                    "资源管理器右键发送",
                    "添加“通过 ClipBridge 发送”",
                    s.shell,
                    Command::Shell,
                ))
                .child(
                    div()
                        .py_3()
                        .flex()
                        .flex_col()
                        .gap_3()
                        .child("文件接收目录")
                        .child(muted(
                            s.settings.receive_directory.to_string_lossy().into_owned(),
                        ))
                        .child(self.action(
                            "directory",
                            "更改目录",
                            Command::ChooseDirectory,
                            false,
                        )),
                ),
        );
        view.into_any_element()
    }
    fn modal(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let overlay = self.overlay.clone();
        let mut panel = card()
            .w(px(570.))
            .max_h(relative(0.90))
            .shadow_lg()
            .p_7()
            .gap_5()
            .id("modal-content")
            .overflow_y_scroll();
        let close = Button::new("close-modal")
            .ghost()
            .icon(IconName::Close)
            .on_click(cx.listener(|this, _, _, cx| {
                if matches!(this.overlay, Overlay::Pair) {
                    this.send(Command::CancelPairing);
                    this.qr = None;
                    this.qr_link.clear();
                }
                this.overlay = Overlay::None;
                cx.notify()
            }));
        match overlay {
            Overlay::None => return div().into_any_element(),
            Overlay::Form(joining) => {
                panel = panel.child(
                    div()
                        .flex()
                        .justify_between()
                        .items_center()
                        .child(title(if joining {
                            "加入同步组"
                        } else {
                            "创建同步组"
                        }))
                        .child(close),
                );
                let mut choice = div().flex().gap_3();
                for (dav, label) in [(false, "专用中转"), (true, "WebDAV")] {
                    let b = Button::new(if dav { "dav" } else { "relay" })
                        .label(label)
                        .on_click(cx.listener(move |this, _, _, cx| {
                            this.dav = dav;
                            cx.notify()
                        }));
                    choice = choice.child(if self.dav == dav {
                        b.primary().into_any_element()
                    } else {
                        b.into_any_element()
                    });
                }
                panel = panel.child(choice);
                if !joining {
                    panel = panel.child(self.field("同步组名称", "name"));
                }
                panel = panel.child(self.field(
                    if self.dav {
                        "WebDAV目录"
                    } else {
                        "服务器地址"
                    },
                    "server",
                ));
                if self.dav {
                    panel = panel
                        .child(self.field("WebDAV账号", "username"))
                        .child(self.field("WebDAV密码", "password"));
                } else if !joining {
                    panel = panel.child(self.field("服务器管理码", "setup"));
                }
                if joining {
                    panel = panel.child(self.field("配对码或完整邀请", "code"));
                }
                panel = panel.child(
                    Button::new("submit-pair")
                        .label(if joining {
                            "连接邀请"
                        } else {
                            "创建并邀请"
                        })
                        .primary()
                        .w_full()
                        .on_click(cx.listener(move |this, _, _, cx| {
                            let connection = Connection {
                                transport: if this.dav { "webdav" } else { "relay" }.into(),
                                server: this.value("server", cx).trim().into(),
                                username: this.value("username", cx).trim().into(),
                                password: this.value("password", cx),
                            };
                            if joining {
                                this.send(Command::Join {
                                    connection,
                                    code: this.value("code", cx).trim().into(),
                                });
                            } else {
                                let name = this.value("name", cx);
                                this.send(Command::Create {
                                    name: if name.trim().is_empty() {
                                        "我的设备".into()
                                    } else {
                                        name
                                    },
                                    connection,
                                    setup: this.value("setup", cx),
                                });
                            }
                            this.notice.clear();
                            cx.notify()
                        })),
                );
            }
            Overlay::Pair => {
                panel = panel.child(
                    div()
                        .flex()
                        .justify_between()
                        .items_center()
                        .child(title("设备配对"))
                        .child(close),
                );
                if let Some(pair) = self.snapshot.as_ref().and_then(|s| s.paired.clone()) {
                    panel = panel.child(muted(pair.status.clone()));
                    if !pair.peer.is_empty() {
                        panel = panel.child(title(pair.peer.clone()));
                    }
                    if pair.host && !pair.done {
                        if let Some(qr) = &self.qr {
                            panel = panel.child(
                                div()
                                    .flex()
                                    .justify_center()
                                    .child(img(qr.clone()).w(px(230.)).h(px(230.)).rounded_lg()),
                            );
                        }
                        panel = panel
                            .child(
                                div()
                                    .text_center()
                                    .text_xl()
                                    .font_weight(FontWeight::SEMIBOLD)
                                    .child(pair.code.clone()),
                            )
                            .child(
                                div()
                                    .flex()
                                    .justify_center()
                                    .gap_3()
                                    .child({
                                        let code = pair.code.clone();
                                        Button::new("copy-code").label("复制配对码").on_click(
                                            move |_, _, _| {
                                                let _ = platform::set_text(&code);
                                            },
                                        )
                                    })
                                    .child({
                                        let link = pair.link.clone();
                                        Button::new("copy-link").label("复制邀请").on_click(
                                            move |_, _, _| {
                                                let _ = platform::set_text(&link);
                                            },
                                        )
                                    }),
                            );
                    }
                    if !pair.safety.is_empty() {
                        panel = panel.child(
                            div()
                                .p_5()
                                .rounded_xl()
                                .bg(rgb(0xe1f3e9))
                                .flex()
                                .flex_col()
                                .items_center()
                                .gap_3()
                                .child(muted("请核对两端安全码"))
                                .child(
                                    div()
                                        .text_3xl()
                                        .font_weight(FontWeight::BOLD)
                                        .child(pair.safety),
                                ),
                        );
                    }
                    if pair.ready && !pair.approved {
                        panel = panel.child(self.action(
                            "approve",
                            "安全码一致，确认",
                            Command::Approve,
                            true,
                        ));
                    }
                    if pair.done {
                        panel =
                            panel.child(Button::new("pair-done").label("完成").primary().on_click(
                                cx.listener(|this, _, _, cx| {
                                    this.overlay = Overlay::None;
                                    this.send(Command::CancelPairing);
                                    this.qr = None;
                                    this.qr_link.clear();
                                    cx.notify()
                                }),
                            ));
                    }
                }
            }
            Overlay::Members(id) => {
                if let Some(group) = self
                    .snapshot
                    .as_ref()
                    .and_then(|s| s.groups.iter().find(|g| g.id == id))
                    .cloned()
                {
                    panel = panel.child(
                        div()
                            .flex()
                            .justify_between()
                            .items_center()
                            .child(title(group.name.clone()))
                            .child(close),
                    );
                    for member in &group.members {
                        let group_id = group.id.clone();
                        let member_id = member.id.clone();
                        let question = if member.own {
                            "退出这个同步组？"
                        } else {
                            "将此设备移出同步组？"
                        };
                        panel = panel.child(
                            div()
                                .flex()
                                .items_center()
                                .justify_between()
                                .gap_3()
                                .py_2()
                                .child(
                                    div().flex_1().child(member.name.clone()).child(muted(
                                        if member.own { "本机" } else { "已配对" },
                                    )),
                                )
                                .child(
                                    Button::new(SharedString::from(format!(
                                        "remove-{}",
                                        member.id
                                    )))
                                    .label(if member.own { "退出" } else { "移出" })
                                    .ghost()
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        this.overlay = Overlay::Confirm(
                                            question.into(),
                                            Command::RemoveMember(
                                                group_id.clone(),
                                                member_id.clone(),
                                            ),
                                        );
                                        cx.notify()
                                    })),
                                ),
                        );
                    }
                    panel = panel.child(self.action(
                        "refresh-members",
                        "刷新成员",
                        Command::Refresh(group.id.clone()),
                        false,
                    ));
                    if !group.left {
                        panel = panel.child(self.action(
                            "invite-member",
                            "邀请设备",
                            Command::Invite(group.id),
                            true,
                        ));
                    }
                }
            }
            Overlay::Send(paths, selected) => {
                panel = panel
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .items_center()
                            .child(title("发送文件"))
                            .child(close),
                    )
                    .child(muted("对方同意后上传 · 单文件上限 100 MB"));
                for path in paths.iter().take(4) {
                    panel = panel.child(
                        div().p_3().rounded_lg().bg(rgb(BG)).child(
                            path.file_name()
                                .unwrap_or_default()
                                .to_string_lossy()
                                .into_owned(),
                        ),
                    );
                }
                if paths.len() > 4 {
                    panel = panel.child(muted(format!("另有 {} 个文件", paths.len() - 4)));
                }
                panel = panel.child(title("接收设备"));
                let targets = self
                    .snapshot
                    .as_ref()
                    .map(|s| s.targets.clone())
                    .unwrap_or_default();
                if targets.is_empty() {
                    panel = panel.child(muted("没有已开启的接收设备"));
                }
                for target in targets {
                    let paths = paths.clone();
                    let id = target.id.clone();
                    let label = format!(
                        "{}{}",
                        if selected == target.id { "✓  " } else { "" },
                        target.name
                    );
                    panel = panel.child(
                        Button::new(SharedString::from(format!("target-{}", target.id)))
                            .label(label)
                            .w_full()
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.overlay = Overlay::Send(paths.clone(), id.clone());
                                cx.notify()
                            })),
                    );
                }
                let paths = paths.clone();
                panel = panel.child(
                    Button::new("send-request")
                        .label("发送请求")
                        .primary()
                        .disabled(selected.is_empty())
                        .on_click(cx.listener(move |this, _, _, cx| {
                            this.send(Command::StageFiles {
                                paths: paths.clone(),
                                peer: selected.clone(),
                            });
                            this.overlay = Overlay::None;
                            cx.notify()
                        })),
                );
            }
            Overlay::Confirm(question, command) => {
                panel = panel
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .child(title("确认操作"))
                            .child(close),
                    )
                    .child(question)
                    .child(muted("移出通知传播后，其他成员会停止向该设备发送新内容。"))
                    .child(
                        Button::new("confirm-action")
                            .label("确认")
                            .danger()
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.send(command.clone());
                                this.overlay = Overlay::None;
                                cx.notify()
                            })),
                    );
            }
        }
        let notice = if !self.notice.is_empty() {
            self.notice.clone()
        } else {
            self.snapshot
                .as_ref()
                .map(|s| s.notice.clone())
                .unwrap_or_default()
        };
        if !notice.is_empty() {
            panel = panel.child(div().text_sm().text_color(rgb(0xb35c3d)).child(notice));
        }
        div()
            .absolute()
            .inset_0()
            .bg(rgba(0x17362c66))
            .flex()
            .items_center()
            .justify_center()
            .child(panel)
            .into_any_element()
    }
}
pub fn hotkey(settings: &Settings) -> String {
    if settings.hotkey_key == 0 {
        return "未设置".into();
    }
    let mut names = vec![];
    if settings.hotkey_modifiers & 2 != 0 {
        names.push("Ctrl".into())
    }
    if settings.hotkey_modifiers & 1 != 0 {
        names.push("Alt".into())
    }
    if settings.hotkey_modifiers & 4 != 0 {
        names.push("Shift".into())
    }
    names.push(match settings.hotkey_key {
        0x30..=0x5a => char::from_u32(settings.hotkey_key)
            .unwrap_or('?')
            .to_string(),
        0x70..=0x87 => format!("F{}", settings.hotkey_key - 0x6f),
        v => format!("Key {v}"),
    });
    names.join(" + ")
}
impl Render for Desktop {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let mut nav = div()
            .w(px(208.))
            .h_full()
            .flex_shrink_0()
            .bg(rgb(0xffffff))
            .border_r_1()
            .border_color(rgb(LINE))
            .p_5()
            .flex()
            .flex_col()
            .gap_3()
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .mt_3()
                    .mb_8()
                    .child(img(self.logo.clone()).size_8())
                    .child(
                        div()
                            .text_lg()
                            .font_weight(FontWeight::BOLD)
                            .child("ClipBridge"),
                    ),
            );
        for (index, label, icon) in [
            (0, "状态", IconName::LayoutDashboard),
            (1, "同步组", IconName::Frame),
            (2, "设置", IconName::Settings2),
        ] {
            let selected = self.page == index;
            nav = nav.child(
                Button::new(SharedString::from(format!("nav-{index}")))
                    .label(label)
                    .icon(icon)
                    .w_full()
                    .h(px(46.))
                    .when(selected, |b| b.primary())
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.page = index;
                        cx.notify()
                    })),
            );
        }
        nav = nav
            .child(div().flex_1())
            .child(muted(
                self.snapshot
                    .as_ref()
                    .map(|s| s.own_name.clone())
                    .unwrap_or_default(),
            ))
            .child(muted("0.7.0"));
        let body = match self.page {
            0 => self.status(cx),
            1 => self.groups(cx),
            _ => self.settings(cx),
        };
        let notice = if !self.notice.is_empty() {
            self.notice.clone()
        } else {
            self.snapshot
                .as_ref()
                .map(|s| s.notice.clone())
                .unwrap_or_default()
        };
        let mut content = div()
            .id("page-scroll")
            .flex_1()
            .min_w_0()
            .h_full()
            .overflow_y_scroll()
            .p_8()
            .child(div().max_w(px(1000.)).mx_auto().child(body));
        if !notice.is_empty() {
            content = content.child(
                div()
                    .mt_4()
                    .p_4()
                    .rounded_lg()
                    .bg(rgb(0xfff3e7))
                    .text_sm()
                    .child(notice),
            );
        }
        let mut root = div()
            .relative()
            .size_full()
            .flex()
            .bg(rgb(BG))
            .text_color(rgb(INK))
            .font_family("Microsoft YaHei UI")
            .text_base()
            .track_focus(&self.focus)
            .on_key_down(cx.listener(|this, event: &KeyDownEvent, _, cx| {
                if !this.recording {
                    return;
                }
                let key = event.keystroke.key.to_ascii_uppercase();
                if key == "ESCAPE" {
                    this.recording = false;
                    cx.notify();
                    return;
                }
                let modifiers = &event.keystroke.modifiers;
                let vk = if key.len() == 1 {
                    key.as_bytes()[0] as u32
                } else if let Some(f) = key.strip_prefix('F').and_then(|f| f.parse::<u32>().ok()) {
                    if (1..=24).contains(&f) { 0x6f + f } else { 0 }
                } else {
                    0
                };
                if vk != 0 && (modifiers.control || modifiers.alt || modifiers.shift) {
                    if let Some(snapshot) = &this.snapshot {
                        let mut settings = snapshot.settings.clone();
                        settings.hotkey_key = vk;
                        settings.hotkey_modifiers = if modifiers.control { 2 } else { 0 }
                            | if modifiers.alt { 1 } else { 0 }
                            | if modifiers.shift { 4 } else { 0 };
                        this.send(Command::Settings(settings));
                        this.recording = false;
                        cx.notify();
                    }
                }
            }))
            .child(nav)
            .child(content);
        if !matches!(self.overlay, Overlay::None) {
            root = root.child(self.modal(cx));
        }
        root
    }
}
pub fn configure(cx: &mut App) {
    gpui_component::init(cx);
    Theme::change(ThemeMode::Light, None, cx);
    let theme = Theme::global_mut(cx);
    theme.font_family = "Microsoft YaHei UI".into();
    theme.radius = px(10.);
    theme.radius_lg = px(20.);
    theme.colors.primary = rgb(GREEN).into();
    theme.colors.primary_foreground = rgb(0xffffff).into();
}
