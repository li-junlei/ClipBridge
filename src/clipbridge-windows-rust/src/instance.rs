use anyhow::{Result, ensure};
use std::{ffi::c_void, io::Write};
use windows::{
    Win32::{
        Foundation::*,
        Security::{Authorization::*, *},
        Storage::FileSystem::*,
        System::{Pipes::*, Threading::*},
    },
    core::PCWSTR,
};
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain([0]).collect()
}
pub struct Guard(HANDLE);
impl Drop for Guard {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
pub fn acquire(key: &str, args: &[String]) -> Result<Option<Guard>> {
    unsafe {
        let name = wide(&format!("Local\\ClipBridge.Rust.{key}"));
        let handle = CreateMutexW(None, false, PCWSTR(name.as_ptr()))?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let _ = CloseHandle(handle);
            let pipe = format!(r"\\.\pipe\ClipBridge.Rust.{key}");
            let message = serde_json::to_vec(args)?;
            ensure!(message.len() < 65536, "启动参数过长");
            for _ in 0..30 {
                if let Ok(mut stream) = std::fs::OpenOptions::new().write(true).open(&pipe) {
                    stream.write_all(&message)?;
                    return Ok(None);
                }
                std::thread::sleep(std::time::Duration::from_millis(100));
            }
            anyhow::bail!("ClipBridge 已在运行，但暂时无法响应，请稍后重试")
        }
        Ok(Some(Guard(handle)))
    }
}
fn descriptor() -> Result<PSECURITY_DESCRIPTOR> {
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)?;
        let guard = Guard(token);
        let mut size = 0;
        let _ = GetTokenInformation(guard.0, TokenUser, None, 0, &mut size);
        let mut bytes = vec![0u8; size as usize];
        GetTokenInformation(
            guard.0,
            TokenUser,
            Some(bytes.as_mut_ptr() as *mut c_void),
            size,
            &mut size,
        )?;
        let user = &*(bytes.as_ptr() as *const TOKEN_USER);
        let mut sid = windows::core::PWSTR::null();
        ConvertSidToStringSidW(user.User.Sid, &mut sid)?;
        let sid_string = sid.to_string()?;
        LocalFree(Some(HLOCAL(sid.0 as *mut c_void)));
        let sddl = wide(&format!("D:P(A;;GA;;;SY)(A;;GA;;;{sid_string})"));
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut descriptor,
            None,
        )?;
        Ok(descriptor)
    }
}
pub fn listen(key: &str, tx: tokio::sync::mpsc::UnboundedSender<crate::runtime::Command>) {
    let key = key.to_owned();
    std::thread::spawn(move || {
        let result: Result<()> = (|| {
            let descriptor = descriptor()?;
            let security = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor.0,
                bInheritHandle: false.into(),
            };
            let name = wide(&format!(r"\\.\pipe\ClipBridge.Rust.{key}"));
            loop {
                unsafe {
                    let pipe = CreateNamedPipeW(
                        PCWSTR(name.as_ptr()),
                        PIPE_ACCESS_INBOUND,
                        PIPE_TYPE_BYTE
                            | PIPE_READMODE_BYTE
                            | PIPE_WAIT
                            | PIPE_REJECT_REMOTE_CLIENTS,
                        1,
                        65536,
                        65536,
                        0,
                        Some(&security),
                    );
                    ensure!(pipe != INVALID_HANDLE_VALUE, "无法创建本地控制通道");
                    let guard = Guard(pipe);
                    let connected = ConnectNamedPipe(pipe, None);
                    if connected.is_ok() || GetLastError() == ERROR_PIPE_CONNECTED {
                        let mut bytes = Vec::new();
                        let mut buffer = [0u8; 4096];
                        loop {
                            let mut read = 0;
                            let result = ReadFile(pipe, Some(&mut buffer), Some(&mut read), None);
                            if result.is_err() || read == 0 {
                                break;
                            }
                            bytes.extend_from_slice(&buffer[..read as usize]);
                            if bytes.len() > 65536 {
                                break;
                            }
                        }
                        if bytes.len() <= 65536 {
                            if let Ok(args) = serde_json::from_slice::<Vec<String>>(&bytes) {
                                let _ = tx.send(crate::runtime::Command::Arguments(args));
                            }
                        }
                    }
                    let _ = DisconnectNamedPipe(pipe);
                    drop(guard);
                }
            }
        })();
        if let Err(e) = result {
            let _ = tx.send(crate::runtime::Command::Notice(format!(
                "单实例通道不可用：{e}"
            )));
        }
    });
}

pub fn installation_guard() -> Result<Guard> {
    unsafe {
        Ok(Guard(CreateMutexW(
            None,
            false,
            windows::core::w!("Local\\ClipBridge.Install"),
        )?))
    }
}
