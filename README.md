# Rc —— 自托管远程桌面

> 为受限网络设计的自托管远程桌面。被控端只做主动出站连接、零监听端口、不依赖任何第三方服务或 VPN。

被控端**只做主动出站 WSS 连接**，**不监听任何端口**、不使用任何 VPN/组网协议，
流量在链路上仅表现为一条普通的 HTTPS/WebSocket 会话。

## 架构

```
[Windows 被控端]                              [公网中转 Relay]              [Windows 主控端]
  Rc.Agent  ──出站 wss://443──►  Rc.Relay  ◄──wss──  Rc.Controller
  屏幕采集 + 分块JPEG增量编码      按 AgentId 配对转发     渲染画面 + 回收键鼠
  ◄──SendInput 键鼠注入
```

三个组件共用 `Rc.Protocol`（消息定义 + 帧编解码 + 自带证书固定的 WSS 客户端）。

## 快速开始

```powershell
.\build.ps1 -Task dist     # 发布自包含单文件产物到 publish\
.\build.ps1 -Task e2e      # 跑端到端测试（ws + wss 各一轮）
```

| 目录 | 文件 | 放到哪台机器 |
|---|---|---|
| `publish\relay-linux` | `rcrelay` | 公网 VPS / NAS |
| `publish\agent` | `rcagent.exe` | **被控端（受控主机）** |
| `publish\controller` | `rccontrol.exe` | 主控端（你自己的电脑） |

被控端和主控端都是**自包含单文件 exe，目标机器无需安装 .NET**。
部署细节（sakurafrp / VPS + Caddy / 证书固定）见 **`deploy/README.md`**。

## 目录

| 路径 | 说明 |
|---|---|
| `src/Rc.Protocol` | 共享协议：消息、帧编解码、自带证书固定与 TCP_NODELAY 的 WSS 客户端 |
| `src/Rc.Relay` | 中转服务（ASP.NET Core，可跑 Linux VPS / NAS / Docker） |
| `src/Rc.Agent` | 被控端（Windows 托盘程序，出站连接，零监听端口） |
| `src/Rc.Controller` | 主控端（Windows 图形界面，深色主题） |
| `tools/Rc.E2E` | 端到端自动化测试：拉起真实中转 + 真实被控端，用真实协议断言 |
| `deploy/` | 部署脚本与中文指南（Caddyfile、systemd、compose、证书/指纹脚本） |

## 验证状态

`dotnet build RemoteControl.slnx` → **0 错误 0 警告**。

`tools/Rc.E2E` 在两种模式（明文 `ws` / `wss` + 证书固定）下全绿：

```
PASS  relay /healthz          ok
PASS  controller handshake    Ok=True 1920x1080 tile=64 peerOnline=True
PASS  first keyframe          510 tiles, 1920x1080, frameId=29
PASS  keyframe on demand      frameId 29 -> 30
PASS  input path              mouse_move injected, socket=Open, agentClean=True
PASS  frame stream alive      66 frame message(s) in 4s
PASS  cert pinning enforced   a wrong pinned fingerprint was rejected   (仅 wss)
PASS  agent log clean         no errors logged
```

## 设计要点

- **网络适配**：被控端零监听端口、零 VPN 协议，只有一条出站 `wss://` 长连接。
- **低延迟**：自研 WSS 客户端在裸 TCP 上设置 `TCP_NODELAY`，避免 Nagle 带来的输入延迟。
- **抗中间人**：`pinnedCertSha256` 在自签名场景下固定服务器证书（已验证错误指纹会被拒绝）；
  用 Let's Encrypt 时改为依赖系统信任链（证书会轮换，不可固定）。
- **省带宽**：屏幕切成 64×64 块做增量比对，只对变化的块做 JPEG 编码；
  发送端单槽背压，发不过来就丢旧帧，永远发最新画面。
- **弱网**：两端都带指数退避重连 + Ping/Pong 心跳超时判定。

## 已知限制

多显示器只采主屏、看不到 UAC/登录界面（需 SYSTEM 服务模式）、无剪贴板/文件传输/音频。
详见 `deploy/README.md` 第 5 节。
