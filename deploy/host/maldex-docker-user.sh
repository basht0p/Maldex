#!/bin/sh
# Maldex: the narrow exception Direct egress (template edit sessions) needs in Docker's DOCKER-USER chain.
# Docker sets the FORWARD policy to DROP, and a chain in another table cannot override that. Exactly two rules, both tagged
# "maldex-direct", for the CURRENT default-route interface only (run at every start of docker.service and by ./build.sh real-setup):
#   -i mxu+ -o <default-iface> -j ACCEPT                                                (Maldex uplinks out)
#   -i <default-iface> -o mxu+ -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT      (replies back)
# What an uplink may actually forward is decided by the lease's own nft table (inet maldex_direct_<id>) before this chain runs.
# Nothing else in Docker's chains, tables or settings is touched. Rules are checked (-C), inserted (-I) and deleted (-D) by their exact
# arguments, never matched by substring; the interface of the previous run is remembered in a state file so its two rules can be
# removed exactly when the default route moved to another interface.
set -eu
IPT=iptables
STATE=${MALDEX_DOCKER_USER_STATE:-/run/maldex-docker-user.iface}
$IPT -n -L DOCKER-USER >/dev/null 2>&1 || { echo "maldex-docker-user: no DOCKER-USER chain (is docker running?)" >&2; exit 1; }
IFACE=$(ip -4 -o route show default | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')
case "$IFACE" in
  ''|*[!A-Za-z0-9_.-]*|mx*|lo) echo "maldex-docker-user: no usable default-route interface ('$IFACE')" >&2; exit 1 ;;
esac


# The two approved rules for interface $2, applied with iptables action $1 (-C, -I or -D): one definition, so check, insert and
# delete always use the same exact arguments.
rule_out() { $IPT "$1" DOCKER-USER -i 'mxu+' -o "$2" -m comment --comment maldex-direct -j ACCEPT; }
rule_back() { $IPT "$1" DOCKER-USER -i "$2" -o 'mxu+' -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT; }

remove_for() {
  rule_out -C "$1" 2>/dev/null && rule_out -D "$1"
  rule_back -C "$1" 2>/dev/null && rule_back -D "$1"
  return 0
}

# The previous run's interface (if it differs from the current one): remove its two rules.
if [ -r "$STATE" ]; then
  OLD=$(cat "$STATE")
  case "$OLD" in
    ''|*[!A-Za-z0-9_.-]*|mx*|lo) ;;
    "$IFACE") ;;
    *) remove_for "$OLD" ;;
  esac
fi

rule_out -C "$IFACE" 2>/dev/null || rule_out -I "$IFACE"
rule_back -C "$IFACE" 2>/dev/null || rule_back -I "$IFACE"
printf '%s\n' "$IFACE" > "$STATE"
