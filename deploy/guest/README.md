# Maldex Windows 11 26H2 guest image

`./build.sh build-guest` builds `.data/vm/base/win11-26h2.qcow2` and `win11-26h2.vars.fd` (both read-only) from stock
media, fully unattended and offline.

## Inputs

| Variable / file | Purpose |
|---|---|
| `MALDEX_WIN_ISO` (required) | Windows 11 x64 Enterprise (evaluation media is fine). Exit code 2 if unset or missing. |
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

- **Image selection:** by name `Windows 11 Enterprise Evaluation`. If `wiminfo` (apt `wimtools`) is installed, the script inspects the ISO and falls back to `/IMAGE/INDEX 1` when no image has that name; without `wiminfo` it uses the name, and `MALDEX_WIN_IMAGE_INDEX=1` is the manual override. The `wiminfo` check first extracts `sources/install.wim` (or `install.esd`) from the ISO into a scratch directory: expect about 5 GB of temporary disk and a minute or two of I/O.
- **No LabConfig bypass keys.** 26H2 still checks TPM 2.0 / Secure Boot / RAM; the OVMF + vTPM domain satisfies them. Offline OOBE uses `BypassNRO` plus a local `analyst` account from the unattend file.
- **The `analyst` password (`Maldex-Analyst-1!`) is a fixed, throwaway value, not a secret.** The guest is disposable, has no network during the build and no route to anything at run time.
- **The agent is not a service.** `Provision.ps1` registers scheduled task `MaldexAgent` (at logon of `analyst`, highest run level, interactive, no time limit, restart every minute up to 999 times, ignore new instances) so it runs in the interactive session; autologon makes it fire at boot. The agent logs to `C:\ProgramData\Maldex\agent.log` (5 MiB, one old file).
- **Defender real-time protection and SmartScreen are turned off** by policy keys and `Set-MpPreference` (best effort). Tamper Protection can refuse this on some builds; the provisioning log (`C:\ProgramData\Maldex\provision.log`) states the outcome and warns if Defender is still on.
- **Edge Startup Boost and background mode are off** (`HKLM:\SOFTWARE\Policies\Microsoft\Edge`, `StartupBoostEnabled=0`, `BackgroundModeEnabled=0`, best effort). Dropped files are collected only from the sample's process tree (children of the agent); a pre-running Edge would take a URL run over outside that tree. **The first real-VM run must confirm that a file downloaded during a URL run is collected as a dropped file.**
- **BitLocker device encryption is prevented** (`PreventDeviceEncryption`), because the vTPM state is not carried into cloned runs.
- **Sysmon.zip** is pinned by SHA-256 in `build.sh`. Sysinternals publishes no checksum file, so the hash was taken from a download; bump `SYSMON_URL` / `SYSMON_SHA256` together when updating.
- A failed critical provisioning step (virtio tools, Sysmon, CA import) still powers the VM off, but the agent task is then NOT registered, so the host-side boot check fails loudly (no `ready` frame). The failing step is named in `C:\ProgramData\Maldex\provision.log`.
- The install domain is created with `virt-install --import` (one persistent definition, `on_reboot=restart`, boot order Windows CD then disk), because virt-install's own install mode stops the domain at Setup's first reboot.
- On any failure the build VM (including its NVRAM and TPM state) and the intermediate files are removed. Set `MALDEX_KEEP_FAILED_BUILD=1` to keep them; the script then prints the `virsh vncdisplay` command.
- The event logs are cleared by `Provision.ps1`, but the verification boot afterwards writes new entries, so the finished image does NOT have pristine logs. This is deliberate and harmless; the per-run overlay is what matters.
- The build has not been run against real media yet (it needs `MALDEX_WIN_ISO`); only static validation was possible.

## Running the E2E suite

`./build.sh e2e` detonates the **harmless** files in `deploy/guest/samples/` (EICAR, a registry-write script, the controlled URL `http://maldex-test.invalid/`) against the real stack: real libvirt VM, namespace networking, agent channel, capture and processing, in `Simulated` network mode through the real DI (`Maldex:Mode=Real`). It never runs malware.

1. Build the image once: `MALDEX_WIN_ISO=/path/to/Win11_26H2.iso ./build.sh build-guest` (and `./build.sh real-setup` for the host toolchain). If `.data/vm/base/win11-26h2.qcow2` or `.vars.fd` is missing, `./build.sh e2e` prints this and exits 2.
2. Run `./build.sh e2e`. It sets `MALDEX_REAL_VM=1` and `MALDEX_REAL_NET=1` and runs only `RealVmE2ETests` (needs Docker for the SQL container, and the passwordless sudo that real mode uses). Without those variables and the image the tests skip with a message.
3. The test output records the time from `Preparing` to agent Ready for every run (measured, not asserted), and each run is followed by a check that no `maldex-*` libvirt domain and no `mx*` link or namespace survived.
4. Calc-macro test: needs Office in the image (`MALDEX_OFFICE_ISO` at build time) and a macro-enabled document whose AutoOpen starts `calc.exe`, which is not stored in the repository. Opt in with `MALDEX_E2E_OFFICE=1 MALDEX_E2E_OFFICE_SAMPLE=/path/to/calc-macro.doc`; otherwise the test skips with a message.

The registry-write sample is a `.ps1`; the agent opens samples with ShellExecute, so `Provision.ps1` re-points the `.ps1` Open verb at PowerShell (Windows 11 otherwise opens it in Notepad).
