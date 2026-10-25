# Maldex Windows 11 guest images

Guest images are built and changed in the app, under **Admin → Sandbox Templates**. The guest build scripts and their shared library are gone, and so are the four `*-guest` targets of `build.sh` (build,
prepare, finalize and discard): each now prints `retired: use Admin → Sandbox Templates → New template (build from
media) or Edit` and exits 2. This directory keeps the files the Build uses as its inputs: `autounattend/Autounattend.xml`,
`provision/` (`Provision.ps1`, `office-config.xml`, `sysmon-config.xml`) and `samples/` (the harmless E2E samples).

## Build a template from media

1. **Host setup once:** `./build.sh real-setup` installs the toolchain (including `wimtools` for `wiminfo`, `xorriso` and
   libguestfs), caches the virtio drivers ISO (`.data/vm/iso/virtio-win.iso`) and the pinned `Sysmon.zip`, and creates the
   media library (`.data/media`). `./build.sh agent` publishes the guest agent (`.data/agent/Maldex.Agent.exe`).
2. **Media:** under **Admin → Sandbox Templates → Media**, upload or fetch a Windows 11 x64 ISO (Enterprise evaluation, or
   multi-edition consumer/business media). The app loop-mounts it read-only, lists the editions with `wiminfo` and picks the
   Enterprise Evaluation, Enterprise or Pro image; it never falls back to Home. Optional Office media is added the same way.
3. **Build:** choose **New template** (or build a new version of an existing one), pick the media and edition. The job
   installs Windows unattended and offline (OVMF Secure Boot, vTPM 2.0, no network), then provisions it with `Provision.ps1`
   (see below). The job page shows the phases (Installing, Provisioning, Configuring) and the build's progress.
4. **Configure:** at **Configuring** the job page opens a browser console on the guest. Make the changes that cannot be
   made unattended, notably turning off Tamper Protection and then Windows Defender real-time protection on Pro media.
5. **Seal:** press **Seal**. The guest shuts down cleanly, the copy is booted once more and verified (the agent build and
   Defender real-time protection off); only then is it published as a new version. A Seal with Defender still on is refused
   and the job stays at Configuring.

## What `Provision.ps1` does

It runs at first logon of the `analyst` account, from the provisioning CD (`MALDEXPROV`, built by the Build from
`Autounattend.xml`, `Provision.ps1`, `sysmon-config.xml`, `Sysmon64.exe`, the agent and the mitmproxy CA `maldex-ca.cer`):
installs the virtio tools, registers and starts the agent task (early, and even if a later step fails, so the host can
always read the result), installs Sysmon, sets up autologon, imports the CA, applies the policy settings listed below and
optionally installs Office. It ends by writing `C:\ProgramData\Maldex\provision-result.json`
(`{"ok":true|false,"failedSteps":[…]}`). It does **not** clear the event logs or shut down: the host reads the result through
the agent, collects the log, powers the guest off, boots it once more without media, checks the agent's build, and only then
opens Configuring. The event logs are cleared later, when the version is sealed.

**The provisioning log** is `C:\ProgramData\Maldex\provision.log` in the guest; the Build copies it out and shows it on the
job page (**Provisioning log**). A failed critical step (virtio tools, agent, Sysmon, autologon, CA import, Office when
selected) is named there and in the result file, and the Build fails with that list. The log also records whether Defender
real-time protection is on; the Build then waits at Configuring with guidance instead of sealing.

## Decisions worth knowing

- **Image selection:** the Build loop-mounts the ISO read-only (`sudo`; Windows media are UDF, which xorriso cannot read) and lists `sources/install.wim` (or `.esd`) with `wiminfo` (apt `wimtools`, installed by `real-setup`). It picks, in order, `Windows 11 Enterprise Evaluation`, `Windows 11 Enterprise`, `Windows 11 Pro`, and never falls back to Home. To build another supported edition (for example Education), choose its index in the **New
template** dialog; unsupported editions are listed with the reason.
- **Product key:** evaluation media need none. Enterprise, Pro and Education get Microsoft's generic KMS client setup key, so Setup does not stop at the key page; the guest stays unactivated, which is fine for a disposable analysis VM. Production with volume licensing can pass its own key or activate against its KMS.
- **No LabConfig bypass keys.** 26H2 still checks TPM 2.0 / Secure Boot / RAM; the OVMF + vTPM domain satisfies them. Offline OOBE uses `BypassNRO` plus a local `analyst` account from the unattend file.
- **The `analyst` password (`Maldex-Analyst-1!`) is a fixed, throwaway value, not a secret.** The guest is disposable, has no network during the build and no route to anything at run time.
- **The agent is not a service.** `Provision.ps1` registers scheduled task `MaldexAgent` (at logon of `analyst`, highest run level, interactive, no time limit, restart every minute up to 999 times, ignore new instances) so it runs in the interactive session; autologon makes it fire at boot. The agent logs to `C:\ProgramData\Maldex\agent.log` (5 MiB, one old file).
- **Defender:** `Provision.ps1` sets the Defender and SmartScreen policy keys and `Set-MpPreference` (best effort), but on Pro
  media Tamper Protection keeps real-time protection on. Turning it off is the admin's step at **Configuring** (Tamper
  Protection first, then Real-time protection). Seal and verification read the state over a minute (it briefly reads "off"
  right after boot) and refuse while it is on or not yet settled.
