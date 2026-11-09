<p align="center">
  <img src="assets/clipbridge.svg" width="112" alt="ClipBridge icon">
</p>
<h1 align="center">ClipBridge</h1>
<p align="center">Windows 与 Android 之间的自托管端到端加密剪贴板同步</p>
<p align="center">
  <a href="https://github.com/li-junlei/ClipBridge/releases"><img src="https://img.shields.io/github/v/release/li-junlei/ClipBridge?include_prereleases" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/status-preview-orange" alt="Preview">
</p>

ClipBridge 将 Windows 客户端、Android LSPosed 模块与 Docker 中转服务连接起来。剪贴板内容在发送端加密，在接收端解密；中转服务负责认证、路由和保存密文。

**当前版本为开发预览，仅同步纯文本。** 不兼容原版 SyncClipboard 协议，也不提供明文传输模式。

## 功能

- **双向文本同步**：支持 Unicode、多行和 UTF-8 64 KiB 文本，提供暂停、重连和剪贴板回环抑制。
- **端到端加密**：AES-256-GCM，随机配对密钥，认证消息头与持久化防重放状态；设备名使用独立加密通道。
- **扫码配对**：电脑填写服务器地址和配对管理码，手机扫描五分钟有效、一次性领取的二维码。
- **Windows 客户端**：安装程序、托盘、开机自启动、配置删除、设备名称和最后同步时间。
- **Android 模块**：Miuix 界面、模块状态、配对设备数、最后同步时间、配置删除和可隐藏的桌面入口。
- **自托管中转**：Docker Compose、独立设备令牌、请求限流、只读容器根文件系统和持久化数据卷。

## 下载与要求

