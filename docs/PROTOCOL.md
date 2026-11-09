# ClipBridge Protocol v1

## 信任和配对

当前每组配对包含两个设备。可信电脑生成 pair/device/peer 的 128 位随机标识、随机 256 位内容根密钥、每设备独立随机 256 位认证令牌。内容密钥只进入两份客户端配置；中转配置仅保存 pair、两设备 ID、令牌 SHA-256。客户端配置通过可信本地文件或一次性加密扫码邀请导入，服务端不获得明文内容密钥。扫码流程见 PAIRING.md。

加密采用 .NET 与 Android 原生支持的 AES-256-GCM。配对使用随机共享秘密，不提供前向保密。

## 消息封装

JSON 外层字段：`version=1, pair, from, to, id, clock, nonce, data`。所有 ID 必须为 32 位小写十六进制；`clock` 为 1..9007199254740991 的整数。`nonce` 和 `data` 为标准 Base64。

AAD 精确为无换行 ASCII：

```
clipbridge:1:{pair}:{from}:{to}:{id}:{clock}
```

clock 为无前导零十进制。每条消息生成新的随机 128 位 id 和 96 位 nonce。消息密钥为 `HMAC-SHA256(rootKey, AAD)`，输出 32 字节直接作为 AES-256-GCM 密钥。使用 128 位认证标签，`data = ciphertext || tag`。重试必须使用已保存的同一封装；修改内容必须生成新的 id。即使进程重启逻辑计数器回滚，也不依赖计数器作为 GCM nonce；但回滚仍破坏防重放状态，因此必须重新配对。

内层 UTF-8 JSON：`{kind:"text", text, created, expires}`。时间是 Unix 秒，默认 expires=created+600。解密认证完成后再解释数据；拒绝来源/目标/pair 错误、异常长度、过期、created 超前超过 60 秒、寿命超过 600 秒和未知类型。文件名、MIME、图片信息未来全部进入内层，禁止为搜索而把内容哈希上传服务器。

## API

剪贴板与设备信息接口使用 `Authorization: Bearer <device-token>`；配对注册使用管理码，邀请领取使用一次性票据。生产环境只能经 HTTPS 暴露。禁用客户端自动跨域重定向。

* `GET /health`：匿名健康状态，无配对信息。
* `POST /v1/messages`：发送封装。令牌必须属于 from，to 必须是同配对另一设备。原样重试返回 200；同一方向旧 clock 或相同 clock 不同封装返回 409。
* `GET /v1/inbox?after={last-message-id}`：只返回调用设备的最新入站封装。若无新消息，等待最多 25 秒，返回 JSON null；新消息到来立即唤醒。重连后重新读取最新消息。

请求和响应最大 600000 字节，解码后 ciphertext+tag 最大 400000 字节，为 JSON 控制字符转义留出空间；纯文本最大 UTF-8 65536 字节。每设备每分钟 120 请求。认证失败 401、身份不匹配 403、格式错误 400、过量 429。当前不提供 WebSocket，HTTPS 长轮询与后续 WSS 均只允许此密文封装。

## 状态与冲突

每端持久化 `clock, winner, pending`。本地变化：clock+1，winner=本机，创建密文并先持久化，然后上传。远端消息只有 `(clock, from)` 的字典序大于本地 `(clock, winner)` 才能生效；接受后先保存状态，再写剪贴板。较新远端事件取代尚未发送的本地 pending。相同文本的远端写入不会作为新本地事件上传。

未见过但尚未过期的延迟消息仍可能在未观察到更新的客户端生效。服务端序列与 cursor 不承担加密真实性；客户端持久化状态才承担防重放。备份恢复不具备防回滚硬件计数器，恢复后必须重新配对。

服务器每目标保存最新封装，原子替换并 flush；只支持单服务实例和本地持久卷，不支持共享目录多副本写入。不保存明文、内容根密钥和客户端配置。

## 0.3 设备名称

POST /v1/device-info 和 GET /v1/device-info 使用现有设备认证和独立内存收件箱，仅保存/返回对方的密文 Envelope，定期重新发布。
消息加密前用 HMAC-SHA256(rootKey, ASCII("clipbridge:device-name:1")) 派生名称通道密钥，再沿用 Wire 的 AES-GCM 消息封装及过期检查。通道隔离防止名称密文被当作剪贴板内容。
名称最多 80 个字符，拒绝控制字符。不增加剪贴板逻辑时钟，不触发最后同步时间。

## v0.5 附件与跨连接去重

设备元数据在原有独立加密通道内携带名称和随机 32 位十六进制安装标识。文本载荷增加 eventId、origin、eventClock；每个安装递增计数，接收端绑定已确认来源标识并持久化高水位，旧客户端缺省字段仍兼容。

附件清单使用独立根通道密钥 `HMAC-SHA256(root, ASCII("clipbridge:attachments-index:1"))`，再按普通 Envelope 的认证头派生消息密钥。清单包含 offers、decisions、created、expires，有效期一小时；不是剪贴板文本载荷。单个清单最多 32 个 offer 和 128 个 decision。

分块 AAD 为 ASCII `clipbridge:attachment:1:{pair}:{from}:{to}:{transfer-id}:{part}`，分块密钥为 `HMAC-SHA256(root, AAD)`。二进制布局是 12 字节随机 nonce、密文、16 字节 GCM tag。分块最多 256 KiB，索引 0–399，单文件最多 100 MiB，完整文件 SHA-256 在加密清单内传输。接收端在所有块认证和完整摘要验证后才保存文件。

专用中转通过 `attachments: 1` 声明能力。对象接口为 `/v1/objects/{device}/{index|transfer-id-part}`，PUT/DELETE 只允许本人设备，GET 只允许配对内设备。服务端只校验密文外层和大小，不持有内容密钥。每设备 512 MiB / 8192 对象配额，24 小时过期，后续写入触发清理。

## v0.5.3 WebDAV 单对象附件

WebDAV 采用 `attachments/<sender-id>/<transfer-id>.cbx` 单对象，依次拼接原 256 KiB 加密块，每块仍为 nonce(12) / ciphertext / tag(16)，AAD 与独立分块相同。加密清单提供总明文大小、块数和 SHA-256，总对象长度必须为 size + 28 * chunks。接收端拒绝截断、额外尾部数据、认证失败及摘要不符。HTTP 固定 Content-Length，传输逐块处理；断线后重传整个对象。专用中转继续使用原有分块接口。

WebDAV 清单按约 60 秒读取、状态变化立即写入、空闲约 15 分钟刷新；错误退避约 5 分钟。文件只有接受请求后才上传，ready 只能在 PUT 成功后发布。客户端均需升级。
