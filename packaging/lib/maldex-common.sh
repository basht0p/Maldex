# shellcheck shell=bash disable=SC2034  # the pins and REAL_FAILED are used by the sourcing scripts
# Shared host-setup helpers, sourced by build.sh (dev box, real-setup / real_verify) and packaging/bin/maldex-setup (packaged
# install). Defines pins and functions only; running nothing at source time (it must load even with an empty PATH).
#
# Privileged commands go through $MALDEX_SUDO: "sudo" for a non-root caller (the dev box), empty as root. A caller may set it
# before sourcing (maldex-setup uses "sudo -n" for a non-root --check). Every function returns non-zero on failure explicitly,
# so it also fails correctly where `set -e` does not apply (an `if` condition).

if [ -z "${MALDEX_SUDO+x}" ]; then
  if [ "$EUID" -eq 0 ]; then MALDEX_SUDO=""; else MALDEX_SUDO="sudo"; fi
fi

# ---- Pins ----
MITMPROXY_VERSION="11.1.3"
MITMPROXY_SHA256="f3f9aa0f719cfda9dac943891b6f3475bb869aa7a6115ee9e17c7e1c88bbc854"
VIRTIO_WIN_VERSION="0.1.302-1"
VIRTIO_WIN_URL="https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-$VIRTIO_WIN_VERSION/virtio-win.iso"
VIRTIO_WIN_SHA256="303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d"
# Sysmon.zip is pinned by the SHA-256 of a download on 2026-10-20 (Sysinternals publishes no checksum file).
SYSMON_URL="https://download.sysinternals.com/files/Sysmon.zip"
SYSMON_SHA256="00ecf1b46aec99299d3ae0bca79dc621458bd014b20b509d7c5c8e8c8611aa54"

# INetSim's template data (the distro package's location on Ubuntu and RHEL).
INETSIM_TEMPLATE_DIR="${INETSIM_TEMPLATE_DIR:-/var/lib/inetsim}"

# Verify file $1 against SHA-256 $2; on mismatch delete it and fail loudly.
verify_sha256() {
  local file="$1" want="$2"
  if ! echo "$want  $file" | sha256sum -c --status -; then
    echo "SHA-256 mismatch for $file (expected $want, got $(sha256sum "$file" | cut -d' ' -f1)); deleting." >&2
    rm -f "$file"
    return 1
  fi
}

# Download $1 to $2 unless it already exists (atomic via .part), then verify it against SHA-256 $3.
fetch_cached() {
  local url="$1" dest="$2" sha="$3"
  if [ ! -s "$dest" ]; then
    echo "Downloading $url"
    curl -fsSL --retry 3 -o "$dest.part" "$url" || return 1
    mv "$dest.part" "$dest" || return 1
  fi
  verify_sha256 "$dest" "$sha"
}

# Run "$@" quietly; on failure print the hint and record the failure.
REAL_FAILED=0
real_check() {
  local what="$1" hint="$2"; shift 2
  if "$@" >/dev/null 2>&1; then
    echo "ok   $what"
  else
    echo "FAIL $what -- install/fix: $hint" >&2
    REAL_FAILED=1
  fi
}

# Run "$@" as user $1: directly when that is us, through runuser as root, otherwise through $MALDEX_SUDO (so "sudo -n" never
# prompts).
maldex_as_user() {
  local user="$1"; shift
  if [ "$(id -un)" = "$user" ]; then
    "$@"
  elif [ -z "$MALDEX_SUDO" ]; then
    runuser -u "$user" -- "$@"
  else
    $MALDEX_SUDO -u "$user" -- "$@"
  fi
}

# A dedicated, powerless system account: own group, no supplementary groups, no home, no shell, no sudoers.
# maldex-mitm (per-run mitmproxy), maldex-dns (per-run dnsmasq, no capability at all), and maldex-isroot / maldex-isuser (root and
# 'inetsim' of the user namespace INetSim runs in; they own nothing on the host).
ensure_system_user() {
  local name="$1"
  if ! id "$name" >/dev/null 2>&1; then
    $MALDEX_SUDO useradd --system --user-group --no-create-home --shell /usr/sbin/nologin "$name" || return 1
  fi
}

