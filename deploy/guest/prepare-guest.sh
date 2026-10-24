#!/usr/bin/env bash
# Manual preparation of the Maldex guest image: changes an operator makes by hand in the Windows UI (for example turning
# off Tamper Protection and Defender, which no unattended step can do on Pro). Normally run through ./build.sh.
#
#   prepare   Copies the read-only base image to $VM_DIR/prepare/ and boots it as domain maldex-prepare: no network,
#             VNC on the LAN (MALDEX_VNC_LISTEN, default: this host's primary IPv4) with a random password, printed.
#             The operator makes the changes and shuts Windows down normally (Start > Power > Shut down).
#   finalize  Requires maldex-prepare to be shut off and no analysis run in progress. Boots the prepared disk once more (VNC on 127.0.0.1 only), waits
#             for the agent's "ready" frame, shuts it down cleanly and replaces the base image (both files 0444). The
#             previous base is kept as *.prev until the next finalize.
#   discard   Removes maldex-prepare and its copy without touching the base image.
#
# ./build.sh build-guest replaces the base with a fresh unattended build: run prepare/finalize again after it.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
ROOT="$(pwd)"

VM_DIR="${VM_DIR:-.data/vm}"
LIBVIRT_URI="${LIBVIRT_URI:-qemu:///session}"
OVMF_CODE_SECBOOT="${OVMF_CODE_SECBOOT:-/usr/share/OVMF/OVMF_CODE_4M.secboot.fd}"
DOMAIN="maldex-prepare"
VERIFY_DOMAIN="maldex-prepare-verify"
IMAGE_ID="win11-26h2"
READY_CAP_SECONDS=180

BASE_DIR="$ROOT/$VM_DIR/base"
PREP_DIR="$ROOT/$VM_DIR/prepare"
PREP_DISK="$PREP_DIR/$IMAGE_ID.qcow2"
PREP_VARS="$PREP_DIR/$IMAGE_ID.vars.fd"

RUN_DIR="$ROOT/$VM_DIR/run"
# shellcheck source=deploy/guest/lib.sh
. deploy/guest/lib.sh

virsh_() { virsh -c "$LIBVIRT_URI" "$@"; }
log() { echo "[prepare-guest $(date +%H:%M:%S)] $*"; }
exists() { virsh_ list --all --name | grep -qx "$1"; }
state() { virsh_ domstate "$1" 2>/dev/null || echo "absent"; }

# Same virtual hardware as the build and the runs (q35, OVMF Secure Boot, vTPM 2.0, agent channel), never a NIC.
define_and_start() { # <domain> <vnc listen address> [vnc password]
  local graphics="vnc,listen=$2"
  [ -n "${3:-}" ] && graphics="$graphics,password=$3"
  virt-install --connect "$LIBVIRT_URI" --name "$1" --memory 4096 --vcpus 2 --cpu host-passthrough --machine q35 \
    --boot "uefi,loader=$OVMF_CODE_SECBOOT,loader.readonly=yes,loader.type=pflash,loader.secure=yes,nvram=$PREP_VARS" \
    --features smm.state=on --tpm emulator,model=tpm-crb,version=2.0 \
    --disk "path=$PREP_DISK,bus=virtio,format=qcow2" \
    --import --network none --graphics "$graphics" \
    --channel unix,target.type=virtio,target.name=org.maldex.agent \
    --osinfo win11 --noautoconsole >/dev/null
}

