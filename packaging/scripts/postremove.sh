#!/bin/sh
# maldex postremove (deb postrm / rpm %postun). Never touches /var/lib/maldex (VM images and evidence) or the users.
#   remove (deb remove or purge, rpm erase): drop the sudo grant, since nothing that uses it is left.
#   deb purge: also remove /etc/maldex.
#   deb: postrm remove | purge | upgrade <new version> | ...      rpm: %postun <0 on erase, >=1 on upgrade>
set -e

REMOVE=0
PURGE=0
case "${1:-}" in
  remove|0) REMOVE=1 ;;
  purge) REMOVE=1; PURGE=1 ;;
esac

if [ "$REMOVE" = 1 ]; then
  rm -f /etc/sudoers.d/maldex /etc/sudoers.d/maldex.maldex-new
fi
if [ "$PURGE" = 1 ]; then
  rm -rf /etc/maldex
fi
if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  systemctl daemon-reload >/dev/null 2>&1 || true
fi
exit 0
