#!/usr/bin/env bash
set -euo pipefail
# Pins and the host-setup helpers shared with packaging/bin/maldex-setup. Pure bash path math: the gate tests source this file with
# an empty PATH.
_mx_here="${BASH_SOURCE[0]%/*}"; [ "$_mx_here" != "${BASH_SOURCE[0]}" ] || _mx_here=.
# shellcheck source=packaging/lib/maldex-common.sh
source "$_mx_here/packaging/lib/maldex-common.sh"
unset _mx_here
cd "$(dirname "${BASH_SOURCE[0]}")"

# Non-interactive shells may not have dotnet on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi

COMPOSE=(docker compose -f docker-compose.dev.yml --env-file .env)

cmd_setup() {
  if [ ! -f .env ]; then
    # Strong password that satisfies SQL Server complexity rules.
    pw="Mx1!$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
    printf 'MSSQL_SA_PASSWORD=%s\n' "$pw" > .env
    chmod 600 .env
    echo "Generated .env"
  fi
  dotnet tool restore
  # Chromium for PDF reports (Microsoft.Playwright); also installs the OS libraries it needs, which uses sudo.
  dotnet run --project src/Maldex.Web -- --install-browsers
}

cmd_dev_db() {
  [ -f .env ] || cmd_setup
  "${COMPOSE[@]}" up -d
  echo "Waiting for SQL Server to become healthy..."
  for _ in $(seq 1 60); do
    cid="$("${COMPOSE[@]}" ps -q sql)"
    status="$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then echo "SQL Server is healthy on localhost:14333"; return 0; fi
    sleep 5
  done
  echo "SQL Server did not become healthy in time" >&2
  return 1
}

cmd_test() {
  dotnet test --solution Maldex.slnx --ignore-exit-code 8  # 8 = a project has no tests yet
  if [ -f ui/package.json ]; then
    npm --prefix ui test -- --run
  fi
}

# The dev backend needs the SA password that `setup` wrote to .env (appsettings.Development.json carries no secret).
export_dev_connection() {
  [ -f .env ] || cmd_setup
  if [ -z "${ConnectionStrings__Maldex:-}" ]; then
    set -a; . ./.env; set +a
    export ConnectionStrings__Maldex="Server=localhost,14333;Database=maldex;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
  fi
}

# Build the React SPA and replace src/Maldex.Web/wwwroot with it (generated; gitignored), so Maldex.Web hosts it same-origin.
cmd_build_spa() {
  [ -d ui/node_modules ] || npm --prefix ui ci
  npm --prefix ui run build
  rm -rf src/Maldex.Web/wwwroot
  mkdir -p src/Maldex.Web/wwwroot
  cp -R ui/dist/. src/Maldex.Web/wwwroot/
}

cmd_build() {
  cmd_build_spa
  dotnet publish src/Maldex.Web -c Release
}

BACKEND_PID=""
VITE_PID=""
BACKEND_LOG=".data/backend.log"

GUACD_STARTED=0

# Real mode only (MALDEX_MODE=Real): guacd proxies the live view to the VM's VNC console, on 127.0.0.1:4822.
start_guacd() {
  [ "${MALDEX_MODE:-${Maldex__Mode:-Fake}}" = "Real" ] || return 0
  docker rm -f maldex-guacd >/dev/null 2>&1 || true
  docker run -d --rm --name maldex-guacd --network host guacamole/guacd:1.5.5 /opt/guacamole/sbin/guacd -b 127.0.0.1 -L info -f >/dev/null
  GUACD_STARTED=1
  export Maldex__Mode=Real   # the backend then layers src/Maldex.Web/appsettings.Real.json over appsettings.json
}

stop_guacd() {
  if [ "$GUACD_STARTED" = "1" ]; then docker stop maldex-guacd >/dev/null 2>&1 || true; GUACD_STARTED=0; fi
}

stop_children() {
  stop_guacd
  for pid in "$VITE_PID" "$BACKEND_PID"; do
    if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi
  done
  for pid in "$VITE_PID" "$BACKEND_PID"; do
    if [ -n "$pid" ]; then wait "$pid" 2>/dev/null || true; fi
  done
  VITE_PID=""; BACKEND_PID=""
}

port_5080_busy() { ss -ltn 2>/dev/null | grep -q ':5080 '; }

