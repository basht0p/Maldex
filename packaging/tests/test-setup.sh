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
EXPECTED_CHECKS=33

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

# Canned `Maldex.Web admin list --json` output: $1 = admin (one enabled local Admin) | breakglass (that admin is break-glass) |
# disabled (the only Admin is disabled) | none (an enabled Analyst only); $2 = passwordLogin; $3 = ssoEnabled.
admin_list() {
  local role=Admin dis=false bg=false
  case "$1" in breakglass) bg=true ;; disabled) dis=true ;; none) role=Analyst ;; esac
  {
    printf '[{"userName":"u1","displayName":"U One","kind":"Local","role":"%s","isDisabled":%s,"totpEnabled":false,"isBreakGlass":%s,"lastSignInAt":null}]\n' \
      "$role" "$dis" "$bg"
    printf '#config {"passwordLogin":%s,"ssoEnabled":%s}\n' "$2" "$3"
  } > "$T/admin-list"
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
  for c in chown chgrp chmod useradd usermod udevadm nft setfacl virsh swtpm suricata inetsim yara wg wiminfo xorriso xmllint \
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
  stub sudo '[ "$STUB_EUID" = 0 ] || [ "$1" = -n ] || exit 1; while [ $# -gt 0 ]; do case "$1" in -n) shift;; -u) shift 2;; --) shift; break;; *) break;; esac; done; STUB_EUID=0 exec "$@"'
  stub runuser 'while [ $# -gt 0 ]; do case "$1" in -u) shift 2;; --) shift; break;; *) break;; esac; done; exec "$@"'
  stub systemctl 'case "$*" in is-active*maldex*) exit "${STUB_ACTIVE_RC:-0}";; esac; exit 0'
  stub dpkg-query 'echo "1.52.0-test"'
  stub libguestfs-make-fixed-appliance '[ ! -e "$STUB_SRV/guestfs-fail" ] || exit 1; mkdir -p "$1"; touch "$1/kernel" "$1/initrd" "$1/root"'
  # maldex-admin: the real wrapper (it runs through the runuser stub) around a stub standing in for Maldex.Web (MALDEX_ADMIN_EXEC).
  cp "$PKG/bin/maldex-admin" "$T/pkg/bin/maldex-admin"
  stub admin-exec 'case "$2" in list) [ -z "${STUB_LIST_STDERR:-}" ] || echo "warn: noise on stderr" >&2; cat "$STUB_ADMIN_LIST"; exit "${STUB_LIST_RC:-0}";; create-user) exit "${STUB_CREATE_RC:-0}";; esac; exit 0'
  export MALDEX_ADMIN_EXEC="$T/bin/admin-exec" STUB_ADMIN_LIST="$T/admin-list" MALDEX_ADMIN_CWD="$T"
  admin_list admin true false
  unset MALDEX_ASSUME_TTY SETUP_STDIN
  printf '#!/usr/bin/env bash\necho "Maldex.Web $*" >> "$STUB_LOG"\nmkdir -p "$PLAYWRIGHT_BROWSERS_PATH/chromium-1"\n' > "$T/opt/app/Maldex.Web"
  /bin/chmod +x "$T/opt/app/Maldex.Web"

  export MALDEX_ETC="$T/etc" MALDEX_DATA="$T/data" MALDEX_OPT="$T/opt" INETSIM_TEMPLATE_DIR="$T/inetsim"
  # Host paths the verify checks read, so a full run passes verify on any host (CI has no KVM).
  touch "$T/ovmf-secboot.fd" "$T/kvm"; echo 0 > "$T/bridge-nf-call-iptables"
  export MALDEX_OVMF_SECBOOT="$T/ovmf-secboot.fd" MALDEX_KVM_DEV="$T/kvm" MALDEX_BR_NF_CALL_IPTABLES="$T/bridge-nf-call-iptables"
  printf '#!/bin/sh\n' > "$T/qemu-system-x86_64"; /bin/chmod +x "$T/qemu-system-x86_64"
  export MALDEX_QEMU_EMULATOR="$T/qemu-system-x86_64" MALDEX_START_WAIT=0
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
  # stdin is a pipe (SETUP_STDIN, empty by default), never the caller's terminal; MALDEX_ASSUME_TTY=1 stands in for [ -t 0 ].
  OUT="$(bash "$T/pkg/bin/maldex-setup" "$@" 2>&1 <<<"${SETUP_STDIN-}")"; RC=$?
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
  check "start enables maldex after verify" grep -q "^systemctl enable maldex\$" "$STUB_LOG"
  check "start restarts maldex (a re-run applies new configuration)" grep -q "^systemctl restart maldex\$" "$STUB_LOG"
  check "start checks maldex stayed active" grep -q "^systemctl is-active --quiet maldex\$" "$STUB_LOG"
  check "start prints ok start" grep -q "^ok   start\$" <<<"$OUT"
  check "start prints the URL and the next step" grep -q "http://127.0.0.1:5080.*" <<<"$OUT"
  teardown

  setup_sandbox; write_config
  export STUB_ACTIVE_RC=3
  run_setup
  check "a service that does not stay active fails start" grep -q "^FAIL start -- maldex did not stay running" <<<"$OUT"
  check "a failed start points at the journal" grep -q "journalctl -u maldex -n 50" <<<"$OUT"
  check "a failed start exits nonzero" [ "$RC" != 0 ]
  check "a failed start does not claim maldex is running" not_in_out "Maldex is running"
  unset STUB_ACTIVE_RC
  teardown

  setup_sandbox; write_config
  echo 1 > "$T/bridge-nf-call-iptables"
  run_setup
  check "a failed verify check fails verify" grep -q "^FAIL verify -- " <<<"$OUT"
  check "a failed verify stops before start" not_logged "^systemctl (enable|restart) maldex"
  teardown
}

