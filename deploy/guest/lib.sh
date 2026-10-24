# Shared by build-guest.sh and prepare-guest.sh (sourced).

# Refuses (exit 2) while any analysis run exists: a libvirt domain maldex-<32 hex> or a leftover run directory of that
# name. Every run's overlay reads the base image as its backing file, so replacing the base under a run corrupts it.
# Usage: refuse_if_runs_active <libvirt uri> <run dir>
refuse_if_runs_active() {
  local domains dirs
  domains="$(virsh -c "$1" list --all --name 2>/dev/null | grep -E '^maldex-[0-9a-f]{32}$' || true)"
  dirs="$(find "$2" -mindepth 1 -maxdepth 1 -type d -regextype posix-extended -regex '.*/maldex-[0-9a-f]{32}' -printf '%f\n' 2>/dev/null || true)"
  if [ -n "$domains$dirs" ]; then
    echo "Refusing to replace the base image while analysis runs exist (their overlays read it):" >&2
    [ -n "$domains" ] && printf '  domain   %s\n' $domains >&2
    [ -n "$dirs" ] && printf '  run dir  %s/%s\n' "$2" $dirs >&2
    echo "Let the runs finish (or stop Maldex and let it reconcile), then try again." >&2
    exit 2
  fi
}