# Build (Debug) and start Maldex.Web on :5080 in the background. Run the dll directly (not `dotnet run`) so the
# PID we hold is the server itself and a plain kill stops it. Extra environment is inherited from the caller.
start_backend() {
  if port_5080_busy; then echo "Something is already listening on :5080; stop it first." >&2; return 1; fi
  export_dev_connection
  dotnet build src/Maldex.Web -c Debug --nologo -v q
  mkdir -p .data
  (
    cd src/Maldex.Web
    ASPNETCORE_ENVIRONMENT=Development exec dotnet bin/Debug/net10.0/Maldex.Web.dll
  ) >"$BACKEND_LOG" 2>&1 &
  BACKEND_PID=$!
}

wait_for_api() {
  echo "Waiting for http://localhost:5080/api/me ..."
  for _ in $(seq 1 120); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5080/api/me || true)" = "200" ]; then
      echo "Backend is up."; return 0
    fi
    if ! kill -0 "$BACKEND_PID" 2>/dev/null; then
      echo "Backend exited early; log:" >&2; tail -n 40 "$BACKEND_LOG" >&2; return 1
    fi
    sleep 1
  done
  echo "Backend did not answer /api/me with 200 in time; log:" >&2
  tail -n 40 "$BACKEND_LOG" >&2
  return 1
}

# Live development: backend on :5080 plus the Vite dev server (which proxies /api and /hubs to it).
cmd_run() {
  cmd_dev_db
  [ -d ui/node_modules ] || npm --prefix ui ci
  trap stop_children EXIT INT TERM
  start_guacd
  start_backend
  wait_for_api
  echo "Backend on http://localhost:5080 (log: $BACKEND_LOG). Starting Vite..."
  npm --prefix ui run dev
}

# End-to-end browser smoke against the HOSTED SPA on :5080 (same origin, no Vite), fake backend at 10x replay speed.
cmd_smoke() {
  cmd_dev_db
  cmd_build_spa
  (cd ui && npx playwright install chromium)
  trap stop_children EXIT INT TERM
  Maldex__Fake__Replay__Speed=10 start_backend
  wait_for_api
  local rc=0
  (cd ui && npx playwright test) || rc=$?
  stop_children
  trap - EXIT INT TERM
  return "$rc"
}

# ---- Real mode (Plan 1b) host toolchain ----

GUACD_IMAGE="guacamole/guacd:1.5.5"
ALPINE_ISO_URL="https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/x86_64/alpine-virt-3.20.3-x86_64.iso"
ALPINE_ISO_SHA256="81df854fbd7327d293c726b1eeeb82061d3bc8f5a86a6f77eea720f6be372261"
OVMF_CODE_SECBOOT="/usr/share/OVMF/OVMF_CODE_4M.secboot.fd"
OVMF_CODE_PLAIN="/usr/share/OVMF/OVMF_CODE_4M.fd"
LIBVIRT_URI="qemu:///session"
VM_DIR=".data/vm"
GUARD_SRC="deploy/host/maldex-host-guard.nft"
GUARD_DST="/etc/maldex/maldex-host-guard.nft"
GUARD_UNIT="/etc/systemd/system/maldex-host-guard.service"
DOCKER_USER_SRC="deploy/host/maldex-docker-user.sh"
DOCKER_USER_DST="/etc/maldex/maldex-docker-user.sh"
DOCKER_USER_UNIT="/etc/systemd/system/maldex-docker-user.service"

# Install the host guard persistently: copy to /etc/maldex, load now, and re-load at every boot via a oneshot unit.
install_host_guard() {
  sudo install -d -m 0755 /etc/maldex
  sudo install -m 0644 "$GUARD_SRC" "$GUARD_DST"
  sudo tee "$GUARD_UNIT" >/dev/null <<UNIT
[Unit]
Description=Maldex host guard (nftables)
Documentation=file://$GUARD_DST
DefaultDependencies=no
After=nftables.service network-pre.target
Before=network.target
Wants=network-pre.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/sbin/nft -f $GUARD_DST

[Install]
WantedBy=multi-user.target
UNIT
  sudo systemctl daemon-reload
  sudo systemctl enable maldex-host-guard.service
  sudo systemctl restart maldex-host-guard.service
}

# True when Docker's systemd unit exists on this host.
docker_installed() { systemctl cat docker.service >/dev/null 2>&1; }

