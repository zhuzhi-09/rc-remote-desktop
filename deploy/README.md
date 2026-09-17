# 部署与使用说明

本文档覆盖三条中转路线：

| 路线 | 适用 | TLS | 推荐度 |
|---|---|---|---|
| **A. sakurafrp** | 快速验证，无需买 VPS | 自签名证书 + 指纹固定 | 过渡 |
| **B. 境外 VPS + 域名 + Caddy** | 长期稳定，端口 443，证书受信任 | Let's Encrypt | ★ 强烈推荐 |
| **C. Cloudflare Tunnel** | 只有 443/80 可用，又不想买 VPS | 受信任 CA 证书 | 备选 |

---

## 0. 先在本机构建（只做一次）

```powershell
cd "D:\远程控制开发"
.\build.ps1 -Task dist        # Release + 发布自包含单文件产物到 publish\
.\build.ps1 -Task e2e         # 跑端到端测试（明文 ws 一轮、TLS wss 一轮）
```

产物：

| 目录 | 文件 | 放到哪台机器 |
|---|---|---|
| `publish\relay-linux` | `rcrelay`（Linux 自包含单文件，约 96 MB） | VPS / NAS |
| `publish\agent` | `rcagent.exe`（约 111 MB） | **被控端主机（受控主机）** |
| `publish\controller` | `rccontrol.exe`（约 111 MB） | 主控端（你自己的电脑） |

`rcagent.exe` / `rccontrol.exe` 是**自包含单文件**，目标机器不需要安装 .NET。

---

## 0.1 已验证到什么程度

`tools/Rc.E2E` 会自动拉起真实的中转服务与真实的被控端，然后用真实协议作为主控端连接并断言。
两种模式都全绿：

| 检查项 | 明文 `ws` | `wss` + 证书固定 |
|---|---|---|
| 中转 `/healthz` | ✅ | ✅ |
| 握手（返回被控端分辨率、tile=64） | ✅ | ✅ |
| 首帧关键帧（1920×1080 / 510 块 / JPEG 合法） | ✅ | ✅ |
| 按需重发关键帧 | ✅ | ✅ |
| 键鼠注入链路（SendInput） | ✅ | ✅ |
| 帧流连续（≈13–16 fps） | ✅ | ✅ |
| 错误指纹被拒绝（防中间人） | — | ✅ |
| 被控端日志无错误 | ✅ | ✅ |

**尚未验证、需要你手动确认**的部分：
- 主控端图形界面的观感与交互（已做启动冒烟：能启动、不崩、无错误日志）。
- 在真实的受限网络环境下适应出站策略的效果（取决于目标网络的具体策略）。
- 多显示器、UAC 弹窗 / 登录界面（见文末「已知限制」）。

---

## 0.2 先测：目标网络的出站策略（决定架构）

**不要先买 VPS。** 先用 `deploy/probe-egress.ps1` 在被控端主机上测出哪些出站端口真的能用。
"能上任意网站"只证明了 443 通，**没有**证明 8080 / 40123 这类高位端口也通。

```powershell
# 在被控端主机的 PowerShell 里直接跑
.\probe-egress.ps1 -Endpoint "node-xx.sakurafrp.com:40123"
```

看结论：

| 探测结果 | 含义 | 走哪条路线 |
|---|---|---|
| 高位端口也 OPEN 且有 HTTP 数据 | 非 443 出站没被限制 | **路线 A** 直接用 |
| 只有 443 / 80 OPEN，高位端口 BLOCKED | 受限网络的常见策略 | **路线 C**（免费拿 443）或 **路线 B** |
| 所有端口"OPEN 但无数据" | 有透明代理 / MITM 设备 | 优先路线 C，并启用证书固定 |

脚本用 `portquiz.net`（在所有 TCP 端口都监听并回 HTTP）作为靶子，能干净地区分
"端口被限制" 和 "网站不可达"。

---

## 1. 三个配置文件的对应关系

```
        relayUrl（含 /agent 或 /control 路径）
Agent ─────────────────────────────────────► Relay ◄───────────────── Controller
   agent.config.json                    appsettings.json        controller.config.json
```

**关键**：`agentId` 是配对钥匙，两边必须一致；`token` 必须与中转服务的 `Relay:Token` 一致。

---

## 推荐路线（基于实测）：VPS + 自签证书 + IP 直连

如果你的实测结果是「**端口全开、无协议识别、无 TLS 中间人，但 DNS 被强制控制**」
（受限网络的常见情形），那这是最稳的方案：**不要域名、不要 DNS、不要 frp、不要 VPN**。

```
被控端 --wss://<VPS的IP>:8443--> Relay(VPS) <--wss-- 主控端
          IP 字面量，不查 DNS          自签证书 + 指纹固定
```

