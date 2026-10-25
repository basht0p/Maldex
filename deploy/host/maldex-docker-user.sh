#!/bin/sh
# Maldex: the narrow exception Direct egress (template edit sessions) needs in Docker's DOCKER-USER chain.
# Docker sets the FORWARD policy to DROP, and a chain in another table cannot override that. Exactly two rules, both tagged
# "maldex-direct", for the CURRENT default-route interface only (run at every start of docker.service and by ./build.sh real-setup):
#   -i mxu+ -o <default-iface> -j ACCEPT                                                (Maldex uplinks out)
#   -i <default-iface> -o mxu+ -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT      (replies back)
# What an uplink may actually forward is decided by the lease's own nft table (inet maldex_direct_<id>) before this chain runs.
# Nothing else in Docker's chains, tables or settings is touched. Rules are checked (-C), inserted (-I) and deleted (-D) by their exact
# arguments, never matched by substring; the interface of the previous run is remembered in a state file so its two rules can be
# removed exactly when the default route moved to another interface. If the state file is missing, stale maldex-direct rules for
# another interface are found by an exact-token parse of `iptables -S DOCKER-USER` (fields split, no eval, no substring match) and
# deleted with the same argument arrays.
# "stop" (the unit's ExecStop, so stop/disable revokes the exception) removes every such rule pair and the state file.
set -eu
IPT=iptables
STATE=${MALDEX_DOCKER_USER_STATE:-/run/maldex-docker-user.iface}
MODE=${1:-start}
case "$MODE" in start|stop) ;; *) echo "usage: maldex-docker-user.sh [start|stop]" >&2; exit 64 ;; esac

# The two approved rules for interface $2, applied with iptables action $1 (-C, -I or -D): one definition, so check, insert and
# delete always use the same exact arguments.
rule_out() { $IPT "$1" DOCKER-USER -i 'mxu+' -o "$2" -m comment --comment maldex-direct -j ACCEPT; }
rule_back() { $IPT "$1" DOCKER-USER -i "$2" -o 'mxu+' -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT; }

remove_for() {
  rule_out -C "$1" 2>/dev/null && rule_out -D "$1"
  rule_back -C "$1" 2>/dev/null && rule_back -D "$1"
  return 0
}

usable_iface() {
  case "$1" in
    ''|*[!A-Za-z0-9_.-]*|mx*|lo) return 1 ;;
  esac
  return 0
}

# Interfaces (other than $1) that carry a maldex-direct rule, read from `iptables -S DOCKER-USER`. A line counts only if its fields
# are exactly one of the two approved rules (12 or 16 fields, each compared whole), never by substring.
stale_ifaces() {
  keep=${1:-}
  $IPT -S DOCKER-USER 2>/dev/null | while IFS= read -r line; do
    set -f
    # shellcheck disable=SC2086
    set -- $line
    set +f
    found=
    if [ $# -eq 12 ] && [ "$1" = -A ] && [ "$2" = DOCKER-USER ] && [ "$3" = -i ] && [ "$4" = 'mxu+' ] && [ "$5" = -o ] \
       && [ "$7" = -m ] && [ "$8" = comment ] && [ "$9" = --comment ] && [ "${10}" = maldex-direct ] \
       && [ "${11}" = -j ] && [ "${12}" = ACCEPT ]; then
      found=$6
    elif [ $# -eq 16 ] && [ "$1" = -A ] && [ "$2" = DOCKER-USER ] && [ "$3" = -i ] && [ "$5" = -o ] && [ "$6" = 'mxu+' ] \
       && [ "$7" = -m ] && [ "$8" = conntrack ] && [ "$9" = --ctstate ] && [ "${10}" = RELATED,ESTABLISHED ] \
       && [ "${11}" = -m ] && [ "${12}" = comment ] && [ "${13}" = --comment ] && [ "${14}" = maldex-direct ] \
       && [ "${15}" = -j ] && [ "${16}" = ACCEPT ]; then
      found=$4
    fi
    if usable_iface "$found" && [ "$found" != "$keep" ]; then printf '%s\n' "$found"; fi
  done | sort -u
}

remove_stale() {
  stale_ifaces "${1:-}" | while IFS= read -r i; do remove_for "$i"; done
}

if ! $IPT -n -L DOCKER-USER >/dev/null 2>&1; then
  if [ "$MODE" = stop ]; then rm -f "$STATE"; exit 0; fi   # Docker is gone, and so is its chain with our rules
  echo "maldex-docker-user: no DOCKER-USER chain (is docker running?)" >&2; exit 1
fi

if [ "$MODE" = stop ]; then
  if [ -r "$STATE" ]; then
    OLD=$(cat "$STATE")
    if usable_iface "$OLD"; then remove_for "$OLD"; fi
  fi
  remove_stale ""
  rm -f "$STATE"
  exit 0
fi

IFACE=$(ip -4 -o route show default | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')
usable_iface "$IFACE" || { echo "maldex-docker-user: no usable default-route interface ('$IFACE')" >&2; exit 1; }

# The previous run's interface (if it differs from the current one): remove its two rules. Without a state file, look for stale ones.
if [ -r "$STATE" ]; then
  OLD=$(cat "$STATE")
  if usable_iface "$OLD" && [ "$OLD" != "$IFACE" ]; then remove_for "$OLD"; fi
else
  remove_stale "$IFACE"
fi

rule_out -C "$IFACE" 2>/dev/null || rule_out -I "$IFACE"
rule_back -C "$IFACE" 2>/dev/null || rule_back -I "$IFACE"
printf '%s\n' "$IFACE" > "$STATE"
