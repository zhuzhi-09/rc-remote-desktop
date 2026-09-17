#!/usr/bin/env bash
#
# One-shot setup for the Rc relay on a fresh Linux VPS.
#
# What it does:
#   1. Installs the self-contained rcrelay binary (no .NET runtime needed on the VPS).
#   2. Generates a self-signed TLS certificate (SAN = the VPS public IP) and a PFX for Kestrel.
#   3. Generates a strong random token.
#   4. Writes appsettings.json binding Kestrel to https://0.0.0.0:<PORT>.
#   5. Installs and starts a hardened systemd unit.
#   6. Opens the port in ufw (if present).
#   7. Prints the certificate fingerprint and ready-to-paste agent/controller configs.
#
# Usage (on the VPS, as root):
#   scp publish/relay-linux/rcrelay root@<vps>:/root/
#   scp publish/agent/rcagent.exe   root@<vps>:/root/          # optional, for the download endpoint
#   scp deploy/setup-relay-vps.sh   root@<vps>:/root/
#   ssh root@<vps> 'bash /root/setup-relay-vps.sh /root/rcrelay 8443 /root/rcagent.exe'
#
# The 3rd argument is optional. When supplied, the script also publishes rcagent.exe plus a
# ready-made agent.config.json over HTTP on DL_PORT, and prints a single PowerShell command you
# can hand to an operator on the controlled endpoint to download and start the agent.
#
# Nothing here needs a domain name: the client configs dial the raw IP, so a poisoned or
# forced DNS resolver (e.g. a forced internal resolver) cannot interfere.

set -euo pipefail

BIN_SRC="${1:-/root/rcrelay}"
PORT="${2:-8443}"
AGENT_SRC="${3:-}"
DL_PORT="${DL_PORT:-8081}"
RELAY_DIR="/opt/rcrelay"
SERVICE_USER="rcrelay"
PUBLIC_IP="${PUBLIC_IP:-}"