每个设计点都对应一条网络观测结果：

| 实测发现 | 对应设计 |
|---|---|
| 非 443 端口完全放行 | 用任意高位端口，不必抢 443 |
| 无 DPI／协议识别 | 直接跑标准 WSS，不需要伪装成 frp 或 CDN |
| DNS 被强制使用且会污染域名 | **URL 用 IP 字面量**，连接时完全不查 DNS |
| 无 TLS 中间人 | 自签证书 + `pinnedCertSha256` 固定指纹，加密与防篡改一样不少 |
| 主控端也是主动出站 | **主控端所在网络无需任何改动**，只有 Relay 需要被连入 |

### 一键部署

```bash
# 1) 上传两个文件到 VPS
scp publish/relay-linux/rcrelay root@<VPS_IP>:/root/
scp deploy/setup-relay-vps.sh   root@<VPS_IP>:/root/

# 2) 一条命令：装 relay、生成自签证书、算指纹、装 systemd、开防火墙、打印两端配置
ssh root@<VPS_IP> 'bash /root/setup-relay-vps.sh /root/rcrelay 8443'
```

脚本结束会**直接打印两个 JSON 块**，分别粘进 `agent.config.json` 与 `controller.config.json` 即可，
里面已经填好了 token、IP、端口和指纹。

### 注意事项

- VPS 厂商的**安全组 / 防火墙**也要放行该端口（`ufw` 只管机器内部）。
- 重新生成证书后，两端的 `pinnedCertSha256` 都要同步更新。
- 被控端的 `agent.config.json` 里还有 `connectIp` 字段，用于「URL 写域名、实际连 IP」的场景
  （在 DNS 被污染时仍保留 SNI）；本方案 URL 直接写 IP，留空即可。

---

## 路线 A：sakurafrp（先用它跑通）

sakurafrp 给的通常**不是 443 端口**，且是公共 frp 节点，在只放行 443 的网络里可能连不上。
它适合**先验证整条链路能不能通**，通了之后再换路线 B。

### A1. NAS 上启动中转服务

```bash
# 假设产物在 /opt/rcrelay/
cd /opt/rcrelay
chmod +x rcrelay

# 用环境变量注入 token（覆盖 appsettings.json）
export Relay__Token='换成一串足够长的随机字符串'
# 注意：appsettings.json 里的 Kestrel:Endpoints 会覆盖 ASPNETCORE_URLS，
#       要改监听地址必须覆盖同一个配置键（双下划线表示层级）
export Kestrel__Endpoints__Http__Url='http://127.0.0.1:8080'
./rcrelay
```

让它常驻：`deploy/rcrelay.service` 拷到 `/etc/systemd/system/`，改好后
`systemctl daemon-reload && systemctl enable --now rcrelay`。

### A2. 在 sakurafrp 建一条 TCP 隧道

- 本地地址：`127.0.0.1`，本地端口：`8080`
- 类型：TCP
- 记下 sakurafrp 给你的公网地址，例如 `node-cn-1.sakurafrp.com:40123`

### A3. 被控端 `agent.config.json`（放在 rcagent.exe 同目录）

```jsonc
{
  "relayUrl": "ws://node-cn-1.sakurafrp.com:40123/agent?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "和中转服务 Relay__Token 完全一致",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "",
  "targetFps": 12,
  "jpegQuality": 60,
  "scale": 100,
  "tileSize": 64,
  "keyframeIntervalSeconds": 10
}
```

> 路线 A 先用 `ws://`（明文）跑通。**明文意味着 token 和画面可能被链路中的网络设备嗅探**，
> 所以只用来验证；验证成功后请立刻改走路线 B，或按下面「A4 加密」启用自签名 TLS。

### A4.（可选）给路线 A 加密

1. 本地生成自签名证书：
   ```powershell
   .\deploy\make-cert.ps1 -DnsName node-cn-1.sakurafrp.com
   ```
   把 `rcrelay.pfx` 上传到 NAS 的中转目录。
2. 中转服务改用 HTTPS 端口（`appsettings.json` 里加 `Kestrel:Endpoints:Https`，见 `make-cert.ps1` 输出）。
3. 取指纹：
   ```powershell
   .\deploy\fingerprint.ps1 -ServerName node-cn-1.sakurafrp.com -Port 40123
   ```
4. 被控端/主控端配置改为：
   ```jsonc
   "relayUrl": "wss://node-cn-1.sakurafrp.com:40123/agent?id=endpoint-1",
   "allowUntrustedCert": false,
   "pinnedCertSha256": "上一步输出的十六进制指纹"
   ```

---

## 路线 B：境外 VPS + 域名 + Caddy（推荐）

流量是标准的 `wss://你的域名`，端口 443，证书由 Let's Encrypt 签发，和普通网站没有区别。

