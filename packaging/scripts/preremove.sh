#!/bin/sh
# maldex preremove (deb prerm / rpm %preun). On removal (not on upgrade) stop and disable maldex. The host guard ruleset stays
# loaded until reboot; only its boot-time enablement is dropped with the unit.
#   deb: prerm remove | upgrade <new version> | deconfigure | failed-upgrade      rpm: %preun <0 on erase, >=1 on upgrade>
set -e

REMOVE=0
case "${1:-}" in
  remove) REMOVE=1 ;;
  0) REMOVE=1 ;;
esac
[ "$REMOVE" = 1 ] || exit 0

if command -v systemctl >/dev/null 2>&1; then
  if [ -d /run/systemd/system ]; then
    systemctl stop maldex.service >/dev/null 2>&1 || true
  fi
  systemctl disable maldex.service >/dev/null 2>&1 || true
  systemctl disable maldex-host-guard.service >/dev/null 2>&1 || true
fi
exit 0
