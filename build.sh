#!/usr/bin/env bash
set -euo pipefail
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

MITMPROXY_VERSION="11.1.3"
MITMPROXY_SHA256="f3f9aa0f719cfda9dac943891b6f3475bb869aa7a6115ee9e17c7e1c88bbc854"
GUACD_IMAGE="guacamole/guacd:1.5.5"
VIRTIO_WIN_VERSION="0.1.302-1"
VIRTIO_WIN_URL="https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-$VIRTIO_WIN_VERSION/virtio-win.iso"
VIRTIO_WIN_SHA256="303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d"
ALPINE_ISO_URL="https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/x86_64/alpine-virt-3.20.3-x86_64.iso"
ALPINE_ISO_SHA256="81df854fbd7327d293c726b1eeeb82061d3bc8f5a86a6f77eea720f6be372261"
OVMF_CODE_SECBOOT="/usr/share/OVMF/OVMF_CODE_4M.secboot.fd"
OVMF_CODE_PLAIN="/usr/share/OVMF/OVMF_CODE_4M.fd"
LIBVIRT_URI="qemu:///session"
VM_DIR=".data/vm"
GUARD_SRC="deploy/host/maldex-host-guard.nft"
GUARD_DST="/etc/maldex/maldex-host-guard.nft"
GUARD_UNIT="/etc/systemd/system/maldex-host-guard.service"

# Verify file $1 against SHA-256 $2; on mismatch delete it and fail loudly.
verify_sha256() {
  local file="$1" want="$2"
  if ! echo "$want  $file" | sha256sum -c --status -; then
    echo "SHA-256 mismatch for $file (expected $want, got $(sha256sum "$file" | cut -d' ' -f1)); deleting." >&2
    rm -f "$file"
    return 1
  fi
}

# Download $1 to $2 unless it already exists (atomic via .part), then verify it against SHA-256 $3.
fetch_cached() {
  local url="$1" dest="$2" sha="$3"
  if [ ! -s "$dest" ]; then
    echo "Downloading $url"
    curl -fsSL --retry 3 -o "$dest.part" "$url"
    mv "$dest.part" "$dest"
  fi
  verify_sha256 "$dest" "$sha"
}

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

# Dedicated, powerless account for the per-run mitmproxy: own group, no supplementary groups, no home, no shell, no sudoers.
ensure_mitm_user() {
  if ! id maldex-mitm >/dev/null 2>&1; then
    sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin maldex-mitm
  fi
}

# Dedicated, powerless account for the per-run dnsmasq: own group, no supplementary groups, no home, no shell, no sudoers.
# It runs with no capability at all (see NetnsEgressRouter).
ensure_dns_user() {
  if ! id maldex-dns >/dev/null 2>&1; then
    sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin maldex-dns
  fi
}

# INetSim insists on root; it runs in a user namespace whose root is maldex-isroot and whose 'inetsim' is maldex-isuser.
# Both are dedicated, powerless system accounts: own group only, no home, no shell, no sudoers, own nothing on the host.
ensure_inetsim_userns_ids() {
  local u
  for u in maldex-isroot maldex-isuser; do
    if ! id "$u" >/dev/null 2>&1; then
      sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin "$u"
    fi
  done
}

# mitmproxy's CA (the guest image trusts it); generated once, copied per run into a private confdir for maldex-mitm.
MITM_CA_DIR=".data/mitm/ca"
ensure_mitm_ca() {
  mkdir -p "$MITM_CA_DIR"
  chmod 0700 "$MITM_CA_DIR"
  if [ ! -f "$MITM_CA_DIR/mitmproxy-ca.pem" ]; then
    # mitmdump writes the CA at startup and then idles; stop it after a few seconds.
    timeout 8 mitmdump --set "confdir=$MITM_CA_DIR" -n -q >/dev/null 2>&1 || true
  fi
  [ -f "$MITM_CA_DIR/mitmproxy-ca.pem" ] && [ -f "$MITM_CA_DIR/mitmproxy-ca-cert.pem" ]
}

# INetSim's template data is only ever read (as root) and copied per run, so it must be root-owned, not writable by others
# and free of symlinks; the guest-facing INetSim user gets only fresh per-run leaf directories.
harden_inetsim_template() {
  sudo chown -R root:root /var/lib/inetsim
  sudo chmod -R go-w /var/lib/inetsim
  sudo chmod 0755 /var/lib/inetsim
}

inetsim_template_clean() {
  [ -z "$(sudo find /var/lib/inetsim \( -type l -o ! -user root -o -perm /022 \) -print -quit)" ]
}

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

