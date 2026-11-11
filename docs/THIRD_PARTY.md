# Third-party notices

QRCoder 1.7.0: MIT, https://github.com/Shane32/QRCoder
Copyright Raffael Herrmann and Shane Krueger. License: https://github.com/Shane32/QRCoder/blob/master/LICENSE

Miuix 0.9.2: Apache-2.0, https://github.com/compose-miuix-ui/miuix
ZXing Android Embedded 4.3.0: Apache-2.0, https://github.com/journeyapps/zxing-android-embedded
Apache license: https://www.apache.org/licenses/LICENSE-2.0

OkHttp 4.12.0 and Okio: Apache-2.0, https://github.com/square/okhttp and https://github.com/square/okio
Kotlin standard library: Apache-2.0, https://github.com/JetBrains/kotlin

SyncClipboard Xposed was consulted as a visual reference; no project source files were copied.

Microsoft.Toolkit.Uwp.Notifications 7.1.3: MIT, https://github.com/CommunityToolkit/WindowsCommunityToolkit
License included in LICENSE-TOAST-TOOLKIT.md.

BouncyCastle.Cryptography 2.7.0 (.NET) and bcprov-jdk18on 1.86 (Android): Bouncy Castle MIT license, https://www.bouncycastle.org/
License included in LICENSE-BOUNCYCASTLE.md. J-PAKE uses the libraries’ NIST 3072-bit group and SHA-256 implementation.


## Windows Rust / GPUI

0.7.0 Windows 使用 GPUI 0.2.2、GPUI Component 0.5.1（Apache-2.0）、RustCrypto AES-GCM / P-256 / HKDF / HMAC、reqwest、tokio、arboard、tray-icon、rfd 和 Windows API crate。锁定版本见 `src/clipbridge-windows-rust/Cargo.lock`。安装包随附 Rust 依赖的许可声明和许可文本，位于 `licenses/RUST-THIRD-PARTY-NOTICES.txt`。

J-PAKE 采用现有协议的 NIST 3072-bit 群参数及 Bouncy Castle 兼容编码，以跨语言测试核对证明、密钥确认和安全码；没有切换到不兼容的自定义配对协议。
