# CloudDrive — plan

CloudDrive merges **WasabiDrive** and **HetznerDrive** into one product that mounts any of a dozen
storage back ends as a native Windows drive, runs as a Windows service independent of any signed-in
user, and reports what it is doing over Telegram, Slack and email.

## Where the code comes from

HetznerDrive is the base. It is the younger project (0.2.0 against WasabiDrive's 0.7.0) but it is
architecturally ahead in every dimension that matters here:

| | WasabiDrive 0.7.0 | HetznerDrive 0.2.0 |
|---|---|---|
| Storage abstraction | none — `WasabiS3Client` directly | `IRemoteStorageClient`, 4 implementations |
| Protocols | S3 | SFTP, SMB, WebDAV, S3 |
| Windows service | no | yes, LocalSystem, converge-on-desired-state |
| Machine-wide config | no | `MachinePaths` + ACL hardening |
| rclone tuning | one flag set | per-protocol flag sets |
| Secret handling | obscure via subprocess | in-process `RcloneObscure` |
| Release hardening | 11 releases | 2 releases |

So HetznerDrive supplies the skeleton, and WasabiDrive contributes its Wasabi region catalogue, its
S3 retry policy and the UI polish it accumulated over nine more releases (tray and row context
menus, log auto-scroll, mojibake fix, on-demand as the default mapping mode).

Neither project survives as-is. The single-brand assumption is baked into both — `HetznerProduct`,
`WasabiRegion`, credentials keyed per mapping — and that assumption is exactly what has to go.

## The central refactor: accounts

Both projects tie one credential set to one mapping. CloudDrive splits them:

```
Account  (one login to one provider)  1 ──── N  Mapping  (one mount)
```

An **Account** is a provider brand plus the settings and secrets needed to reach it: an AWS access
key pair, a Hetzner Storage Box username and password, a Google OAuth refresh token. A **Mapping**
names an account, a path inside it (bucket, share, folder), and how it should surface on this
machine (drive letter, directory, or Files On-Demand folder).

This is what delivers *"must support multiple accounts from each brand"* — three AWS accounts and
two Google Drives are five `Account` rows, and any number of mappings can hang off each.

## Provider matrix

Twelve brands, but only five *kinds* of back end underneath. The kind determines the code; the brand
determines a descriptor — endpoints, quirks, credential form, capabilities.

| Provider | Kind | Auth | rclone backend | On-demand client |
|---|---|---|---|---|
| Wasabi | S3 | key pair | `s3` / `Wasabi` | `S3StorageClient` |
| AWS S3 | S3 | key pair | `s3` / `AWS` | `S3StorageClient` |
| Backblaze B2 | S3 | key id + app key | `s3` / `Other` | `S3StorageClient` |
| Hetzner Object Storage | S3 | key pair | `s3` / `Hetzner` | `S3StorageClient` |
| Generic S3 | S3 | key pair | `s3` / `Other` | `S3StorageClient` |
| Hetzner Storage Box | file | password / SSH key | `sftp`,`smb`,`webdav` | Sftp/Smb/WebDav |
| SFTP (= SSH, = SSHFS) | file | password / SSH key | `sftp` | `SftpStorageClient` |
| SMB / CIFS | file | password | `smb` | `SmbStorageClient` |
| FTP / FTPS | file | password | `ftp` | `FtpStorageClient` |
| WebDAV | file | password | `webdav` | `WebDavStorageClient` |
| OneDrive (+ SharePoint) | Graph | OAuth 2 | `onedrive` | `GraphStorageClient` |
| Google Drive | Drive v3 | OAuth 2 | `drive` | `GoogleDriveStorageClient` |

**On the requested "SMB, SSH, SSHFS, FTP, SFTP":** SSH, SSHFS and SFTP are one wire protocol. SFTP
is a subsystem of SSH; SSHFS is the name of the *Linux FUSE client* that speaks it. There is nothing
to build three times, and offering three menu entries that produce identical connections would be a
lie in the UI. CloudDrive ships one **SFTP / SSH** provider and says so. FTP and FTPS are a genuinely
different protocol and get their own back end.

**On Backblaze:** B2 has a native API and an S3-compatible one. CloudDrive uses the S3-compatible
endpoint throughout, so B2 reuses the whole S3 path — client, retry policy, presigned share links,
multipart upload — instead of adding a fifth back-end kind for no user-visible gain.

Adding a brand is a descriptor plus an endpoint table, not a new code path. That is the only way
twelve providers stays maintainable.

## Service-first architecture

*"Must run as a service and not be dependent on a Windows user."* Taken literally, that means the
service — not the tray app — owns the system.

```
                    ┌──────────────────────────────────────────┐
                    │  CloudDrive.Service      (LocalSystem)   │
   %ProgramData%    │  · owns accounts, mappings, secrets      │
   \CloudDrive  ────│  · mounts every drive-letter mapping     │
                    │  · sends every alert                     │
                    │  · named-pipe IPC server (ACL'd)         │
                    └───────────────┬──────────────────────────┘
                                    │  \\.\pipe\CloudDrive
                    ┌───────────────┴───────────────┐
                    │                               │
         ┌──────────┴──────────┐        ┌───────────┴──────────┐
         │  CloudDrive.App     │        │  CloudDrive.Cli      │
         │  WPF tray + UI      │        │  Server Core, CI     │
         │  + on-demand roots  │        └──────────────────────┘
         └─────────────────────┘
```

Config exists **once**, in `%ProgramData%\CloudDrive`. There is no per-user copy to drift, a second
Windows account sees the same mounts, and alerts keep firing at 3am with nobody signed in — which is
the only time an alert is worth having.

**The one thing that cannot move into the service** is the Files On-Demand folder. `cfapi` registers
a sync root inside a user profile and calls back into that user's session; there is no session-0
equivalent. So on-demand roots run in the tray app, and pull their credentials from the service over
the pipe, authorised by the caller's SID. Drive-letter mounts — the ones that need to exist before
sign-in — belong entirely to the service.

**Secret protection improves.** HetznerDrive re-encrypts the service's credential copy at DPAPI
`LocalMachine` scope, which *any* process on the box can decrypt; only the file ACL keeps other users
out. CloudDrive instead protects secrets under `CurrentUser` scope *from inside the service*, i.e.
under SYSTEM's own profile. Decryption then genuinely requires running as SYSTEM, and the ACL becomes
defence in depth rather than the entire defence.

## Mounting as a Windows disk, not a network drive

Google Drive's desktop client presents `G:` under **Devices and drives**, not **Network locations**.
rclone can do exactly this, and it is already the default: `--network-mode` is what opts *into* the
network presentation. HetznerDrive passes that flag; CloudDrive stops passing it.

Three consequences to handle rather than discover:

1. **Recycle Bin.** Fixed disks get one; network drives do not. Left alone, deleting a file on the
   mount copies it into `$RECYCLE.BIN` on the *remote*, so it keeps costing money and never actually
   frees space. CloudDrive sets `NukeOnDelete` for the mounted volume
   (`HKCU\…\BitBucket\Volume\{guid}`) so deletes go straight through, and sweeps any pre-existing
   `$RECYCLE.BIN` on first mount.
2. **Drive icon and label.** A disk-mode drive can carry a custom icon via
   `…\Explorer\DriveIcons\<letter>\DefaultIcon`, which is how Google Drive gets its logo in Explorer.
   CloudDrive registers a per-mapping icon and volume label and removes them on unmount.
3. **Escape hatch.** rclone's own guidance is that a few applications misbehave against fixed-disk
   FUSE mounts. `--network-mode` stays available as a per-mapping override, off by default.

## Windows support surface

Target is Windows 10 1607+, Windows 11, and Windows Server 2016 through 2025 — but the surface is
not uniform, and the app detects rather than assumes.

| Capability | Requirement | Behaviour when absent |
|---|---|---|
| Drive-letter mount | WinFsp, any target OS | installer deploys WinFsp |
| Windows service | any target OS | — |
| Alerts | any target OS | — |
| CLI | any target OS, incl. Server Core | — |
| Tray app (WPF) | Desktop Experience | Server Core → CLI only |
| **Files On-Demand** | **Windows 10 1709+ / Server 2019+** | **disabled, with a reason** |

Files On-Demand is the sharp edge. `cldapi.dll` shipped with Windows 10 1709 (build 16299); Server
2016 is build 14393 and does not have it. Microsoft's `CfRegisterSyncRoot` page lists "Minimum
supported server: Windows Server 2016", but that row is a docs-template default, not a statement
about the DLL. CloudDrive therefore probes for `cldapi.dll` at runtime (`OsCapabilities`) instead of
trusting either the documentation or an OS version check, hides the on-demand mapping mode when it is
missing, and says why.

Because Server Core has no WPF, `CloudDrive.Cli` is not a convenience — it is the only management
surface on that SKU, and it covers the full account/mapping/service lifecycle.

## Alerts

A new `CloudDrive.Notifications` assembly, driven by the service so alerts do not depend on a session.

- **Channels** — `TelegramChannel` (Bot API `sendMessage`), `SlackChannel` (incoming webhook or bot
  token), `EmailChannel` (SMTP over MailKit). All behind `INotificationChannel`.
- **Events** — mount up / down / auto-restarted / gave up, credentials rejected, **OAuth
  re-authorisation required**, sync conflict, sync error rate, cache disk low, remote quota near
  limit, service start/stop, update available.
- **Routing** — per channel, a severity floor and an event-type filter, so a chat channel is not
  told about every successful remount.
- **Dedup, cooldown, digest** — non-negotiable. A flapping mount would otherwise emit hundreds of
  messages. Events coalesce on (type, mapping) with a cooldown, and an optional periodic digest
  replaces per-event delivery.
- **Durable spool** — alerts queue to disk and retry, so a notification survives a service restart or
  an outage of the very network link whose failure triggered it.

## Bundled tools: one directory, versioned, self-updating, on PATH

CloudDrive does not ship its dependencies scattered next to the exe. Every external binary lives
under one managed root, machine-wide so the service and every user share one copy:

```
%ProgramData%\CloudDrive\tools\
   bin\                     ← the only directory added to PATH; junctions/shims to current versions
   rclone\1.71.1\rclone.exe
   sshfs-win\3.7.21011\...
   winfsp\2.0.23075\winfsp.msi
   tools.json               ← installed versions, vendor sources, checksums, last-checked
```

`bin` is appended to the **machine** `PATH` (`HKLM\SYSTEM\…\Environment`) and a `WM_SETTINGCHANGE`
broadcast makes running shells pick it up without a reboot. Machine scope rather than user scope,
because the service has no user hive — the same reason the config lives in `%ProgramData%`.

A `ToolManager` in Core owns the lifecycle. Each tool declares a **vendor** source, not a CloudDrive
mirror, so a security fix reaches users when the vendor ships it and not when CloudDrive next
releases:

| Tool | Vendor source | Why it is here |
|---|---|---|
| rclone | `rclone/rclone` GitHub releases | drive-letter mounts |
| WinFsp | `winfsp/winfsp` GitHub releases | the FUSE layer rclone mounts through |
| sshfs-win | `winfsp/sshfs-win` GitHub releases | optional direct SSHFS mounting |

Updates are checked on a schedule, downloaded to a staging directory, verified against the vendor's
published SHA-256 **and** Authenticode signature where one exists, then swapped in by repointing
`bin`. A tool in use by a live mount is never overwritten in place; the swap waits for the next
remount or an idle window. Versions are kept side by side so a bad release rolls back by repointing
`bin` at the previous directory rather than re-downloading.

Verifying before trusting matters here more than usual: this code downloads an executable and puts
it on the system `PATH`. Checksum-and-signature-or-refuse is the rule, with no "install anyway"
option.

## Updating CloudDrive itself

Two mechanisms, deliberately separate from the tool updater above.

**Release feed.** The service polls the `RHC-Solutions/CloudDrive` GitHub releases API on an interval
(default 6 hours, jittered so a fleet does not stampede), honouring ETags so an unchanged feed costs
one 304. There is no push transport that survives NAT without a persistent outbound connection, so
"push" here means the update arrives and applies without anyone asking — not that GitHub initiates
it. A manual **Check now** and a CLI `clouddrive update --check` force an immediate poll.

**Apply on idle.** An update is downloaded as soon as it is found and installed only when the machine
is quiet, because applying one means dropping every mount. Quiet is all of:

- no mapping has moved bytes for a configurable window (default 10 minutes);
- no file on any mount is held open by another process;
- no on-demand hydration or upload is in flight;
- the interactive user, if any, has been idle (`GetLastInputInfo`) past the same window — skipped
  entirely when nobody is signed in, which is the normal case on a server;
- the current time is inside the configured maintenance window, if one is set.

Failing any check, it waits and re-evaluates. Mounts are remounted after the swap, and the update is
announced through the alert channels — before, so a watching admin can veto, and after, with the
outcome. A `--defer` and a per-mapping "never auto-update while mounted" flag exist for the mapping
someone is running a backup job against.

## Layout

```
src/CloudDrive.Core/           models, provider descriptors, stores, rclone, mount engine,
                               ToolManager, updater, idle detection
src/CloudDrive.CloudFiles/     cfapi on-demand engine + IRemoteStorageClient implementations
src/CloudDrive.Notifications/  alert channels, routing, dedup, spool
src/CloudDrive.Ipc/            named-pipe contracts shared by service, app and CLI
src/CloudDrive.Service/        Windows service: owns state, mounts, alerts, serves the pipe
src/CloudDrive.App/            WPF tray and management UI (pipe client)
src/CloudDrive.Cli/            clouddrive.exe for Server Core and automation
src/CloudDrive.Tests/          xUnit
tools/                         manifests describing vendor sources (binaries fetched, not committed)
installer/                     Inno Setup
```

## Source control

The repository is `https://github.com/RHC-Solutions/CloudDrive`. WasabiDrive and HetznerDrive are
**read-only inputs**: CloudDrive copies and adapts their code but never modifies either working tree,
and neither repository is touched by this work.

## Phases

Each phase ends with something that runs.

**Phase 1 — foundation.** Account/mapping model, provider descriptors, service-first architecture
with IPC, the five S3 brands and the four file protocols, disk-mode mounting, on-demand folders,
managed tool directory on PATH, tray app. This is the merge of the two projects plus everything that
does not need OAuth.

**Phase 2 — OneDrive and Google Drive.** OAuth 2 with PKCE and a loopback redirect, authorised
interactively in the tray app; the service only ever refreshes. Graph and Drive v3 storage clients.
Bundled client IDs with a per-account override.

**Phase 3 — alerts and updating.** Notification channels, routing, dedup and spool; the GitHub
release feed, vendor tool-update checks, and apply-on-idle. Alerts come first in this phase because
the updater reports through them.

**Phase 4 — CLI, installer, migration.** `clouddrive.exe`, Inno Setup packaging, WinFsp bootstrap,
PATH registration, and an importer that reads existing WasabiDrive and HetznerDrive config so current
users do not reconfigure by hand.

## Known risks

| Risk | Handling |
|---|---|
| Google refresh tokens expire in 7 days while the OAuth app is unverified | ship the per-account client-ID override; document verification |
| OneDrive refresh tokens die after 90 days idle | service refreshes on a schedule; alert on failure |
| Fixed-disk mode breaks a specific application | per-mapping `--network-mode` override |
| Server 2016 lacks `cldapi.dll` | runtime probe, mode hidden with an explanation |
| DPAPI under SYSTEM ties secrets to the machine | export excludes secrets, as today; document re-entry on restore |
| Twelve providers × two mount modes is a wide test matrix | provider descriptors are data, unit-tested as tables |
| A vendor tool update ships a regression | versions kept side by side; rollback repoints `bin` |
| Auto-update drops mounts mid-job | idle gate, maintenance window, per-mapping opt-out, alert before applying |
| Machine `PATH` edits are global and easy to corrupt | append-if-absent only, never rewrite; removed cleanly on uninstall |
