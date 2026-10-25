#!/usr/bin/env bash
# shellcheck disable=SC2016  # the grep patterns match literal $ in the install scripts
# Static checks of the package tree (packaging/nfpm.yaml and what it ships). Run by ./build.sh test; changes nothing on the host.
#   bash packaging/tests/check-package.sh           the checks; nfpm sources under packaging/stage/ are checked when a stage exists
#   bash packaging/tests/check-package.sh --stage   first stage the tree (./build.sh package --stage-only), then every check
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
PKG="$REPO/packaging"
NFPM="$PKG/nfpm.yaml"
UNIT="$PKG/systemd/maldex.service"
EXTRA_CONFIG="/opt/maldex/app/appsettings.Package.json:/etc/maldex/appsettings.Production.json"

PASS=0
FAIL=0
ok()   { echo "ok   - $1"; PASS=$((PASS + 1)); }
nok()  { echo "FAIL - $1"; FAIL=$((FAIL + 1)); [ -z "${OUT:-}" ] || printf '%s\n' "$OUT" | sed 's/^/      | /'; }
skip() { echo "skip - $1"; }
check() { local name="$1"; shift; OUT=""; if "$@"; then ok "$name"; else nok "$name"; fi; }

if [ "${1:-}" = "--stage" ]; then
  (cd "$REPO" && ./build.sh package --stage-only) || { echo "FAIL - ./build.sh package --stage-only"; exit 1; }
fi