# Direct egress (template edit sessions) needs a narrow exception in Docker's DOCKER-USER chain (Docker's FORWARD policy is DROP):
# exactly two rules for the default-route interface, tagged maldex-direct (see deploy/host/maldex-docker-user.sh). Re-applied at
# every docker.service start; nothing else in Docker's chains, tables or settings is touched. (Approved by basht0p, 2026-10-24.)
install_direct_docker_exception() {
  if ! docker_installed; then
    echo "skip Direct egress DOCKER-USER exception: Docker is not installed (the FORWARD drop it works around only exists with Docker)"
    return 0
  fi
  sudo install -d -m 0755 /etc/maldex
  sudo install -m 0755 "$DOCKER_USER_SRC" "$DOCKER_USER_DST"
  sudo tee "$DOCKER_USER_UNIT" >/dev/null <<UNIT
[Unit]
Description=Maldex Direct egress exception in Docker's DOCKER-USER chain
Documentation=file://$DOCKER_USER_DST
Wants=network-online.target
After=docker.service network-online.target
PartOf=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=$DOCKER_USER_DST
ExecStop=$DOCKER_USER_DST stop

[Install]
WantedBy=docker.service multi-user.target
UNIT
  sudo systemctl daemon-reload
  sudo systemctl enable maldex-docker-user.service
  sudo systemctl restart maldex-docker-user.service
}

# True when both maldex-direct rules exist in DOCKER-USER for the current default-route interface.
direct_docker_exception_present() {
  local i
  i=$(ip -4 -o route show default | awk '{for (k = 1; k < NF; k++) if ($k == "dev") { print $(k + 1); exit }}')
  [ -n "$i" ] || return 1
  sudo -n iptables -C DOCKER-USER -i 'mxu+' -o "$i" -m comment --comment maldex-direct -j ACCEPT \
    && sudo -n iptables -C DOCKER-USER -i "$i" -o 'mxu+' -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT
}

# mitmproxy's CA (the guest image trusts it); generated once, copied per run into a private confdir for maldex-mitm.
MITM_CA_DIR=".data/mitm/ca"

# libguestfs's fixed appliance for the guest disk editor (see ensure_guestfs_appliance in packaging/lib/maldex-common.sh).
GUESTFS_APPLIANCE_DIR="/var/lib/maldex/guestfs-appliance"

# Stop and disable a system unit Maldex runs per run; tolerate only a missing unit, then verify it is inactive.
disable_system_unit() {
  local unit="$1"
  if systemctl list-unit-files "$unit.service" 2>/dev/null | grep -q "^$unit.service"; then
    # Stop explicitly: `disable --now` does not stop SysV-style units (inetsim) on this host.
    sudo systemctl stop "$unit.service"
    sudo systemctl disable "$unit.service"
  fi
  if systemctl is-active --quiet "$unit.service"; then
    echo "$unit.service is still active after disable" >&2; return 1
  fi
}

# The pinned Sysmon.zip the provisioning CD of a Build from ISO carries (Sysmon64.exe is extracted from it).
ensure_sysmon_zip() {
  mkdir -p "$VM_DIR/iso"
  fetch_cached "$SYSMON_URL" "$VM_DIR/iso/Sysmon.zip" "$SYSMON_SHA256"
}

cmd_real_setup() {
  local me="${SUDO_USER:-$(id -un)}"
  sudo apt-get update -qq
  sudo apt-get install -y qemu-system-x86 qemu-utils libvirt-daemon libvirt-clients virtinst ovmf swtpm swtpm-tools \
    dnsmasq-base nftables tcpdump suricata inetsim wireguard-tools xorriso libxml2-utils socat genisoimage wimtools \
    libguestfs-tools

  if ! command -v pwsh >/dev/null 2>&1; then
    sudo snap install powershell --classic   # script parse checks only
  fi

  if ! { [ -x /opt/mitmproxy/mitmdump ] && /opt/mitmproxy/mitmdump --version 2>/dev/null | grep -q "$MITMPROXY_VERSION"; }; then
    local tmp; tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN
    curl -fsSL --retry 3 -o "$tmp/mitmproxy.tar.gz" \
      "https://downloads.mitmproxy.org/$MITMPROXY_VERSION/mitmproxy-$MITMPROXY_VERSION-linux-x86_64.tar.gz"
    verify_sha256 "$tmp/mitmproxy.tar.gz" "$MITMPROXY_SHA256"
    sudo mkdir -p /opt/mitmproxy
    sudo tar -xzf "$tmp/mitmproxy.tar.gz" -C /opt/mitmproxy
    rm -rf "$tmp"; trap - RETURN
  fi
  sudo ln -sf /opt/mitmproxy/mitmdump /usr/local/bin/mitmdump

  docker pull "$GUACD_IMAGE"

  mkdir -p "$VM_DIR/base" "$VM_DIR/run" "$VM_DIR/iso"
  fetch_cached "$VIRTIO_WIN_URL" "$VM_DIR/iso/virtio-win.iso" "$VIRTIO_WIN_SHA256"

  # kvm group for future logins; ACL so this session can use /dev/kvm without re-login.
  sudo usermod -aG kvm "$me"
  # udev re-creates/re-triggers /dev/kvm (apt installs, hotplug events) and drops the ACL, so also re-apply it from a rule.
  echo "KERNEL==\"kvm\", ACTION==\"add|change\", RUN+=\"/usr/bin/setfacl -m u:$me:rw \$env{DEVNAME}\"" \
    | sudo tee /etc/udev/rules.d/99-maldex-kvm.rules >/dev/null
  sudo udevadm control --reload
  if ! { [ -r /dev/kvm ] && [ -w /dev/kvm ]; }; then
    sudo setfacl -m "u:$me:rw" /dev/kvm
  fi

  # Maldex runs these per run / on demand, not as system services.
  disable_system_unit inetsim
  disable_system_unit suricata

  install_host_guard
  install_direct_docker_exception
  local u
  for u in maldex-mitm maldex-dns maldex-isroot maldex-isuser; do ensure_system_user "$u"; done
  ensure_mitm_ca "$MITM_CA_DIR" "$(id -un)"
  harden_inetsim_template
  ensure_guestfs_appliance "$GUESTFS_APPLIANCE_DIR"
  mkdir -p .data/media
  ensure_sysmon_zip

  real_verify
}

