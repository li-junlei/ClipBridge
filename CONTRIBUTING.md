# 贡献指南

欢迎提交问题与 Pull Request。改动应聚焦可复现的问题，并保持只支持端到端加密的设计。

## 开发检查

```sh
dotnet build src/ClipBridge.Relay -c Release
dotnet run --project tests/ClipBridge.Tests
python tests/integration.py
python -m pip install -r tests/requirements.txt
python tests/pairing_flow.py
```

Windows 改动在 Windows 上构建并检查安装包；Android 改动执行 `assembleDebug`。剪贴板、框架注入、自启动和系统恢复行为需要真实终端验证，请在 PR 中区分自动检查和手工验证。

不得提交 private、data、构建产物、签名密钥、管理码、配对配置、日志或真实用户数据。示例域名使用 example.com，文档不包含特定服务器、个人磁盘或账号信息。

Windows 发布必须生成安装程序；版本同时更新 Windows 项目、Android versionName/versionCode 和发行说明。发布资产应只包含客户端安装文件、文档与校验文件，GitHub 自动源码包由提交生成。

提交贡献表示你有权提供该代码，并同意按项目 MIT 许可发布。第三方代码与资源必须保留来源和许可。
