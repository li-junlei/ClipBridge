# 部署中转服务

## Docker Compose

从仓库根目录运行：

```sh
docker compose -f deploy/compose.yaml up -d --build
docker compose -f deploy/compose.yaml logs relay
```

默认后端地址为 `http://127.0.0.1:18787`，数据保存在命名卷 `relay-data`。服务以镜像中的非 root 用户运行，根文件系统只读，临时文件使用 tmpfs。不要删除数据卷：配对认证、管理码和密文消息保存在其中。

服务启动时生成并在服务器日志显示配对管理码；管理码保存在数据卷中的 `setup-code`。具有日志或数据卷读取权限的管理员属于管理码信任边界。日志也可能被运维平台收集，请限制访问。可以通过环境变量 `CLIPBRIDGE_SETUP_CODE` 指定管理码，使用足够强的随机值，不要提交到 Git。

## HTTPS 反向代理

将自行持有的域名（例如 `https://clip.example.com`）代理至 `http://127.0.0.1:18787`，使用有效证书，保留 `Authorization`，读取超时至少 40 秒。客户端不会跳过 TLS 验证。

反向代理位于其他容器或主机时，自行配置安全的内部网络；不要把代理容器内的 localhost 当作宿主地址，也不要将未加密的后端认证接口直接暴露公网。

匿名健康端点为 `/health`，配对能力端点为 `/v1/setup/info`。中转没有网页管理界面。

## 更新与备份

```sh
git pull
docker compose -f deploy/compose.yaml up -d --build
```

更新前备份数据卷与部署配置。保持同一数据卷，可保留管理码、已注册配对和最新密文。不要执行 `docker compose down -v`，除非明确希望清空服务端数据。恢复过旧的客户端防重放状态存在回放风险，请使用新配对。

## 可选：CasaOS

CasaOS 是可选的容器管理方式，不是运行依赖。

先从仓库根目录构建本地镜像：

```sh
docker build -t clipbridge-relay:0.3.1 -f deploy/Dockerfile .
```

在面板的自定义安装中导入 `deploy/compose.casaos.yaml`。模板使用上述本地镜像与命名数据卷，不包含特定服务器地址或磁盘路径。按实际环境配置 HTTPS 反代，配对管理码从服务器日志中获取。升级前先构建新镜像，再修改现有应用的镜像标签，避免重复安装两份服务。

## 可选：手动配对

推荐使用客户端扫码配对。如需离线生成：

```sh
dotnet run --project src/ClipBridge.Tool -- pair https://clip.example.com private
```

仅将 `private/relay.json` 只读挂载到中转的 `/config/relay.json`；它包含设备令牌摘要，不包含内容密钥。Windows 与 Android 配置包含明文内容密钥，只能导入可信终端，不要上传中转或公开分享。使用新目录，工具不会覆盖已有配置。
