# Maldex Windows 11 26H2 guest image

`./build.sh build-guest` builds `.data/vm/base/win11-26h2.qcow2` and `win11-26h2.vars.fd` (both read-only) from stock
media, fully unattended and offline.

## Inputs

| Variable / file | Purpose |
|---|---|
| `MALDEX_WIN_ISO` (required) | Windows 11 x64: Enterprise evaluation media, or multi-edition (consumer/business) media. Exit code 2 if unset or missing. |
| `MALDEX_WIN_PRODUCT_KEY` (optional) | Setup key for non-evaluation media. Default: Microsoft's published generic KMS client setup key for the selected edition (installs, does not activate). |
| `.data/vm/iso/virtio-win.iso` | Cached by `./build.sh real-setup`. |
| `MALDEX_OFFICE_ISO` (optional) | Office media; installed with `provision/office-config.xml` (adjust product and channel to your media). Macro security is set to "enable all" and Protected View off for `analyst`. |
| `MALDEX_WIN_IMAGE_INDEX` (optional) | Force `/IMAGE/INDEX` instead of selecting by name. |
| `.data/mitm/ca` | mitmproxy CA directory. Generated on first build; the guest trusts the same CA that mitmdump uses as `confdir`. |

## What happens

1. The agent is published (`.data/agent/Maldex.Agent.exe`), Sysmon is taken from the pinned `Sysmon.zip`, and the mitmproxy CA is exported as DER `maldex-ca.cer`.
2. `provision.iso` (label `MALDEXPROV`) is built with `Autounattend.xml`, `Provision.ps1`, `sysmon-config.xml`, `Sysmon64.exe`, `Maldex.Agent.exe`, `maldex-ca.cer`.
3. A `maldex-build` domain (OVMF Secure Boot, vTPM 2.0, no network) installs Windows. Setup finds `Autounattend.xml` on the provisioning CD; drivers come from the virtio CD (`D:` to `G:`).
4. `Provision.ps1` runs at first logon of `analyst`, installs the virtio tools and Sysmon, imports the CA, registers the agent (only if every critical step succeeded), clears the event logs and shuts down (the shutdown is in a `finally`, so a failure cannot stall the build).
5. The script boots the disk once more without installer media and waits up to 180 s for the agent's `"type":"ready"` frame on the `org.maldex.agent` channel, then shuts down cleanly and finalises the files. The whole install is capped at 3 h and reports progress every minute (VNC is on 127.0.0.1 for watching).

## Decisions worth knowing

- **Image selection:** the script loop-mounts the ISO read-only (`sudo`; Windows media are UDF, which xorriso cannot read) and lists `sources/install.wim` (or `.esd`) with `wiminfo` (apt `wimtools`, installed by `real-setup`). It picks, in order, `Windows 11 Enterprise Evaluation`, `Windows 11 Enterprise`, `Windows 11 Pro`, and fails (exit 2, listing the images) rather than fall back to Home. `MALDEX_WIN_IMAGE_INDEX` forces an index. Without `wiminfo` or `sudo` it asks Setup for the evaluation image by name.
- **Product key:** evaluation media need none. Enterprise, Pro and Education get Microsoft's generic KMS client setup key, so Setup does not stop at the key page; the guest stays unactivated, which is fine for a disposable analysis VM. Production with volume licensing can pass its own key or activate against its KMS.
- **No LabConfig bypass keys.** 26H2 still checks TPM 2.0 / Secure Boot / RAM; the OVMF + vTPM domain satisfies them. Offline OOBE uses `BypassNRO` plus a local `analyst` account from the unattend file.
- **The `analyst` password (`Maldex-Analyst-1!`) is a fixed, throwaway value, not a secret.** The guest is disposable, has no network during the build and no route to anything at run time.
- **The agent is not a service.** `Provision.ps1` registers scheduled task `MaldexAgent` (at logon of `analyst`, highest run level, interactive, no time limit, restart every minute up to 999 times, ignore new instances) so it runs in the interactive session; autologon makes it fire at boot. The agent logs to `C:\ProgramData\Maldex\agent.log` (5 MiB, one old file).
- **Defender real-time protection and SmartScreen are turned off** by policy keys and `Set-MpPreference` (best effort). Tamper Protection can refuse this on some builds; the provisioning log (`C:\ProgramData\Maldex\provision.log`) states the outcome and warns if Defender is still on.
- **Edge Startup Boost and background mode are off** (`HKLM:\SOFTWARE\Policies\Microsoft\Edge`, `StartupBoostEnabled=0`, `BackgroundModeEnabled=0`, best effort). Edge also resolves names through Windows (`BuiltInDnsClientEnabled=0`, `DnsOverHttpsMode=off`), so its lookups appear as Sysmon DNS events. Dropped files are collected only from the sample's process tree (children of the agent); a pre-running Edge would take a URL run over outside that tree. **The first real-VM run must confirm that a file downloaded during a URL run is collected as a dropped file.**
- **BitLocker device encryption is prevented** (`PreventDeviceEncryption`), because the vTPM state is not carried into cloned runs.
- **Sysmon.zip** is pinned by SHA-256 in `build.sh`. Sysinternals publishes no checksum file, so the hash was taken from a download; bump `SYSMON_URL` / `SYSMON_SHA256` together when updating.
- A failed critical provisioning step (virtio tools, Sysmon, CA import) still powers the VM off, but the agent task is then NOT registered, so the host-side boot check fails loudly (no `ready` frame). The failing step is named in `C:\ProgramData\Maldex\provision.log`.
- The install domain is created with `virt-install --import` (one persistent definition, `on_reboot=restart`, boot order Windows CD then disk), because virt-install's own install mode stops the domain at Setup's first reboot.
- On any failure the build VM (including its NVRAM and TPM state) and the intermediate files are removed. Set `MALDEX_KEEP_FAILED_BUILD=1` to keep them; the script then prints the `virsh vncdisplay` command.
- The event logs are cleared by `Provision.ps1`, but the verification boot afterwards writes new entries, so the finished image does NOT have pristine logs. This is deliberate and harmless; the per-run overlay is what matters.
- The build has not been run against real media yet (it needs `MALDEX_WIN_ISO`); only static validation was possible.