### B1. 买 VPS + 解析域名

- 任意一家境外 VPS（1 核 1G 足够）
- 域名加一条 A 记录：`rc.你的域名.com` → VPS 公网 IP

### B2. 安装 Caddy 与中转服务

```bash
# 1) 放中转服务
mkdir -p /opt/rcrelay && cd /opt/rcrelay
# 上传 publish/relay 里的文件到这里

# 2) 装 Caddy
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list
apt update && apt install -y caddy

# 3) 占位页
mkdir -p /var/www/decoy
# 上传 deploy/decoy/index.html 到 /var/www/decoy/

# 4) 配置反代
# 上传 deploy/Caddyfile 到 /etc/caddy/Caddyfile，并把 rc.example.com 改成你的域名
caddy validate --config /etc/caddy/Caddyfile
systemctl reload caddy
```

Caddy 会自动申请证书。用浏览器打开 `https://rc.你的域名.com` 应看到一个普通页面（占位页）。

### B3. 中转服务常驻

用 `deploy/rcrelay.service`（记得把 `Relay__Token` 换成强随机串）：

```bash
useradd -r -s /usr/sbin/nologin rcrelay
cp deploy/rcrelay.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now rcrelay
curl -s http://127.0.0.1:8080/healthz   # 应输出 ok
```

`rcrelay.service` 里已把服务绑定在 `127.0.0.1:8080`，**只通过 Caddy 对外**。

### B4. 两端配置

被控端 `agent.config.json`：
```jsonc
{
  "relayUrl": "wss://rc.你的域名.com/agent?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "与 Relay__Token 一致",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "",
  "targetFps": 12,
  "jpegQuality": 60,
  "scale": 100,
  "tileSize": 64,
  "keyframeIntervalSeconds": 10
}
```

主控端 `controller.config.json`：
```jsonc
{
  "relayUrl": "wss://rc.你的域名.com/control?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "与 Relay__Token 一致",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "",
  "jpegQuality": 60,
  "scale": 100
}
```

> ⚠️ 用 Let's Encrypt 时**不要**填 `pinnedCertSha256`：证书约 60 天轮换一次，
> 固定指纹会导致到期后连不上。让系统信任链来验证即可（`allowUntrustedCert` 保持 `false`）。

---

## 路线 C：Cloudflare Tunnel（不买 VPS 也能拿到 443）

如果探测结果是**只有 443/80 可用**，又不想买 VPS，这条路能免费给你一个真正的 443：

```
[被控端] --wss://443--> [Cloudflare 边缘] --加密隧道--> [你家 NAS 的 cloudflared] --> [rcrelay:8080]
```

- 被控端看到的是一条"到 Cloudflare 的正常 HTTPS 连接"，和访问普通网站没有区别。
- **NAS 主动出站**连到 Cloudflare，所以 NAS 不需要公网 IP。

### C1. 准备

- 一个域名（几块钱一年的 `.top` / `.xyz` 即可），托管到 Cloudflare（免费套餐）
- NAS 上安装 `cloudflared`

### C2. 先做 5 分钟可行性验证（连域名都不用）

```bash
# NAS 上，rcrelay 已在 127.0.0.1:8080 运行
cloudflared tunnel --url http://127.0.0.1:8080
```

它会打印一个 `https://随机名.trycloudflare.com` 地址。把它填到被控端的 `agent.config.json`：

```jsonc
"relayUrl": "wss://随机名.trycloudflare.com/agent?id=endpoint-1",
"allowUntrustedCert": false,
"pinnedCertSha256": ""
```

能连上，就证明**整条 443 链路在你的网络里是通的**。该地址重启即变，只用于验证。

### C3. 正式配置

```bash
cloudflared tunnel login
cloudflared tunnel create rcrelay
cloudflared tunnel route dns rcrelay rc.你的域名.com
```

`~/.cloudflared/config.yml`：

```yaml
tunnel: rcrelay
credentials-file: /root/.cloudflared/<tunnel-id>.json
ingress:
  - hostname: rc.你的域名.com
    service: http://127.0.0.1:8080
  - service: http_status:404
```

```bash
cloudflared service install     # 装成系统服务，开机自启
systemctl start cloudflared
```

被控端配置：

```jsonc
"relayUrl": "wss://rc.你的域名.com/agent?id=endpoint-1",
"allowUntrustedCert": false,
"pinnedCertSha256": ""
```

### C4. 注意事项

- ⚠️ **绝对不要填 `pinnedCertSha256`**。Cloudflare 在边缘终结 TLS，证书由 Cloudflare 签发且
  自动轮换，固定指纹必然导致连不上。靠系统信任链验证即可 —— 那已经是可信 CA 签发的证书。
