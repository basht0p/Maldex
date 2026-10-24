#!/bin/sh
# Maldex: the narrow exception Direct egress (template edit sessions) needs in Docker's DOCKER-USER chain.
# Docker sets the FORWARD policy to DROP, and a chain in another table cannot override that. Exactly two rules, both tagged
# "maldex-direct", for the CURRENT default-route interface only (run at every start of docker.service and by ./build.sh real-setup):
#   -i mxu+ -o <default-iface> -j ACCEPT                                                (Maldex uplinks out)
#   -i <default-iface> -o mxu+ -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT      (replies back)
# What an uplink may actually forward is decided by the lease's own nft table (inet maldex_direct_<id>) before this chain runs.
# Nothing else in Docker's chains, tables or settings is touched; tagged rules for another interface are removed.
set -eu
IPT=iptables
$IPT -n -L DOCKER-USER >/dev/null 2>&1 || { echo "maldex-docker-user: no DOCKER-USER chain (is docker running?)" >&2; exit 1; }
IFACE=$(ip -4 -o route show default | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')
case "$IFACE" in
  ''|*[!A-Za-z0-9_.-]*|mx*|lo) echo "maldex-docker-user: no usable default-route interface ('$IFACE')" >&2; exit 1 ;;
esac

# stale tagged rules (another interface)
$IPT -S DOCKER-USER | grep -- '--comment maldex-direct' | while read -r _ _ spec; do
  case " $spec " in
    *" -o $IFACE "*|*" -i $IFACE "*) ;;
    *) eval "$IPT -D DOCKER-USER $spec" ;;
  esac
done

ensure() {
  $IPT -C DOCKER-USER "$@" 2>/dev/null || $IPT -I DOCKER-USER "$@"
}
ensure -i 'mxu+' -o "$IFACE" -m comment --comment maldex-direct -j ACCEPT
ensure -i "$IFACE" -o 'mxu+' -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT
