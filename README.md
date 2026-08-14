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

- **Tray control** (`setcli`, saving settings, validation): requires the client to be both the installed `RunAsHelper.exe` — identified by image path (the binary sitting next to the service), so unsigned local/CI builds still work — **and** running with an elevated token. Neither condition alone is sufficient. A caller's Authenticode signature is recorded for diagnostics; pinning it to a specific publisher is optional future hardening (see *Planned features*).
- **CLI gate** (any other process): when the elevated tray opens the gate, **any** process that can reach the pipe gets elevated to TrustedInstaller — the pipe ACL is the boundary. The gate resets whenever the owning tray exits or crashes.

Keep the pipe ACL and the install-path identity check intact if you modify the pipe security.

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

The installer bundles the app's public code-signing certificate and offers an
**optional, off-by-default** step to trust it on the machine (a checkbox during
setup). When enabled, the certificate is imported into `LocalMachine\Root` so
Windows shows *Serenity Software* as a verified publisher for the app's signature
instead of "Unknown publisher". It trusts anything signed by that certificate on
that computer and is **not** removed on uninstall. To enable it silently:

```
msiexec /i RunAsHelper-Setup-<version>.msi /qn INSTALLCERT=1
```

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
- **Quick run (one-off)** — launch a path once without saving it: pick a
  priority, type or **Browse…** to a path, then click **Run as TrustedInstaller**
  or **Run as SYSTEM** (the button you click chooses the account).
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
RunAsHelper.exe [/capture] [/timeout:N] [/p:N] [/as:ACCOUNT] <path> [args]
RunAsHelper.exe -h | --help | /?      :: show full help
```

| Flag          | Meaning                                                                        |
|---------------|--------------------------------------------------------------------------------|
| `/p:1`        | Normal priority (default)                                                      |
| `/p:2`        | Idle                                                                           |
| `/p:3`        | High                                                                           |
| `/p:4`        | Realtime                                                                       |
| `/p:5`        | Below Normal                                                                   |
| `/p:6`        | Above Normal                                                                   |
| `/as:ti`      | Run as TrustedInstaller (default)                                              |
| `/as:system`  | Run as plain LocalSystem                                                       |
| `/capture`    | Stream child stdout/stderr back through the pipe; CLI blocks until child exits |
| `/timeout:N`  | Hard ceiling in seconds; on timeout the stream closes, child is left running   |

Non-executable targets are launched via their host automatically (`.msc`→`mmc`,
`.cpl`→`control`, `.bat`/`.cmd`→`cmd /c`, `.ps1`→`powershell`), and a bare name
(e.g. `notepad.exe`, `lusrmgr.msc`) is resolved on the PATH — including when
arguments follow, so targets outside `System32` such as `powershell.exe` launch
correctly too.

```bat
:: Open a TrustedInstaller command prompt
RunAsHelper.exe cmd.exe

:: Launch regedit at high priority
RunAsHelper.exe /p:3 regedit.exe

:: Run as plain SYSTEM
RunAsHelper.exe /as:system cmd.exe

:: Run a PowerShell command as SYSTEM (see "Passing arguments" below)
RunAsHelper.exe /as:system powershell.exe -NoProfile -Command "Restart-Service WSLService -Force"

:: Stream child output back to the caller (blocks until the script exits)
RunAsHelper.exe /capture /as:system powershell.exe -NoProfile -Command "Get-Service Wuauserv"

:: Capture with a 30-second timeout; child is left running if it doesn't finish
RunAsHelper.exe /capture /timeout:30 /as:system powershell.exe -NoProfile -File C:\scripts\fix.ps1