cmd_real_setup() {
  local me="${SUDO_USER:-$(id -un)}"
  sudo apt-get update -qq
  sudo apt-get install -y qemu-system-x86 qemu-utils libvirt-daemon libvirt-clients virtinst ovmf swtpm swtpm-tools \
    dnsmasq-base nftables tcpdump suricata inetsim wireguard-tools xorriso libxml2-utils socat genisoimage wimtools

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
  ensure_mitm_user
  ensure_dns_user
  ensure_inetsim_userns_ids
  ensure_mitm_ca
  harden_inetsim_template

  real_verify
}

# Run "$@" quietly; on failure print the hint and record the failure.
REAL_FAILED=0
real_check() {
  local what="$1" hint="$2"; shift 2
  if "$@" >/dev/null 2>&1; then
    echo "ok   $what"
  else
    echo "FAIL $what -- install/fix: $hint" >&2
    REAL_FAILED=1
  fi
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
  real_check "xorriso" "apt install xorriso" xorriso -version
  real_check "xmllint" "apt install libxml2-utils" xmllint --version
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

# Unattended Windows 11 26H2 guest image (needs MALDEX_WIN_ISO; see deploy/guest/README.md).
# Sysmon.zip is pinned by the SHA-256 of a download on 2026-10-20 (Sysinternals publishes no checksum file).
SYSMON_URL="https://download.sysinternals.com/files/Sysmon.zip"
SYSMON_SHA256="00ecf1b46aec99299d3ae0bca79dc621458bd014b20b509d7c5c8e8c8611aa54"

cmd_build_guest() {
  if [ -z "${MALDEX_WIN_ISO:-}" ] || [ ! -f "${MALDEX_WIN_ISO:-}" ]; then
    echo "MALDEX_WIN_ISO must point to a Windows 11 x64 Enterprise (evaluation) ISO (got: '${MALDEX_WIN_ISO:-}')." >&2
    return 2
  fi
  mkdir -p "$VM_DIR/iso"
  fetch_cached "$SYSMON_URL" "$VM_DIR/iso/Sysmon.zip" "$SYSMON_SHA256"
  SYSMON_ZIP="$PWD/$VM_DIR/iso/Sysmon.zip" VM_DIR="$VM_DIR" LIBVIRT_URI="$LIBVIRT_URI" OVMF_CODE_SECBOOT="$OVMF_CODE_SECBOOT" \
    deploy/guest/build-guest.sh
}

# Harmless real-VM end-to-end suite (needs the built guest image; see deploy/guest/README.md "Running the E2E suite").
cmd_e2e() {
  local base="$VM_DIR/base"
  if [ ! -f "$base/win11-26h2.qcow2" ] || [ ! -f "$base/win11-26h2.vars.fd" ]; then
    echo "The Windows 11 26H2 base image is missing ($base/win11-26h2.qcow2 and .vars.fd)." >&2
    echo "Build it first:  MALDEX_WIN_ISO=/path/to/Win11_26H2.iso ./build.sh build-guest" >&2
    return 2
  fi
  MALDEX_REAL_VM=1 MALDEX_REAL_NET=1 dotnet test --project tests/Maldex.Integration.Tests --filter-class "*RealVmE2ETests"
}

case "${1:-}" in
  setup)  cmd_setup ;;
  dev-db) cmd_dev_db ;;
  test)   cmd_test ;;
  run)    cmd_run ;;
  smoke)  cmd_smoke ;;
  build)  cmd_build ;;
  real-setup) cmd_real_setup ;;
  real-smoke) cmd_real_smoke ;;
  build-guest) cmd_build_guest ;;
  prepare-guest) VM_DIR="$VM_DIR" LIBVIRT_URI="$LIBVIRT_URI" OVMF_CODE_SECBOOT="$OVMF_CODE_SECBOOT" deploy/guest/prepare-guest.sh prepare ;;
  finalize-guest) VM_DIR="$VM_DIR" LIBVIRT_URI="$LIBVIRT_URI" OVMF_CODE_SECBOOT="$OVMF_CODE_SECBOOT" deploy/guest/prepare-guest.sh finalize ;;
  discard-guest) VM_DIR="$VM_DIR" LIBVIRT_URI="$LIBVIRT_URI" deploy/guest/prepare-guest.sh discard ;;
  e2e)    cmd_e2e ;;
  *) echo "usage: $0 setup|dev-db|run|test|smoke|build|real-setup|real-smoke|build-guest|prepare-guest|finalize-guest|discard-guest|e2e" >&2; exit 64 ;;
esac