- Cloudflare 免费套餐支持 WebSocket。我们的中转每 15 秒发一次 Ping，连接不会被判为空闲。
- 如果目标网络连 Cloudflare 也不通，先用 `probe-egress.ps1` 确认；确认不通就只能走路线 B。
- Cloudflare 能看到流量元数据（域名、时长、大小），看不到内容（端到端仍是你的 WSS 加密）。

---

## 2. 被控端主机怎么放

1. 把 `rcagent.exe` 拷到被控端主机，例如 `C:\Users\你的用户名\rcagent\`。
2. 第一次运行会在同目录生成 `agent.config.json`，改好后**重新运行**。
   - 调试时用 `rcagent.exe --console` 可以看实时日志。
   - 出问题看同目录的 `agent.log`。
3. 开机自启（可选）：任务计划程序 → 创建任务 → 触发器「登录时」→ 操作指向 `rcagent.exe` → 勾选「使用最高权限运行」。
4. **不要**把它放到系统目录或加自启动注册表项 —— 这台机器是你自己的，保持可解释、可卸载。

### 权限说明

| 场景 | 需要 |
|---|---|
| 看普通桌面、操作普通窗口 | 普通用户即可 |
| 操作以管理员身份运行的程序 | rcagent 需要以管理员身份运行 |
| 看 UAC 弹窗 / 登录界面 / 锁屏 | 需要以 SYSTEM 服务运行（**当前版本未支持**，属于后续路线） |

---

## 3. 排障

| 现象 | 排查 |
|---|---|
| 主控端一直「等待被控端」 | 中转 `/healthz` 是否正常；两边 `agentId`/`token` 是否一致；被控端 `agent.log` |
| 连不上 `/agent` | 域名解析、证书、路径是否写全（`/agent?id=xxx`）；目标网络是否限制了该端口 |
| 画面卡/花屏 | 降低 `scale`（75 或 50）、降低 `jpegQuality`、下调 `targetFps` |
| 键鼠位置偏移 | 被控端是多显示器或分辨率变化所致（当前版本只采集主显示器） |
| 一连就断 | 检查 `pinnedCertSha256` 是否过期/写错；Let's Encrypt 场景请留空 |

---

## 4. 安全须知

- `token` 是唯一凭据，请用足够长的随机串；泄露等于把电脑交出去。
- 路线 B 全程 TLS，且证书受信任，中间人无法解密。
- 路线 A 的 `ws://` 是明文的，**只用于验证**。
- 本软件用于控制**你自己拥有/获得授权**的机器。未经授权访问他人设备在中国属违法行为（《刑法》285/286 条）。

---

## 5. 已知限制

| 限制 | 说明 | 影响 |
|---|---|---|
| 只采集主显示器 | 被控端 `ScreenCapturer` 使用 `SM_CXSCREEN/SM_CYSCREEN` | 多显示器时看不到副屏；且若副屏在主屏左侧（负坐标），副屏上的窗口可能被裁切 |
| 看不到 UAC 弹窗 / 登录界面 | 被控端以普通用户身份运行，注入进不了 Secure Desktop | 需要「以管理员身份运行 rcagent.exe」才能操作以管理员启动的程序；要看登录界面需要 SYSTEM 服务模式（尚未实现） |
| 键鼠注入受 UIPI 限制 | Windows 不允许低完整性进程向高完整性窗口注入 | 同上，提升 rcagent 权限即可 |
| 画质/缩放是整屏参数 | `set_quality` / `set_scale` 作用于全部画面 | 文字清晰度和带宽需要手动权衡 |
| 无剪贴板/文件传输 | 协议里留了 `Clipboard` 消息类型，但两端未实现 | 只能看画面和操作键鼠 |
| 无音频 | — | — |
| 无自启动安装器 | 需要手动放文件；自启用「任务计划程序」 | 见第 2 节 |

### 网络环境适配

被控端只做出站 WSS 连接、零监听端口、零 VPN 协议，因此在只放行出站流量的网络里通常可用。
但这不代表任何网络都一定可用，具体取决于目标网络的出站策略：

- 如果网络对**全部非 443 端口**的出站都做了限制，高位端口的中转会连不上 → 必须改用 443 端口（路线 B 或 C）。
- 如果网络配置了 **SNI 白名单**（只放行已知域名），即使 443 也可能被拦 → 需要换用白名单内的域名，或退到 DNS 隧道（本方案未实现，速度很慢）。
- 如果网络部署了 **JA3/TLS 指纹识别**并对非浏览器指纹告警，标准 .NET `SslStream` 的指纹通常与常见浏览器不同 → 可能被识别，但一般只影响主动识别型设备。

**先用 `probe-egress.ps1` 评估目标网络的出站策略，再决定中转路线。**
