#!/bin/sh
# maldex postinstall (deb postinst / rpm %post). Offline, idempotent and fast: it never downloads anything, never starts maldex
# (the connection string is not set yet) and never deletes /var/lib/maldex. Optional steps warn; it fails the install only when
# the users (created by preinstall) are missing or the sudoers file does not pass visudo.
#   deb: postinst configure [<previous version>]      rpm: %post <count of installed instances: 1 install, >=2 upgrade>
set -e

APP_USER=maldex
SUDOERS_SRC=/opt/maldex/share/sudoers/maldex
SUDOERS_DST=/etc/sudoers.d/maldex
CONFIG=/etc/maldex/appsettings.Production.json
VM_DIR=/var/lib/maldex/.data/vm

UPGRADE=0
FORMAT=deb
case "${1:-}" in
  configure) [ -z "${2:-}" ] || UPGRADE=1 ;;
  abort-upgrade|abort-remove|abort-deconfigure) exit 0 ;;
  [0-9]*) FORMAT=rpm; [ "$1" -lt 2 ] || UPGRADE=1 ;;
esac

warn() { echo "maldex: warning: $*" >&2; }
have_systemd() { [ -d /run/systemd/system ]; }

# ---- users (preinstall created them) ----
for u in "$APP_USER" maldex-mitm maldex-dns maldex-isroot maldex-isuser; do
  getent passwd "$u" >/dev/null 2>&1 || { echo "maldex: user $u is missing (the preinstall script creates it)" >&2; exit 1; }
done
# kvm and libvirt normally come from the qemu and libvirt packages (configured before this one); create them when they did not.
ensure_group() { getent group "$1" >/dev/null 2>&1 || groupadd --system "$1"; }
ensure_group kvm
ensure_group libvirt
usermod -a -G kvm,libvirt "$APP_USER"

# ---- directories ----
# .data/vm exists from the start so the SELinux label below has something to land on.
install -d -m 0750 -o "$APP_USER" -g "$APP_USER" /var/lib/maldex /var/lib/maldex/.data "$VM_DIR" /var/log/maldex
install -d -m 0755 -o root -g root /etc/maldex
if [ -f "$CONFIG" ] && [ ! -L "$CONFIG" ]; then
  # The package ships it root:maldex 0640; this repairs a copy whose group or mode was changed.
  chown root:"$APP_USER" "$CONFIG"
  chmod 0640 "$CONFIG"
fi

# ---- sudoers: only a file that passes visudo reaches /etc/sudoers.d ----
VISUDO="$(command -v visudo || true)"
[ -n "$VISUDO" ] || { [ ! -x /usr/sbin/visudo ] || VISUDO=/usr/sbin/visudo; }
if [ -z "$VISUDO" ]; then
  echo "maldex: visudo is missing (install sudo); $SUDOERS_DST was not installed" >&2
  exit 1
fi
if ! "$VISUDO" -cf "$SUDOERS_SRC"; then
  echo "maldex: $SUDOERS_SRC fails visudo -c; $SUDOERS_DST was not installed" >&2
  exit 1
fi
# A name with a dot is skipped by sudo's includedir, so the half-written copy is never read; the rename is atomic.
install -m 0440 -o root -g root "$SUDOERS_SRC" "$SUDOERS_DST.maldex-new"
mv -f "$SUDOERS_DST.maldex-new" "$SUDOERS_DST"
if ! "$VISUDO" -c >/dev/null 2>&1; then
  rm -f "$SUDOERS_DST"
  echo "maldex: the sudoers configuration fails visudo -c with $SUDOERS_DST in place; it was removed" >&2
  exit 1
fi

# ---- udev: /dev/kvm for the kvm group ----
if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload >/dev/null 2>&1 || warn "udevadm control --reload failed"
  udevadm trigger -s misc >/dev/null 2>&1 || warn "udevadm trigger -s misc failed"
fi

# ---- the distro inetsim and suricata services: Maldex runs both per run ----
if command -v systemctl >/dev/null 2>&1; then
  for unit in inetsim suricata; do
    if have_systemd; then systemctl stop "$unit.service" >/dev/null 2>&1 || true; fi
    systemctl disable "$unit.service" >/dev/null 2>&1 || true
    systemctl mask "$unit.service" >/dev/null 2>&1 || warn "could not mask $unit.service"
  done
fi

# ---- SELinux (rpm): the VM tree gets the label qemu session disks need ----
if [ "$FORMAT" = rpm ] && command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; then
  if command -v semanage >/dev/null 2>&1; then
    semanage fcontext -a -t virt_image_t "$VM_DIR(/.*)?" >/dev/null 2>&1 \
      || semanage fcontext -m -t virt_image_t "$VM_DIR(/.*)?" >/dev/null 2>&1 \
      || warn "semanage fcontext for $VM_DIR failed"
    if [ -d "$VM_DIR" ]; then restorecon -R "$VM_DIR" || warn "restorecon -R $VM_DIR failed"; fi
  else
    warn "semanage is missing (policycoreutils-python-utils); $VM_DIR is not labelled virt_image_t"
  fi
fi

# ---- units ----
if command -v systemctl >/dev/null 2>&1; then
  if have_systemd; then
    systemctl daemon-reload || warn "systemctl daemon-reload failed"
    if [ "$UPGRADE" = 1 ]; then
      # Load a changed ruleset: nft -f is atomic and the file is idempotent (it replaces its own table). maldex Requires= the
      # guard, so a running maldex is restarted with it.
      systemctl enable maldex-host-guard.service || warn "could not enable maldex-host-guard.service"
      systemctl restart maldex-host-guard.service || warn "could not restart maldex-host-guard.service"
    else
      systemctl enable --now maldex-host-guard.service || warn "could not enable and start maldex-host-guard.service"
    fi
    # guacd listens on 127.0.0.1 only (the drop-in). Start it: EPEL's guacd package does not, and maldex-setup checks it is
    # active. When it is already running (Ubuntu's postinst starts it), restart it so it picks up the drop-in.
    if systemctl is-active --quiet guacd.service; then
      systemctl enable guacd.service || warn "could not enable guacd.service"
      systemctl try-restart guacd.service || warn "could not restart guacd.service"
    else
      systemctl enable --now guacd.service || warn "could not enable and start guacd.service"
    fi
  else
    systemctl enable maldex-host-guard.service guacd.service >/dev/null 2>&1 || warn "could not enable maldex-host-guard and guacd"
  fi
fi

if [ "$UPGRADE" = 1 ]; then
  if have_systemd; then systemctl try-restart maldex.service || warn "could not restart maldex.service"; fi
else
  echo "Maldex is installed. Set ConnectionStrings:Maldex in $CONFIG, then:"
  echo "Run: sudo maldex-setup"
fi
exit 0