# The contents entries of nfpm.yaml as "src<TAB>dst<TAB>type<TAB>packager" lines. Every entry is written as a "  - src:" block
# with "dst:", optional "type:" and optional "packager:" lines (nfpm.yaml keeps to that layout).
nfpm_entries() {
  awk '
    function f(v) { return v == "" ? "-" : v }
    function flush() { if (src != "" || dst != "") printf "%s\t%s\t%s\t%s\n", f(src), f(dst), f(type), f(pk); src = dst = type = pk = "" }
    /^contents:/ { inside = 1; next }
    inside && /^[^ #]/ { flush(); inside = 0 }
    !inside { next }
    /^  - / { flush() }
    { line = $0; sub(/^  - /, "    ", line) }
    line ~ /^    src:/      { v = line; sub(/^    src:[ ]*/, "", v); src = v }
    line ~ /^    dst:/      { v = line; sub(/^    dst:[ ]*/, "", v); dst = v }
    line ~ /^    type:/     { v = line; sub(/^    type:[ ]*/, "", v); type = v }
    line ~ /^    packager:/ { v = line; sub(/^    packager:[ ]*/, "", v); pk = v }
    END { flush() }
  ' "$NFPM"
}

# ---- nfpm sources exist ----
srcs_exist() {
  local src dst type pk missing=0 staged=0
  [ -f "$NFPM" ] || { OUT="$NFPM is missing"; return 1; }
  [ -d "$PKG/stage" ] && staged=1
  while IFS=$'\t' read -r src dst type pk; do
    case "$type" in symlink|dir) continue ;; esac
    [ "$src" != - ] || { OUT+="entry for $dst has no src"$'\n'; missing=1; continue; }
    case "$src" in packaging/stage/*) [ "$staged" = 1 ] || continue ;; esac
    [ -e "$REPO/$src" ] || { OUT+="missing: $src (for $dst)"$'\n'; missing=1; }
  done < <(nfpm_entries)
  [ "$staged" = 1 ] || echo "      (packaging/stage/ is absent: staged sources not checked; run with --stage)"
  [ "$(nfpm_entries | wc -l)" -gt 0 ] || { OUT+="no contents entries"; return 1; }
  [ "$missing" = 0 ]
}
check "every nfpm.yaml source exists" srcs_exist

required_dsts() {
  local d dsts
  dsts="$(nfpm_entries | cut -f2)"
  for d in /opt/maldex/app /opt/maldex/agent /opt/maldex/mitmproxy /opt/maldex/deploy /opt/maldex/rules \
           /opt/maldex/bin/maldex-setup /opt/maldex/lib/maldex-common.sh /usr/sbin/maldex-setup /usr/bin/mitmdump \
           /etc/maldex/appsettings.Production.json /etc/maldex/maldex-host-guard.nft /etc/udev/rules.d/99-maldex-kvm.rules \
           /usr/lib/systemd/system/maldex.service /usr/lib/systemd/system/maldex-host-guard.service \
           /usr/lib/systemd/system/guacd.service.d/maldex.conf; do
    grep -qxF -- "$d" <<<"$dsts" || { OUT+="no entry for $d"$'\n'; }
  done
  [ -z "$OUT" ]
}
check "nfpm.yaml ships every path of the layout" required_dsts

symlinks_right() {
  local e
  e="$(nfpm_entries)"
  grep -qxF $'/opt/maldex/bin/maldex-setup\t/usr/sbin/maldex-setup\tsymlink\t-' <<<"$e" || OUT+="/usr/sbin/maldex-setup is not a symlink to /opt/maldex/bin/maldex-setup"$'\n'
  grep -qxF $'/opt/maldex/mitmproxy/mitmdump\t/usr/bin/mitmdump\tsymlink\t-' <<<"$e" || OUT+="/usr/bin/mitmdump is not a symlink to /opt/maldex/mitmproxy/mitmdump"$'\n'
  [ -z "$OUT" ]
}
check "the maldex-setup and mitmdump symlinks" symlinks_right

etc_noreplace() {
  local src dst type pk n=0
  while IFS=$'\t' read -r src dst type pk; do
    case "$dst" in /etc/maldex/*) n=$((n + 1)); [ "$type" = "config|noreplace" ] || OUT+="$dst is type '$type'"$'\n' ;; esac
  done < <(nfpm_entries)
  [ "$n" -ge 2 ] || OUT+="expected at least 2 /etc/maldex entries, found $n"$'\n'
  [ -z "$OUT" ]
}
check "/etc/maldex entries are config|noreplace" etc_noreplace

# ---- scripts parse ----
scripts_parse() {
  local f
  for f in "$PKG"/scripts/*.sh "$PKG"/bin/* "$PKG"/lib/*; do
    [ -f "$f" ] || { OUT+="missing: $f"$'\n'; continue; }
    bash -n "$f" 2>>/dev/null || OUT+="bash -n failed: $f"$'\n'
  done
  for f in preinstall postinstall preremove postremove; do [ -f "$PKG/scripts/$f.sh" ] || OUT+="missing: scripts/$f.sh"$'\n'; done
  # The install scripts run under /bin/sh (rpm scriptlets; the deb maintainer scripts' shebang), so they must be POSIX.
  if command -v dash >/dev/null 2>&1; then
    for f in "$PKG"/scripts/*.sh; do [ -f "$f" ] && { dash -n "$f" 2>/dev/null || OUT+="dash -n failed: $f"$'\n'; }; done
  fi
  [ -z "$OUT" ]
}
check "bash -n (and dash -n for the install scripts)" scripts_parse

# ---- sudoers ----
VISUDO="$(command -v visudo || { [ -x /usr/sbin/visudo ] && echo /usr/sbin/visudo; } || true)"
for fmt in deb rpm; do
  f="$PKG/sudoers/maldex.$fmt"
  if [ -z "$VISUDO" ]; then
    skip "visudo -cf sudoers/maldex.$fmt (visudo is not installed)"
  else
    sudoers_valid() { [ -f "$f" ] || { OUT="missing: $f"; return 1; }; OUT="$("$VISUDO" -cf "$f" 2>&1)"; }
    check "visudo -cf sudoers/maldex.$fmt" sudoers_valid
  fi
done

# The commands the app runs through sudo -n: the first word after PrivilegedCommands.Sudo("…" / Sudo(["…" and "sudo", ["-n", "…".
app_sudo_commands() {
  grep -rhoE --include='*.cs' --exclude-dir=obj --exclude-dir=bin 'Sudo\(\[?"[^"]+"|"sudo", \["-n", "[^"]+"' "$REPO/src" \
    | sed -E 's/.*"([^"]+)"$/\1/' | sort -u
}
# The command basenames a sudoers file grants (the Cmnd_Alias list and any direct NOPASSWD list).
sudoers_commands() {
  sed -E 's/#.*//' "$1" | tr ',' '\n' | grep -oE '(^|[[:space:]=:])/[^[:space:],]+' | sed -E 's/^[[:space:]=:]//' \
    | xargs -r -n1 basename | sort -u
}
sudoers_paths_absolute() {
  # every granted command is an absolute path; no wildcards or ALL
  [ -f "$1" ] || { OUT="missing: $1"; return 1; }
  ! sed -E 's/#.*//' "$1" | grep -E 'NOPASSWD|Cmnd_Alias' | grep -qE '(NOPASSWD:[[:space:]]*ALL|\*)'
}
APP_CMDS="$(app_sudo_commands)"
for fmt in deb rpm; do
  f="$PKG/sudoers/maldex.$fmt"
  sudo_set_matches() {
    [ -f "$f" ] || { OUT="missing: $f"; return 1; }
    [ -n "$APP_CMDS" ] || { OUT="found no sudo commands in src/"; return 1; }
    local granted; granted="$(sudoers_commands "$f")"
    OUT="$(diff <(echo "$APP_CMDS") <(echo "$granted") | sed -e 's/^</app runs, not granted:/' -e 's/^>/granted, app does not run:/')"
    [ "$APP_CMDS" = "$granted" ]
  }
  check "sudoers/maldex.$fmt grants exactly the commands the app runs through sudo -n" sudo_set_matches
  root_equiv_comment() { grep -qi 'root-equivalent' "$f" && grep -qi 'dedicated' "$f"; }
  check "sudoers/maldex.$fmt states the grant is root-equivalent, for a dedicated host" root_equiv_comment
  check "sudoers/maldex.$fmt grants absolute paths only (no ALL, no wildcards)" sudoers_paths_absolute "$f"