:: Quote paths that contain spaces
RunAsHelper.exe "C:\Program Files\Some Tool\tool.exe" --flag
```

#### Passing arguments

Everything after the target path is forwarded to it. Two rules keep the
arguments intact — get these wrong and the target may start but do nothing:

- **Pass each switch as its own token; quote only the parts that contain
  spaces** (typically just a `-Command`/`-c` script block). The CLI re-quotes any
  *single* argument that contains a space, so a whole multi-switch command bundled
  into one quoted string arrives at the target as one unparseable token. Given
  such a blob, `powershell.exe` starts and exits without running anything —
  `cmd.exe` only survives it because it re-parses the line internally, which is
  what can make a `cmd /c …` wrapper look "required" when it isn't.

  ```bat
  :: GOOD — switches are separate tokens; only the script block is quoted
  RunAsHelper.exe /as:system powershell.exe -NoProfile -Command "Get-Service WSLService | Restart-Service -Force"

  :: BAD — the entire argument string is one quoted blob; PowerShell can't parse it
  RunAsHelper.exe /as:system powershell.exe "-NoProfile -Command Get-Service WSLService | Restart-Service -Force"
  ```

- **Don't add `-ExecutionPolicy Bypass` for an inline `-Command`.** Execution
  policy applies only to script *files* (`.ps1`), never to `-Command`, so it is
  noise there. The tool already supplies it when *it* hosts a `.ps1` for you (the
  host-mapping above). Note that where execution policy is enforced by Group
  Policy, the switch is ignored for `.ps1` regardless.

The CLI streams the service's log lines to stdout and exits `0` on success, `1`
on failure. It requires the **RunASHelper** service running and an elevated
context (see the gate note below).

> 🔒 **The command line is disabled by default.** As a hardening measure, the
> service rejects CLI-sourced launches unless you enable them this session via
> *Settings → "Allow command line"* (off again on every tray launch/exit). The
> tray's own launches are unaffected. When the gate is open, **any** process that
> can reach the pipe is elevated — the pipe ACL (Administrators + interactive
> session) is the boundary, not per-caller elevation.
>
> **To use the CLI:** open the tray, click **Activate** (approve your OS/endpoint
> elevation prompt), then toggle *Settings → "Allow command line"*. Only the
> elevated, installed tray can open the gate, and it closes again when the tray
> exits — so re-enable it per session. An already-elevated tray launches without
> needing the gate at all; the gate exists for *non-elevated* CLI callers.
>
> **Awareness:** while the tray is running it watches the service's event log and
> shows a tray balloon for every *command-line*-sourced launch, so an escalation
> made through the open gate that you didn't initiate is flagged immediately.

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

### Signed build (optional)

A plain build is **unsigned** — that is fully supported (the service trusts the
tray by install path, not by signature). To produce Authenticode-signed EXEs and
MSI, use the helper scripts in [`signing/`](signing):

```powershell
# One-time: create a self-signed code-signing cert (CN=Serenity Software) in your
# user store and, with -TrustMachine (elevated), trust it on this machine.
.\signing\New-SigningCert.ps1 -TrustMachine