real_verify() {
  REAL_FAILED=0
  real_check "/dev/kvm readable+writable" "add user to kvm group / sudo setfacl -m u:\$USER:rw /dev/kvm" \
    bash -c '[ -r /dev/kvm ] && [ -w /dev/kvm ]'
  real_check "virsh $LIBVIRT_URI" "libvirt-clients, libvirt-daemon" virsh -c "$LIBVIRT_URI" version
  real_check "OVMF secboot firmware" "apt install ovmf" test -f "$OVMF_CODE_SECBOOT"
  real_check "swtpm" "apt install swtpm swtpm-tools" swtpm --version
  real_check "suricata" "apt install suricata" bash -c 'suricata --build-info | head -1'
  real_check "mitmdump" "./build.sh real-setup (mitmproxy $MITMPROXY_VERSION)" mitmdump --version
  real_check "guacd image" "docker pull $GUACD_IMAGE" docker image inspect "$GUACD_IMAGE"
  real_check "inetsim" "apt install inetsim" inetsim --version
  real_check "wireguard-tools" "apt install wireguard-tools" wg --version
  real_check "wiminfo" "apt install wimtools" wiminfo --version
  real_check "xorriso" "apt install xorriso" xorriso -version
  real_check "xmllint" "apt install libxml2-utils" xmllint --version
  real_check "Sysmon.zip" "./build.sh real-setup" test -f "$VM_DIR/iso/Sysmon.zip"
  real_check "virtio-win.iso" "./build.sh real-setup" test -f "$VM_DIR/iso/virtio-win.iso"
  real_check "media library (.data/media)" "./build.sh real-setup" test -d .data/media
  real_check "pwsh" "sudo snap install powershell --classic" pwsh -v
  real_check "host guard table loaded" "./build.sh real-setup" sudo -n nft list table inet maldex_guard
  real_check "br_netfilter not filtering bridged traffic (host guard assumes this)" "sudo sysctl -w net.bridge.bridge-nf-call-iptables=0 and do not load br_netfilter" \
    bash -c '[ ! -e /proc/sys/net/bridge/bridge-nf-call-iptables ] || [ "$(cat /proc/sys/net/bridge/bridge-nf-call-iptables)" != 1 ]'
  real_check "maldex-mitm user exists with no supplementary groups" "./build.sh real-setup" \
    bash -c '[ "$(id -nG maldex-mitm)" = maldex-mitm ] && ! sudo -n grep -rqs maldex-mitm /etc/sudoers /etc/sudoers.d'
  real_check "maldex-dns user exists with no supplementary groups" "./build.sh real-setup" \
    bash -c '[ "$(id -nG maldex-dns)" = maldex-dns ] && [ "$(id -u maldex-dns)" != 0 ] && ! sudo -n grep -rqs maldex-dns /etc/sudoers /etc/sudoers.d'
  for u in maldex-isroot maldex-isuser; do
    real_check "$u user exists, non-root, own group only, no sudoers" "./build.sh real-setup" \
      bash -c "[ \"\$(id -nG $u)\" = $u ] && [ \"\$(id -u $u)\" != 0 ] && ! sudo -n grep -rqs $u /etc/sudoers /etc/sudoers.d"
  done
  real_check "INetSim user-namespace ids are distinct" "./build.sh real-setup" \
    bash -c '[ "$(id -u maldex-isroot)" != "$(id -u maldex-isuser)" ] && [ "$(id -g maldex-isroot)" != "$(id -g maldex-isuser)" ]'
  real_check "mitmproxy CA present (.data/mitm/ca)" "./build.sh real-setup" \
    bash -c "[ -f $MITM_CA_DIR/mitmproxy-ca.pem ] && [ -f $MITM_CA_DIR/mitmproxy-ca-cert.pem ]"
  real_check "/var/lib/inetsim is root-owned, not group/world-writable, symlink-free" "./build.sh real-setup" inetsim_template_clean
  if docker_installed; then
    real_check "Direct egress exception in DOCKER-USER (both maldex-direct rules, current default interface)" "./build.sh real-setup" direct_docker_exception_present
    real_check "Direct egress exception enabled at boot (maldex-docker-user)" "./build.sh real-setup" systemctl is-enabled maldex-docker-user
  else
    echo "skip Direct egress DOCKER-USER exception checks (Docker is not installed; no FORWARD drop to work around)"
  fi
  real_check "guest disk editor (libguestfs appliance)" "./build.sh real-setup" \
    env LIBGUESTFS_BACKEND=direct "LIBGUESTFS_PATH=$GUESTFS_APPLIANCE_DIR" libguestfs-test-tool
  real_check "host guard enabled at boot" "./build.sh real-setup" systemctl is-enabled maldex-host-guard
  return "$REAL_FAILED"
}