done

# ---- systemd units ----
if command -v systemd-analyze >/dev/null 2>&1; then
  units_verify() {
    # verify checks that ExecStart binaries exist; the packaged app is not on this host, so it points at a stand-in.
    local tmp f out
    tmp="$(mktemp -d)"
    for f in "$PKG"/systemd/*.service; do
      sed -E 's#^ExecStart=/opt/maldex/app/Maldex.Web#ExecStart=/bin/true#' "$f" > "$tmp/$(basename "$f")"
    done
    out="$(cd "$tmp" && SYSTEMD_LOG_LEVEL=warning systemd-analyze verify --man=no ./*.service 2>&1)" || true
    rm -rf "$tmp"
    OUT="$out"
    [ -z "$out" ]
  }
  check "systemd-analyze verify packaging/systemd/*.service" units_verify

  # The drop-in over stand-ins for both distro guacd units (Ubuntu: forking, /etc/default/guacd; EPEL: -f $OPTS from
  # /etc/sysconfig/guacd). Each stand-in's own ExecStart names a binary that does not exist, so verify fails unless the drop-in
  # resets it; the drop-in's guacd points at /bin/true because guacd is not installed here.
  guacd_dropin_verify() {
    local tmp flavour out all=""
    for flavour in ubuntu epel; do
      tmp="$(mktemp -d)"
      mkdir -p "$tmp/guacd.service.d"
      if [ "$flavour" = ubuntu ]; then
        printf '%s\n' '[Unit]' 'Description=stand-in' '[Service]' 'Type=forking' 'PIDFile=/run/guacd/guacd.pid' \
          'EnvironmentFile=-/etc/default/guacd' 'ExecStart=/nonexistent/guacd $DAEMON_ARGS -b $LISTEN_ADDRESS -l $LISTEN_PORT' \
          '[Install]' 'WantedBy=multi-user.target' > "$tmp/guacd.service"
      else
        printf '%s\n' '[Unit]' 'Description=stand-in' '[Service]' 'EnvironmentFile=-/etc/sysconfig/guacd' \
          'ExecStart=/nonexistent/guacd -f $OPTS' 'Restart=on-failure' '[Install]' 'WantedBy=multi-user.target' > "$tmp/guacd.service"
      fi
      sed -E 's#^ExecStart=/usr/sbin/guacd #ExecStart=/bin/true #' "$PKG/systemd/guacd-maldex.conf" > "$tmp/guacd.service.d/maldex.conf"
      out="$(cd "$tmp" && SYSTEMD_UNIT_PATH="$tmp:" SYSTEMD_LOG_LEVEL=warning systemd-analyze verify --man=no guacd.service 2>&1)" \
        || out+=$'\n'"(systemd-analyze exited non-zero)"
      rm -rf "$tmp"
      [ -z "$out" ] || all+="$flavour: $out"$'\n'
    done
    OUT="$all"
    [ -z "$all" ]
  }
  check "systemd-analyze verify the guacd drop-in over the Ubuntu and EPEL units" guacd_dropin_verify
else
  skip "systemd-analyze verify (systemd-analyze is not installed)"
fi

unit_has() { grep -qxF -- "$1" "$UNIT" || { OUT="$UNIT lacks: $1"; return 1; }; }
check "maldex.service: User=maldex" unit_has "User=maldex"
check "maldex.service: SupplementaryGroups=kvm libvirt" unit_has "SupplementaryGroups=kvm libvirt"
check "maldex.service: ASPNETCORE_URLS=http://127.0.0.1:5080" unit_has "Environment=ASPNETCORE_URLS=http://127.0.0.1:5080"
check "maldex.service: ASPNETCORE_ENVIRONMENT=Production" unit_has "Environment=ASPNETCORE_ENVIRONMENT=Production"
check "maldex.service: PLAYWRIGHT_BROWSERS_PATH" unit_has "Environment=PLAYWRIGHT_BROWSERS_PATH=/var/lib/maldex/ms-playwright"
check "maldex.service: MALDEX_EXTRA_CONFIG" unit_has "Environment=MALDEX_EXTRA_CONFIG=$EXTRA_CONFIG"
check "maldex.service: ExecStart=/opt/maldex/app/Maldex.Web" unit_has "ExecStart=/opt/maldex/app/Maldex.Web"
check "maldex.service: WorkingDirectory=/var/lib/maldex" unit_has "WorkingDirectory=/var/lib/maldex"
check "maldex.service: Requires=maldex-host-guard.service" unit_has "Requires=maldex-host-guard.service"

config_group() { [ "$(grep -cE '^      group: maldex$' "$NFPM")" -ge 2 ] || { OUT="the /etc/maldex config entries lack file_info group: maldex"; return 1; }; }
check "nfpm.yaml: appsettings.Production.json is group maldex (both formats)" config_group
preinstall_wired() { grep -qxF '  preinstall: packaging/scripts/preinstall.sh' "$NFPM"; }
check "nfpm.yaml wires preinstall.sh (the users exist before unpacking)" preinstall_wired

guard_unit() { local u="$PKG/systemd/maldex-host-guard.service"; grep -qxF "ExecStart=/usr/sbin/nft -f /etc/maldex/maldex-host-guard.nft" "$u" && grep -qxF "Type=oneshot" "$u"; }
check "maldex-host-guard.service loads /etc/maldex/maldex-host-guard.nft" guard_unit
guacd_dropin() { local u="$PKG/systemd/guacd-maldex.conf"; grep -qxF "ExecStart=" "$u" && grep -qE '^ExecStart=/usr/sbin/guacd .*-b 127\.0\.0\.1 -l 4822' "$u"; }
check "guacd drop-in binds 127.0.0.1:4822" guacd_dropin

# ---- the admin config template ----
template_parses() {
  local f
  for f in "$PKG"/etc/appsettings.Production.json; do
    [ -f "$f" ] || { OUT="missing: $f"; return 1; }
    OUT="$(python3 -I -c 'import json,re,sys; s=open(sys.argv[1]).read(); json.loads(re.sub(r"(?m)^\s*//.*$","",s)); print("ok")' "$f" 2>&1)" || return 1
    grep -q '"Mode"' "$f" && { OUT="$f sets Maldex:Mode (the package sets Real; the template only comments on it)"; return 1; }
    grep -qi 'expects.*Real' "$f" || { OUT="$f lacks the comment that the package expects Maldex:Mode Real"; return 1; }
  done
}
check "etc/appsettings.Production.json parses (comments stripped) and leaves Mode to the package" template_parses

