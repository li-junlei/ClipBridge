use anyhow::{Result, ensure};
use std::{
    ffi::c_void,
    path::{Path, PathBuf},
    sync::{
        OnceLock,
        atomic::{AtomicIsize, AtomicU32, Ordering},
    },
};
use windows::{
    Win32::{
        Foundation::*,
        Security::Cryptography::*,
        System::{DataExchange::*, LibraryLoader::GetModuleHandleW},
        UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
    },
    core::{HSTRING, PCWSTR, w},
};
thread_local! {static PAUSE_MENU:std::cell::RefCell<Option<tray_icon::menu::MenuItem>>=const{std::cell::RefCell::new(None)};}
pub static CLIP_SEQUENCE: AtomicU32 = AtomicU32::new(0);
thread_local! { static REGISTERED_HOTKEY: std::cell::Cell<(u32,u32)> = const { std::cell::Cell::new((0,0)) }; }
static WINDOW: AtomicIsize = AtomicIsize::new(0);
static EVENTS: OnceLock<tokio::sync::mpsc::UnboundedSender<crate::runtime::Command>> =
    OnceLock::new();
pub fn directory() -> Result<PathBuf> {
    let root = PathBuf::from(std::env::var("LOCALAPPDATA")?).join("ClipBridgeRust");
    std::fs::create_dir_all(&root)?;
    Ok(root)
}
pub fn protect(data: &[u8], seal: bool) -> Result<Vec<u8>> {
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: data.len().try_into()?,
            pbData: data.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        if seal {
            CryptProtectData(
                &input,
                None,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )?
        } else {
            CryptUnprotectData(
                &input,
                None,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )?
        }
        let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        LocalFree(Some(HLOCAL(output.pbData as *mut c_void)));
        Ok(bytes)
    }
}
pub fn write_private(path: &Path, plain: &[u8]) -> Result<()> {
    use std::io::Write;
    let cipher = protect(plain, true)?;
    let tmp = path.with_extension("tmp");
    {
        let mut f = std::fs::File::create(&tmp)?;
        f.write_all(&cipher)?;
        f.sync_all()?
    }
    unsafe {
        use windows::Win32::Storage::FileSystem::{
            MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
        };
        let from: Vec<u16> = tmp
            .as_os_str()
            .to_string_lossy()
            .encode_utf16()
            .chain([0])
            .collect();
        let to: Vec<u16> = path
            .as_os_str()
            .to_string_lossy()
            .encode_utf16()
            .chain([0])
            .collect();
        MoveFileExW(
            PCWSTR(from.as_ptr()),
            PCWSTR(to.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )?;
    }
    Ok(())
}
pub fn sequence() -> u32 {
    unsafe { GetClipboardSequenceNumber() }
}
pub fn set_text(text: &str) -> Result<()> {
    arboard::Clipboard::new()?.set_text(text)?;
    CLIP_SEQUENCE.store(sequence(), Ordering::SeqCst);
    Ok(())
}
pub fn set_image(path: &Path) -> Result<()> {
    let (width, height) = image::ImageReader::open(path)?
        .with_guessed_format()?
        .into_dimensions()?;
    ensure!(
        (width as u64) * (height as u64) <= 20_000_000,
        "图片超过 2000 万像素"
    );
    let mut reader = image::ImageReader::open(path)?.with_guessed_format()?;
    let mut limits = image::Limits::default();
    limits.max_alloc = Some(96 * 1024 * 1024);
    reader.limits(limits);
    let image = reader.decode()?.into_rgba8();
    ensure!(
        (image.width() as u64) * (image.height() as u64) <= 20_000_000,
        "图片过大"
    );
    arboard::Clipboard::new()?.set_image(arboard::ImageData {
        width: image.width() as usize,
        height: image.height() as usize,
        bytes: std::borrow::Cow::Owned(image.into_raw()),
    })?;
    CLIP_SEQUENCE.store(sequence(), Ordering::SeqCst);
    Ok(())
}
pub enum Clip {
    Text(String),
    Image(Vec<u8>),
}
pub fn clipboard(images: bool) -> Result<Option<Clip>> {
    let mut c = arboard::Clipboard::new()?;
    if images {
        if let Ok(image) = c.get_image() {
            ensure!(
                image.width * image.height <= 20_000_000,
                "图片超过 2000 万像素，请作为文件发送"
            );
            let rgba = image::RgbaImage::from_raw(
                image.width as u32,
                image.height as u32,
                image.bytes.into_owned(),
            )
            .ok_or_else(|| anyhow::anyhow!("图片无效"))?;
            let mut png = std::io::Cursor::new(vec![]);
            image::DynamicImage::ImageRgba8(rgba).write_to(&mut png, image::ImageFormat::Png)?;
            return Ok(Some(Clip::Image(png.into_inner())));
        }
    }
    if let Ok(text) = c.get_text() {
        if !text.starts_with("clipbridge://") {
            return Ok(Some(Clip::Text(text)));
        }
    }
    Ok(None)
}
fn exe() -> Result<String> {
    Ok(std::env::current_exe()?.to_string_lossy().into_owned())
}
pub fn startup() -> bool {
    use winreg::{RegKey, enums::*};
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run")
        .and_then(|k| k.get_value::<String, _>("ClipBridge"))
        .is_ok()
}
pub fn set_startup(value: bool) -> Result<()> {
    use winreg::{RegKey, enums::*};
    let (k, _) = RegKey::predef(HKEY_CURRENT_USER)
        .create_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run")?;
    if value {
        k.set_value("ClipBridge", &format!("\"{}\" --startup", exe()?))?
    } else {
        let _ = k.delete_value("ClipBridge");
    }
    Ok(())
}
pub fn shell() -> bool {
    use winreg::{RegKey, enums::*};
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Classes\*\shell\ClipBridge\command")
        .is_ok()
}
pub fn set_shell(value: bool) -> Result<()> {
    use winreg::{RegKey, enums::*};
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let path = r"Software\Classes\*\shell\ClipBridge";
    if !value {
        let _ = root.delete_subkey_all(path);
        return Ok(());
    }
    let (k, _) = root.create_subkey(path)?;
    k.set_value("", &"通过 ClipBridge 发送")?;
    k.set_value("Icon", &exe()?)?;
    k.set_value("MultiSelectModel", &"Player")?;
    let (command, _) = k.create_subkey("command")?;
    command.set_value("", &format!("\"{}\" --send \"%1\"", exe()?))?;
    Ok(())
}
fn escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
pub fn register_notifications() -> Result<()> {
    use winreg::{RegKey, enums::*};
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let (k, _) = root.create_subkey(r"Software\Classes\AppUserModelId\ClipBridge.Desktop")?;
    k.set_value("DisplayName", &"ClipBridge")?;
    k.set_value("IconUri", &exe()?)?;
    let (protocol, _) = root.create_subkey(r"Software\Classes\clipbridge-rust")?;
    protocol.set_value("", &"URL:ClipBridge")?;
    protocol.set_value("URL Protocol", &"")?;
    let (command, _) = protocol.create_subkey(r"shell\open\command")?;
    command.set_value("", &format!("\"{}\" --notification \"%1\"", exe()?))?;
    unsafe {
        windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID(w!(
            "ClipBridge.Desktop"
        ))?
    }
    Ok(())
}
pub fn notify(tag: &str, title: &str, message: &str, actions: &[(&str, String)]) -> Result<()> {
    unsafe {
        let _ = windows::Win32::System::WinRT::RoInitialize(
            windows::Win32::System::WinRT::RO_INIT_MULTITHREADED,
        );
    }
    use windows::{
        Data::Xml::Dom::XmlDocument,
        UI::Notifications::{ToastNotification, ToastNotificationManager},
    };
    let actions = if actions.is_empty() {
        String::new()
    } else {
        format!(
            "<actions>{}</actions>",
            actions
                .iter()
                .map(|(title, url)| format!(
                    "<action content=\"{}\" arguments=\"{}\" activationType=\"protocol\"/>",
                    escape(title),
                    escape(url)
                ))
                .collect::<String>()
        )
    };
    let xml = XmlDocument::new()?;
    xml.LoadXml(&HSTRING::from(format!("<toast><visual><binding template=\"ToastGeneric\"><text>{}</text><text>{}</text></binding></visual>{actions}</toast>",escape(title),escape(message))))?;
    let toast = ToastNotification::CreateToastNotification(&xml)?;
    toast.SetTag(&HSTRING::from(tag.chars().take(16).collect::<String>()))?;
    ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from("ClipBridge.Desktop"))?
        .Show(&toast)?;
    Ok(())
}
pub fn open_file(path: &Path, folder: bool) -> Result<()> {
    ensure!(path.is_file(), "文件已移动或删除");
    if folder {
        std::process::Command::new("explorer.exe")
            .arg(format!("/select,{}", path.display()))
            .spawn()?;
    } else {
        let value: Vec<u16> = path
            .as_os_str()
            .to_string_lossy()
            .encode_utf16()
            .chain([0])
            .collect();
        unsafe {
            let result = windows::Win32::UI::Shell::ShellExecuteW(
                None,
                w!("open"),
                PCWSTR(value.as_ptr()),
                None,
                None,
                SW_SHOWNORMAL,
            );
            ensure!(result.0 as isize > 32, "找不到可打开此文件的程序")
        }
    }
    Ok(())
}
unsafe extern "system" fn proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if let Some(tx) = EVENTS.get() {
        match msg {
            n if n == WM_APP + 1 => {
                PAUSE_MENU.with(|menu| {
                    if let Some(item) = menu.borrow().as_ref() {
                        item.set_text(if wparam.0 == 1 {
                            "启用同步"
                        } else {
                            "暂停同步"
                        });
                    }
                });
            }
            WM_CLIPBOARDUPDATE => {
                let seq = sequence();
                if seq != CLIP_SEQUENCE.load(Ordering::SeqCst) {
                    let _ = tx.send(crate::runtime::Command::Clipboard(false));
                }
            }
            WM_HOTKEY => {
                let _ = tx.send(crate::runtime::Command::Clipboard(true));
            }
            WM_APP => unsafe {
                let modifiers = (wparam.0 & 0xffff) as u32;
                let key = lparam.0 as u32;
                let old = REGISTERED_HOTKEY.with(|value| value.get());
                let _ = UnregisterHotKey(Some(hwnd), 0xCB01);
                let success = key == 0
                    || RegisterHotKey(
                        Some(hwnd),
                        0xCB01,
                        HOT_KEY_MODIFIERS(modifiers | MOD_NOREPEAT.0),
                        key,
                    )
                    .is_ok();
                if success {
                    REGISTERED_HOTKEY.with(|value| value.set((modifiers, key)));
                } else if old.1 != 0 {
                    let _ = RegisterHotKey(
                        Some(hwnd),
                        0xCB01,
                        HOT_KEY_MODIFIERS(old.0 | MOD_NOREPEAT.0),
                        old.1,
                    );
                }
                let _ = tx.send(crate::runtime::Command::HotkeyApplied(
                    modifiers, key, success,
                ));
            },
            _ => {}
        }
    }
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}
pub fn set_hotkey(modifiers: u32, key: u32) -> Result<()> {
    let hwnd = WINDOW.load(Ordering::SeqCst);
    ensure!(hwnd != 0, "快捷键监听尚未启动");
    unsafe {
        PostMessageW(
            Some(HWND(hwnd as *mut c_void)),
            WM_APP,
            WPARAM(modifiers as usize),
            LPARAM(key as isize),
        )?
    }
    Ok(())
}
pub fn start(tx: tokio::sync::mpsc::UnboundedSender<crate::runtime::Command>) {
    std::thread::spawn(move || {
        let _ = EVENTS.set(tx.clone());
        unsafe {
            let instance = GetModuleHandleW(None).unwrap();
            let class = WNDCLASSW {
                lpfnWndProc: Some(proc),
                hInstance: instance.into(),
                lpszClassName: w!("ClipBridgeRustListener"),
                ..Default::default()
            };
            RegisterClassW(&class);
            let hwnd = CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                w!("ClipBridgeRustListener"),
                w!("ClipBridge"),
                WINDOW_STYLE::default(),
                0,
                0,
                0,
                0,
                Some(HWND_MESSAGE),
                None,
                Some(instance.into()),
                None,
            )
            .unwrap();
            WINDOW.store(hwnd.0 as isize, Ordering::SeqCst);
            let _ = AddClipboardFormatListener(hwnd);
            let menu = tray_icon::menu::Menu::new();
            let send = tray_icon::menu::MenuItem::new("发送文件", true, None);
            let pause = tray_icon::menu::MenuItem::new("暂停同步", true, None);
            PAUSE_MENU.with(|menu| *menu.borrow_mut() = Some(pause.clone()));
            let quit = tray_icon::menu::MenuItem::new("退出", true, None);
            let _ = menu.append_items(&[&send, &pause, &quit]);
            let send_id = send.id().clone();
            let pause_id = pause.id().clone();
            let quit_id = quit.id().clone();
            let commands = tx.clone();
            tray_icon::menu::MenuEvent::set_event_handler(Some(
                move |event: tray_icon::menu::MenuEvent| {
                    let command = if event.id == send_id {
                        crate::runtime::Command::ChooseFiles
                    } else if event.id == pause_id {
                        crate::runtime::Command::TogglePause
                    } else if event.id == quit_id {
                        crate::runtime::Command::Quit
                    } else {
                        return;
                    };
                    let _ = commands.send(command);
                },
            ));
            tray_icon::TrayIconEvent::set_event_handler(Some(move |event| {
                if matches!(event, tray_icon::TrayIconEvent::DoubleClick { .. }) {
                    let _ = tx.send(crate::runtime::Command::Open);
                }
            }));
            let png = image::load_from_memory(include_bytes!("../../ClipBridge.Windows/Tray.ico"))
                .unwrap()
                .into_rgba8();
            let (w, h) = png.dimensions();
            let icon = tray_icon::Icon::from_rgba(png.into_raw(), w, h).unwrap();
            let _tray = tray_icon::TrayIconBuilder::new()
                .with_tooltip("ClipBridge")
                .with_menu(Box::new(menu))
                .with_icon(icon)
                .build();
            let mut msg = MSG::default();
            while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            let _ = RemoveClipboardFormatListener(hwnd);
        }
    });
}

