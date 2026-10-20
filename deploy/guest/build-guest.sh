#!/usr/bin/env bash
# Build the Maldex Windows 11 26H2 analysis guest image, unattended (Autounattend + Provision.ps1), then boot it once
# and wait for the guest agent's "ready" frame on the virtio-serial port.
#
# Normally run through `./build.sh build-guest`, which also downloads and pins Sysmon (SYSMON_ZIP).
#
# Inputs:  MALDEX_WIN_ISO          required, Windows 11 x64 Enterprise (evaluation) media
#          MALDEX_OFFICE_ISO       optional, Office media (installed with deploy/guest/provision/office-config.xml)
#          MALDEX_WIN_IMAGE_INDEX  optional, force /IMAGE/INDEX instead of selecting the image by name
#          SYSMON_ZIP              Sysmon.zip (pinned by build.sh)
# Outputs: $VM_DIR/base/win11-26h2.qcow2 and win11-26h2.vars.fd (both chmod 0444)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
ROOT="$(pwd)"

VM_DIR="${VM_DIR:-.data/vm}"
LIBVIRT_URI="${LIBVIRT_URI:-qemu:///session}"
OVMF_CODE_SECBOOT="${OVMF_CODE_SECBOOT:-/usr/share/OVMF/OVMF_CODE_4M.secboot.fd}"
OVMF_VARS_TEMPLATE="${OVMF_VARS_TEMPLATE:-/usr/share/OVMF/OVMF_VARS_4M.ms.fd}"
MITM_CA_DIR="${MITM_CA_DIR:-.data/mitm/ca}"
MITMDUMP="${MITMDUMP:-mitmdump}"
DOMAIN="maldex-build"
IMAGE_ID="win11-26h2"
IMAGE_NAME="Windows 11 Enterprise Evaluation"
INSTALL_CAP_SECONDS=$((3 * 3600))
READY_CAP_SECONDS=180

VIRTIO_ISO="$ROOT/$VM_DIR/iso/virtio-win.iso"
BASE_DIR="$ROOT/$VM_DIR/base"
WORK="$ROOT/$VM_DIR/build"
STAGE="$WORK/provision"
PROV_ISO="$WORK/provision.iso"
BUILDING_DISK="$BASE_DIR/$IMAGE_ID.building.qcow2"
BUILDING_VARS="$BASE_DIR/$IMAGE_ID.building.vars.fd"

virsh_() { virsh -c "$LIBVIRT_URI" "$@"; }
log() { echo "[build-guest $(date +%H:%M:%S)] $*"; }

# ---- Guards ----
if [ -z "${MALDEX_WIN_ISO:-}" ] || [ ! -f "${MALDEX_WIN_ISO:-}" ]; then
  echo "MALDEX_WIN_ISO must point to a Windows 11 x64 Enterprise (evaluation) ISO (got: '${MALDEX_WIN_ISO:-}')." >&2
  exit 2
fi
if [ -n "${MALDEX_OFFICE_ISO:-}" ] && [ ! -f "$MALDEX_OFFICE_ISO" ]; then
  echo "MALDEX_OFFICE_ISO is set but '$MALDEX_OFFICE_ISO' does not exist." >&2
  exit 2
fi
if [ ! -f "$VIRTIO_ISO" ]; then
  echo "Missing $VIRTIO_ISO; run ./build.sh real-setup first." >&2
  exit 2
fi
if [ -z "${SYSMON_ZIP:-}" ] || [ ! -f "$SYSMON_ZIP" ]; then
  echo "SYSMON_ZIP is not set or missing; run this through ./build.sh build-guest (it downloads and pins Sysmon)." >&2
  exit 2
fi
for tool in xorriso xmllint virt-install virsh qemu-img socat unzip openssl dotnet "$MITMDUMP"; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Missing tool: $tool (see ./build.sh real-setup)" >&2; exit 2; }
done
for f in "$OVMF_CODE_SECBOOT" "$OVMF_VARS_TEMPLATE"; do
  [ -f "$f" ] || { echo "Missing firmware file $f (apt install ovmf)" >&2; exit 2; }
done
{ [ -r /dev/kvm ] && [ -w /dev/kvm ]; } || { echo "/dev/kvm is not usable by this session; run ./build.sh real-setup" >&2; exit 2; }

mkdir -p "$BASE_DIR" "$WORK"

