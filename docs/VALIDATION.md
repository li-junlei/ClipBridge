# 验证范围

## 可重复检查

| 检查 | 命令与覆盖 |
| --- | --- |
| 核心协议 | `dotnet run --project tests/ClipBridge.Tests`：Unicode、64 KiB、篡改、过期、防重放、设备名通道隔离 |
| HTTP 与互操作 | `python tests/integration.py`：真实 HTTP 服务、认证、收件箱、持久化、Java/.NET AEAD |
| 配对 | `python tests/pairing_flow.py`：管理码、一次性邀请、本地解密、密文路由、设备名接口认证 |
| Windows | `installer/build.ps1`：自带运行环境发布与安装程序编译 |
| Android | `android/gradlew assembleDebug`：管理界面和模块编译 |

配对测试依赖 `tests/requirements.txt`。服务测试使用隔离临时目录和随机凭据，不连接现有部署。

## 运行边界

开发期间已验证双向文本同步闭环；不代表所有系统/框架组合均已验证。模拟器已检查 Android 首页与隐藏桌面入口后的模块设置启动。服务端容器运行曾在 ARM64 环境验证。

真实终端仍需覆盖：锁屏与休眠、网络切换、系统与框架重启、Windows 开机自启动、剪贴板占用、普通第三方 App 调用隔离和 Android OEM 后台策略。编译通过不代表安全审计、功耗测试或完整兼容认证。

## v0.3.1 发布检查

核心协议 25 项、HTTP/Java 互操作 16 项、配对专项 13 项检查通过。Windows 安装程序与 Android APK 构建通过；公开源码和客户端发布物已扫描已知私人信息标记，未发现残留。此检查不等同于独立安全审计。

## v0.4.0 WebDAV 检查

`python tests/webdav_flow.py` 验证 .NET 与 Android Java 的实际 WebDAV HTTP 请求、双向密文、设备名、重复过滤、错误认证、禁止重定向、响应大小和离线二维码配置。隔离测试通过；原 25 项核心、16 项 HTTP/互操作和 13 项专用配对检查通过。Windows 安装程序及 Android APK 构建通过。尚未使用实际 WebDAV 服务商账号实测。

## v0.5.0 图片、文件与多配置检查

核心 25 项、原 HTTP/Java 16 项、配对 13 项、WebDAV 检查继续通过。附件检查覆盖分块篡改/位置/空文件、100 MiB 上限、清单有效期、Java/.NET 与独立 AES-GCM、接收方同意前零内容上传、拒绝、路由故障切换和去重。真实中转附件接口通过 14 项权限、大小、持久化及删除检查。Windows STA 入口检查与安装程序编译通过。

独立 Android 模拟器通过 16 项：独立配置开关、跨配置事件去重与顺序、删除/重新导入、系统分享入口、前台接收服务、图片预览及保存/拒绝按钮、实际 MediaStore 相册保存、拒绝文件不写下载、同设备合并、多选文件逐项请求与同意前不上传。检查仅运行在专用空配置模拟器，不连接真实配对或用户手机。

复现：`python tests/attachment_flow.py`、`python tests/relay_attachment_flow.py`；前者需要 JAVA_HOME 和 tests/requirements.txt。Android 专项需先构建 assembleDebugAndroidTest，在独立空配置模拟器安装两个 APK 并授予通知权限，然后执行 `adb -s <专用模拟器序列号> shell am instrument -w io.clipbridge.android.test/io.clipbridge.android.MediaSmokeRunner`。不要在日常手机运行专项测试。

模拟器不包含 LSPosed；真实 HyperOS / Vector Hook、锁屏后台、长时间网络切换、Windows 原生通知交互和具体 WebDAV 服务商仍需实机验证。此检查不构成独立安全审计。