# The .rpm's copy of the template names RHEL 9's edk2-ovmf firmware (the app's default is Ubuntu's /usr/share/OVMF/*_4M*).
rpm_template() {
  local f="$PKG/stage/etc/appsettings.Production.rpm.json"
  grep -qF '"OvmfCodePath": "/usr/share/edk2/ovmf/OVMF_CODE.secboot.fd"' "$f" \
    && grep -qF '"OvmfVarsTemplatePath": "/usr/share/edk2/ovmf/OVMF_VARS.secboot.fd"' "$f" && ! grep -q '/usr/share/OVMF/' "$f" \
    || { OUT="$f does not name the edk2-ovmf paths"; return 1; }
}
deb_template() {
  local f="$PKG/etc/appsettings.Production.json"
  grep -qF '"OvmfCodePath": "/usr/share/OVMF/OVMF_CODE_4M.secboot.fd"' "$f" && grep -qF '"OvmfVarsTemplatePath": "/usr/share/OVMF/OVMF_VARS_4M.ms.fd"' "$f"
}
check "the deb template names Ubuntu's OVMF paths" deb_template
if [ -d "$PKG/stage" ]; then
  check "the staged rpm template names RHEL 9's edk2-ovmf paths" rpm_template
  staged_version() {
    local want got
    want="$(cat "$PKG/stage/VERSION" 2>/dev/null)" || { OUT="packaging/stage/VERSION is missing"; return 1; }
    got="$("$PKG/stage/opt/maldex/app/Maldex.Web" --version 2>&1)" || { OUT="Maldex.Web --version failed: $got"; return 1; }
    OUT="package version '$want', Maldex.Web --version '$got'"
    [ -n "$want" ] && [[ "$got" == "$want"* ]]
  }
  check "the staged Maldex.Web --version starts with the package version" staged_version