test_verify_yara_and_emulator() {
  setup_sandbox; write_config
  stub yara 'exit 127'   # (the host may have a real yara on PATH)
  rm -f "$T/qemu-system-x86_64"
  run_setup --check
  check "a missing yara fails its check" grep -q "^FAIL yara -- .*no YARA detections" <<<"$OUT"
  check "a missing emulator fails its check" grep -q "^FAIL VM emulator $T/qemu-system-x86_64 -- .*qemu-kvm" <<<"$OUT"
  teardown
  setup_sandbox; write_config
  run_setup --check
  check "yara present: ok yara" grep -q "^ok   yara\$" <<<"$OUT"
  check "an executable emulator: ok" grep -q "^ok   VM emulator $T/qemu-system-x86_64\$" <<<"$OUT"
  check "the inetsim hint no longer sends RHEL to EPEL" not_in_out "inetsim.*from EPEL"
  teardown
}

test_offline_browsers() {
  setup_sandbox; write_config
  run_setup --offline "$T/offline"
  check "--offline with no Chromium seed: browsers passes" grep -q "^ok   browsers\$" <<<"$OUT"
  check "--offline with no Chromium seed warns about PDF reports" grep -q "^warn browsers -- .*PDF reports" <<<"$OUT"
  check "--offline never runs the Playwright download" not_logged "^(Maldex.Web|sudo -u maldex env PLAYWRIGHT)"
  check "--offline never calls curl (whole run)" not_logged '^curl'
  check "--offline with no Chromium: verify warns instead of failing" grep -q "^warn Playwright Chromium" <<<"$OUT"
  check "--offline with no Chromium: verify and start pass" bash -c 'grep -q "^ok   verify$" <<<"$1" && grep -q "^ok   start$" <<<"$1"' _ "$OUT"
  teardown

  setup_sandbox; write_config
  mkdir -p "$T/offline/ms-playwright/chromium-1234/chrome-linux" "$T/offline/ms-playwright/ffmpeg-1"
  printf 'bin\n' > "$T/offline/ms-playwright/chromium-1234/chrome-linux/chrome"
  run_setup --offline "$T/offline"
  check "--offline with a seed: browsers passes" grep -q "^ok   browsers\$" <<<"$OUT"
  check "--offline with a seed copies it to the browsers dir" cmp -s "$T/offline/ms-playwright/chromium-1234/chrome-linux/chrome" "$T/data/ms-playwright/chromium-1234/chrome-linux/chrome"
  check "--offline with a seed copies every entry" [ -d "$T/data/ms-playwright/ffmpeg-1" ]
  check "--offline with a seed gives it to maldex" grep -q "^chown -R -h maldex:maldex $T/data/ms-playwright\$" "$STUB_LOG"
  check "--offline with a seed: no warning" not_in_out "^warn "
  check "--offline with a seed: ok Chromium in verify" grep -q "^ok   Playwright Chromium" <<<"$OUT"
  check "--offline with a seed never downloads" not_logged "^(curl|Maldex.Web)"
  teardown
}