- **Edge Startup Boost and background mode are off** (`HKLM:\SOFTWARE\Policies\Microsoft\Edge`, `StartupBoostEnabled=0`, `BackgroundModeEnabled=0`, best effort). Edge also resolves names through Windows (`BuiltInDnsClientEnabled=0`, `DnsOverHttpsMode=off`), so its lookups appear as Sysmon DNS events. Dropped files are collected only from the sample's process tree (children of the agent); a pre-running Edge would take a URL run over outside that tree. **The first real-VM run must confirm that a file downloaded during a URL run is collected as a dropped file.**
- **BitLocker device encryption is prevented** (`PreventDeviceEncryption`), because the vTPM state is not carried into cloned runs.
- **Sysmon.zip** is pinned by SHA-256 in `build.sh`. Sysinternals publishes no checksum file, so the hash was taken from a download; bump `SYSMON_URL` / `SYSMON_SHA256` together when updating.
- A failed critical provisioning step fails the Build with the step names from `provision-result.json`; the blank disk is
  deleted and only the provisioning log is kept.

## Changing an existing template: Edit and Refresh in the app

Once an image is a template (the first start imports it as v1), change it in the app: under
**Admin → Sandbox Templates**, **Edit** boots a writable copy of the current version with a browser console (network
`None`, or `Direct` for internet-only updates) and **Seal** turns it into a new version; **Refresh components** re-applies
the current agent, Sysmon configuration and interception CA without a console. Both verify the sealed copy (agent build,
Defender real-time protection off) before publishing it as a new version, and **Roll back** returns to the previous one.
No disk is ever replaced in place: every version is a new file `base/<version id>.qcow2`, so the hand-prepared base stays
as it is.

Before the first boot, each Edit and Refresh job writes the current agent into its own working copy offline with
libguestfs (guestfish), so even an image with an older agent can be updated. This needs `libguestfs-tools` and the fixed
appliance at `/var/lib/maldex/guestfs-appliance`, which root builds; `./build.sh real-setup` installs and builds both. The
agent comes from `.data/agent/Maldex.Agent.exe` and its `Maldex.Agent.version`: run `./build.sh agent` first.


## Running the E2E suite

`./build.sh e2e` detonates the **harmless** files in `deploy/guest/samples/` (EICAR, a registry-write script, the controlled URL `http://maldex-test.invalid/`) against the real stack: real libvirt VM, namespace networking, agent channel, capture and processing, in `Simulated` network mode through the real DI (`Maldex:Mode=Real`). It never runs malware.

1. Have the default template's image `win11-26h2` (build it in the app as above, or import an existing image listed in `Maldex:Images`) and run `./build.sh real-setup` for the host toolchain. Without `.data/vm/base/win11-26h2.qcow2` and `.vars.fd` the tests skip with a message naming the default template.
2. Run `./build.sh e2e`. It sets `MALDEX_REAL_VM=1` and `MALDEX_REAL_NET=1` and runs only `RealVmE2ETests` (needs Docker for the SQL container, and the passwordless sudo that real mode uses). Without those variables and the image the tests skip with a message.
3. The test output records the time from `Preparing` to agent Ready for every run (measured, not asserted), and each run is followed by a check that no `maldex-*` libvirt domain and no `mx*` link or namespace survived.
4. Calc-macro test: needs Office in the template (add Office media to the library and select it in **New template**, which
   installs it during provisioning) and a macro-enabled document whose AutoOpen starts `calc.exe`, which is not stored in the repository. Opt in with `MALDEX_E2E_OFFICE=1 MALDEX_E2E_OFFICE_SAMPLE=/path/to/calc-macro.doc`; otherwise the test skips with a message.

