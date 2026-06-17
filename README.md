# RunAS Helper

Launch any program as **TrustedInstaller** — the highest privilege level on Windows, above standard Administrator — from a system-tray app or a one-line command.

[![Release](https://github.com/bigfnj/runas-helper/actions/workflows/release.yml/badge.svg)](https://github.com/bigfnj/runas-helper/actions/workflows/release.yml)

---

## Why

Some files, registry keys, and services are owned by `NT SERVICE\TrustedInstaller` and are off-limits even to an elevated Administrator. Editing a protected system file, replacing a guarded driver, or poking a locked-down service normally means manually hijacking the TrustedInstaller token by hand. RunAS Helper does that for you and gives you a `TrustedInstaller`-level process — a shell, an editor, whatever you point it at.

> ⚠️ **This is a power tool for system administration and troubleshooting.** A TrustedInstaller process can modify or destroy any part of Windows. Use it deliberately.

## How it works

RunAS Helper is two cooperating processes:

```
  ┌─────────────────────┐         named pipe          ┌──────────────────────────┐
  │  RunAsHelper.exe     │   \\.\pipe\RunAsHelper      │  RunAsHelper.Service      │
  │  tray app + CLI      │ ──────────────────────────► │  Windows service          │
  │  (runs as you/admin) │   LaunchRequest (JSON)      │  (runs as LocalSystem)    │
  │                      │ ◄────────────────────────── │                          │
  └─────────────────────┘   log + result stream        └────────────┬─────────────┘
                                                                     │ impersonates winlogon,
                                                                     │ steals the TrustedInstaller
                                                                     │ token, remaps it to your
                                                                     ▼ desktop session
                                                          your program, running as
                                                          NT SERVICE\TrustedInstaller
```

1. The **client** (tray app or CLI) sends a launch request over a named pipe.
2. The **service**, running as LocalSystem, enables `SeDebug`/`SeImpersonate`/`SeTcb`, impersonates `winlogon.exe`, starts the `TrustedInstaller` service, and duplicates its access token.
3. It remaps that token to the requesting client's session so the new process shows up on **your** desktop instead of the invisible Session 0, then launches it via `CreateProcessAsUserW`.

### Security model

The named pipe ACL grants access to BUILTIN\Administrators, SYSTEM, and the interactive session — this lets endpoint-privilege-management tools (Avecto, BeyondTrust, CyberArk, etc.) elevate a standard-user tray process on-demand.

The service enforces identity server-side, not via the client-supplied `Source` field:

- **Tray control** (`setcli`, saving settings, validation): requires the client to be both the Authenticode-signed `RunAsHelper.exe` **and** running with an elevated token. Neither condition alone is sufficient.
- **CLI gate** (any other process): when the elevated signed tray opens the gate, **any** process that can reach the pipe gets elevated to TrustedInstaller — the pipe ACL is the boundary. The gate resets whenever the owning tray exits or crashes.

Keep the pipe ACL and the Authenticode check intact if you modify the pipe security.

## Install

1. Download `RunAsHelper-Setup-<version>.msi` from the [latest release](https://github.com/bigfnj/runas-helper/releases/latest).
2. Run it (it's a per-machine install and needs elevation):
   ```
   msiexec /i RunAsHelper-Setup-<version>.msi /passive
   ```

The installer is a single self-contained file — **no .NET runtime is required** on the target machine. It:

- installs the **RunASHelper** Windows service (LocalSystem, auto-start),
- installs the tray app and a Start Menu shortcut,
- optionally launches the tray right after install (Finish-dialog checkbox).

The tray runs **non-elevated** and registers its own per-user login auto-start
(an `HKCU\…\Run` entry that opens just the tray icon). Click the tray's
**Activate** button to elevate on demand (no standing scheduled task).

### Uninstall

```
msiexec /x RunAsHelper-Setup-<version>.msi /passive
```
…or via *Settings → Apps → RunAS Helper*. This stops and removes the service and the shortcut. (Remove the per-user login auto-start, if you want it gone, via *Settings → Apps → Startup* or by deleting the `RunAsHelper` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.)

## Usage

### Tray app

Launch **RunAS Helper** from the Start Menu, or let it start at login as a tray
icon (see *Startup* below). The main window is a **saved-applications manager**:

- **Saved applications** — a list (Name / File Location / Parameter). **Add
  Application** stores a launch with its location, arguments, working directory,
  **window state** (Normal / Minimized / Maximized / Hidden), priority, and
  **account** (TrustedInstaller or SYSTEM). Double-click or **Run** to launch;
  **Edit** / **Remove** / **↑ ↓** to manage (Del / F2 / Enter shortcuts).
- **Quick run (one-off)** — a top row to launch a path once (with priority +
  account) without saving it.
- **Tools menu** — Settings, Validate Installation, Open PowerShell
  (TrustedInstaller), Import/Export saved apps, and **How to Use**.

**Elevation (Activate).** The tray runs **non-elevated** (greyed icon). The
service's control pipe only admits elevated administrators, so click the
**Activate** bar to relaunch elevated (via your OS/endpoint elevation prompt);
the bar disappears once elevated and the icon turns colour when the service is
reachable.

**Accounts.** *TrustedInstaller* launches with a SYSTEM token carrying the
`NT SERVICE\TrustedInstaller` group (needed for TI-owned files/keys/services);
*SYSTEM* launches with a plain LocalSystem token. Because the TrustedInstaller
service runs as LocalSystem, `whoami` reads `nt authority\system` for both — the
difference is the TrustedInstaller group membership.

**Startup.** *Settings → Start with Windows* (on by default) registers a per-user
`HKCU\…\Run` entry that opens the tray icon at login (window stays closed until
you click it). There is **no scheduled task**.

Settings are stored in `%AppData%\RunAsHelper\settings.json`.

### Command line

```
RunAsHelper.exe [/p:N] [/as:ACCOUNT] <path> [args]
RunAsHelper.exe -h | --help | /?      :: show full help
```

`/p:N` sets the priority class; `/as:ACCOUNT` chooses the account:

| Flag   | Priority         |   | Flag         | Account                      |
|--------|------------------|---|--------------|------------------------------|
| `/p:1` | Normal (default) |   | `/as:ti`     | TrustedInstaller (default)   |
| `/p:2` | Idle             |   | `/as:system` | LocalSystem                  |
| `/p:3` | High             |
| `/p:4` | Realtime         |
| `/p:5` | Below Normal     |
| `/p:6` | Above Normal     |

Non-executable targets are launched via their host automatically (`.msc`→`mmc`,
`.cpl`→`control`, `.bat`/`.cmd`→`cmd /c`, `.ps1`→`powershell`), and a bare name
(e.g. `notepad.exe`, `lusrmgr.msc`) is resolved on the PATH.

```bat
:: Open a TrustedInstaller command prompt
RunAsHelper.exe cmd.exe

:: Launch regedit at high priority
RunAsHelper.exe /p:3 regedit.exe

:: Run as plain SYSTEM
RunAsHelper.exe /as:system cmd.exe

:: Quote paths that contain spaces
RunAsHelper.exe "C:\Program Files\Some Tool\tool.exe" --flag
```

The CLI streams the service's log lines to stdout and exits `0` on success, `1`
on failure. It requires the **RunASHelper** service running and an elevated
context.

> 🔒 **The command line is disabled by default.** As a hardening measure, the
> service rejects CLI-sourced launches unless you enable them this session via
> *Settings → "Allow command line"* (off again on every tray launch/exit). The
> tray's own launches are unaffected. When the gate is open, **any** process that
> can reach the pipe is elevated — the pipe ACL (Administrators + interactive
> session) is the boundary, not per-caller elevation.

## Build from source

**Requirements:** Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download). The WiX 4 toolset is pulled in automatically as a NuGet package — nothing else to install.

```
dotnet build RunAsHelper.sln -c Release
```

This produces a single self-contained installer at:

```
RunAsHelper.Installer\bin\x64\Release\RunAsHelper-Setup.msi
```

To stamp a specific version into the MSI **and** the EXE `FileVersion`/`AssemblyVersion` (the release workflow does this from the git tag):

```
dotnet build RunAsHelper.sln -c Release -p:ProductVersion=1.2.3
```

### Projects

| Project | Output | Role |
|---|---|---|
| `RunAsHelper` | `RunAsHelper.exe` | Tray GUI + CLI client (WinForms) |
| `RunAsHelper.Service` | `RunAsHelper.Service.exe` | LocalSystem Windows service; performs the elevation |
| `RunAsHelper.Shared` | library | Named-pipe wire protocol (framed JSON) |
| `RunAsHelper.Installer` | `RunAsHelper-Setup.msi` | WiX 4 installer; publishes both apps self-contained and embeds them |

## Releasing

Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml) on a Windows runner.

- **Cut a release:** push a version tag.
  ```
  git tag v1.2.3
  git push origin v1.2.3
  ```
  The workflow builds `RunAsHelper-Setup-1.2.3.msi` (with `ProductVersion=1.2.3`) and attaches it to a new GitHub Release with auto-generated notes.
- **Dry run:** *Actions → Release → Run workflow*, supply a version. This builds and uploads a downloadable artifact but does **not** create a Release.

Use increasing versions for successive releases. `MajorUpgrade` detects and
replaces a prior install; `AllowSameVersionUpgrades` lets an equal version
reinstall in place (handy during development).

## What's new in 1.4.0

- **Window foreground fix** — launched processes now reliably appear in the foreground instead of opening behind existing windows. The service sends the new process's PID to the tray, which calls `AllowSetForegroundWindow` before acknowledging the launch.
- **Authenticode-based pipe gating** — the service now verifies the connecting binary's Authenticode signature (not just its path) before granting tray-control verbs. A renamed copy of the binary is rejected.
- **Structured Windows Event Log** — events 1001 (request received), 1002 (launched), 1003 (denied), 1004 (token failure), and 1005 (service start/stop) are written to the Application log under the `RunAsHelper` source. Registered by the installer at install time.
- **Binary versioning** — `FileVersion` and `AssemblyVersion` in both EXEs are now stamped from the release tag (was always `1.0.0.0`). Enterprise software-inventory tools will see the correct version.
- **Code cleanup** — removed dead VB6 and twinBASIC legacy sources that were carried in the repo since the original port.

## Planned features

Not done yet — tracked for future work:

- **Non-executable launch, round 2** — `.reg` files (via `regedit /s`) and
  arbitrary documents (via shell association), beyond the current host-mapped
  set (`.msc`/`.cpl`/`.bat`/`.ps1`).
- **Per-app icons** in the saved-applications list (extract the target's icon).
- **List quality-of-life** — drag-to-reorder, and a search/filter box for long
  lists.
- **Silent-install auto-start** — register the per-user login entry for
  unattended (`/passive`, `/qn`) deployments, where there's no Finish dialog to
  trigger the first run.
- **Authenticode signing** — sign the release binaries so the Authenticode pipe check also verifies the publisher, not just the presence of a signature.

## License

[MIT](LICENSE)
