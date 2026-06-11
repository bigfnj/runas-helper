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
3. It remaps that token to the active console session so the new process shows up on **your** desktop instead of the invisible Session 0, then launches it via `CreateProcessWithTokenW`.

### Security model

The named pipe's ACL grants access to **`BUILTIN\Administrators` and `LocalSystem` only**. A non-administrator cannot connect, so this is *not* a local privilege-escalation path — it only lets an administrator (who can already reach TrustedInstaller by other means) do so conveniently. Keep it that way if you modify the pipe security.

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

Launch **RunAS Helper** from the Start Menu (or it appears automatically at logon). From the window / tray icon you can:

- browse for and launch any executable as TrustedInstaller,
- set the process priority,
- keep a list of **saved applications** for one-click launches,
- see **recent launches** (MRU),
- check live service status, start the service, and import/export settings.

Settings are stored in `%AppData%\RunAsHelper\settings.json`.

### Command line

```
RunAsHelper.exe [/p:n] <path> [args]
```

`/p:n` sets the priority class of the launched process:

| Flag   | Priority      |
|--------|---------------|
| `/p:1` | Normal (default) |
| `/p:2` | Idle          |
| `/p:3` | High          |
| `/p:4` | Realtime      |
| `/p:5` | Below Normal  |
| `/p:6` | Above Normal  |

Examples:

```bat
:: Open a TrustedInstaller command prompt
RunAsHelper.exe cmd.exe

:: Launch regedit at high priority
RunAsHelper.exe /p:3 regedit.exe

:: Quote paths that contain spaces
RunAsHelper.exe "C:\Program Files\Some Tool\tool.exe" --flag
```

The CLI streams the service's log lines to stdout and exits `0` on success, `1` on failure. It requires the **RunASHelper** service to be installed and running, and must itself be run from an elevated context.

## Build from source

**Requirements:** Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download). The WiX 4 toolset is pulled in automatically as a NuGet package — nothing else to install.

```
dotnet build RunAsHelper.sln -c Release
```

This produces a single self-contained installer at:

```
RunAsHelper.Installer\bin\x64\Release\RunAsHelper-Setup.msi
```

To stamp a specific version into the MSI (the release workflow does this from the git tag):

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

Use increasing versions for successive releases — the MSI's `MajorUpgrade` relies on a higher `ProductVersion` to detect and replace a prior install.

## License

[MIT](LICENSE)