ejector_pid=""
cleanup() {
  if [ -n "$ejector_pid" ]; then kill "$ejector_pid" 2>/dev/null || true; fi
  if virsh_ domstate "$DOMAIN" 2>/dev/null | grep -q running; then virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

# Remove a leftover build domain from an earlier attempt (keeps files; they are rebuilt below).
if virsh_ list --all --name | grep -qx "$DOMAIN"; then
  log "Removing leftover domain $DOMAIN"
  virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true
  virsh_ undefine "$DOMAIN" --nvram >/dev/null 2>&1 || virsh_ undefine "$DOMAIN" >/dev/null
fi
rm -f "$BUILDING_DISK" "$BUILDING_VARS"

# ---- 1. Guest agent ----
log "Publishing the guest agent"
dotnet publish src/Maldex.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "$ROOT/.data/agent" --nologo -v q
[ -f .data/agent/Maldex.Agent.exe ] || { echo "Agent publish did not produce Maldex.Agent.exe" >&2; exit 1; }

# ---- 2. Sysmon, mitmproxy CA ----
rm -rf "$STAGE"; mkdir -p "$STAGE"
unzip -oq -j "$SYSMON_ZIP" Sysmon64.exe -d "$STAGE"

ca_pem="$ROOT/$MITM_CA_DIR/mitmproxy-ca-cert.pem"
if [ ! -f "$ca_pem" ]; then
  log "Generating the mitmproxy CA in $MITM_CA_DIR"
  mkdir -p "$ROOT/$MITM_CA_DIR"
  "$MITMDUMP" --set "confdir=$ROOT/$MITM_CA_DIR" --listen-host 127.0.0.1 --listen-port 0 -q >/dev/null 2>&1 &
  mitm_pid=$!
  for _ in $(seq 1 30); do [ -f "$ca_pem" ] && break; sleep 1; done
  kill "$mitm_pid" 2>/dev/null || true
  wait "$mitm_pid" 2>/dev/null || true
  [ -f "$ca_pem" ] || { echo "mitmdump did not generate a CA in $MITM_CA_DIR" >&2; exit 1; }
fi
# Windows wants a DER .cer (the mitmproxy .cer is PEM text).
openssl x509 -in "$ca_pem" -outform DER -out "$STAGE/maldex-ca.cer"

# ---- 3. Windows image selection: by name, falling back to index 1 ----
cp deploy/guest/autounattend/Autounattend.xml "$STAGE/Autounattend.xml"
use_index=""
if [ -n "${MALDEX_WIN_IMAGE_INDEX:-}" ]; then
  use_index="$MALDEX_WIN_IMAGE_INDEX"
elif command -v wiminfo >/dev/null 2>&1; then
  # Look at the media's image list; if no image has the expected name, use index 1.
  wimtmp="$WORK/wiminfo"; rm -rf "$wimtmp"; mkdir -p "$wimtmp"
  for f in install.wim install.esd; do
    if xorriso -osirrox on -indev "$MALDEX_WIN_ISO" -extract "/sources/$f" "$wimtmp/$f" >/dev/null 2>&1 && [ -s "$wimtmp/$f" ]; then
      if wiminfo "$wimtmp/$f" 2>/dev/null | grep -q "^Name:.*$IMAGE_NAME"; then log "Media contains '$IMAGE_NAME'"; else use_index=1; fi
      break
    fi
  done
  rm -rf "$wimtmp"
else
  log "wiminfo not installed: selecting the image by name '$IMAGE_NAME' (set MALDEX_WIN_IMAGE_INDEX=1 if the media names it differently)"
fi
if [ -n "$use_index" ]; then
  log "Selecting the Windows image by /IMAGE/INDEX $use_index"
  sed -i -e "s|<Key>/IMAGE/NAME</Key>|<Key>/IMAGE/INDEX</Key>|" -e "s|<Value>$IMAGE_NAME</Value>|<Value>$use_index</Value>|" "$STAGE/Autounattend.xml"
fi
xmllint --noout "$STAGE/Autounattend.xml"

# ---- 4. Provisioning ISO ----
cp deploy/guest/provision/Provision.ps1 deploy/guest/provision/sysmon-config.xml "$STAGE/"
cp .data/agent/Maldex.Agent.exe "$STAGE/"
[ -n "${MALDEX_OFFICE_ISO:-}" ] && cp deploy/guest/provision/office-config.xml "$STAGE/"
rm -f "$PROV_ISO"
xorriso -as mkisofs -quiet -J -R -V MALDEXPROV -o "$PROV_ISO" "$STAGE"

# ---- 5. Disk, vars, install domain ----
qemu-img create -f qcow2 "$BUILDING_DISK" 64G >/dev/null
cp "$OVMF_VARS_TEMPLATE" "$BUILDING_VARS"

osinfo=(--osinfo win11)
virt-install --osinfo list 2>/dev/null | grep -qx 'win11' || osinfo=(--osinfo "detect=on,require=off")
office=()
[ -n "${MALDEX_OFFICE_ISO:-}" ] && office=(--disk "path=$MALDEX_OFFICE_ISO,device=cdrom")

log "Starting the unattended install (no network; VNC on 127.0.0.1, see 'virsh -c $LIBVIRT_URI vncdisplay $DOMAIN')"
virt-install --connect "$LIBVIRT_URI" --name "$DOMAIN" --memory 4096 --vcpus 2 --cpu host-passthrough --machine q35 \
  --boot "uefi,loader=$OVMF_CODE_SECBOOT,loader.readonly=yes,loader.type=pflash,loader.secure=yes,nvram=$BUILDING_VARS" \
  --features smm.state=on \
  --tpm emulator,model=tpm-crb,version=2.0 \
  --disk "path=$BUILDING_DISK,bus=virtio,format=qcow2" \
  --cdrom "$MALDEX_WIN_ISO" \
  --disk "path=$VIRTIO_ISO,device=cdrom" \
  --disk "path=$PROV_ISO,device=cdrom" \
  "${office[@]}" \
  --events on_reboot=restart \
  --network none --graphics vnc,listen=127.0.0.1 \
  --channel unix,target.type=virtio,target.name=org.maldex.agent \
  --noautoconsole "${osinfo[@]}"

# The Windows ISO shows "Press any key to boot from CD or DVD" and an unattended VM cannot answer it: tap a key for a while.
(
  for _ in $(seq 1 25); do virsh_ send-key "$DOMAIN" KEY_SPACE >/dev/null 2>&1 || true; sleep 2; done
) &
ejector_pid=$!

# ---- 6. Wait for the domain to shut off (Provision.ps1 ends with shutdown) ----
start=$SECONDS
last_report=0
while :; do
  state="$(virsh_ domstate "$DOMAIN" 2>/dev/null || echo unknown)"
  elapsed=$((SECONDS - start))
  [ "$state" = "shut off" ] && break
  if [ "$elapsed" -ge "$INSTALL_CAP_SECONDS" ]; then
    echo "Install did not finish within $((INSTALL_CAP_SECONDS / 3600)) h (state: $state); inspect with VNC." >&2
    exit 1
  fi
  if [ $((elapsed - last_report)) -ge 60 ]; then
    log "Installing... elapsed $((elapsed / 60)) min, domain state: $state"
    last_report=$elapsed
  fi
  sleep 10
done
kill "$ejector_pid" 2>/dev/null || true; ejector_pid=""
log "Install and provisioning finished after $(((SECONDS - start) / 60)) min"

# ---- 7. Verify: boot without installer media and wait for the agent's ready frame ----
log "Ejecting installer media"
while read -r target; do
  if [ -n "$target" ]; then virsh_ change-media "$DOMAIN" "$target" --eject --config >/dev/null 2>&1 || true; fi
done < <(virsh_ domblklist "$DOMAIN" --details | awk '$2 == "cdrom" { print $3 }')

virsh_ start "$DOMAIN" >/dev/null
sock="$(virsh_ dumpxml "$DOMAIN" | xmllint --xpath "string(//channel/target[@name='org.maldex.agent']/../source/@path)" -)"
[ -n "$sock" ] || { echo "Could not determine the agent channel socket path" >&2; exit 1; }
log "Waiting up to ${READY_CAP_SECONDS}s for the agent ready frame on $sock"
serial_out="$WORK/agent-channel.out"; : > "$serial_out"
# shellcheck disable=SC2016  # $1/$2 expand in the inner shell
timeout "$((READY_CAP_SECONDS + 5))" bash -c 'while :; do socat -u "UNIX-CONNECT:$1" - >> "$2" 2>/dev/null; sleep 1; done' _ "$sock" "$serial_out" &
reader_pid=$!
ready=0
for _ in $(seq 1 "$((READY_CAP_SECONDS / 2))"); do
  if grep -aq '"type":"ready"' "$serial_out"; then ready=1; break; fi
  sleep 2
done
kill "$reader_pid" 2>/dev/null || true
wait "$reader_pid" 2>/dev/null || true
if [ "$ready" != 1 ]; then
  echo "The agent did not report ready within ${READY_CAP_SECONDS}s. Check C:\\ProgramData\\Maldex\\provision.log and agent.log in the guest." >&2
  exit 1
fi
log "Agent reported ready: $(grep -ao '{[^}]*"type":"ready"[^}]*}' "$serial_out" | head -1)"

# ---- 8. Clean shutdown, keep the vars file, finalise ----
nvram_path="$(virsh_ dumpxml "$DOMAIN" | xmllint --xpath 'string(//os/nvram)' - 2>/dev/null || true)"
[ -n "$nvram_path" ] && [ -f "$nvram_path" ] || nvram_path="$BUILDING_VARS"
virsh_ shutdown "$DOMAIN" >/dev/null 2>&1 || true
for _ in $(seq 1 45); do
  [ "$(virsh_ domstate "$DOMAIN" 2>/dev/null)" = "shut off" ] && break
  sleep 2
done
virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true
cp -f "$nvram_path" "$BUILDING_VARS.final"
virsh_ undefine "$DOMAIN" --keep-nvram --keep-tpm >/dev/null 2>&1 \
  || virsh_ undefine "$DOMAIN" --keep-nvram >/dev/null 2>&1 \
  || virsh_ undefine "$DOMAIN" >/dev/null
mv -f "$BUILDING_VARS.final" "$BUILDING_VARS"

rm -f "$BASE_DIR/$IMAGE_ID.qcow2" "$BASE_DIR/$IMAGE_ID.vars.fd"
mv "$BUILDING_DISK" "$BASE_DIR/$IMAGE_ID.qcow2"
mv "$BUILDING_VARS" "$BASE_DIR/$IMAGE_ID.vars.fd"
chmod 0444 "$BASE_DIR/$IMAGE_ID.qcow2" "$BASE_DIR/$IMAGE_ID.vars.fd"
log "Done: $BASE_DIR/$IMAGE_ID.qcow2 and $IMAGE_ID.vars.fd (read-only)"