# Proves session libvirt + KVM + OVMF + TPM 2.0 work, using a throwaway Alpine domain (no Windows media).
cmd_real_smoke() {
  local name="maldex-smoke" iso="$VM_DIR/iso/alpine-virt-3.20.3-x86_64.iso"
  mkdir -p "$VM_DIR/iso"
  fetch_cached "$ALPINE_ISO_URL" "$iso" "$ALPINE_ISO_SHA256"
  [ -f "$OVMF_CODE_SECBOOT" ] || { echo "Missing $OVMF_CODE_SECBOOT; run ./build.sh real-setup" >&2; return 1; }
  # Fail instead of silently falling back to software emulation.
  { [ -r /dev/kvm ] && [ -w /dev/kvm ]; } || { echo "/dev/kvm is not usable by this session; run ./build.sh real-setup" >&2; return 1; }
  virsh -c "$LIBVIRT_URI" domcapabilities --virttype kvm >/dev/null 2>&1 \
    || { echo "libvirt reports no KVM support for $LIBVIRT_URI" >&2; return 1; }

  virsh -c "$LIBVIRT_URI" destroy "$name" >/dev/null 2>&1 || true
  smoke_cleanup() { virsh -c "$LIBVIRT_URI" destroy "$name" >/dev/null 2>&1 || true; trap - RETURN; }
  trap smoke_cleanup RETURN

  # Alpine is not Secure-Boot-signed, so Secure Boot is off for this domain only; the point is OVMF + TPM + KVM.
  # Prefer the secboot CODE file; fall back to the plain 4M CODE file if virt-install refuses it with secure=no.
  local code osinfo=(--osinfo alpinelinux3.19) started=0
  if ! virt-osinfo-query os short-id=alpinelinux3.19 >/dev/null 2>&1; then osinfo=(--osinfo detect=on,require=off); fi
  for code in "$OVMF_CODE_SECBOOT" "$OVMF_CODE_PLAIN"; do
    [ -f "$code" ] || continue
    if virt-install --connect "$LIBVIRT_URI" --transient --name "$name" --memory 512 --vcpus 1 --cdrom "$iso" --disk none \
        --boot "uefi,loader=$code,loader.readonly=yes,loader.type=pflash,loader.secure=no" \
        --tpm emulator,model=tpm-crb,version=2.0 --network none --graphics none --noautoconsole \
        "${osinfo[@]}"; then
      started=1; echo "Smoke domain started with firmware $code"; break
    fi
    echo "virt-install failed with $code" >&2
  done
  [ "$started" = 1 ] || { echo "Could not start smoke domain" >&2; return 1; }

  local state=""
  for _ in $(seq 1 30); do
    state="$(virsh -c "$LIBVIRT_URI" domstate "$name" 2>/dev/null || true)"
    [ "$state" = "running" ] && break
    sleep 1
  done
  [ "$state" = "running" ] || { echo "Smoke domain did not reach running (state: $state)" >&2; return 1; }
  sleep 10
  state="$(virsh -c "$LIBVIRT_URI" domstate "$name" 2>/dev/null || true)"
  [ "$state" = "running" ] || { echo "Smoke domain stopped early (state: $state)" >&2; return 1; }

  virsh -c "$LIBVIRT_URI" destroy "$name"
  if virsh -c "$LIBVIRT_URI" list --all --name | grep -qx "$name"; then
    echo "$name still defined after destroy" >&2; return 1
  fi
  echo "real-smoke OK: OVMF + TPM 2.0 + KVM work under $LIBVIRT_URI"
}