pub fn attach_window(window: &gpui::Window) {
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    if let Ok(handle) = HasWindowHandle::window_handle(window) {
        if let RawWindowHandle::Win32(win) = handle.as_raw() {
            APP_WINDOW.store(win.hwnd.get(), Ordering::SeqCst);
            unsafe {
                let module = GetModuleHandleW(None).unwrap();
                if let Ok(icon) = LoadImageW(
                    Some(module.into()),
                    PCWSTR(1usize as *const u16),
                    IMAGE_ICON,
                    32,
                    32,
                    LR_DEFAULTCOLOR,
                ) {
                    let hwnd = HWND(win.hwnd.get() as *mut c_void);
                    let _ = SendMessageW(
                        hwnd,
                        WM_SETICON,
                        Some(WPARAM(1)),
                        Some(LPARAM(icon.0 as isize)),
                    );
                    let _ = SendMessageW(
                        hwnd,
                        WM_SETICON,
                        Some(WPARAM(0)),
                        Some(LPARAM(icon.0 as isize)),
                    );
                }
            }
        }
    }
}
static APP_WINDOW: AtomicIsize = AtomicIsize::new(0);
pub fn show(visible: bool) {
    let handle = APP_WINDOW.load(Ordering::SeqCst);
    if handle != 0 {
        unsafe {
            let hwnd = HWND(handle as *mut c_void);
            let _ = ShowWindow(hwnd, if visible { SW_SHOW } else { SW_HIDE });
            if visible {
                let _ = SetForegroundWindow(hwnd);
            }
        }
    }
}

