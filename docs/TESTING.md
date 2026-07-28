# Testing CloudDrive without the installer

You do not need the installer, and you do not need administrator rights.

The service normally keeps its configuration in `%ProgramData%\CloudDrive`, which is ACL'd to SYSTEM
and Administrators. Setting **`CLOUDDRIVE_DATA_DIR`** points it at a scratch directory instead, so the
whole thing runs end to end as a normal user and cleans up by deleting one folder.

## Build

```powershell
scripts\fetch-tools.ps1        # rclone + WinFsp into third_party (once)
dotnet build CloudDrive.slnx
dotnet test  src\CloudDrive.Tests
```

## Run it

Two terminals. **First**, the service as a console application:

```powershell
$env:CLOUDDRIVE_DATA_DIR = "$env:LOCALAPPDATA\CloudDrive-test"
.\src\CloudDrive.Service\bin\Debug\net10.0-windows\CloudDrive.Service.exe
```

It prints the redirected directory, then runs. Leave it.

**Second**, the CLI:

```powershell
.\src\CloudDrive.Cli\bin\Debug\net10.0-windows\cdrive.exe status
.\src\CloudDrive.Cli\bin\Debug\net10.0-windows\cdrive.exe tools list
.\src\CloudDrive.Cli\bin\Debug\net10.0-windows\cdrive.exe info
```

`status` reporting a version and *"No mappings are configured"* means the named pipe, the dispatcher
and the machine store are all working.

The tray app connects to the same pipe:

```powershell
.\src\CloudDrive.App\bin\Debug\net10.0-windows10.0.19041.0\CloudDrive.exe
```

Add an account and a mapping there, then watch `cdrive status` and the service console.

Throw it all away with `Remove-Item -Recurse $env:LOCALAPPDATA\CloudDrive-test`.

## Or run the real thing

For a proper install without building an installer, publish and register the service. One elevated
prompt:

```powershell
scripts\publish.ps1
.\publish\cdrive.exe service install     # needs elevation
.\publish\CloudDrive.exe
```

`cdrive service uninstall` removes it again. Configuration under `%ProgramData%\CloudDrive` is left
alone, which is what you want between test runs.

## What will and will not work unelevated

| | Unelevated console run | Installed service (LocalSystem) |
|---|---|---|
| IPC, config, alerts, tool checks | ✓ | ✓ |
| Mount a drive letter | ✓ (this session only) | ✓ (all sessions, before sign-in) |
| Add the tools directory to `PATH` | — needs HKLM | ✓ |
| Harden the store's ACL | — skipped, and said so | ✓ |
| Files On-Demand | ✓ | n/a — runs in the tray app |

None of the unelevated gaps are failures. CloudDrive resolves its tools by absolute path, so a missing
`PATH` entry only affects typing `rclone` yourself.

## Things that legitimately look like errors

**`Up to date: version 1.0.0`** when you expected an update — correct, and not an error. The repository
is public and the releases API answers anonymously, but no releases have been published yet, so there
is nothing newer to find. Auto-update starts working with the first release that carries a
`CloudDrive-Setup*.exe` asset; until then the updater polls, finds an empty list, and says so.

**`GitHub returned 404 for '…'`** — the repository is not visible anonymously, which for a private
repository is permanent: the releases API needs authentication and CloudDrive polls it without any.
Reported once per service start rather than on every poll. This was the state before the repository was
made public.

**`Not adding …\tools\bin to the system PATH`** — expected unelevated; see the table above.

**Tools showing `INSTALLED  -`** — `scripts\fetch-tools.ps1` populates `third_party\` for the build.
The *managed* copy under `<data dir>\tools` is separate and is filled by
`cdrive tools install rclone`, or automatically by the service.

## Diagnosing

The service writes `<data dir>\logs\service-YYYY-MM-DD.log`, and the tray app writes
`%LOCALAPPDATA%\CloudDrive\logs\app-*.log`. `cdrive log 200` tails the service log over the pipe.

Two startup self-checks exist because both of their failures were, once, invisible:

- **Machine store** — the store is probed for writability before the host starts, so an access problem
  is one clear message instead of an unhandled exception from inside a background service.
- **IPC wire format** — an envelope is serialised and parsed back before the pipe opens. A partially
  written `bin\` directory once left the service unable to load part of the JSON stack, and the only
  symptom was every client reporting *"the connection to the CloudDrive service was lost"*.

If you hit something strange after lots of incremental builds — especially in a OneDrive-synced folder,
where sync can hold files open mid-write — delete `bin` and `obj` and rebuild. That was the actual
cause of the failure the second self-check now catches.