log()  { printf '\033[36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33m[!] %s\033[0m\n' "$*"; }
die()  { printf '\033[31m[x] %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Run me as root (or with sudo)."
[ -f "$BIN_SRC" ]     || die "Relay binary not found at $BIN_SRC. Copy publish/relay-linux/rcrelay there first."

command -v openssl >/dev/null || die "openssl is required."

# ---------------------------------------------------------------- public IP
if [ -z "$PUBLIC_IP" ]; then
	log "Detecting the public IP"
	PUBLIC_IP="$(curl -fsS4 --max-time 8 https://api.ipify.org 2>/dev/null \
		|| curl -fsS4 --max-time 8 https://ifconfig.me 2>/dev/null \
		|| true)"
fi
[ -n "$PUBLIC_IP" ] || die "Could not detect the public IP. Re-run with: PUBLIC_IP=1.2.3.4 bash $0 $*"
log "Public IP: $PUBLIC_IP"

# ---------------------------------------------------------------- secrets
TOKEN="$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 40)"
PFX_PASS="$(head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"

# ---------------------------------------------------------------- layout
log "Installing into $RELAY_DIR"
install -d -m 0755 "$RELAY_DIR"
install -m 0755 "$BIN_SRC" "$RELAY_DIR/rcrelay"
cd "$RELAY_DIR"

# ---------------------------------------------------------------- certificate
log "Generating a self-signed certificate for IP $PUBLIC_IP"
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
	-keyout relay.key -out relay.crt \
	-subj "/CN=$PUBLIC_IP" \
	-addext "subjectAltName=IP:$PUBLIC_IP,IP:127.0.0.1" >/dev/null 2>&1 \
	|| die "openssl req failed (needs OpenSSL 1.1.1+ for -addext)."

openssl pkcs12 -export -out relay.pfx -inkey relay.key -in relay.crt \
	-password "pass:$PFX_PASS" >/dev/null 2>&1 \
	|| die "openssl pkcs12 failed."

# Fingerprint as plain uppercase hex, which is what pinnedCertSha256 expects.
FINGERPRINT="$(openssl x509 -in relay.crt -noout -fingerprint -sha256 \
	| sed 's/.*=//' | tr -d ':' | tr 'a-f' 'A-F')"

# ---------------------------------------------------------------- appsettings
log "Writing appsettings.json (HTTPS on port $PORT)"
cat > appsettings.json <<JSON
{
  "Relay": {
    "Token": "$TOKEN",
    "MaxMessageBytes": 67108864,
    "SessionTimeoutSeconds": 60
  },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:$PORT",
        "Certificate": {
          "Path": "$RELAY_DIR/relay.pfx",
          "Password": "$PFX_PASS"
        }
      }
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
JSON
chmod 600 appsettings.json relay.key relay.pfx relay.crt

# ---------------------------------------------------------------- service user
id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd -r -s /usr/sbin/nologin "$SERVICE_USER"
chown -R "$SERVICE_USER":"$SERVICE_USER" "$RELAY_DIR"

# ---------------------------------------------------------------- systemd
log "Installing the systemd unit"
cat > /etc/systemd/system/rcrelay.service <<UNIT
[Unit]
Description=Rc Relay (self-hosted remote desktop relay)
After=network.target

[Service]
Type=simple
WorkingDirectory=$RELAY_DIR
ExecStart=$RELAY_DIR/rcrelay
Environment=ASPNETCORE_ENVIRONMENT=Production
# The relay ships as a self-contained SINGLE FILE, so the .NET host extracts its native libraries
# into a temp directory at every cold start. Give it a guaranteed-writable directory, and keep the
# filesystem hardening at "full" (read-only /usr,/boot,/etc) instead of "strict" - strict makes the
# whole hierarchy read-only and the extraction then fails with a confusing startup error.
RuntimeDirectory=rcrelay
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/run/rcrelay/bundle
Restart=always
RestartSec=3
User=$SERVICE_USER
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now rcrelay

# ---------------------------------------------------------------- firewall
if command -v ufw >/dev/null 2>&1; then
	log "Allowing $PORT/tcp through ufw"
	ufw allow "$PORT/tcp" >/dev/null || true
fi

# ---------------------------------------------------------------- agent download endpoint
SERVE_AGENT="no"
if [ -n "$AGENT_SRC" ] && [ -f "$AGENT_SRC" ]; then
	if command -v python3 >/dev/null 2>&1; then
		log "Publishing the agent for download on port $DL_PORT"
		install -d -m 0755 "$RELAY_DIR/www"
		install -m 0644 "$AGENT_SRC" "$RELAY_DIR/www/rcagent.exe"

		# Ship the config alongside the binary so the controlled endpoint needs no editing at all.
		cat > "$RELAY_DIR/www/agent.config.json" <<JSON
{
  "relayUrl": "wss://$PUBLIC_IP:$PORT/agent?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "$TOKEN",
  "connectIp": "$PUBLIC_IP",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "$FINGERPRINT",
  "targetFps": 12,
  "jpegQuality": 60,
  "scale": 100,
  "tileSize": 64,
  "keyframeIntervalSeconds": 10
}
JSON
		cat > /etc/systemd/system/rcrelay-download.service <<UNIT
[Unit]
Description=Rc agent download endpoint (static files for the controlled endpoint)
After=network.target

[Service]
Type=simple
WorkingDirectory=$RELAY_DIR/www
ExecStart=/usr/bin/python3 -m http.server $DL_PORT --bind 0.0.0.0 --directory $RELAY_DIR/www
Restart=always
RestartSec=3
User=$SERVICE_USER
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true

[Install]
WantedBy=multi-user.target
UNIT
		chown -R "$SERVICE_USER":"$SERVICE_USER" "$RELAY_DIR/www"
		systemctl daemon-reload
		systemctl enable --now rcrelay-download
		if command -v ufw >/dev/null 2>&1; then
			ufw allow "$DL_PORT/tcp" >/dev/null || true
		fi
		SERVE_AGENT="yes"
	else
		warn "python3 is missing, so the agent will not be published for download."
	fi
elif [ -n "$AGENT_SRC" ]; then
	warn "Agent binary not found at $AGENT_SRC - skipping the download endpoint."
fi

# ---------------------------------------------------------------- verify
log "Waiting for the relay to come up"
OK="no"
for _ in $(seq 1 30); do
	if curl -fsSk --max-time 3 "https://127.0.0.1:$PORT/healthz" 2>/dev/null | grep -q '^ok$'; then
		OK="yes"; break
	fi
	sleep 1
done

echo
echo "================================================================"
if [ "$OK" = "yes" ]; then
	printf '\033[32m Relay is UP on https://%s:%s \033[0m\n' "$PUBLIC_IP" "$PORT"
else
	printf '\033[31m Relay did not answer /healthz. Check: journalctl -u rcrelay -n 50 \033[0m\n'
fi
echo "================================================================"
echo
echo "---- paste into agent.config.json (controlled endpoint) ----"
cat <<JSON
{
  "relayUrl": "wss://$PUBLIC_IP:$PORT/agent?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "$TOKEN",
  "connectIp": "$PUBLIC_IP",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "$FINGERPRINT",
  "targetFps": 12,
  "jpegQuality": 60,
  "scale": 100,
  "tileSize": 64,
  "keyframeIntervalSeconds": 10
}
JSON
echo
echo "---- paste into controller.config.json (home PC) ----"
cat <<JSON
{
  "relayUrl": "wss://$PUBLIC_IP:$PORT/control?id=endpoint-1",
  "agentId": "endpoint-1",
  "token": "$TOKEN",
  "connectIp": "$PUBLIC_IP",
  "allowUntrustedCert": false,
  "pinnedCertSha256": "$FINGERPRINT",
  "jpegQuality": 60,
  "scale": 100
}
JSON
echo
echo "cert sha256 = $FINGERPRINT"
echo "token       = $TOKEN"
echo

if [ "$SERVE_AGENT" = "yes" ]; then
	echo "================================================================"
	echo " HAND THIS ONE LINE to the operator on the controlled endpoint"
	echo "================================================================"
	cat <<CMD
\$d="\$env:USERPROFILE\rcagent"; New-Item -ItemType Directory -Force \$d | Out-Null; curl.exe -sS -o "\$d\rcagent.exe" "http://$PUBLIC_IP:$DL_PORT/rcagent.exe"; curl.exe -sS -o "\$d\agent.config.json" "http://$PUBLIC_IP:$DL_PORT/agent.config.json"; Unblock-File "\$d\rcagent.exe"; Start-Process -FilePath "\$d\rcagent.exe" -WorkingDirectory \$d
CMD
	echo
	echo "It fetches the agent AND its pre-filled config, clears the Mark-of-the-Web, then starts it."
	echo "The agent needs no admin rights and listens on nothing. A tray icon appears when it connects."
	echo
fi

if [ "$SERVE_AGENT" = "yes" ]; then
	warn "Open TCP $PORT AND $DL_PORT in your VPS provider's security group - ufw alone is not enough."
	warn "AFTER the agent is installed and running on the controlled endpoint, shut the download endpoint down:"
	warn "    systemctl disable --now rcrelay-download   # then remove the $DL_PORT security-group rule"
else
	warn "Open TCP $PORT in your VPS provider's security group / firewall too - ufw alone is not enough."
fi
warn "If you ever regenerate the certificate, update pinnedCertSha256 in BOTH configs."
