# Real-mode host pieces

Operator notes for the host-side components Maldex uses in `Maldex:Mode = Real`.

## Setup and checks

- `./build.sh real-setup` is idempotent. It installs, via apt: QEMU, libvirt client/daemon, virt-install, OVMF, swtpm,
  dnsmasq-base, nftables, tcpdump, Suricata, INetSim, wireguard-tools, xorriso, libxml2-utils, socat, genisoimage.
  It also installs PowerShell (snap, for script parse checks only), mitmproxy 11.1.3 under `/opt/mitmproxy`
  (symlink `/usr/local/bin/mitmdump`), pulls `guacamole/guacd:1.5.5`, downloads the virtio-win ISO, grants the
  current user access to `/dev/kvm`, disables the system `inetsim` and `suricata` services (Maldex runs them per run),
  creates `.data/vm/{base,run,iso}`, installs the boot-persistent host guard, and then verifies every tool.
- `./build.sh real-smoke` boots a throwaway Alpine domain on `qemu:///session` with OVMF and a libvirt-managed
  TPM 2.0, checks it stays running for 10 s, destroys it and checks nothing is left. No Windows media is needed.

## Host guard tables

`deploy/host/maldex-host-guard.nft` defines `table inet maldex_guard` (drops forwarded and input traffic on `mx*`
interfaces) and `table bridge maldex_guard` (run bridges `mxb*` carry only IPv4 and ARP). Load with
`sudo nft -f deploy/host/maldex-host-guard.nft`; the file deletes and recreates its tables, so reloading is safe.
`real-setup` makes it boot-persistent: the file is copied to `/etc/maldex/maldex-host-guard.nft` and the oneshot unit
`/etc/systemd/system/maldex-host-guard.service` (ordered before `network.target`, enabled at boot) loads it. Check with
`systemctl is-enabled maldex-host-guard` and `sudo nft list table inet maldex_guard`.

## Pinned downloads

The mitmproxy tarball, the Alpine smoke ISO and the virtio-win 0.1.302-1 ISO are pinned by SHA-256 in `build.sh`.
A mismatch deletes the file and fails.

## Files

- `deploy/net/inetsim/inetsim.conf`: INetSim services (dns, http, https, smtp, ftp), DNS default IP `198.18.0.10`.
- `deploy/net/suricata/suricata.yaml`, `dev.rules`: offline pcap processing, EVE JSON output to `eve.json`.
- `.data/vm/{base,run,iso}`: base images, per-run files, cached ISOs (git-ignored).
