#!/usr/bin/env bash
# Install smoke assertions, run as root inside the container after the package is installed: packaging/ci/smoke.sh <deb|rpm>
# Exits nonzero if any assertion fails. systemctl may be unavailable in a container, so the units are checked as files.
set -uo pipefail

KIND="${1:-}"
case "$KIND" in deb|rpm) ;; *) echo "usage: smoke.sh <deb|rpm>" >&2; exit 64 ;; esac

FAILS=0
pass() { echo "ok   $*"; }
fail() { echo "FAIL $*" >&2; FAILS=$((FAILS+1)); }

# the five service users
for u in maldex maldex-mitm maldex-dns maldex-isroot maldex-isuser; do
  if id "$u" >/dev/null 2>&1; then pass "user $u"; else fail "user $u missing"; fi
done

# sudoers grant parses
if [ -f /etc/sudoers.d/maldex ] && visudo -c >/dev/null 2>&1 && visudo -cf /etc/sudoers.d/maldex >/dev/null 2>&1; then pass "visudo -c"; else fail "visudo -c failed or /etc/sudoers.d/maldex missing"; fi

# units present (files; systemctl may not work here)
for unit in maldex.service maldex-host-guard.service; do
  if [ -f "/usr/lib/systemd/system/$unit" ] || [ -f "/lib/systemd/system/$unit" ]; then pass "unit $unit"; else fail "unit $unit not installed"; fi
done

# the app binary runs
if /opt/maldex/app/Maldex.Web --version >/dev/null 2>&1; then pass "Maldex.Web --version"; else fail "/opt/maldex/app/Maldex.Web --version did not exit 0"; fi

# maldex-setup --check runs without crashing and reports FAIL lines (no KVM in CI). FAIL lines go to stderr.
if [ -x /usr/sbin/maldex-setup ]; then
  out="$(/usr/sbin/maldex-setup --check 2>&1)"; rc=$?
  if [ "$rc" -gt 1 ]; then fail "maldex-setup --check crashed (exit $rc)"; printf '%s\n' "$out" >&2
  elif ! printf '%s\n' "$out" | grep -qE '^(ok  |FAIL) '; then fail "maldex-setup --check printed no step lines"; printf '%s\n' "$out" >&2
  elif ! printf '%s\n' "$out" | grep -q '^FAIL '; then fail "maldex-setup --check reported no FAIL (expected some without KVM)"; printf '%s\n' "$out" >&2
  else pass "maldex-setup --check ($(printf '%s\n' "$out" | grep -c '^FAIL ') FAIL lines expected in CI)"; fi
else
  fail "/usr/sbin/maldex-setup not installed"
fi

# config file perms
cfg=/etc/maldex/appsettings.Production.json
if [ -f "$cfg" ]; then
  got="$(stat -c '%a %U:%G' "$cfg")"
  if [ "$got" = "640 root:maldex" ]; then pass "$cfg is 0640 root:maldex"; else fail "$cfg is '$got', expected '640 root:maldex'"; fi
else
  fail "$cfg missing"
fi

if [ "$FAILS" -ne 0 ]; then echo "smoke ($KIND): $FAILS assertion(s) failed" >&2; exit 1; fi
echo "smoke ($KIND): all assertions passed"
