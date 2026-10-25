#!/bin/sh
# maldex preinstall (deb preinst / rpm %pre): create the system users before the files are unpacked, so that
# /etc/maldex/appsettings.Production.json is unpacked as root:maldex (rpm -V stays clean). Idempotent; never downloads anything.
#   deb: preinst install | upgrade <old version> | abort-upgrade      rpm: %pre <1 install, >=2 upgrade>
set -e

case "${1:-}" in abort-upgrade) exit 0 ;; esac

NOLOGIN=/usr/sbin/nologin

# A system account with no home created and no login shell, in its own group: --user-group, or -g when a group of that name
# already exists (useradd refuses --user-group then). Extra useradd options follow the name.
ensure_user() {
  _name="$1"; shift
  getent passwd "$_name" >/dev/null 2>&1 && return 0
  if getent group "$_name" >/dev/null 2>&1; then _grp="-g $_name"; else _grp="--user-group"; fi
  # shellcheck disable=SC2086  # _grp is one or two words on purpose
  useradd --system $_grp --no-create-home --shell "$NOLOGIN" "$@" "$_name"
}

# The service user's home is the data dir: qemu:///session keeps its state there.
ensure_user maldex --home-dir /var/lib/maldex
# Per-run helpers (maldex-setup checks they have their own group only and no sudo).
for u in maldex-mitm maldex-dns maldex-isroot maldex-isuser; do ensure_user "$u"; done
exit 0
