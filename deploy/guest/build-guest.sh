#!/usr/bin/env bash
# Build the Maldex Windows 11 26H2 analysis guest image, unattended (Autounattend + Provision.ps1), then boot it once
# and wait for the guest agent's "ready" frame on the virtio-serial port.
#
# Normally run through `./build.sh build-guest`, which also downloads and pins Sysmon (SYSMON_ZIP).
#
# Inputs:  MALDEX_WIN_ISO          required, Windows 11 x64 media: Enterprise evaluation, or multi-edition media (Enterprise or Pro is picked)
#          MALDEX_OFFICE_ISO       optional, Office media (installed with deploy/guest/provision/office-config.xml)
#          MALDEX_WIN_IMAGE_INDEX  optional, force /IMAGE/INDEX instead of selecting the image by name
#          MALDEX_WIN_PRODUCT_KEY  optional, setup key for non-evaluation media (default: the edition's generic KMS client key)
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

RUN_DIR="$ROOT/$VM_DIR/run"
# shellcheck source=deploy/guest/lib.sh
. deploy/guest/lib.sh

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

refuse_if_runs_active "$LIBVIRT_URI" "$RUN_DIR" # fail fast; checked again right before the swap
mkdir -p "$BASE_DIR" "$WORK"

keypress_pid=""
finished=0
cleanup() {
  local rc=$?
  if [ -n "$keypress_pid" ]; then kill "$keypress_pid" 2>/dev/null || true; fi
  if [ "$rc" -ne 0 ] && [ "$finished" -ne 1 ]; then
    if [ "${MALDEX_KEEP_FAILED_BUILD:-}" = 1 ]; then
      echo "Build failed; keeping the VM and files (MALDEX_KEEP_FAILED_BUILD=1)." >&2
      echo "  Watch it: virsh -c $LIBVIRT_URI vncdisplay $DOMAIN   (VNC on 127.0.0.1)" >&2
      echo "  Remove it later: virsh -c $LIBVIRT_URI destroy $DOMAIN; virsh -c $LIBVIRT_URI undefine $DOMAIN --nvram" >&2
      return
    fi
    echo "Build failed (exit $rc); removing the build VM and intermediate files. Re-run with MALDEX_KEEP_FAILED_BUILD=1 to keep them." >&2
    virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true
    if virsh_ list --all --name 2>/dev/null | grep -qx "$DOMAIN"; then
      virsh_ undefine "$DOMAIN" --nvram >/dev/null 2>&1 || virsh_ undefine "$DOMAIN" >/dev/null 2>&1 || true
    fi
    rm -rf "$BUILDING_DISK" "$BUILDING_VARS" "$BUILDING_VARS.final" "$PROV_ISO" "$STAGE"
  elif virsh_ domstate "$DOMAIN" 2>/dev/null | grep -q running; then
    virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true
  fi
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
"$ROOT/build.sh" agent
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

# ---- 3. Windows image selection and product key ----
# Windows media keep their files on UDF, which xorriso cannot read: inspect install.wim/esd through a read-only loop mount.
# Preference: Enterprise Evaluation (no key needed), then Enterprise, then Pro. Never fall back to Home silently.
cp deploy/guest/autounattend/Autounattend.xml "$STAGE/Autounattend.xml"
use_index="${MALDEX_WIN_IMAGE_INDEX:-}"
use_name=""
images=""
if command -v wiminfo >/dev/null 2>&1; then
  iso_mnt="$WORK/isomnt"; mkdir -p "$iso_mnt"
  if sudo -n mount -o loop,ro,nosuid,nodev,noexec "$MALDEX_WIN_ISO" "$iso_mnt" 2>/dev/null; then
    for f in install.wim install.esd; do
      if [ -f "$iso_mnt/sources/$f" ]; then
        # "<index>\t<name>" per image
        images="$(wiminfo "$iso_mnt/sources/$f" | awk -F': *' '/^Index:/ { i = $2 } /^Name:/ { print i "\t" $2 }')"
        break
      fi
    done
    sudo -n umount "$iso_mnt" || true
  fi
  rmdir "$iso_mnt" 2>/dev/null || true
fi
if [ -n "$use_index" ]; then
  use_name="$(printf '%s\n' "$images" | awk -F'\t' -v i="$use_index" '$1 == i { print $2 }')"
elif [ -n "$images" ]; then
  for want in "$IMAGE_NAME" "Windows 11 Enterprise" "Windows 11 Pro"; do
    use_index="$(printf '%s\n' "$images" | awk -F'\t' -v n="$want" '$2 == n { print $1; exit }')"
    if [ -n "$use_index" ]; then use_name="$want"; break; fi
  done
  if [ -z "$use_index" ]; then
    echo "No Enterprise or Pro image on $MALDEX_WIN_ISO. Images on the media:" >&2
    printf '%s\n' "$images" | sed 's/^/  /' >&2
    echo "Pick one with MALDEX_WIN_IMAGE_INDEX=<index> (and MALDEX_WIN_PRODUCT_KEY if it needs a key)." >&2
    exit 2
  fi
else
  log "Could not list the images on the media (needs wiminfo and sudo for a read-only loop mount): selecting '$IMAGE_NAME' by name"
fi
if [ -n "$use_index" ]; then
  log "Selecting the Windows image by /IMAGE/INDEX $use_index${use_name:+ ($use_name)}"
  sed -i -e "s|<Key>/IMAGE/NAME</Key>|<Key>/IMAGE/INDEX</Key>|" -e "s|<Value>$IMAGE_NAME</Value>|<Value>$use_index</Value>|" "$STAGE/Autounattend.xml"
fi
# Evaluation media need no key; other media stop at the key prompt without one. Default to Microsoft's published generic
# KMS client setup key for the edition: it installs, it does not activate (fine for a disposable analysis guest).
product_key="${MALDEX_WIN_PRODUCT_KEY:-}"
if [ -z "$product_key" ]; then
  case "$use_name" in
    *Evaluation|"") ;;
    "Windows 11 Enterprise") product_key="NPPR9-FWDCX-D2C8J-H872K-2YT43" ;;
    "Windows 11 Pro") product_key="W269N-WFGWX-YVC9B-4J6C9-T83GX" ;;
    "Windows 11 Education") product_key="NW6C2-QMPVW-D7KKK-3GKT6-VCFB2" ;;
    *) echo "'$use_name' needs a product key: set MALDEX_WIN_PRODUCT_KEY." >&2; exit 2 ;;
  esac
