#!/usr/bin/env bash
# shellcheck disable=SC2016  # the stub bodies are written verbatim; they expand when the stub runs
# Tests for packaging/bin/maldex-setup. Plain bash: every privileged or networked command is a PATH stub that records its
# call, and MALDEX_ETC / MALDEX_DATA / MALDEX_OPT point into a temp dir. Nothing on the host is changed.
#   bash packaging/tests/test-setup.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG="$(cd "$HERE/.." && pwd)"
SETUP_SRC="$PKG/bin/maldex-setup"
LIB_SRC="$PKG/lib/maldex-common.sh"

# Checks --check prints: db plus the verify list (the downloads' file presence is part of the verify list).
EXPECTED_CHECKS=30

PASS=0
FAIL=0
T=""

ok()   { echo "ok   - $1"; PASS=$((PASS + 1)); }
nok()  { echo "FAIL - $1"; FAIL=$((FAIL + 1)); [ -z "${OUT:-}" ] || printf '%s\n' "$OUT" | sed 's/^/      | /'; }
check() { local name="$1"; shift; if "$@"; then ok "$name"; else nok "$name"; fi; }
called_with() { grep -q -- "$1" "$STUB_LOG" 2>/dev/null; }
not_logged() { ! grep -Eq -- "$1" "$STUB_LOG"; }
not_in_out() { ! grep -Eq -- "$1" <<<"$OUT"; }
only_id_called() { [ ! -e "$T/data" ] && ! grep -Eqv '^id ' "$STUB_LOG"; }

stub() { # name, body (the body runs after the call is recorded)
  printf '#!/usr/bin/env bash\necho "%s $*" >> "$STUB_LOG"\n%s\n' "$1" "$2" > "$T/bin/$1"
  /bin/chmod +x "$T/bin/$1"
}

# A fresh sandbox: stubs, a copy of the script and library with test pins, config, an offline dir and a fake download server.
setup_sandbox() {
  T="$(mktemp -d)"
  mkdir -p "$T/bin" "$T/pkg/bin" "$T/pkg/lib" "$T/etc" "$T/srv" "$T/offline" "$T/opt/app" "$T/opt/agent" "$T/inetsim"
  export STUB_LOG="$T/calls.log" STUB_SRV="$T/srv" STUB_EUID=0
  : > "$STUB_LOG"

  printf 'test sysmon\n' > "$T/srv/Sysmon.zip"
  printf 'test virtio\n' > "$T/srv/virtio-win.iso"
  cp "$T/srv/"* "$T/offline/"
  local s_sha v_sha
  s_sha="$(sha256sum "$T/srv/Sysmon.zip" | cut -d' ' -f1)"
  v_sha="$(sha256sum "$T/srv/virtio-win.iso" | cut -d' ' -f1)"
  # The script finds its library next to itself (../lib); the copy carries hashes of the test files instead of the real pins.
  cp "$SETUP_SRC" "$T/pkg/bin/maldex-setup" 2>/dev/null
  sed -e "s/^SYSMON_SHA256=.*/SYSMON_SHA256=\"$s_sha\"/" -e "s/^VIRTIO_WIN_SHA256=.*/VIRTIO_WIN_SHA256=\"$v_sha\"/" \
    "$LIB_SRC" > "$T/pkg/lib/maldex-common.sh" 2>/dev/null

  printf 'agent\n' > "$T/opt/agent/Maldex.Agent.exe"
  printf '1.0.0\n' > "$T/opt/agent/Maldex.Agent.version"

  # Recording no-ops for everything that would change the host.
  for c in chown chgrp chmod useradd usermod udevadm nft setfacl virsh swtpm suricata inetsim wg wiminfo xorriso xmllint \
           libguestfs-test-tool find; do
    stub "$c" 'exit 0'
  done
  stub mitmdump 'case "$*" in *--version*) echo "Mitmproxy: 11.1.3"; exit 0;; esac
for a in "$@"; do case "$a" in confdir=*) d="${a#confdir=}"; mkdir -p "$d"; for f in mitmproxy-ca.pem mitmproxy-ca-cert.pem mitmproxy-dhparam.pem; do echo x > "$d/$f"; done;; esac; done'
  stub id 'case "$1" in
  -u) if [ $# -eq 1 ]; then echo "$STUB_EUID"; else case "$2" in maldex-isroot) echo 990;; maldex-isuser) echo 991;; *) echo 992;; esac; fi;;
  -g) case "$2" in maldex-isroot) echo 990;; maldex-isuser) echo 991;; *) echo 992;; esac;;
  -un) if [ "$STUB_EUID" = 0 ]; then echo root; else echo tester; fi;;
  -nG) if [ "$2" = maldex ]; then echo "maldex kvm libvirt"; else echo "$2"; fi;;
  *) exit 0;;