cmd_prepare() {
  [ -f "$BASE_DIR/$IMAGE_ID.qcow2" ] && [ -f "$BASE_DIR/$IMAGE_ID.vars.fd" ] \
    || { echo "No base image in $BASE_DIR; run ./build.sh build-guest first." >&2; exit 2; }
  if exists "$DOMAIN"; then
    echo "$DOMAIN already exists (state: $(state "$DOMAIN")). Finish it (finalize) or discard it first." >&2
    exit 2
  fi
  local listen="${MALDEX_VNC_LISTEN:-$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{ for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit } }')}"
  [ -n "$listen" ] || { echo "Could not determine this host's LAN address; set MALDEX_VNC_LISTEN." >&2; exit 2; }

  mkdir -p "$PREP_DIR"
  log "Copying the base image to $PREP_DIR (a full, writable copy)"
  cp --reflink=auto "$BASE_DIR/$IMAGE_ID.qcow2" "$PREP_DISK.tmp" && mv "$PREP_DISK.tmp" "$PREP_DISK"
  cat "$BASE_DIR/$IMAGE_ID.vars.fd" > "$PREP_VARS"
  chmod 0600 "$PREP_DISK" "$PREP_VARS"

  # VNC passwords are at most 8 characters. Plain VNC is unencrypted: LAN only, and only while this domain runs.
  # (not "tr < /dev/urandom | head": tr dies of SIGPIPE, which pipefail turns into a silent exit)
  local pass; pass="$(openssl rand -base64 24 | tr -dc 'A-HJ-NP-Za-km-z2-9' | cut -c1-8)"
  [ "${#pass}" -eq 8 ] || { echo "Could not generate a VNC password." >&2; exit 1; }
  define_and_start "$DOMAIN" "$listen" "$pass"
  local display; display="$(virsh_ vncdisplay "$DOMAIN")"
  local port=$((5900 + ${display##*:}))
  log "Started $DOMAIN (no network)."
  echo
  echo "  VNC:       $listen:$port   (display ${display##*:})"
  echo "  Password:  $pass"
  echo "  Windows:   logs on automatically as 'analyst' (password Maldex-Analyst-1!, a throwaway value)."
  echo
  echo "Make your changes, then shut Windows down from the Start menu. Then run: ./build.sh finalize-guest"
}

cmd_finalize() {
  exists "$DOMAIN" || { echo "No $DOMAIN to finalize; run ./build.sh prepare-guest first." >&2; exit 2; }
  [ "$(state "$DOMAIN")" = "shut off" ] \
    || { echo "$DOMAIN is $(state "$DOMAIN"); shut Windows down from the Start menu first (a clean shutdown, not a reset)." >&2; exit 2; }
  refuse_if_runs_active "$LIBVIRT_URI" "$RUN_DIR" # fail fast; checked again right before the swap
  virsh_ undefine "$DOMAIN" --keep-nvram >/dev/null 2>&1 || virsh_ undefine "$DOMAIN" >/dev/null

  # Verify on a fresh definition with the console on localhost only: the agent must still come up.
  exists "$VERIFY_DOMAIN" && { virsh_ destroy "$VERIFY_DOMAIN" >/dev/null 2>&1 || true; virsh_ undefine "$VERIFY_DOMAIN" --keep-nvram >/dev/null 2>&1 || true; }
  trap 'virsh_ destroy "$VERIFY_DOMAIN" >/dev/null 2>&1 || true; virsh_ undefine "$VERIFY_DOMAIN" --keep-nvram >/dev/null 2>&1 || true' EXIT
  define_and_start "$VERIFY_DOMAIN" 127.0.0.1
  local sock; sock="$(virsh_ dumpxml "$VERIFY_DOMAIN" | xmllint --xpath "string(//channel/target[@name='org.maldex.agent']/../source/@path)" -)"
  [ -n "$sock" ] || { echo "Could not determine the agent channel socket path" >&2; exit 1; }
  log "Waiting up to ${READY_CAP_SECONDS}s for the agent ready frame"
  local out="$PREP_DIR/agent-channel.out"; : > "$out"
  # shellcheck disable=SC2016  # $1/$2 expand in the inner shell
  timeout "$((READY_CAP_SECONDS + 5))" bash -c 'while :; do socat -u "UNIX-CONNECT:$1" - >> "$2" 2>/dev/null; sleep 1; done' _ "$sock" "$out" &
  local reader=$! ready=0
  for _ in $(seq 1 "$((READY_CAP_SECONDS / 2))"); do
    if grep -aq '"type":"ready"' "$out"; then ready=1; break; fi
    sleep 2
  done
  kill "$reader" 2>/dev/null || true; wait "$reader" 2>/dev/null || true
  [ "$ready" = 1 ] || { echo "The agent did not report ready; the base image was NOT replaced (the prepared copy is kept in $PREP_DIR)." >&2; exit 1; }
  log "Agent reported ready: $(grep -ao '{[^}]*"type":"ready"[^}]*}' "$out" | head -1)"

  virsh_ shutdown "$VERIFY_DOMAIN" >/dev/null 2>&1 || true
  for _ in $(seq 1 45); do [ "$(state "$VERIFY_DOMAIN")" = "shut off" ] && break; sleep 2; done
  [ "$(state "$VERIFY_DOMAIN")" = "shut off" ] \
    || { echo "The guest did not shut down cleanly within 90 s; the base image was NOT replaced." >&2; exit 1; }
  virsh_ undefine "$VERIFY_DOMAIN" --keep-nvram >/dev/null 2>&1 || virsh_ undefine "$VERIFY_DOMAIN" >/dev/null
  trap - EXIT

  refuse_if_runs_active "$LIBVIRT_URI" "$RUN_DIR" # a run may have started during the verification boot
  for f in "$IMAGE_ID.qcow2" "$IMAGE_ID.vars.fd"; do
    rm -f "$BASE_DIR/$f.prev"
    [ -f "$BASE_DIR/$f" ] && mv "$BASE_DIR/$f" "$BASE_DIR/$f.prev"
  done
  mv "$PREP_DISK" "$BASE_DIR/$IMAGE_ID.qcow2"
  mv "$PREP_VARS" "$BASE_DIR/$IMAGE_ID.vars.fd"
  chmod 0444 "$BASE_DIR/$IMAGE_ID.qcow2" "$BASE_DIR/$IMAGE_ID.vars.fd"
  rm -rf "$PREP_DIR"
  log "Done: the prepared image is the new base ($BASE_DIR/$IMAGE_ID.qcow2, read-only); the previous one is *.prev."
}

cmd_discard() {
  if exists "$DOMAIN"; then
    virsh_ destroy "$DOMAIN" >/dev/null 2>&1 || true
    virsh_ undefine "$DOMAIN" --keep-nvram >/dev/null 2>&1 || virsh_ undefine "$DOMAIN" >/dev/null 2>&1 || true
  fi
  rm -rf "$PREP_DIR"
  log "Discarded $DOMAIN; the base image is unchanged."
}

case "${1:-}" in
  prepare) cmd_prepare ;;
  finalize) cmd_finalize ;;
  discard) cmd_discard ;;
  *) echo "usage: $0 prepare|finalize|discard" >&2; exit 64 ;;
esac
