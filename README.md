# CloudDrive

Mount Wasabi, AWS S3, Backblaze B2, Hetzner, OneDrive, Google Drive, SFTP, SMB, FTP, WebDAV and any
S3-compatible service as **native Windows drives** — owned by a Windows service, so they exist
before anyone signs in.

CloudDrive replaces **WasabiDrive** and **HetznerDrive** with one application. Those two projects are
read-only inputs to this one; neither is modified by it.

## Download

### [⬇ CloudDrive-Setup.exe — 1.0.5](https://github.com/RHC-Solutions/CloudDrive/releases/download/v1.0.5/CloudDrive-Setup.exe)

78 MB · Windows 10 1607+, Windows 11, Windows Server 2016–2025 · x64 · self-contained, no .NET needed
&nbsp;·&nbsp; [All releases](https://github.com/RHC-Solutions/CloudDrive/releases)

SHA-256, to check before running an unsigned installer:

```
c0b6215f40e06c77dbfb0628f0a3c85b3a12d883997d27c4a4ed3caa72a3edd5
```

```powershell
(Get-FileHash .\CloudDrive-Setup.exe -Algorithm SHA256).Hash
```

> **1.0.5 is a prerelease.** The link above points at the tag rather than
> `releases/latest/download/…`, because GitHub's *latest* excludes prereleases and that shortcut
> currently 404s. It starts working — and this link can become permanent — with the first stable
> release.
>
> Two things follow from the prerelease flag and are expected: the in-app updater queries the *latest*
> release and so will not offer a prerelease, and SmartScreen will warn because the installer is
> unsigned.
>
> More to the point: the code paths that talk to remote storage have **never been run against real
> storage**. No mount has been made against any provider and no OAuth sign-in has been completed
> against a real app registration.

---

## What is different

**Drives are Windows disks, not network drives.** rclone presents a mount as a fixed disk unless
`--network-mode` is passed, and both predecessors passed it unconditionally — which is why their
drives appeared under *Network locations*. CloudDrive stops passing it, so `H:` shows up under
**Devices and drives** with a volume label and the CloudDrive icon, exactly the way Google Drive
presents itself. The catch is that fixed disks get a Recycle Bin, which would turn every delete into
a server-side copy that keeps costing money; CloudDrive sets the volume's `NukeOnDelete` policy and
sweeps any `$RECYCLE.BIN` it finds. Network mode remains a per-mapping override for the rare
application that misbehaves against a fixed-disk FUSE mount.

**Accounts and mappings are separate.** An **Account** is one login to one provider. A **Mapping** is
one mount that uses it. Three AWS logins and two Storage Boxes are five accounts, and any number of
mappings hang off each — no re-entering a key per mount, which is what both predecessors required.

**The service owns everything.** Configuration lives once, in `%ProgramData%\CloudDrive`, owned by a
LocalSystem service that does all drive mounting and all alerting. The tray app and the CLI are thin
clients over an ACL'd named pipe. A second Windows account sees the same mounts, and alerts still
fire at 3am with nobody logged in — which is the only time an alert is worth having.

---

## Providers

Twelve brands, five back ends. The brand supplies endpoints and quirks; the back end moves the bytes.

| Provider | Protocol | Auth | Drive mount | Files On-Demand |
|---|---|---|---|---|
| Wasabi | S3 | key pair | ✓ | ✓ |
| Amazon S3 | S3 | key pair | ✓ | ✓ |
| Backblaze B2 | S3 | keyID + applicationKey | ✓ | ✓ |
| Hetzner Object Storage | S3 | key pair | ✓ | ✓ |
| S3-compatible (MinIO, Ceph, R2, Storj…) | S3 | key pair | ✓ | ✓ |
| Hetzner Storage Box | SFTP / SMB / WebDAV | password or SSH key | ✓ | ✓ |
| SFTP / SSH | SFTP | password or SSH key | ✓ | ✓ |
| SMB / CIFS | SMB | password | ✓ | ✓ |
| FTP / FTPS | FTP | password | ✓ | ✓ |
| WebDAV | WebDAV | password | ✓ | ✓ |
| OneDrive + SharePoint | Graph | OAuth 2 | ✓ | *next release* |
| Google Drive | Drive v3 | OAuth 2 | ✓ | *next release* |

**SSH, SSHFS and SFTP are one protocol.** SFTP is a subsystem of SSH; SSHFS is the name of the
*Linux FUSE client* that speaks it. Three menu entries producing byte-identical connections would be
a lie, so there is one **SFTP / SSH** provider. FTP is genuinely different and has its own back end.

**Backblaze goes through its S3-compatible endpoint**, so it reuses the entire S3 path — client,
retry policy, presigned share links, multipart upload — rather than adding a back end that would
duplicate all of it for no user-visible difference.

---

## Two ways to mount

**Drive or folder mountpoint** — a virtual drive at `H:` or a folder such as
`C:\CloudDrive\Backups`, via rclone and WinFsp. Can be hosted by the service, so it is there before
sign-in and visible from every session.

**Files On-Demand folder** — a normal folder backed by the Windows Cloud Files API, the way OneDrive
works: files show in Explorer as placeholders, download when opened, and support the native **Status**
column and **"Free up space"**. Two-way sync, with change detection and conflict logging. Runs in
your session, because a sync root lives in a user profile.

---

## Windows support

Windows 10 1607+, Windows 11, and Windows Server 2016 through 2025 — including Server Core. The
surface is not uniform and CloudDrive **detects** rather than assumes.

| | Win 10/11 | Server 2019+ | Server 2016 | Server Core |
|---|---|---|---|---|
| Drive / folder mounts | ✓ | ✓ | ✓ | ✓ |
| Windows service | ✓ | ✓ | ✓ | ✓ |
| Alerts | ✓ | ✓ | ✓ | ✓ |
| CLI | ✓ | ✓ | ✓ | ✓ |
| Tray app | ✓ | ✓ | ✓ | — no shell |
| **Files On-Demand** | ✓ | ✓ | **—** | — |

`cldapi.dll` shipped with Windows 10 1709; Server 2016 is build 14393 and does not have it.
Microsoft's `CfRegisterSyncRoot` page lists "Minimum supported server: Windows Server 2016", but that
row is a documentation-template default rather than a claim about the DLL. CloudDrive probes for the
export at runtime instead of trusting either the docs or a version check, and hides the mode with an
explanation. `CloudDrive.Core`, `.Ipc` and `.Service` carry no OS-version target framework, so a
Server 2016 install cannot even load code that would fail there.

---

## Alerts

Telegram, Slack and email, sent **by the service** so they do not need a session.

Events cover mount success, failure, loss and recovery, credentials rejected, OAuth re-authorisation
required, sync conflicts, low cache disk, and every stage of an update. Each target has a severity
floor and an event filter.

Three things make this usable rather than noisy:

- **Dedup with cooldown.** A flapping mount would otherwise send hundreds of messages and get the
  channel muted, which is worse than no alerting. Repeats are coalesced and counted.
- **Recovery is not suppressed.** The cooldown is cleared when a mount comes back, because "it is
  working again" is the message people most want promptly.
- **A durable spool.** Alerts queue to disk and retry, so one survives a service restart or an outage
  of the very network link whose failure triggered it.

---

## Updating

**CloudDrive itself.** The service polls `RHC-Solutions/CloudDrive` releases on a jittered interval,
downloads a new release as soon as it finds one, and installs it when the machine is quiet. "Quiet"
means all of: no mapping has moved bytes recently, no file is held open on a mount, no mapping is
flagged never-interrupt, the interactive user (if any) has been idle, and the time is inside the
maintenance window if one is set. Nobody signed in does not block anything — that is the normal state
on a server. An alert goes out before applying, so a watching administrator can intervene, and after,
with the outcome.

**Bundled tools.** rclone, WinFsp and sshfs-win live under `%ProgramData%\CloudDrive\tools`, versioned
side by side, with `tools\bin` on the machine `PATH`. Each is checked against its **vendor's** own
release feed, so a security fix arrives when the vendor ships it rather than when CloudDrive next
releases. Downloads are verified against the published digest **and** an Authenticode signature before
use — this code puts an executable on the system PATH, so there is no "install anyway". A bad release
rolls back by repointing at the previous version already on disk, which works with no network at all.

---

## Security

- Secrets are DPAPI-protected under **SYSTEM's own profile**, not at `LocalMachine` scope. Any process
  can decrypt a LocalMachine blob, which would make the file ACL the entire defence; binding to SYSTEM
  means decryption genuinely requires being SYSTEM, and the ACL becomes defence in depth. Both
  predecessors used LocalMachine.
- Nothing outside the service can read a secret. The tray app asks over the pipe and is refused unless
  it owns the mapping — otherwise any standard user could read the machine-wide config, pick someone
  else's mapping id, and have LocalSystem decrypt their password.
- Passwords reach rclone through the environment, never a command line, where the process list would
  expose them to every user on the machine.
- Configuration changes require administrator rights, because a mapping names a mount point and a
  remote path — editing one is equivalent to controlling what the LocalSystem service does.
- Export deliberately omits secrets. They are bound to this machine and cannot be restored elsewhere.

---

## Layout

```
src/CloudDrive.Core/           models, provider catalogue, stores, rclone, mounting,
                               tool manager, updater, idle detection
src/CloudDrive.CloudFiles/     cfapi on-demand engine + five storage clients
src/CloudDrive.Notifications/  Telegram, Slack, email; routing, dedup, spool
src/CloudDrive.Ipc/            named-pipe contracts, ACL'd server, client
src/CloudDrive.Service/        the Windows service: owns state, mounts, alerts, serves the pipe
src/CloudDrive.App/            WPF tray and management UI
src/CloudDrive.Cli/            cdrive.exe — Server Core and automation
src/CloudDrive.Tests/          xUnit
```

Adding a provider is a `ProviderDescriptor` entry plus an endpoint table in
[`ProviderCatalog`](src/CloudDrive.Core/Providers/ProviderCatalog.cs) — not a new code path. That is
the only way twelve brands stays maintainable, and the catalogue is unit-tested as data.

---

## Build

```powershell
scripts\fetch-tools.ps1        # download rclone + WinFsp into third_party (once)
dotnet build CloudDrive.slnx
dotnet test  src\CloudDrive.Tests
dotnet run   --project src\CloudDrive.App
```

Then install the service, from an elevated prompt:

```powershell
cdrive service install          # elevated. From PowerShell with a full path,
                                # prefix it with the call operator: & "C:\...\cdrive.exe" service install
```

## Command line

```
cdrive status                  What is mounted right now
cdrive list [accounts]         Mappings, or accounts
cdrive mount <name|id>         Mount one mapping
cdrive unmount <name|id>       Unmount one mapping
cdrive service <verb>          install | uninstall | start | stop | restart | status
cdrive tools <verb>            list | check | install <id> | rollback <id> | path
cdrive update [check|install]  Look for a new release, or apply the pending one
cdrive info                    What this machine supports
```

## Where things live

`%ProgramData%\CloudDrive\` (ACL'd to SYSTEM + Administrators):

| File | Contents |
|---|---|
| `mappings.json` | accounts and mappings — no secrets |
| `settings.json` | settings, alert targets — no secrets |
| `credentials.dat` | every secret, DPAPI under SYSTEM |
| `tools\` | managed rclone / WinFsp / sshfs-win, `tools\bin` on PATH |
| `spool\` | undelivered alerts awaiting retry |
| `logs\` | service logs; it also writes to the Event Log |

`%LOCALAPPDATA%\CloudDrive\` holds only what is genuinely per-user: on-demand sync state, window
geometry, and the app's own log.

## Testing

See [docs/TESTING.md](docs/TESTING.md) — you can run the whole thing end to end without the
installer and without administrator rights.

## Licence

[MIT](LICENSE) © [RHC Solutions](https://rhcsolutions.com/). Built on
[rclone](https://rclone.org) and [WinFsp](https://winfsp.dev).