# Build a signed release. Resolves signtool + the cert, signs both EXEs before
# WiX packs them, then signs the MSI (RFC3161-timestamped when reachable).
.\signing\Build-Signed.ps1 -Version 1.5.7
```

Signing is opt-in at the MSBuild level: the installer's sign targets fire only
when `-p:SigningCertThumbprint` and `-p:SignToolPath` are supplied (the wrapper
does this). A **self-signed** cert is trusted only where its public certificate
has been imported into Trusted Root — it is not a substitute for a CA/EV cert for
public distribution, and does not earn SmartScreen reputation. The public
certificate is committed at `signing/serenity-software.cer`; private keys
(`*.pfx`/`*.p12`) are git-ignored and never leave your certificate store.

Give each installed build a **distinct** version — Windows Installer can't tell
two builds apart if they share a `ProductVersion`, so a same-version reinstall
may silently keep the old bits. The tray shows its running version in the window
title to make this unambiguous.

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

## What's new in 1.6.0

- **Output capture** — add `/capture` to the CLI to stream the elevated child's stdout and stderr
  back through the named pipe to your shell. No more write-to-file workarounds for scripts: the
  CLI blocks until the child exits and prints its output directly. Combine with `/timeout:N`
  (seconds) to put a hard ceiling on how long to wait; on timeout the stream closes but the child
  is left running on the desktop.
- **SYSTEM token validation** — the *Tools → Validate Installation* dialog now runs a fourth check:
  acquiring and releasing a plain LocalSystem (SYSTEM) token, confirming the `account=system`
  launch path works end-to-end in addition to the existing TrustedInstaller check.

## What's new in 1.5.7

- **Optional certificate trust at install** — the installer bundles the public
  *Serenity Software* code-signing certificate and adds an off-by-default checkbox
  to import it into the machine's Trusted Root store, so the app's signature reads
  as a verified publisher. Opt in with the checkbox or `INSTALLCERT=1`; it is left
  in place on uninstall. A self-signed root is a machine-wide trust change, so it
  stays opt-in.

## What's new in 1.5.6

- **Quick-run redesign** — the one-off launcher is now priority + path + **Browse…**
  on one row, with explicit **Run as TrustedInstaller** and **Run as SYSTEM**
  buttons below (the account is chosen by which button you click, replacing the
  old account dropdown). Both buttons carry the UAC shield. The path box keeps a
  fixed right margin at any window width (sized explicitly rather than via a
  `Left|Right` anchor, which `AutoScaleMode.Font` layout was overriding).
- **Version in the title bar** — the window title reads `RunAS Helper - vX.Y.Z`
  so it is always obvious which build is running.
- **Self-signed code signing (opt-in)** — a build pipeline that Authenticode-signs
  both EXEs and the MSI under the **Serenity Software** publisher, with the author
  recorded in the binaries' file metadata. See *Signed build* under *Build from
  source*. Unsigned builds remain fully supported.

## What's new in 1.5.0

- **Command-line launch notifications** — while the tray is running it subscribes to the service's structured event log and pops a tray balloon for every *command-line*-sourced launch (showing the command). An escalation made through the open CLI gate that you didn't initiate is flagged immediately. Runs even on the non-elevated tray; best-effort, so it silently no-ops if the Application log can't be subscribed to.

## What's new in 1.4.1–1.4.2

- **Install-path tray identity** (1.4.1) — the service identifies the tray by image path (the installed `RunAsHelper.exe` next to the service) instead of an Authenticode signature, so unsigned local/CI builds work again. A signature, when present, is recorded as diagnostics only (publisher pinning is tracked under *Planned features*).
- **Launch-target PATH resolution** (1.4.2) — targets with arguments that live outside `System32` (e.g. `powershell.exe`) now resolve via PATH and launch correctly. Previously only `System32`-resident targets like `cmd.exe` worked once arguments were present.
- **Git-tag auto-versioning** — a plain local `dotnet build` now stamps the current git tag (e.g. `1.4.2`) into the binaries and MSI instead of `1.0.0.0`, so a locally built MSI can upgrade an installed release.

## What's new in 1.4.0

- **Window foreground fix** — launched processes now reliably appear in the foreground instead of opening behind existing windows. The service sends the new process's PID to the tray, which calls `AllowSetForegroundWindow` before acknowledging the launch.
- **Install-path pipe gating** — the service identifies the tray by image path: the connecting binary must be `RunAsHelper.exe` installed alongside the service. (1.4.0 briefly required an Authenticode *signature* here; 1.4.1 replaced that so unsigned local/CI builds work — a signature, when present, is now optional diagnostics, with publisher pinning tracked under *Planned features*.) A renamed or relocated copy is rejected.
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
- **Publisher pinning** — signing now exists (opt-in, self-signed *Serenity
  Software*; see *Signed build*), but the pipe gate still identifies the tray by
  image path only. Remaining work: a trusted/purchased cert for public
  distribution, and wiring the pipe check to additionally require a valid
  signature from a pinned publisher (the service already records whether a caller
  is signed; today that is diagnostics only).
- **CLI gate auto-expiry** — when "Allow command line" is enabled, start a
  countdown (default 60 min) after which the service auto-closes the gate, so an
  open gate isn't accidentally left enabled for a long-running tray session. The
  service enforces the timeout; the tray mirrors it (auto-unchecks + toast).

## License

[MIT](LICENSE)