else
  skip "the staged rpm template and Maldex.Web --version (packaging/stage/ is absent; run with --stage)"
fi

# The install scripts: the version-dependent branches nfpm hands each format's arguments to.
scripts_args() {
  local p="$PKG/scripts"
  grep -q 'configure) \[ -z "${2:-}" \] || UPGRADE=1' "$p/postinstall.sh" || OUT+="postinstall: no deb upgrade branch"$'\n'
  grep -q '\[ "$1" -lt 2 \] || UPGRADE=1' "$p/postinstall.sh" || OUT+="postinstall: no rpm upgrade branch"$'\n'
  grep -q 'try-restart maldex.service' "$p/postinstall.sh" || OUT+="postinstall: no try-restart on upgrade"$'\n'
  grep -q 'Run: sudo maldex-setup' "$p/postinstall.sh" || OUT+="postinstall: no setup hint"$'\n'
  grep -qi 'visudo" -cf "$SUDOERS_SRC"' "$p/postinstall.sh" || OUT+="postinstall: no visudo -cf"$'\n'
  ! grep -Eq 'systemctl (start|enable --now|restart) maldex\.service' "$p/postinstall.sh" || OUT+="postinstall starts maldex"$'\n'
  ! grep -Eq 'curl|wget' "$p"/*.sh || OUT+="an install script downloads"$'\n'
  ! grep -v '^[[:space:]]*#' "$p/postremove.sh" "$p/preremove.sh" | grep -q '/var/lib/maldex' || OUT+="a remove script touches /var/lib/maldex"$'\n'
  grep -q 'purge) REMOVE=1; PURGE=1' "$p/postremove.sh" && grep -q 'rm -rf /etc/maldex' "$p/postremove.sh" || OUT+="postremove: no purge of /etc/maldex"$'\n'
  [ -z "$OUT" ]
}
check "install scripts: upgrade/remove branches, no start of maldex, no downloads, /var/lib/maldex untouched" scripts_args

echo
echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" = 0 ]