# Publishes the guest agent (self-contained single-file win-x64) to .data/agent/Maldex.Agent.exe. Used by template sealing and the Build from ISO.
cmd_agent() {
  dotnet publish src/Maldex.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "$PWD/.data/agent" --nologo -v q
  [ -f .data/agent/Maldex.Agent.exe ] || { echo "Agent publish did not produce Maldex.Agent.exe" >&2; return 1; }
  # The agent reports "<version>+<12 hex of its exe>"; the hash half the host computes from the exe, the version half is
  # recorded here from the same project properties the exe was just compiled with (a Windows exe's file version is the .NET host's).
  local version
  version="$(dotnet msbuild src/Maldex.Agent -getProperty:Version -p:Configuration=Release -p:RuntimeIdentifier=win-x64 --nologo | head -n 1 | cut -d+ -f1 | tr -d '[:space:]')"
  [ -n "$version" ] || { echo "Could not read the agent version" >&2; return 1; }
  printf '%s\n' "$version" > .data/agent/Maldex.Agent.version
}

# build-guest, prepare-guest, finalize-guest and discard-guest are retired: the app builds and edits templates.
cmd_retired() {
  echo "retired: use Admin → Sandbox Templates → New template (build from media) or Edit" >&2
  return 2
}

# The code a real template E2E (./build.sh e2e-templates) exercises. When any of it changed since the commit recorded in
# $TEMPLATE_E2E_STAMP, ./build.sh e2e points at e2e-templates. The one list for both the notice and the stamp.
TEMPLATE_E2E_PATHS=(
  src/Maldex.Orchestrator/Templates/
  src/Maldex.Orchestrator/VmSlotGate.cs
  src/Maldex.Orchestrator/Reconciler.cs
  src/Maldex.Agent/
  src/Maldex.Core/Agent/
  'src/Maldex.Core/Domain/TemplateJob*'
  'src/Maldex.Core/Lifecycle/TemplateJob*'
  src/Maldex.Data/Templates/
  src/Maldex.Data/VmSlots.cs
  src/Maldex.Infrastructure/Libvirt/
  src/Maldex.Infrastructure/Agent/
  src/Maldex.Infrastructure/Net/
  src/Maldex.Infrastructure/Isolation/
  src/Maldex.Web/Endpoints/TemplateEndpoints.cs
  src/Maldex.Web/Endpoints/GuacamoleTunnelEndpoint.cs
  src/Maldex.Web/Endpoints/MediaEndpoints.cs
  src/Maldex.Core/Media/
  src/Maldex.Infrastructure/Media/
  src/Maldex.Orchestrator/Media/
  deploy/guest/provision/
  deploy/guest/autounattend/
  deploy/host/
  src/Maldex.Orchestrator/SelfTestGate.cs
  src/Maldex.Orchestrator/TransferInbox.cs
  src/Maldex.Orchestrator/DependencyInjection.cs
  src/Maldex.Core/Abstractions/
  src/Maldex.Infrastructure/Execution/
  src/Maldex.Infrastructure/Real/
  src/Maldex.Infrastructure/DependencyInjection.cs
  src/Maldex.Web/Templates/TemplateProvenance.cs
  tests/Maldex.Integration.Tests/E2E/RealTemplateJobTests.cs
  tests/Maldex.Integration.Tests/E2E/RealGuestDiskEditorTests.cs
  tests/Maldex.Integration.Tests/E2E/RealVmE2ETests.cs
  tests/Maldex.Integration.Tests/E2E/ProvenanceRoundTrips.cs
  tests/Maldex.Integration.Tests/E2E/RealTemplateBuildTests.cs
)
TEMPLATE_E2E_STAMP=".data/e2e-templates.validated"