esac'
  stub curl 'out=""; while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift 2;; -*) shift;; *) url="$1"; shift;; esac; done
cp "$STUB_SRV/${url##*/}" "$out"'
  stub timeout 'shift; case "$*" in *dev/tcp*) exit "${STUB_TCP_RC:-0}";; esac; exec "$@"'
  # For a non-root caller, sudo without -n would prompt for a password: the stub fails it, as a hang would.
  stub sudo '[ "$STUB_EUID" = 0 ] || [ "$1" = -n ] || exit 1; while [ $# -gt 0 ]; do case "$1" in -n) shift;; -u) shift 2;; --) shift; break;; *) break;; esac; done; exec "$@"'
  stub runuser 'while [ $# -gt 0 ]; do case "$1" in -u) shift 2;; --) shift; break;; *) break;; esac; done; exec "$@"'
  stub systemctl 'exit 0'
  stub dpkg-query 'echo "1.52.0-test"'
  stub libguestfs-make-fixed-appliance '[ ! -e "$STUB_SRV/guestfs-fail" ] || exit 1; mkdir -p "$1"; touch "$1/kernel" "$1/initrd" "$1/root"'
  printf '#!/usr/bin/env bash\necho "Maldex.Web $*" >> "$STUB_LOG"\nmkdir -p "$PLAYWRIGHT_BROWSERS_PATH/chromium-1"\n' > "$T/opt/app/Maldex.Web"
  /bin/chmod +x "$T/opt/app/Maldex.Web"

  export MALDEX_ETC="$T/etc" MALDEX_DATA="$T/data" MALDEX_OPT="$T/opt" INETSIM_TEMPLATE_DIR="$T/inetsim"
  # Host paths the verify checks read, so a full run passes verify on any host (CI has no KVM).
  touch "$T/ovmf-secboot.fd" "$T/kvm"; echo 0 > "$T/bridge-nf-call-iptables"
  export MALDEX_OVMF_SECBOOT="$T/ovmf-secboot.fd" MALDEX_KVM_DEV="$T/kvm" MALDEX_BR_NF_CALL_IPTABLES="$T/bridge-nf-call-iptables"
  export PATH="$T/bin:/usr/bin:/bin"
}

write_config() {
  cat > "$T/etc/appsettings.Production.json" <<'JSON'
{
  "ConnectionStrings": {
    "Maldex": "Server=sql.example.internal,14330;Database=maldex;User Id=maldex;Password=p\"w;TrustServerCertificate=True"
  },
  "Maldex": { "Real": { "Root": "/var/lib/maldex" } }
}
JSON
}

teardown() { [ -z "$T" ] || rm -rf "$T"; T=""; }

run_setup() {
  OUT="$(bash "$T/pkg/bin/maldex-setup" "$@" 2>&1)"; RC=$?
  [ "${MALDEX_TEST_VERBOSE:-0}" = 0 ] || printf '>>> maldex-setup %s (rc=%s)\n%s\n' "$*" "$RC" "$OUT"
}

# ---------------------------------------------------------------------------------------------------------------------

test_check_without_config() {
  setup_sandbox; export STUB_EUID=1000
  run_setup --check
  check "--check with no config exits 1" [ "$RC" = 1 ]
  check "--check with no config names ConnectionStrings:Maldex on the db line" \
    grep -Eq '^FAIL db -- .*ConnectionStrings:Maldex' <<<"$OUT"
  check "--check leaves the data dir untouched" [ ! -e "$T/data" ]
  check "--check calls nothing that changes the host" \
    not_logged '^(curl|useradd|chown|chgrp|chmod|mitmdump --set|libguestfs-make-fixed-appliance|Maldex.Web|systemctl enable)'
  teardown
}

