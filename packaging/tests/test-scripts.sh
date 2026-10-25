#!/usr/bin/env bash
# shellcheck disable=SC2016  # the harness and the stub bodies are shell text for the container
# Behavioural test of the install scripts (packaging/scripts/*.sh): runs them under sh, the way dpkg and rpm do, in throwaway
# ubuntu:24.04 and rockylinux:9 containers, with the package's unpack simulated and systemctl, visudo, udevadm, semanage,
# restorecon and selinuxenabled stubbed (useradd, usermod and groupadd are logged, then run for real inside the container).
# Nothing runs on this host. Skips with a notice when Docker is unavailable.
#   bash packaging/tests/test-scripts.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG="$(cd "$HERE/.." && pwd)"

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  echo "skip - install-script tests (Docker is not available)"
  exit 0
fi

# The harness, run as root inside the container with the packaging dir at /pkg (read-only). $1 = deb | rpm.
harness() {
  cat <<'HARNESS'
set -u
FMT="$1"
PASS=0; FAIL=0; OUT=""; RC=0
LOG=/tmp/stub.log; ALL=/tmp/all.log
ok()  { echo "ok   - [$FMT] $1"; PASS=$((PASS + 1)); }
nok() { echo "FAIL - [$FMT] $1"; FAIL=$((FAIL + 1)); printf '%s\n' "$OUT" | sed 's/^/      | /'; sed 's/^/      log| /' "$LOG"; }
check() { local n="$1"; shift; if "$@"; then ok "$n"; else nok "$n"; fi; }
logged() { grep -Eq -- "$1" "$LOG"; }
not_logged() { ! grep -Eq -- "$1" "$LOG"; }
count() { grep -Ec -- "$1" "$LOG" || true; }

mkdir -p /stubs
stub() { printf '#!/bin/sh\necho "%s $*" >> %s\necho "%s $*" >> %s\n%s\n' "$1" "$LOG" "$1" "$ALL" "$2" > "/stubs/$1"; chmod +x "/stubs/$1"; }
REAL_USERADD="$(command -v useradd)"; REAL_USERMOD="$(command -v usermod)"; REAL_GROUPADD="$(command -v groupadd)"
[ -n "$REAL_USERADD" ] && [ -n "$REAL_USERMOD" ] && [ -n "$REAL_GROUPADD" ] || { echo "FAIL - [$FMT] the image lacks useradd/usermod/groupadd"; exit 1; }
stub systemctl 'exit 0'
stub udevadm 'exit 0'
stub visudo '[ ! -e /tmp/visudo-fail ] || exit 1; exit 0'
stub selinuxenabled 'exit 0'
stub semanage 'exit 0'
stub restorecon 'for a in "$@"; do t="$a"; done; [ -d "$t" ] && echo "restorecon target exists: $t" >> '"$LOG"'; exit 0'
stub useradd "exec $REAL_USERADD \"\$@\""
stub usermod "exec $REAL_USERMOD \"\$@\""
stub groupadd "exec $REAL_GROUPADD \"\$@\""
export PATH="/stubs:$PATH"
mkdir -p /run/systemd/system /etc/sudoers.d   # systemd "running": the scripts' systemctl branches are exercised

run() { # script args...
  local s="$1"; shift
  : > "$LOG"
  OUT="$(sh "/pkg/scripts/$s.sh" "$@" 2>&1)"; RC=$?
}
unpack() { # what dpkg/rpm do between preinstall and postinstall
  install -D -m 0440 "/pkg/sudoers/maldex.$FMT" /opt/maldex/share/sudoers/maldex
  install -d -m 0755 /etc/maldex
  install -m 0640 -o root -g maldex /pkg/etc/appsettings.Production.json /etc/maldex/appsettings.Production.json
}
snapshot() { find /var/lib/maldex -printf '%p %u:%g %m %s\n' | sort; }
never_starts_maldex() { ! grep -Eq 'systemctl (start|restart|enable( --now)?) maldex\.service' "$ALL"; }
users_ok() { local u; for u in maldex maldex-mitm maldex-dns maldex-isroot maldex-isuser; do id "$u" >/dev/null 2>&1 || return 1; done; }
own_group_only() { [ "$(id -nG "$1")" = "$1" ]; }
mode_owner() { [ "$(stat -c '%a %U:%G' "$2")" = "$1" ]; }
kept_etc() { [ -f /etc/sudoers.d/maldex ] && [ -d /etc/maldex ]; }
no_hint() { ! grep -q "^Run: sudo maldex-setup" <<<"$OUT"; }
same_file() { [ "$(sha256sum < "$1")" = "$(sha256sum < "$2")" ]; }   # no cmp in rockylinux:9
no_sudoers() { [ ! -e /etc/sudoers.d/maldex ] && [ ! -e /etc/sudoers.d/maldex.maldex-new ]; }

if [ "$FMT" = deb ]; then INSTALL=(configure); UPGRADE=(configure 1.0); PRE_I=(install); PRE_U=(upgrade 1.0)
  OLD_PRERM_U=(upgrade 2.0); OLD_POSTRM_U=(upgrade 2.0); RM=(remove)
else INSTALL=(1); UPGRADE=(2); PRE_I=(1); PRE_U=(2); OLD_PRERM_U=(1); OLD_POSTRM_U=(1); RM=(0)
fi

# A group named like a run user already exists (useradd --user-group would refuse it).
"$REAL_GROUPADD" --system maldex-dns

# ---- fresh install ----
run preinstall "${PRE_I[@]}"
check "preinstall (install) exits 0" [ "$RC" = 0 ]
check "preinstall creates the five users (useradd 5 times)" [ "$(count '^useradd')" = 5 ]
check "the users exist" users_ok
check "an existing same-name group is reused (maldex-dns: own group only)" own_group_only maldex-dns
check "run users have their own group only" own_group_only maldex-mitm
unpack
run postinstall "${INSTALL[@]}"
check "postinstall (install) exits 0" [ "$RC" = 0 ]
check "postinstall creates no users" [ "$(count '^useradd')" = 0 ]
check "maldex is in kvm and libvirt" bash -c 'g=" $(id -nG maldex) "; [[ "$g" == *" kvm "* && "$g" == *" libvirt "* ]]'
check "visudo -cf checks the shipped sudoers" logged '^visudo -cf /opt/maldex/share/sudoers/maldex$'
check "sudoers installed 0440 root:root" mode_owner "440 root:root" /etc/sudoers.d/maldex
check "sudoers content is the $FMT file" same_file "/pkg/sudoers/maldex.$FMT" /etc/sudoers.d/maldex
check "/var/lib/maldex is 0750 maldex" mode_owner "750 maldex:maldex" /var/lib/maldex
check "/var/log/maldex is 0750 maldex" mode_owner "750 maldex:maldex" /var/log/maldex
check "/var/lib/maldex/.data/vm exists (0750 maldex)" mode_owner "750 maldex:maldex" /var/lib/maldex/.data/vm
check "the config is root:maldex 0640" mode_owner "640 root:maldex" /etc/maldex/appsettings.Production.json
check "host guard enabled and started" logged '^systemctl enable --now maldex-host-guard\.service$'
check "guacd enabled" logged '^systemctl enable guacd\.service$'
check "inetsim and suricata masked" bash -c "grep -q '^systemctl mask inetsim.service' $LOG && grep -q '^systemctl mask suricata.service' $LOG"
check "udev rules reloaded and misc triggered" bash -c "grep -q '^udevadm control --reload' $LOG && grep -q '^udevadm trigger -s misc' $LOG"
check "install: no try-restart of maldex" not_logged 'try-restart maldex\.service'
check "install: prints Run: sudo maldex-setup" grep -q '^Run: sudo maldex-setup$' <<<"$OUT"
if [ "$FMT" = rpm ]; then
  check "SELinux: semanage fcontext virt_image_t on the VM tree" logged '^semanage fcontext -a -t virt_image_t /var/lib/maldex/\.data/vm\(/\.\*\)\?$'
  check "SELinux: restorecon -R on the existing VM tree" logged '^restorecon target exists: /var/lib/maldex/\.data/vm$'
else
  check "deb: no SELinux labelling" not_logged '^(semanage|restorecon)'
fi
touch /var/lib/maldex/.data/vm/evidence.qcow2
BEFORE="$(snapshot)"

# ---- upgrade (deb: old prerm, new preinst, old postrm, new postinst; rpm: new %pre/%post, then old %preun/%postun) ----
if [ "$FMT" = deb ]; then
  run preremove "${OLD_PRERM_U[@]}"
  check "old prerm (upgrade) stops nothing" not_logged '^systemctl'
fi
run preinstall "${PRE_U[@]}"
check "preinstall (upgrade) creates no users" [ "$(count '^useradd')" = 0 ]
if [ "$FMT" = deb ]; then
  run postremove "${OLD_POSTRM_U[@]}"
  check "old postrm (upgrade) keeps sudoers and /etc/maldex" kept_etc
fi
run postinstall "${UPGRADE[@]}"
check "postinstall (upgrade) exits 0" [ "$RC" = 0 ]
check "upgrade: try-restart maldex" logged '^systemctl try-restart maldex\.service$'
check "upgrade: restart the host guard (a changed ruleset loads)" logged '^systemctl restart maldex-host-guard\.service$'
check "upgrade: no setup hint" no_hint
if [ "$FMT" = rpm ]; then
  run preremove "${OLD_PRERM_U[@]}"
  check "old %preun (upgrade) stops nothing" not_logged '^systemctl'
  run postremove "${OLD_POSTRM_U[@]}"
  check "old %postun (upgrade) keeps sudoers and /etc/maldex" kept_etc
fi
check "upgrade leaves /var/lib/maldex as it was" [ "$(snapshot)" = "$BEFORE" ]

# ---- sudoers that fails visudo ----
rm -f /etc/sudoers.d/maldex; touch /tmp/visudo-fail
run postinstall "${UPGRADE[@]}"
check "postinstall fails when visudo rejects the sudoers file" [ "$RC" != 0 ]
check "a rejected sudoers file is not installed" no_sudoers
rm -f /tmp/visudo-fail
run postinstall "${UPGRADE[@]}"
check "a re-run with a valid file installs it" [ -f /etc/sudoers.d/maldex ]

# ---- remove ----
run preremove "${RM[@]}"
check "remove: stops and disables maldex" bash -c "grep -qx 'systemctl stop maldex.service' $LOG && grep -qx 'systemctl disable maldex.service' $LOG"
run postremove "${RM[@]}"
check "remove: drops the sudo grant" [ ! -e /etc/sudoers.d/maldex ]
check "remove: keeps /etc/maldex" [ -f /etc/maldex/appsettings.Production.json ]
check "remove: keeps the users" users_ok
if [ "$FMT" = deb ]; then
  run postremove purge
  check "purge: removes /etc/maldex" [ ! -e /etc/maldex ]
  check "purge: keeps the users" users_ok
fi
check "/var/lib/maldex is never touched by removal" [ "$(snapshot)" = "$BEFORE" ]
check "maldex is never started by any script" never_starts_maldex

echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
HARNESS
}

PASS=0
FAIL=0
for pair in deb:ubuntu:24.04 rpm:rockylinux:9; do
  fmt="${pair%%:*}"; image="${pair#*:}"
  echo "# $fmt in $image"
  out="$(harness | docker run --rm -i --network none -v "$PKG:/pkg:ro" "$image" bash -s "$fmt" 2>&1)"; rc=$?
  printf '%s\n' "$out"
  p="$(grep -c '^ok   - ' <<<"$out")"; f="$(grep -c '^FAIL - ' <<<"$out")"
  PASS=$((PASS + p)); FAIL=$((FAIL + f))
  if [ "$rc" != 0 ] && [ "$f" = 0 ]; then echo "FAIL - [$fmt] the harness exited $rc"; FAIL=$((FAIL + 1)); fi
done
echo
echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