# Exit 0 when a real template E2E is due (the reason and the changed files go to stdout), 1 when the stamp covers HEAD: the stamp
# names a commit that is an ancestor of HEAD, nothing under TEMPLATE_E2E_PATHS changed since it, and nothing there is uncommitted.
template_e2e_check() {
  local sha="" changed
  [ -f "$TEMPLATE_E2E_STAMP" ] || { echo "no validated template E2E run is recorded ($TEMPLATE_E2E_STAMP)"; return 0; }
  sha="$(tr -d '[:space:]' < "$TEMPLATE_E2E_STAMP")"
  if [ -z "$sha" ] || ! git cat-file -e "$sha^{commit}" 2>/dev/null || ! git merge-base --is-ancestor "$sha" HEAD 2>/dev/null; then
    echo "the commit recorded in $TEMPLATE_E2E_STAMP ('${sha:-empty}') is not an ancestor of HEAD"
    return 0
  fi
  local due=1
  changed="$(git diff --name-only "$sha" HEAD -- "${TEMPLATE_E2E_PATHS[@]}")"
  if [ -n "$changed" ]; then
    echo "changed since the last validated template E2E ($sha):"
    printf '%s\n' "$changed" | sed 's/^/  /'
    due=0
  fi
  # Uncommitted or untracked files under the watched paths (porcelain: "XY path").
  changed="$(git status --porcelain --untracked-files=all -- "${TEMPLATE_E2E_PATHS[@]}" | cut -c4-)"
  if [ -n "$changed" ]; then
    echo "uncommitted changes in template-affecting paths:"
    printf '%s\n' "$changed" | sed 's/^/  /'
    due=0
  fi
  return "$due"
}

# The end of ./build.sh e2e: say when the real template E2E is due; ask only on a terminal (default No).
template_e2e_gate_notice() {
  local out answer
  out="$(template_e2e_check)" || return 0
  echo
  echo "NOTE: the real template E2E (RealTemplateJobTests, RealGuestDiskEditorTests, RealTemplateBuildTests) is due; plain e2e does not run it."
  printf '%s\n' "$out"
  echo "Run it with: MALDEX_WIN_ISO=/path/to/win11.iso ./build.sh e2e-templates"
  if [ -t 0 ] && [ -t 1 ]; then
    read -r -p "run ./build.sh e2e-templates now? [y/N] " answer || answer=""
    case "$answer" in y|Y|yes|YES) cmd_e2e_templates ;; esac
  fi
  return 0
}

require_base_image() {
  local base="$VM_DIR/base"
  if [ ! -f "$base/win11-26h2.qcow2" ] || [ ! -f "$base/win11-26h2.vars.fd" ]; then
    echo "The Windows 11 26H2 base image is missing ($base/win11-26h2.qcow2 and .vars.fd)." >&2
    echo "Build it first: Admin → Sandbox Templates → New template (build from media), or import an existing image (Maldex:Images)." >&2
    return 2
  fi
}

# Harmless real-VM end-to-end suite (the tests skip with a message when the default template's image is missing; see deploy/guest/README.md "Running the E2E suite").
cmd_e2e() {
  local rc=0
  MALDEX_REAL_VM=1 MALDEX_REAL_NET=1 dotnet test --project tests/Maldex.Integration.Tests --filter-class "*RealVmE2ETests" || rc=$?
  template_e2e_gate_notice
  return "$rc"
}

# The Build-from-ISO E2E targets need installer media: $1 names the target in the refusal. Makes MALDEX_WIN_ISO absolute.
require_win_iso() {
  if [ -z "${MALDEX_WIN_ISO:-}" ] || [ ! -f "${MALDEX_WIN_ISO:-}" ]; then
    echo "$1: MALDEX_WIN_ISO must point to a Windows 11 x64 installer ISO to build a template from (got: '${MALDEX_WIN_ISO:-}')." >&2
    return 2
  fi
  MALDEX_WIN_ISO="$(realpath -- "$MALDEX_WIN_ISO")"
  export MALDEX_WIN_ISO
}