从 [Releases](https://github.com/li-junlei/ClipBridge/releases) 下载：

| 组件 | 要求与发布形式 |
| --- | --- |
| Windows | Windows 10/11 x64；安装程序自带 .NET 运行环境 |
| Android | Android 10+ 是安装下限；需要可用的 LSPosed 或兼容框架，APK 同时包含管理界面和模块 |
| 中转服务 | Docker Compose，自行配置有效 HTTPS 域名与反向代理 |

Android 的安装下限不代表所有系统均已验证。剪贴板 Hook 依赖系统与框架实现，兼容性需要在目标设备确认。模块本身不请求 root、不调用 `su`；安装和运行 Hook 框架所需条件由所选框架决定。

预览版 Windows 安装包尚未代码签名，Android APK 使用开发签名。请仅从本仓库下载并核对 Release 中的 SHA-256 校验文件。

## 快速开始

### 1. 部署中转服务

```sh
git clone https://github.com/li-junlei/ClipBridge.git
cd ClipBridge
docker compose -f deploy/compose.yaml up -d --build
docker compose -f deploy/compose.yaml logs relay
```

服务默认仅绑定 `127.0.0.1:18787`，日志中显示服务器配对管理码。配置 HTTPS 反向代理，将例如 `https://clip.example.com` 转发至 `http://127.0.0.1:18787`，保留 `Authorization` 请求头并将读取超时设为至少 40 秒。

使用 `https://clip.example.com/health` 检查服务状态。后端没有网页管理界面；返回 JSON 是正常行为。代理如果位于另一个容器，需要自行配置内部网络，不能把该容器的 `127.0.0.1` 当作宿主机地址。

### 2. 安装客户端

- **Windows**：退出旧托盘程序，运行 Release 中的安装程序。安装和升级保留本机配对。
- **Android**：安装 APK，在模块管理器中启用 ClipBridge，只勾选 `com.android.systemui`（系统界面）作用域，然后按框架要求重启设备。

### 3. 扫码配对

1. Windows 打开“设备 → 添加配对”，填写 HTTPS 服务器地址和服务器日志中的配对管理码。
2. Android 扫描电脑上的二维码，核对服务器并确认。
3. Windows 显示邀请已领取后，点击“完成并启用”。
4. 在两端分别复制测试文本并验证实际粘贴。设备名通常在连接后约 15 秒内更新。

二维码包含一次性解密凭据，请勿分享。管理码用于授权创建配对，不能用于解密剪贴板内容。详细操作见 [使用指南](docs/QUICKSTART.md)。

## 同步行为与限制

- 每个客户端当前只启用一组双设备配对。删除配置仅解除本机连接，不会远程删除对方配置或撤销服务器令牌。
- 每个方向只保留最新一条待同步内容，自动同步消息有效期为十分钟；离线较久后需要重新复制。
- “最后同步”记录本机成功发送到中转或成功写入收到内容的时间。空闲轮询不会刷新；发送成功不代表对方已写入。
- Android 跳过系统标记为敏感的剪贴板；Windows 暂不提供应用排除。不要假设所有敏感应用都会正确设置标记。
- 尚无图片/文件同步、历史记录、远程设备撤销、前向保密、自动更新或跨系统兼容性保证。

## 安全模型

中转不会获得内容加密密钥，但仍可见设备标识、网络地址、消息时间和密文大小。端到端加密不能防御已被控制的设备，也不保证恶意服务器的可用性。本项目尚未经过独立安全审计。

Windows 使用当前用户 DPAPI 保护本地配置；Android 使用 Keystore 包装配置，并将 SystemUI 及共享其系统 UID 的组件纳入信任边界。详见 [安全说明](SECURITY.md) 与 [协议文档](docs/PROTOCOL.md)。

## 从源码构建

| 目标 | 构建依赖 |
| --- | --- |
| 服务端 / Windows | .NET 10 SDK；Windows 安装包还需要 Inno Setup 6，含简体中文语言 |
| Android | JDK 17+、Android SDK 37；Gradle 9.3.1 由 wrapper 提供 |
| 测试 | Python 3、JDK 17+；配对专项测试还需要 `cryptography` |

```powershell
# Windows：构建、检查，并生成安装程序
.\build.ps1

# 仅生成 Windows 安装程序
.\installer\build.ps1

# Android 开发 APK
cd android
.\gradlew.bat --no-daemon assembleDebug
```

Windows 安装程序输出至 `artifacts/installer/`，版本自动读取项目文件。后续 Windows 发布始终提供安装程序，见 [发布说明](docs/WINDOWS-INSTALLER.md)。Android wrapper 使用带 SHA-256 校验的镜像下载地址，可按需改为官方 Gradle 分发地址。

```text
src/ClipBridge.Core       加密协议、配对与同步状态
src/ClipBridge.Relay      ASP.NET Core 中转服务
src/ClipBridge.Windows    Windows 客户端
src/ClipBridge.Tool       本地手动配对工具
android/                 Android 管理界面与 LSPosed 模块
deploy/                  Docker 与可选 CasaOS 模板
installer/               Windows 安装程序构建
tests/                   协议、HTTP 和 Java/.NET 互操作检查
```

## 文档与贡献

[部署指南](docs/DEPLOYMENT.md) · [使用指南](docs/QUICKSTART.md) · [配对协议](docs/PAIRING.md) · [验证范围](docs/VALIDATION.md) · [路线图](docs/MILESTONES.md) · [贡献指南](CONTRIBUTING.md)

欢迎提交可复现的问题与范围明确的改进。提交日志或截图前，请移除剪贴板内容、服务器管理码、配对二维码、令牌和本机路径。

## 致谢与许可

技术路线参考 [SyncClipboard Xposed](https://github.com/shaklow/syncclipboard-xposed)，功能设计参考 [SyncClipboard](https://github.com/Jeric-X/SyncClipboard)。客户端界面使用 [Miuix](https://github.com/compose-miuix-ui/miuix)，二维码使用 QRCoder 和 ZXing。

项目源码采用 [MIT License](LICENSE)。第三方组件保留各自许可，见 [第三方声明](docs/THIRD_PARTY.md)。
