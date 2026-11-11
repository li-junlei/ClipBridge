#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use clipbridge_desktop::{
    platform,
    runtime::{self, Command},
    ui::{self, Desktop},
};
use gpui::{prelude::*, *};
use gpui_component::Root;
use std::path::PathBuf;
fn main() {
    if let Err(e) = run() {
        let text: Vec<u16> = format!("ClipBridge 无法启动：{e}")
            .encode_utf16()
            .chain([0])
            .collect();
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::MessageBoxW(
                None,
                windows::core::PCWSTR(text.as_ptr()),
                windows::core::w!("ClipBridge"),
                windows::Win32::UI::WindowsAndMessaging::MB_ICONERROR,
            );
        }
    }
}
fn run() -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args
        .first()
        .is_some_and(|a| a == "--unregister-notifications")
    {
        platform::unregister_notifications()?;
        return Ok(());
    }
    let data_dir = args
        .iter()
        .position(|a| a == "--data-dir")
        .filter(|index| *index == 0)
        .and_then(|i| args.get(i + 1))
        .map(PathBuf::from);
    let instance_key = clipbridge_desktop::crypto::hash(
        data_dir
            .as_ref()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or(std::env::var("LOCALAPPDATA")?),
    );
    let Some(_guard) = clipbridge_desktop::instance::acquire(&instance_key, &args)? else {
        return Ok(());
    };
    if args.first().is_some_and(|a| a == "--quit") {
        return Ok(());
    }
    let _installation = clipbridge_desktop::instance::installation_guard()?;
    let (events, rx) = async_channel::unbounded();
    let tx = runtime::launch(events, data_dir);
    clipbridge_desktop::instance::listen(&instance_key, tx.clone());
    platform::start(tx.clone());
    let _ = platform::register_notifications();
    let startup = args.first().is_some_and(|a| a == "--startup");
    let commands = tx.clone();
    let arguments = args.clone();
    Application::new()
        .with_assets(gpui_component_assets::Assets)
        .run(move |cx: &mut App| {
            ui::configure(cx);
            let bounds = Bounds::centered(None, size(px(1080.), px(780.)), cx);
            let tx = tx.clone();
            let rx = rx.clone();
            let result = cx.open_window(
                WindowOptions {
                    window_bounds: Some(WindowBounds::Windowed(bounds)),
                    titlebar: Some(TitlebarOptions {
                        title: Some("ClipBridge".into()),
                        ..Default::default()
                    }),
                    window_min_size: Some(size(px(860.), px(640.))),
                    show: !startup,
                    ..Default::default()
                },
                move |window, cx| {
                    platform::attach_window(window);
                    window.on_window_should_close(cx, |_, _| {
                        platform::show(false);
                        false
                    });
                    let desktop = cx.new(|cx| Desktop::new(tx, rx, window, cx));
                    cx.new(|cx| Root::new(desktop, window, cx))
                },
            );
            if let Err(error) = result {
                let message: Vec<u16> = format!("无法创建图形窗口：{error}")
                    .encode_utf16()
                    .chain([0])
                    .collect();
                unsafe {
                    windows::Win32::UI::WindowsAndMessaging::MessageBoxW(
                        None,
                        windows::core::PCWSTR(message.as_ptr()),
                        windows::core::w!("ClipBridge"),
                        windows::Win32::UI::WindowsAndMessaging::MB_ICONERROR,
                    );
                }
                cx.quit();
                return;
            }
            let _ = commands.send(Command::Arguments(arguments.clone()));
        });
    Ok(())
}
