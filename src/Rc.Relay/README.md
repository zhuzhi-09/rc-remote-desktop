# Rc.Relay — 中转服务

ASP.NET Core (net10.0) 单体服务。**唯一监听端口的组件**：按 `id` 把 1 个 Agent 和 1 个 Controller 配对，双向原样转发 `Rc.Protocol` 消息（`[1 byte MsgType][payload]`，不重新解码帧）。可部署在 Linux VPS / 家用 NAS，通常挂在反向代理（Caddy/nginx）或 frp 隧道后面。

## 运行

```bash
dotnet run --project src/Rc.Relay                      # 开发
# 发布产物
dotnet publish src/Rc.Relay -c Release -o out
Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 Relay__Token=<secret> ./out/rcrelay
```

## 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/healthz` | 健康检查，返回 `200 ok`（纯文本） |
| GET(WS) | `/agent?id=<AgentId>` | 被控端接入；重复 Agent 会被以 `PolicyViolation` 关闭 |
| GET(WS) | `/control?id=<AgentId>` | 主控端接入；重复 Controller 同样被拒绝 |

鉴权：请求头 `X-RC-Token` 必须与 `Relay.Token` 一致（UTF-8 字节常数时间比较），否则 `401`；`id` 必须匹配 `^[A-Za-z0-9_\-.]{1,64}$`，否则 `400`。

配对约定：双方各自发 `Hello` → relay 回 `HelloAck`（Controller 的 ack 携带 Agent 几何信息与 `PeerOnline`；Agent 后上线时会补推一条带几何信息的 `HelloAck` 给等待中的 Controller）→ 之后双向透传。每 15s 发 `Ping`；任一侧超过 `SessionTimeoutSeconds` 无任何入站流量则整对断开。

## 配置（appsettings.json / 环境变量）

| 键 | 默认 | 说明 |
|---|---|---|
| `Relay:Token` | `CHANGE_ME` | 共享密钥，**上线前必须修改**；为空时所有 WS 连接 401 |
| `Relay:MaxMessageBytes` | `67108864` | 单条消息上限（字节）；超过则关闭（`MessageTooBig`）。协议层本身有 64 MiB 接收上限 |
| `Relay:SessionTimeoutSeconds` | `60` | 空闲多久断开（秒） |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:8080` | 监听地址，可被环境变量 `Kestrel__Endpoints__Http__Url` 覆盖 |

> 服务启用了 `X-Forwarded-For` / `X-Forwarded-Proto`（信任来自任意代理的转发头，便于容器/反代部署）。请通过防火墙或监听地址限制，只让反代能访问本端口。

## 反向代理（TLS 终止在代理层，推荐）

Caddy：

```
relay.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

nginx：

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600s;   # 别让空闲的长连接被掐断
}
```

## 原生 HTTPS（PFX，默认不启用）

不想用代理时，可让 Kestrel 直接终结 TLS。把证书放到服务器上（例如 `/etc/rcrelay/cert.pfx`），在 `appsettings.Production.json` 中加入：

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8080" },
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": {
          "Path": "/etc/rcrelay/cert.pfx",
          "Password": "PFX_PASSWORD"
        }
      }
    }
  }
}
```

也可以用环境变量注入口令，避免写进文件：

```bash
Kestrel__Endpoints__Https__Certificate__Path=/etc/rcrelay/cert.pfx \
Kestrel__Endpoints__Https__Certificate__Password=<pfx-password> \
./rcrelay
```

注意：

- 仅当需要直连 TLS 时启用；默认 `Http` 端点保持开启便于健康检查与反代。
- 客户端 `Rc.Protocol.WsClient` 支持证书固定（`PinnedCertSha256`），自签证书时建议固定指纹。
- 8443 端口记得在防火墙放行，且与 8080 一样只暴露必要来源。

## Docker

在仓库根目录构建（上下文需要同时包含 `Directory.Build.props` 与 `src/`）：

```bash
docker build -f src/Rc.Relay/Dockerfile -t rcrelay .
docker run -d --name rcrelay -p 8080:8080 \
  -e Relay__Token=<secret> \
  --restart unless-stopped rcrelay
```

镜像基于 `mcr.microsoft.com/dotnet/aspnet:10.0`，`EXPOSE 8080`，入口 `rcrelay.dll`。
