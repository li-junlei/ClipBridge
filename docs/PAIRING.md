# 0.2 配对协议

Windows 本地生成配对内容密钥、双方设备令牌、邀请标识和单次传输密钥。POST /v1/pairings 使用管理码授权，只提交认证摘要与加密的 Android 配置。管理码不可用于剪贴板解密。

二维码为 clipbridge://pair?server=<HTTPS origin>&id=<ticket>#<base64url transfer key>。片段密钥不发送到中转；邀请 AES-256-GCM AAD 为 clipbridge-pair:1:{id}:{expires}。五分钟有效，仅一次领取。POST /v1/pairing-invites/claim 只发送票据，手机在本地解密并核对服务器和配置。二维码必须由用户确认来自自己的电脑。

Windows 用设备令牌查询 GET /v1/pairing-invites/{ticket}/status；claimed 表示密文已领取，不证明手机已成功启用。用户点击完成后切换电脑当前配对。可由所有者通过 POST /v1/pairings/{pair}/invites 刷新邀请。服务器不允许覆盖已有组。

服务端保留旧 relay.json 认证，新增组保存于 data/pairings.state；管理码在 data/setup-code 持久化，容器启动日志显示。两者均限制文件权限。剪贴板消息仍沿用原端到端密文协议。