test_check_lists_every_check() {
  setup_sandbox; export STUB_EUID=1000
  run_setup --check
  local n; n="$(grep -Ec '^(ok   |FAIL )' <<<"$OUT")"
  check "--check prints all $EXPECTED_CHECKS checks after a failure (got $n)" [ "$n" = "$EXPECTED_CHECKS" ]
  check "--check reports a db failure and still goes on" grep -q '^FAIL db' <<<"$OUT"
  write_config
  run_setup --check
  n="$(grep -Ec '^(ok   |FAIL )' <<<"$OUT")"
  check "--check with config prints all $EXPECTED_CHECKS checks (got $n)" [ "$n" = "$EXPECTED_CHECKS" ]
  check "--check with config and a reachable server: ok db" grep -q '^ok   db' <<<"$OUT"
  check "the db probe targets the configured host and port" called_with "dev/tcp"
  export STUB_TCP_RC=1
  run_setup --check
  check "an unreachable server fails db with host:port" grep -Eq '^FAIL db -- .*sql\.example\.internal:14330' <<<"$OUT"
  unset STUB_TCP_RC
  teardown
}

test_downloads_skip_and_refetch() {
  setup_sandbox; write_config
  mkdir -p "$T/data/.data/vm/iso"
  cp "$T/srv/Sysmon.zip" "$T/data/.data/vm/iso/Sysmon.zip"
  printf 'corrupt\n' > "$T/data/.data/vm/iso/virtio-win.iso"
  touch "$T/srv/guestfs-fail"   # stop after the steps under test
  run_setup
  check "downloads step passes" grep -q '^ok   downloads' <<<"$OUT"
  check "a file that already verifies is not downloaded again" not_logged '^curl .*Sysmon.zip'
  check "a corrupt file is fetched again" grep -q '^curl .*virtio-win.iso' "$STUB_LOG"
  check "the re-fetched file verifies" cmp -s "$T/srv/virtio-win.iso" "$T/data/.data/vm/iso/virtio-win.iso"
  check "without SELinux, restorecon is not run" not_logged '^restorecon'
  teardown

  setup_sandbox; write_config
  stub selinuxenabled 'exit 0'
  stub restorecon 'exit 0'
  touch "$T/srv/guestfs-fail"
  run_setup
  check "with SELinux enabled, the downloads step relabels the VM tree" grep -qx "restorecon -R $T/data/.data/vm" "$STUB_LOG"
  teardown
}

test_offline() {
  setup_sandbox; write_config
  touch "$T/srv/guestfs-fail"
  run_setup --offline "$T/offline"
  check "--offline: downloads step passes" grep -q '^ok   downloads' <<<"$OUT"
  check "--offline never calls curl" not_logged '^curl'
  check "--offline copies Sysmon.zip" cmp -s "$T/offline/Sysmon.zip" "$T/data/.data/vm/iso/Sysmon.zip"
  check "--offline copies virtio-win.iso" cmp -s "$T/offline/virtio-win.iso" "$T/data/.data/vm/iso/virtio-win.iso"
  teardown

  setup_sandbox; write_config
  printf 'tampered\n' > "$T/offline/virtio-win.iso"
  run_setup --offline "$T/offline"
  check "--offline with a wrong hash exits 1" [ "$RC" = 1 ]
  check "--offline with a wrong hash fails downloads" grep -q '^FAIL downloads -- ' <<<"$OUT"
  check "--offline with a wrong hash says expected and got" grep -Eq 'SHA-256 mismatch .*expected [0-9a-f]{64}, got [0-9a-f]{64}' <<<"$OUT"
  check "--offline with a wrong hash leaves no bad file" [ ! -e "$T/data/.data/vm/iso/virtio-win.iso" ]
  check "--offline with a wrong hash stops before mitm-ca" not_in_out 'mitm-ca'
  check "--offline with a wrong hash never calls curl" not_logged '^curl'
  teardown
}

test_rerun_after_guestfs_failure() {
  setup_sandbox; write_config
  touch "$T/srv/guestfs-fail"
  run_setup
  check "first run exits 1" [ "$RC" = 1 ]
  check "first run fails at guestfs" grep -q '^FAIL guestfs -- ' <<<"$OUT"
  local s; for s in db downloads mitm-ca inetsim; do check "first run: ok $s" grep -q "^ok   $s\$" <<<"$OUT"; done
  check "first run stops at the first failure (no browsers step)" not_in_out '^(ok   |FAIL )browsers'
  check "first run fetched both files" [ "$(grep -c '^curl' "$STUB_LOG")" = 2 ]
  check "first run created the mitm CA" grep -q '^mitmdump --set' "$STUB_LOG"
  rm -f "$T/srv/guestfs-fail"; : > "$STUB_LOG"
  run_setup
  for s in db downloads mitm-ca inetsim guestfs browsers verify start; do check "re-run: ok $s" grep -q "^ok   $s\$" <<<"$OUT"; done
  check "re-run does not download again" not_logged '^curl'
  check "re-run does not regenerate the mitm CA" not_logged '^mitmdump --set'
  check "re-run does not create users again" not_logged '^useradd'
  check "re-run builds the appliance" [ -f "$T/data/guestfs-appliance/kernel" ]
  check "browsers step installs Chromium as maldex with the data dir's browser path" \
    grep -q "^sudo -u maldex env PLAYWRIGHT_BROWSERS_PATH=$T/data/ms-playwright $T/opt/app/Maldex.Web --install-browsers --no-deps" "$STUB_LOG"
  check "verify finds the packaged agent bundle" grep -q "^ok   agent bundle ($T/opt/agent)" <<<"$OUT"
  check "no agent copy into the data dir" [ ! -e "$T/data/.data/agent" ]
  teardown
}

