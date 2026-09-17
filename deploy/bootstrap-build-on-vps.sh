#!/usr/bin/env bash
#
# Build the Rc artifacts ON the VPS from source.
#
# Why this exists: uploading the published binaries from a home connection is slow
# (~145 MB, and flaky on a long-haul link). The source tarball is only ~50 KB, and an overseas
# VPS pulls the .NET SDK and runtime packs from Microsoft/NuGet far faster than a home uplink
# can push them.
#
# Usage (on the VPS, as root), after uploading rc-src.tar.gz:
#   scp rc-src.tar.gz deploy/bootstrap-build-on-vps.sh root@<vps>:/root/
#   ssh root@<vps> 'bash /root/bootstrap-build-on-vps.sh'
#
# Produces:
#   /root/rcrelay     - linux-x64 self-contained single file (for the relay)
#   /root/rcagent.exe - win-x64 self-contained single file (served to the controlled endpoint)

set -euo pipefail

SRC_TARBALL="${1:-/root/rc-src.tar.gz}"
SRC_DIR="${SRC_DIR:-/root/rc-src}"
BUILD_DIR="${BUILD_DIR:-/root/rc-build}"
SDK_DIR="${SDK_DIR:-/usr/share/dotnet}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

log() { printf '\033[36m==> %s\033[0m\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { echo "Run me as root." >&2; exit 1; }
[ -f "$SRC_TARBALL" ] || { echo "Source tarball not found: $SRC_TARBALL" >&2; exit 1; }

# ---------------------------------------------------------------- .NET SDK
if ! command -v dotnet >/dev/null 2>&1; then
	log "Installing the .NET 10 SDK into $SDK_DIR"
	curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
	bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$SDK_DIR" --no-path
	ln -sf "$SDK_DIR/dotnet" /usr/local/bin/dotnet
fi
log "dotnet $(dotnet --version)"

# ---------------------------------------------------------------- sources
log "Unpacking sources into $SRC_DIR"
rm -rf "$SRC_DIR" "$BUILD_DIR"
mkdir -p "$SRC_DIR" "$BUILD_DIR"
tar -xzf "$SRC_TARBALL" -C "$SRC_DIR"

# ---------------------------------------------------------------- relay (linux-x64)
log "Building the relay for linux-x64 (this downloads the runtime pack the first time)"
dotnet publish "$SRC_DIR/src/Rc.Relay/Rc.Relay.csproj" \
	-c Release -r linux-x64 --self-contained true \
	-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
	-o "$BUILD_DIR/relay"

# ---------------------------------------------------------------- agent (win-x64)
# EnableWindowsTargeting is required to cross-compile a net*-windows project from Linux.
log "Building the agent for win-x64 (cross-compile)"
dotnet publish "$SRC_DIR/src/Rc.Agent/Rc.Agent.csproj" \
	-c Release -r win-x64 --self-contained true \
	-p:EnableWindowsTargeting=true \
	-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
	-p:EnableCompressionInSingleFile=true \
	-o "$BUILD_DIR/agent"

# ---------------------------------------------------------------- stage for the setup script
log "Staging binaries where setup-relay-vps.sh expects them"
install -m 0755 "$BUILD_DIR/relay/rcrelay" /root/rcrelay
install -m 0644 "$BUILD_DIR/agent/rcagent.exe" /root/rcagent.exe

echo
ls -la /root/rcrelay /root/rcagent.exe
echo
echo "BOOTSTRAP OK"