# The real template-job E2E (Refresh, Edit with Direct network, Roll back, guest disk editor, Build from MALDEX_WIN_ISO): about two
# to three hours, so it runs only when affecting code changed. A full green run records HEAD in $TEMPLATE_E2E_STAMP. It refuses
# without MALDEX_WIN_ISO (the Build test would only skip, and a skip never validates). Optional $1: one test (method-name part).
cmd_e2e_templates() {
  local filter="${1:-}" log rc=0 head dirty
  require_win_iso e2e-templates || return $?
  require_base_image || return $?
  case "$filter" in *[!A-Za-z0-9_.*]*) echo "e2e-templates: the filter may contain only letters, digits, _ . and *" >&2; return 64 ;; esac
  if [ ! -f .data/agent/Maldex.Agent.exe ] || [ ! -f .data/agent/Maldex.Agent.version ]; then
    echo "The guest agent build is missing; running ./build.sh agent first."
    cmd_agent
  fi
  local args=(--project tests/Maldex.Integration.Tests --filter-class "*RealTemplateJobTests" --filter-class "*RealGuestDiskEditorTests"
    --filter-class "*RealTemplateBuildTests")
  [ -z "$filter" ] || args+=(--filter-method "*$filter*")
  log="$(mktemp)"
  MALDEX_REAL_VM=1 MALDEX_REAL_NET=1 MALDEX_REAL_TEMPLATES=1 dotnet test "${args[@]}" 2>&1 | tee "$log" || rc=$?
  if [ "$rc" -ne 0 ]; then rm -f "$log"; return "$rc"; fi
  if [ -n "$filter" ]; then
    rm -f "$log"; echo "Filtered run passed; $TEMPLATE_E2E_STAMP not updated (only a full run validates)."; return 0
  fi
  if ! grep -Eq '^[[:space:]]*skipped: 0[[:space:]]*$' "$log" || grep -Eq '^[[:space:]]*total: 0[[:space:]]*$' "$log"; then
    rm -f "$log"; echo "Some template E2E tests were skipped or none ran; $TEMPLATE_E2E_STAMP not updated." >&2; return 1
  fi
  rm -f "$log"
  dirty="$(git status --porcelain --untracked-files=all -- "${TEMPLATE_E2E_PATHS[@]}")"
  if [ -n "$dirty" ]; then
    echo "Green, but $TEMPLATE_E2E_STAMP not updated: uncommitted changes in template-affecting paths (commit them and run again):" >&2
    printf '%s\n' "$dirty" >&2
    return 0
  fi
  head="$(git rev-parse HEAD)"
  mkdir -p "$(dirname "$TEMPLATE_E2E_STAMP")"
  printf '%s\n' "$head" > "$TEMPLATE_E2E_STAMP"
  echo "Template E2E validated at $head ($TEMPLATE_E2E_STAMP)."
}

# Interactive acceptance of a Build from MALDEX_WIN_ISO (RealTemplateBuildAcceptanceTests; a person runs it, it is never part of
# e2e-templates and never writes the stamp). The web host listens on localhost:5080 only (DevAuth): open it through an SSH forward
# (ssh -L 5080:localhost:5080 <user>@<host>), turn off Tamper Protection and Defender in the job's console and press Seal.
cmd_e2e_build_acceptance() {
  local rc=0
  require_win_iso e2e-build-acceptance || return $?
  require_base_image || return $?
  if port_5080_busy; then echo "e2e-build-acceptance: something is already listening on :5080; stop it first." >&2; return 1; fi
  if [ ! -f .data/agent/Maldex.Agent.exe ] || [ ! -f .data/agent/Maldex.Agent.version ]; then
    echo "The guest agent build is missing; running ./build.sh agent first."
    cmd_agent
  fi
  cmd_build_spa   # the job page and its console are the hosted SPA
  trap stop_guacd EXIT INT TERM
  MALDEX_MODE=Real start_guacd
  MALDEX_REAL_VM=1 MALDEX_REAL_NET=1 MALDEX_REAL_TEMPLATES=1 MALDEX_E2E_INTERACTIVE=1 \
    dotnet test --project tests/Maldex.Integration.Tests --filter-class "*RealTemplateBuildAcceptanceTests" || rc=$?
  stop_guacd
  trap - EXIT INT TERM
  return "$rc"
}

# Sourced by the gate tests (tests/Maldex.Core.Tests/Build); only dispatch when executed.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  case "${1:-}" in
    setup)  cmd_setup ;;
    dev-db) cmd_dev_db ;;
    test)   cmd_test ;;
    run)    cmd_run ;;
    smoke)  cmd_smoke ;;
    build)  cmd_build ;;
    real-setup) cmd_real_setup ;;
    real-smoke) cmd_real_smoke ;;
    agent) cmd_agent ;;
    build-guest|prepare-guest|finalize-guest|discard-guest) cmd_retired ;;
    e2e)    cmd_e2e ;;
    e2e-templates) cmd_e2e_templates "${2:-}" ;;
    e2e-build-acceptance) cmd_e2e_build_acceptance ;;
    *) echo "usage: $0 setup|dev-db|run|test|smoke|build|agent|real-setup|real-smoke|e2e|e2e-templates [test-filter]|e2e-build-acceptance" >&2; exit 64 ;;
  esac
fi