5. Template jobs (`RealTemplateJobTests`, and `RealGuestDiskEditorTests` for the libguestfs disk editor): a Refresh of
   `win11-26h2` that must publish v2 and then pass the three scenarios above on v2; an Edit with `Direct` network whose guest
   self-test must reach a public address and not the LAN gateway, the host or the control plane, then a discard; and a Refresh
   followed by a Roll back (v1 current, v2 retired). Each test deletes the disks its jobs created and checks that
   `.data/vm/base` holds exactly the files it held before; nothing touches `win11-26h2.*`. They take up to about two hours in
   all, so they are **not** part of `./build.sh e2e`: they also require `MALDEX_REAL_TEMPLATES=1` and skip with a pointer to
   `./build.sh e2e-templates` without it. They also need the base image and the real-setup toolchain (the libguestfs appliance).

6. Build from ISO (`RealTemplateBuildTests`, part of `./build.sh e2e-templates`, which needs `MALDEX_WIN_ISO` and refuses
   without it): the ISO is fetched into a media library of the test's own from a loopback server (the real fetcher, with
   private destinations allowed for that host only, and the real read-only loop-mount inspection, which must find the Pro
   image and pick its index), then a Build with publish off installs and provisions Windows unattended (about 15-25 min here;
   the test logs the time per phase). At Configuring the post-reboot agent check has passed (the bundle's agent), the
   provisioning log has no failed step, and a Seal with Defender real-time protection on is refused (the job stays
   Configuring, `sealRequested` false). The cancel must leave no version, VM, link, blank disk, work directory or held slot;
   the test deletes the fetched copy, the provisioning log and the empty template, and checks the base image and the ISO
   unchanged. It never modifies `MALDEX_WIN_ISO`.

7. Interactive Build acceptance (`RealTemplateBuildAcceptanceTests`, `MALDEX_WIN_ISO=/path/to/win11.iso ./build.sh
   e2e-build-acceptance`; also gated by `MALDEX_E2E_INTERACTIVE=1`, never part of `e2e-templates`): it builds the SPA, starts
   guacd and serves the app on `localhost:5080` only (DevAuth), builds a new version of `win11-26h2` with publish on and
   prints the job page (`http://localhost:5080/admin/template-jobs/<id>`) and the forward to open it with
   (`ssh -L 5080:localhost:5080 maldex_admin@<host>`). In the job's console turn off Tamper Protection, then Real-time
   protection, then press Seal, within the configure idle timeout (2 h). The version must publish as an ISO build with the
   bundle's agent and pass the three harmless scenarios; its disks are deleted afterwards.

### The template E2E gate

`MALDEX_WIN_ISO=/path/to/win11.iso ./build.sh e2e-templates [test-filter]` runs those three classes (`MALDEX_REAL_VM=1
MALDEX_REAL_NET=1 MALDEX_REAL_TEMPLATES=1`; about two to three hours). It refuses without `MALDEX_WIN_ISO` (exit 2: the Build test
would only skip, and a skipped run never validates), checks the base image (exit 2 if missing) and runs `./build.sh agent` when `.data/agent/Maldex.Agent.exe` is missing. An
optional argument runs only the tests whose method name contains it (for example `./build.sh e2e-templates Edit`); a filtered
run never updates the stamp.

A full run that is green, with no skipped test, writes `git rev-parse HEAD` to **`.data/e2e-templates.validated`** (the stamp;
`.data/` is untracked), but only if the template-affecting paths have no uncommitted changes (otherwise it says why and leaves
the stamp alone). The paths are the one `TEMPLATE_E2E_PATHS` list in `build.sh`: the template orchestrator code, agent,
domain/lifecycle/data/infrastructure for templates, Libvirt/Agent/Net/Isolation, the template and tunnel endpoints,
the media library (`Core`, `Infrastructure` and `Orchestrator` `Media/`, the media endpoints), `deploy/guest/provision/`,
`deploy/guest/autounattend/`, `deploy/host/` and the E2E test files.

At the end of `./build.sh e2e`, build.sh tells you when the template E2E is due: the stamp is missing, its commit is not an
ancestor of HEAD, a watched path changed since it (`git diff --name-only <stamp> HEAD -- <paths>`), or a watched path has
uncommitted changes. It prints the changed files and, on a terminal, asks `run ./build.sh e2e-templates now? [y/N]` (default
No); with no terminal it only prints. Plain `e2e` still runs only `RealVmE2ETests`.

The registry-write sample is a `.ps1`; the agent opens samples with ShellExecute, so `Provision.ps1` re-points the `.ps1` Open verb at PowerShell (Windows 11 otherwise opens it in Notepad).
