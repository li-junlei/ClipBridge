# WebDAV 中转

适合已有 WebDAV 存储的用户，无需运行 ClipBridge Docker 服务。仍然只有客户端拥有内容密钥。

## 要求

- 有效 HTTPS 证书和一个已存在的目录，例如 `https://dav.example.com/folder/`。
- Basic 用户名与密码/应用密码，支持 GET、PUT、MKCOL；客户端自行创建子目录。
- 不需要 PROPFIND、MOVE、锁定或服务器端脚本，不跟随地址重定向；请直接填写最终地址。
- 当前没有 OAuth、Digest、验证码或原版 SyncClipboard 格式适配。非 ASCII 账号使用 UTF-8 Basic 编码，需要服务器支持。

推荐使用专用目录及权限受限的应用密码。服务商的请求限额和缓存行为可能影响同步。

## 配对

Windows 设备页添加配对，选择 WebDAV，输入连接信息并生成二维码；手机扫码、核对地址并确认，最后电脑点击完成。电脑在明确完成前保留原连接。

二维码内容是本地生成的完整 Android 配置，包含 WebDAV 凭据和内容密钥，不经过远程二维码服务、不上传存储。它不是一次性邀请，不会自动过期，只供自己的设备扫描。请勿分享截图。连接信息太长时，需要缩短目录地址或应用密码。

## 文件与同步

```text
<所填目录>/clipbridge/<pair-id>/
  <设备A-id>.clip.json
  <设备B-id>.clip.json
  <设备A-id>.device.json
  <设备B-id>.device.json
```

每个客户端只写自己的文件，读取对方文件；双方设备文件互不覆盖。所有内容为加密 Envelope，不上传明文配置、明文内容或内容密钥。设备名使用独立派生密钥。

约两秒检查新文件，空目录或对方尚未上传视为正常等待。GET 禁用缓存；服务器若强制缓存，可能增加延迟。PUT 使用固定内容长度。网络异常按退避策略重试。内容仍受十分钟有效期与客户端防重放检查约束。

服务器可能保存旧版本和备份。附件完成后会尝试删除密文块；剪贴板和设备信息文件持续保留；删除本机配对也不会删除 WebDAV 数据。确认两端均不再使用某组配对后，可以在 WebDAV 中人工删除对应目录。

## 验证范围

使用隔离测试服务检查 .NET 和 Android Java HTTP 实现的目录创建、双向密文、认证、缺失文件、禁止重定向和响应大小限制；同时检查密文不含明文内容、密钥与密码。尚未针对具体第三方 WebDAV 服务商进行账号实测，不承诺所有实现均兼容。

```sh
python tests/webdav_flow.py
```

.NET 检查不依赖真实账号。Android JVM HTTP 检查使用 Gradle 已下载的 OkHttp/Okio/Kotlin 依赖，需要先构建 Android，并配置 JDK；缺少这些依赖时该部分会明确跳过。

## 附件扩展（0.5.0）

新增 `<device-id>.transfers.json` 加密清单和 `attachments/<sender-id>/<transfer-id>-<part>.bin` 分块。清单、文件名和摘要均在端侧加密，分块为 256 KiB。建议允许 DELETE 清理附件；服务商版本历史和回收站不受客户端控制。见 [图片与文件指南](MEDIA.md)。