fi
if [ -n "$product_key" ]; then
  [[ "$product_key" =~ ^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$ ]] || { echo "MALDEX_WIN_PRODUCT_KEY is not a XXXXX-XXXXX-XXXXX-XXXXX-XXXXX key." >&2; exit 2; }
  log "Using a setup product key for the selected edition (installs without activating)"
  sed -i "s|<ProductKey><WillShowUI>OnError</WillShowUI></ProductKey>|<ProductKey><Key>$product_key</Key><WillShowUI>OnError</WillShowUI></ProductKey>|" "$STAGE/Autounattend.xml"
  grep -q "<Key>$product_key</Key>" "$STAGE/Autounattend.xml" || { echo "Could not insert the product key into Autounattend.xml" >&2; exit 1; }
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

# --import defines ONE persistent domain (virt-install's install mode would set on_reboot=destroy and stop at Setup's first
# reboot). Boot order: Windows CD first, then the disk; the "press any key" prompt times out on later reboots.
log "Starting the unattended install (no network; VNC on 127.0.0.1, see 'virsh -c $LIBVIRT_URI vncdisplay $DOMAIN')"
virt-install --connect "$LIBVIRT_URI" --name "$DOMAIN" --memory 4096 --vcpus 2 --cpu host-passthrough --machine q35 \
  --boot "uefi,loader=$OVMF_CODE_SECBOOT,loader.readonly=yes,loader.type=pflash,loader.secure=yes,nvram=$BUILDING_VARS" \
  --features smm.state=on \
  --tpm emulator,model=tpm-crb,version=2.0 \
  --disk "path=$BUILDING_DISK,bus=virtio,format=qcow2,boot.order=2" \
  --disk "path=$MALDEX_WIN_ISO,device=cdrom,boot.order=1" \
  --disk "path=$VIRTIO_ISO,device=cdrom" \
  --disk "path=$PROV_ISO,device=cdrom" \
  "${office[@]}" \
  --import --events on_reboot=restart \
  --network none --graphics vnc,listen=127.0.0.1 \
  --channel unix,target.type=virtio,target.name=org.maldex.agent \
  --noautoconsole "${osinfo[@]}"

# The Windows ISO shows "Press any key to boot from CD or DVD" and an unattended VM cannot answer it: tap a key for a while.
(
  for _ in $(seq 1 25); do virsh_ send-key "$DOMAIN" KEY_SPACE >/dev/null 2>&1 || true; sleep 2; done
) &
keypress_pid=$!

# ---- 6. Wait for the domain to shut off (Provision.ps1 ends with shutdown) ----
start=$SECONDS
last_report=0
while :; do
  state="$(virsh_ domstate "$DOMAIN" 2>/dev/null || echo unknown)"
  elapsed=$((SECONDS - start))
  [ "$state" = "shut off" ] && break
  if [ "$elapsed" -ge "$INSTALL_CAP_SECONDS" ]; then
    echo "Install did not finish within $((INSTALL_CAP_SECONDS / 3600)) h (state: $state). Re-run with MALDEX_KEEP_FAILED_BUILD=1 to keep the VM for inspection." >&2
    exit 1
  fi
  if [ $((elapsed - last_report)) -ge 60 ]; then
    log "Installing... elapsed $((elapsed / 60)) min, domain state: $state"
    last_report=$elapsed
  fi
  sleep 10
done
kill "$keypress_pid" 2>/dev/null || true; keypress_pid=""
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
  if [ "$(virsh_ domstate "$DOMAIN" 2>/dev/null)" = "shut off" ]; then break; fi
  sleep 2
done
if [ "$(virsh_ domstate "$DOMAIN" 2>/dev/null)" != "shut off" ]; then
  echo "The guest did not shut down cleanly within 90 s; refusing to finalise a possibly dirty image." >&2
  exit 1
fi
cp -f "$nvram_path" "$BUILDING_VARS.final"
virsh_ undefine "$DOMAIN" --keep-nvram >/dev/null 2>&1 || virsh_ undefine "$DOMAIN" >/dev/null
mv -f "$BUILDING_VARS.final" "$BUILDING_VARS"

refuse_if_runs_active "$LIBVIRT_URI" "$RUN_DIR" # a run may have started during the build
rm -f "$BASE_DIR/$IMAGE_ID.qcow2" "$BASE_DIR/$IMAGE_ID.vars.fd"
mv "$BUILDING_DISK" "$BASE_DIR/$IMAGE_ID.qcow2"
mv "$BUILDING_VARS" "$BASE_DIR/$IMAGE_ID.vars.fd"
chmod 0444 "$BASE_DIR/$IMAGE_ID.qcow2" "$BASE_DIR/$IMAGE_ID.vars.fd"
finished=1
log "Done: $BASE_DIR/$IMAGE_ID.qcow2 and $IMAGE_ID.vars.fd (read-only)"