# mitmproxy's CA (the guest image trusts it) in confdir $1, generated once as user $2; the app copies it per run into a private
# confdir for maldex-mitm. When $2 is the caller, it is generated in place (the dev box). Otherwise mitmdump runs as $2 in a fresh
# temp dir, the files are moved into $1, and $1 is left 0700 $2:$2 (maldex-setup then grants the app's group read on the cert).
ensure_mitm_ca() {
  local confdir="$1" owner="$2" f
  if [ "$owner" = "$(id -un)" ]; then
    mkdir -p "$confdir" || return 1
    chmod 0700 "$confdir" || return 1
    if [ ! -f "$confdir/mitmproxy-ca.pem" ]; then
      # mitmdump writes the CA at startup and then idles; stop it after a few seconds.
      timeout 8 mitmdump --set "confdir=$confdir" -n -q >/dev/null 2>&1 || true
    fi
    [ -f "$confdir/mitmproxy-ca.pem" ] && [ -f "$confdir/mitmproxy-ca-cert.pem" ]
    return
  fi
  if $MALDEX_SUDO test -L "$confdir"; then echo "refusing to use $confdir: it is a symlink" >&2; return 1; fi
  if ! { $MALDEX_SUDO test -f "$confdir/mitmproxy-ca.pem" && $MALDEX_SUDO test -f "$confdir/mitmproxy-ca-cert.pem"; }; then
    local tmp
    tmp="$($MALDEX_SUDO mktemp -d)" || return 1
    $MALDEX_SUDO chown "$owner:$owner" "$tmp" || { $MALDEX_SUDO rm -rf "$tmp"; return 1; }
    maldex_as_user "$owner" timeout 8 mitmdump --set "confdir=$tmp" -n -q >/dev/null 2>&1 || true
    if ! { $MALDEX_SUDO test -f "$tmp/mitmproxy-ca.pem" && $MALDEX_SUDO test -f "$tmp/mitmproxy-ca-cert.pem"; }; then
      echo "mitmdump did not create a CA in $tmp" >&2
      $MALDEX_SUDO rm -rf "$tmp"
      return 1
    fi
    $MALDEX_SUDO mkdir -p "$confdir" || { $MALDEX_SUDO rm -rf "$tmp"; return 1; }
    for f in "$tmp"/mitmproxy-*; do
      $MALDEX_SUDO cp -p "$f" "$confdir/" || { $MALDEX_SUDO rm -rf "$tmp"; return 1; }
    done
    $MALDEX_SUDO rm -rf "$tmp"
  fi
  $MALDEX_SUDO chown -hR "$owner:$owner" "$confdir" || return 1
  $MALDEX_SUDO chmod 0700 "$confdir" || return 1
}

# The installed libguestfs version: dpkg-query on a .deb system, rpm on an .rpm one.
maldex_guestfs_version() {
  if command -v dpkg-query >/dev/null 2>&1 && dpkg-query -W -f='${Version}' libguestfs-tools 2>/dev/null; then
    return 0
  fi
  rpm -q --qf '%{VERSION}-%{RELEASE}' libguestfs 2>/dev/null
}

# libguestfs's fixed appliance (LIBGUESTFS_PATH) in $1: the guest disk editor writes the current agent into a template working copy
# with guestfish as the app's own user. Ubuntu's 0600 /boot/vmlinuz-* stops supermin from building an appliance for a non-root
# user, so one is built once as root into a world-readable directory. The stamp records the libguestfs version it was built from.
ensure_guestfs_appliance() {
  local dir="$1" want
  want="$(maldex_guestfs_version)" || { echo "cannot read the installed libguestfs version" >&2; return 1; }
  [ -n "$want" ] || { echo "cannot read the installed libguestfs version" >&2; return 1; }
  if [ -f "$dir/maldex-libguestfs-version" ] && [ "$(cat "$dir/maldex-libguestfs-version")" = "$want" ] \
     && [ -f "$dir/kernel" ] && [ -f "$dir/initrd" ] && [ -f "$dir/root" ]; then
    return 0
  fi
  local fresh="$dir.new"
  $MALDEX_SUDO rm -rf "$fresh" || return 1
  $MALDEX_SUDO mkdir -p "$(dirname "$dir")" || return 1
  $MALDEX_SUDO libguestfs-make-fixed-appliance "$fresh" || return 1
  echo "$want" | $MALDEX_SUDO tee "$fresh/maldex-libguestfs-version" >/dev/null || return 1
  $MALDEX_SUDO chown -R root:root "$fresh" || return 1
  $MALDEX_SUDO chmod -R a+rX,go-w "$fresh" || return 1
  $MALDEX_SUDO rm -rf "$dir" || return 1
  $MALDEX_SUDO mv "$fresh" "$dir"
}

# INetSim's template data is only ever read (as root) and copied per run, so it must be root-owned, not writable by others
# and free of symlinks; the guest-facing INetSim user gets only fresh per-run leaf directories.
harden_inetsim_template() {
  $MALDEX_SUDO chown -R root:root "$INETSIM_TEMPLATE_DIR" || return 1
  $MALDEX_SUDO chmod -R go-w "$INETSIM_TEMPLATE_DIR" || return 1
  $MALDEX_SUDO chmod 0755 "$INETSIM_TEMPLATE_DIR"
}

inetsim_template_clean() {
  local found
  found="$($MALDEX_SUDO find "$INETSIM_TEMPLATE_DIR" \( -type l -o ! -user root -o -perm /022 \) -print -quit)" || return 1
  [ -z "$found" ]
}