test_connection_string_parse() {
  setup_sandbox
  # A "}" in the password, full-line comments and a differently cased key: the JSON parse handles all three.
  cat > "$T/etc/appsettings.Production.json" <<'JSON'
{
  // the admin's settings
  "connectionStrings": {
    // SQL Server
    "maldex": "Server=sql2.example.internal,1500;Database=maldex;User Id=maldex;Password=a}b{c;TrustServerCertificate=True"
  }
}
JSON
  run_setup --check
  check "JSON parse: ok db despite } in the password" grep -q '^ok   db$' <<<"$OUT"
  check "JSON parse: probes the configured host and port" grep -q 'dev/tcp/$1/$2 _ sql2.example.internal 1500' "$STUB_LOG"
  teardown

  setup_sandbox; write_config
  stub python3 'exit 1'
  run_setup --check
  check "sed fallback when python3 fails: ok db" grep -q '^ok   db$' <<<"$OUT"
  check "sed fallback probes the configured host and port" grep -q 'sql.example.internal 14330' "$STUB_LOG"
  teardown

  setup_sandbox
  printf '{ "Maldex": {} }\n' > "$T/etc/appsettings.Production.json"
  run_setup --check
  check "no connection string: the hint names the drop-in limit" grep -q '^FAIL db -- .*ConnectionStrings__Maldex.*drop-in' <<<"$OUT"
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

test_admin_step() {
  # an enabled Admin exists: a no-op
  setup_sandbox; write_config
  run_setup
  check "admin step passes when an enabled admin exists" grep -q '^ok   admin$' <<<"$OUT"
  check "admin step is a no-op when an admin exists (no create-user)" not_logged 'create-user'
  check "admin step lists users as JSON through maldex-admin" called_with 'admin-exec admin list --json'
  check "maldex-admin gives the app the unit environment" \
    called_with 'env ASPNETCORE_ENVIRONMENT=Production MALDEX_EXTRA_CONFIG=/opt/maldex/app/appsettings.Package.json:/etc/maldex/appsettings.Production.json ASPNETCORE_CONTENTROOT=/opt/maldex/app HOME=/var/lib/maldex'
  check "admin step runs between db and downloads" bash -c '[ "$(grep -n "^ok   " <<<"$1" | sed -n "1,2p" | cut -d: -f2 | tr "\n" ",")" = "ok   db,ok   admin," ]' _ "$OUT"
  teardown

  # no admin, on a terminal: prompts and creates (default name)
  setup_sandbox; write_config; admin_list none true false
  export MALDEX_ASSUME_TTY=1 SETUP_STDIN=""
  run_setup
  check "admin step creates the first admin on a tty (default name admin)" called_with 'admin-exec admin create-user admin --admin$'
  check "admin step prompts for the user name" grep -q 'First admin user name \[admin\]: ' <<<"$OUT"
  check "admin step passes when the create succeeds" grep -q '^ok   admin$' <<<"$OUT"
  check "admin step does not add --break-glass while password login is on" not_logged 'create-user.*--break-glass'
  teardown

  setup_sandbox; write_config; admin_list disabled true false
  export MALDEX_ASSUME_TTY=1 SETUP_STDIN="alice"
  run_setup
  check "a disabled admin does not count; the typed name is used" called_with 'admin-exec admin create-user alice --admin$'
  teardown

  setup_sandbox; write_config; admin_list none true false
  export MALDEX_ASSUME_TTY=1 SETUP_STDIN="--evil"
  run_setup
  check "a user name that looks like an option is refused" grep -q '^FAIL admin -- ' <<<"$OUT"
  check "a refused name creates nothing" not_logged 'create-user'
  teardown

  setup_sandbox; write_config; admin_list none true false
  export MALDEX_ASSUME_TTY=1 SETUP_STDIN="" STUB_CREATE_RC=1
  run_setup
  check "a refused create-user fails the admin step" grep -q '^FAIL admin -- ' <<<"$OUT"
  check "a failed admin step stops before downloads" not_in_out '^ok   downloads'
  unset STUB_CREATE_RC
  teardown

  # password login off: the first admin is a break-glass admin
  setup_sandbox; write_config; admin_list none false true
  export MALDEX_ASSUME_TTY=1 SETUP_STDIN=""
  run_setup
  check "admin step adds --break-glass when password login is off" called_with 'create-user admin --admin --break-glass$'
  teardown

  # no admin, not on a terminal: fail with the command to run
  setup_sandbox; write_config; admin_list none true false
  run_setup
  check "admin step fails non-interactively" grep -q '^FAIL admin -- ' <<<"$OUT"
  check "admin step names the create-user command when non-interactive" \
    grep -qF 'no Maldex admin exists; create one with: sudo maldex-admin create-user <name> --admin' <<<"$OUT"
  check "admin step does not create a user non-interactively" not_logged 'create-user'
  check "admin step exits 1 and stops before downloads" bash -c '[ "$1" = 1 ] && ! grep -q "^ok   downloads" <<<"$2"' _ "$RC" "$OUT"
  teardown

  # maldex-admin itself failing (exit 78: configuration or database)
  setup_sandbox; write_config
  export STUB_LIST_RC=78
  run_setup
  check "a failing maldex-admin list fails the admin step with its exit status" grep -Eq '^FAIL admin -- .*(exit status 78|78)' <<<"$OUT"
  unset STUB_LIST_RC
  teardown

  # stderr noise must not corrupt the JSON stream
  setup_sandbox; write_config
  export STUB_LIST_STDERR=1
  run_setup
  check "stderr output of maldex-admin list does not break the admin step" grep -q '^ok   admin$' <<<"$OUT"
  unset STUB_LIST_STDERR
  teardown

  # the wrapper runs from the unit's working directory and says so when it is missing
  setup_sandbox; write_config
  export MALDEX_ADMIN_CWD="$T/nonexistent"
  run_setup
  check "a missing /var/lib/maldex fails the admin step with a clear hint" grep -q 'FAIL admin -- .*/var/lib/maldex is missing' <<<"$OUT"
  teardown
}

test_admin_check() {
  setup_sandbox; write_config; export STUB_EUID=1000
  admin_list none true false
  run_setup --check
  check "--check fails without an admin" grep -q '^FAIL admin -- ' <<<"$OUT"
  check "--check names the create-user command without an admin" grep -qF 'sudo maldex-admin create-user <name> --admin' <<<"$OUT"
  check "--check without an admin exits 1" [ "$RC" = 1 ]
  check "--check never creates a user" not_logged 'create-user'
  check "--check reaches maldex-admin through sudo -n" called_with 'sudo -n .*maldex-admin list --json'
  teardown

  setup_sandbox; write_config; export STUB_EUID=1000
  admin_list disabled true false
  run_setup --check
  check "--check fails when the only admin is disabled" grep -q '^FAIL admin -- ' <<<"$OUT"
  teardown

  setup_sandbox; write_config; export STUB_EUID=1000
  run_setup --check
  check "--check passes with an enabled admin" grep -q '^ok   admin$' <<<"$OUT"
  check "--check is quiet when password login is on" not_in_out '^warn admin'
  teardown

  setup_sandbox; write_config; export STUB_EUID=1000
  admin_list admin false true
  run_setup --check
  check "--check warns about a missing break-glass admin (password login off, SSO on)" \
    grep -Eq '^warn admin -- .*break-glass.*set-break-glass' <<<"$OUT"
  check "--check still passes admin on that warning" grep -q '^ok   admin$' <<<"$OUT"
  teardown

  setup_sandbox; write_config; export STUB_EUID=1000
  admin_list breakglass false true
  run_setup --check
  check "--check does not warn when a break-glass admin exists" not_in_out '^warn admin'
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
         test_rerun_after_guestfs_failure test_requires_root test_start_step test_check_never_prompts test_modes_and_symlinks test_usage \
         test_verify_yara_and_emulator test_offline_browsers test_connection_string_parse test_admin_step test_admin_check; do
  echo "# $t"
  OUT=""
  "$t"
done
echo
echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