pub fn set_paused(paused: bool) {
    let handle = WINDOW.load(Ordering::SeqCst);
    if handle != 0 {
        unsafe {
            let _ = PostMessageW(
                Some(HWND(handle as *mut c_void)),
                WM_APP + 1,
                WPARAM(paused as usize),
                LPARAM(0),
            );
        }
    }
}

pub fn unregister_notifications() -> Result<()> {
    use winreg::{RegKey, enums::*};
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let current = exe()?;
    let app = r"Software\Classes\AppUserModelId\ClipBridge.Desktop";
    let owned = root
        .open_subkey(app)
        .and_then(|key| key.get_value::<String, _>("IconUri"))
        .map(|value| value.eq_ignore_ascii_case(&current))
        .unwrap_or(false);
    if owned {
        let _ = root.delete_subkey_all(app);
    }
    let protocol = r"Software\Classes\clipbridge-rust";
    let command = format!("\"{current}\" --notification \"%1\"");
    let owned = root
        .open_subkey(format!("{protocol}\\shell\\open\\command"))
        .and_then(|key| key.get_value::<String, _>(""))
        .map(|value| value.eq_ignore_ascii_case(&command))
        .unwrap_or(false);
    if owned {
        let _ = root.delete_subkey_all(protocol);
    }
    Ok(())
}