## Manual preparation (prepare-guest / finalize-guest)

Some changes cannot be made unattended, notably turning off Tamper Protection and Defender on Pro media. They are made by
hand on a copy of the base image:

1. `./build.sh prepare-guest` copies the base to `.data/vm/prepare/` and boots it as `maldex-prepare` with **no network**.
   VNC listens on this host's LAN address (`MALDEX_VNC_LISTEN` overrides it) with a random 8-character password; the
   address and password are printed. Plain VNC is unencrypted: keep it on a trusted LAN, and it only exists while
   `maldex-prepare` runs. Windows logs on automatically as `analyst`.
2. Make the changes in the Windows UI, then shut Windows down from the Start menu (a clean shutdown, not a reset).
3. `./build.sh finalize-guest` refuses unless the VM is shut off, boots the prepared disk once more with VNC on
   127.0.0.1 only, waits for the agent's `ready` frame and shuts it down cleanly. Only then does the copy replace the
   base (0444); the previous base stays as `*.prev` until the next finalize. A failed check leaves the base untouched.
4. `./build.sh discard-guest` throws the copy away instead.

`./build.sh build-guest` produces a fresh unattended base and loses these changes: prepare and finalize again after it.

Both `build-guest` and `finalize-guest` replace the base in place, so both refuse (exit 2) while any analysis run exists (a `maldex-<32 hex>` domain or run directory): every run's overlay reads the base as its backing file. They check before starting and again right before the swap.

## Running the E2E suite

`./build.sh e2e` detonates the **harmless** files in `deploy/guest/samples/` (EICAR, a registry-write script, the controlled URL `http://maldex-test.invalid/`) against the real stack: real libvirt VM, namespace networking, agent channel, capture and processing, in `Simulated` network mode through the real DI (`Maldex:Mode=Real`). It never runs malware.

1. Build the image once: `MALDEX_WIN_ISO=/path/to/Win11_26H2.iso ./build.sh build-guest` (and `./build.sh real-setup` for the host toolchain). If `.data/vm/base/win11-26h2.qcow2` or `.vars.fd` is missing, `./build.sh e2e` prints this and exits 2.
2. Run `./build.sh e2e`. It sets `MALDEX_REAL_VM=1` and `MALDEX_REAL_NET=1` and runs only `RealVmE2ETests` (needs Docker for the SQL container, and the passwordless sudo that real mode uses). Without those variables and the image the tests skip with a message.
3. The test output records the time from `Preparing` to agent Ready for every run (measured, not asserted), and each run is followed by a check that no `maldex-*` libvirt domain and no `mx*` link or namespace survived.
4. Calc-macro test: needs Office in the image (`MALDEX_OFFICE_ISO` at build time) and a macro-enabled document whose AutoOpen starts `calc.exe`, which is not stored in the repository. Opt in with `MALDEX_E2E_OFFICE=1 MALDEX_E2E_OFFICE_SAMPLE=/path/to/calc-macro.doc`; otherwise the test skips with a message.

The registry-write sample is a `.ps1`; the agent opens samples with ShellExecute, so `Provision.ps1` re-points the `.ps1` Open verb at PowerShell (Windows 11 otherwise opens it in Notepad).