test_requires_root() {
  setup_sandbox; write_config; export STUB_EUID=1000
  run_setup
  check "non-root run exits 1" [ "$RC" = 1 ]
  check "non-root run says it must run as root" grep -q 'maldex-setup must run as root' <<<"$OUT"
  check "non-root run changes nothing" only_id_called
  run_setup --offline "$T/offline"
  check "non-root --offline also refuses" grep -q 'maldex-setup must run as root' <<<"$OUT"
  run_setup --check
  check "non-root --check runs" grep -q '^FAIL\|^ok' <<<"$OUT"
  teardown
}

test_start_step() {
  setup_sandbox; write_config
  run_setup
  check "a full run passes verify" grep -q "^ok   verify\$" <<<"$OUT"
  check "start enables maldex after verify" grep -q "^systemctl enable --now maldex" "$STUB_LOG"
  check "start prints the URL and the next step" grep -q "http://127.0.0.1:5080.*" <<<"$OUT"
  teardown

  setup_sandbox; write_config
  echo 1 > "$T/bridge-nf-call-iptables"
  run_setup
  check "a failed verify check fails verify" grep -q "^FAIL verify -- " <<<"$OUT"
  check "a failed verify stops before start" not_logged "^systemctl enable --now maldex"
  teardown
}

test_check_never_prompts() {
  setup_sandbox; write_config; export STUB_EUID=1000
  run_setup --check
  check "non-root --check uses sudo for the root-only checks" grep -q "^sudo " "$STUB_LOG"
  check "non-root --check passes -n to every sudo call" not_logged "^sudo (\$|[^-]|-[^n])"
  check "non-root --check still lists every check" [ "$(grep -Ec "^(ok   |FAIL )" <<<"$OUT")" = "$EXPECTED_CHECKS" ]
  teardown
}

test_modes_and_symlinks() {
  setup_sandbox; write_config
  mkdir -p "$T/data/.data/vm/iso"
  cp "$T/srv/Sysmon.zip" "$T/data/.data/vm/iso/Sysmon.zip"
  touch "$T/srv/guestfs-fail"
  run_setup
  check "a kept download gets its owner and mode again" grep -q "^chown -h maldex:maldex $T/data/.data/vm/iso/Sysmon.zip" "$STUB_LOG"
  check "the CA key is 0600" grep -q "^chmod 0600 $T/data/.data/mitm/ca/mitmproxy-ca.pem" "$STUB_LOG"
  check "the CA cert is 0640 for the app" grep -q "^chmod 0640 $T/data/.data/mitm/ca/mitmproxy-ca-cert.pem" "$STUB_LOG"
  teardown

  setup_sandbox; write_config
  mkdir -p "$T/data/.data/vm" "$T/elsewhere"
  ln -s "$T/elsewhere" "$T/data/.data/vm/iso"
  run_setup
  check "a symlinked data dir fails downloads" grep -q "^FAIL downloads -- " <<<"$OUT"
  check "a symlinked data dir is refused by name" grep -q "refusing to use $T/data/.data/vm/iso: it is a symlink" <<<"$OUT"
  check "nothing lands behind the symlink" [ -z "$(ls -A "$T/elsewhere")" ]
  check "the symlink target is never chowned" not_logged "^chown .*elsewhere"
  teardown
}

test_usage() {
  setup_sandbox
  run_setup --bogus
  check "an unknown flag exits 64" [ "$RC" = 64 ]
  run_setup --offline
  check "--offline without a dir exits 64" [ "$RC" = 64 ]
  teardown
}

for t in test_check_without_config test_check_lists_every_check test_downloads_skip_and_refetch test_offline \
         test_rerun_after_guestfs_failure test_requires_root test_start_step test_check_never_prompts test_modes_and_symlinks test_usage; do
  echo "# $t"
  OUT=""
  "$t"
done
echo
echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
