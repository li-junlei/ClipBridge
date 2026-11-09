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
