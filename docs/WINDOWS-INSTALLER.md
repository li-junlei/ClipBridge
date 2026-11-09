# Windows 安装程序

每个 Windows 版本均提供安装程序。项目根目录 `build.ps1` 自动调用安装包构建，也可单独执行：

```powershell
.\installer\build.ps1
```

构建需要 .NET 10 SDK 和 Inno Setup 6（含简体中文语言）。版本读取 `src/ClipBridge.Windows/ClipBridge.Windows.csproj`，输出 `artifacts/installer/ClipBridge-Setup-版本-win-x64.exe`。包含运行环境及第三方许可证，无需另外安装 .NET。

按当前用户安装到 `%LOCALAPPDATA%\Programs\ClipBridge`，提供开始菜单、可选桌面快捷方式与系统卸载项。升级前从托盘退出旧版本。已开启的自启动路径会迁移到安装目录，卸载时清除对应的自启动。

配置位于 `%LOCALAPPDATA%\ClipBridge`，与安装目录分离，升级与卸载默认保留。需要解除本机配对时使用客户端“删除配置”。预览版安装程序尚未代码签名。
