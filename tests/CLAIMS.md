# Claims ledger

Every sentence in README.md and HelpText.cs that asserts a behavior maps here to the
`file:line` that makes it true and to the case that exercised it. A claim with no anchor,
or an anchor that does not say what the claim says, is a bug.

Legend: VERIFIED (anchor checked against the code and a case passed against the installed
integrated build, 2.2.90, on 2026-09-29), RELEASE (holds for the signed release build; the
dev build skips it), CODE (verified by reading the code; no live case drives it).

## README.md claims

| # | Claim | Anchor | Evidence | Status |
|---|---|---|---|---|
| C1 | RunAsHelper.exe is a GUI-subsystem binary; a shell does not wait for it and gets no output | `RunAsHelper/RunAsHelper.csproj:3` OutputType WinExe; PE subsystem 2 | A1, A5, R2 | VERIFIED |
| C2 | RunAsHelper.com is a console-subsystem launcher; the shell waits and $LASTEXITCODE is set | `RunAsHelper.Launcher/RunAsHelper.Launcher.csproj` OutputType Exe; `RunAsHelper.Launcher/Program.cs` RunConsole (wait + GetExitCodeProcess) | A1 (subsystem 3), A2, B1 | VERIFIED |
| C3 | The launcher sets __COMPAT_LAYER=RunAsInvoker so the child runs at the caller's level | `RunAsHelper.Launcher/Program.cs:74-75`, `LauncherLogic.MergeCompatLayer` | A4 (exit 1, not 740), PROBE-RESULTS | VERIFIED |
| C4 | A direct exe call without the launcher fails Win32 740 from a non-elevated shell | `RunAsHelper/app.manifest:7` highestAvailable | A5, R2 | VERIFIED |
| C5 | Installing puts the folder on the machine PATH; a new terminal is needed | `RunAsHelper.Installer/Package.wxs:170` Environment RunAsHelperPath; the PATH refresh rule in tests/README.md | A3, A11, install cycle I5 (after the harness fix); machine PATH kind stays REG_EXPAND_SZ | VERIFIED |
| C6 | Uninstall removes the PATH entry and keeps the trusted list | `Package.wxs` PathComponent (Permanent=no, `-` prefix); AllowedCallerSids is not MSI-owned | install cycle U4, U5 | VERIFIED |
| C7 | With /capture the exit code is the child's own; timeout is 124; RunAsHelper failures are 1 | `RunAsHelper/Core/CliLaunchResult.cs:10` (124) and ToProcessExitCode; `PipeServer.cs:974` exit frame | B1 (3, 7, 0), B2 (124), B4/B5 (1) | VERIFIED |
| C8 | For a .ps1 target the host is /ps:, then #Requires, then caller shell, then 5.1 | `RunAsHelper/Core/PowerShellHost.cs:87-100` Resolve; `RunAsHelper/Core/CallerShell.cs:17` skip list | B8 rows (pwsh, 5.1, /ps:5, /ps:7 via #Requires, Desktop, cmd default), unit tests | VERIFIED |
| C9 | The same command picks a different host from pwsh than from cmd; /ps:5 pins 5.1 | same as C8 | B8 rows 1, 3 and the cmd row | VERIFIED |
| C10 | /trusted, /trusted:add, /trusted:remove manage trusted users from an elevated shell | `RunAsHelper/Program.cs:78` dispatch, `:304` RunTrustedCommand | B4 (remove, add), B10 (list, group refused, Medium denied), R7 | VERIFIED |
| C11 | The service refuses to start from a shell (exit 1, one line) | `RunAsHelper.Service/Program.cs:13` IsWindowsService guard | A9 | VERIFIED |
| C12 | Unknown verbs are rejected, not run as a launch | `RunAsHelper.Service/Worker/PipeServer.cs:731-733` | no live case: the shipped client never sends an unknown verb; verified by reading the dispatch | CODE |
| C13 | Project status is tracked in BACKLOG.md | BACKLOG.md | file present | VERIFIED |
| C14 | Signing covers all three binaries and the release verifies all four | `RunAsHelper.Installer.wixproj:88` SignPublishedApps; `release.yml:167` verify list; slice A's local signed build (Valid, timestamped .com) | A13 skips on the unsigned dev build; runs on the released MSI | RELEASE |
| C15 | RunAsHelper.Shared is linked source, not a built assembly | `RunAsHelper/RunAsHelper.csproj:17-18` and the service csproj Compile Include; no csproj in the folder | Release build after the delete | VERIFIED |
| C16 | Captured cmd / 5.1 output decodes correctly (OEM fallback) | `RunAsHelper.Service/Core/CaptureDecoder.cs` | CaptureDecoderTests (unit); live capture output in R3/B1 is ASCII | VERIFIED (unit) |
| C17 | A Ctrl+C in the launcher ends the exe and exits 0xC000013A; the elevated target may keep running | `RunAsHelper.Launcher/Program.cs` OnCtrl | A7 | VERIFIED |

## HelpText.cs claims

| # | Claim | Anchor | Evidence | Status |
|---|---|---|---|---|
| H1 | Help names RunAsHelper.com and explains .com vs .exe | `RunAsHelper/HelpText.cs` OVERVIEW and SCRIPTING notes | HelpTextTests green (86 tests) | VERIFIED |
| H2 | Help documents /trusted, /ps:, the exit codes incl. 124 | `RunAsHelper/HelpText.cs` COMMAND LINE | HelpTextTests | VERIFIED |
| H3 | No example line tells a shell user to type RunAsHelper.exe | `RunAsHelper/HelpText.cs` EXAMPLES | HelpTextTests Cli_HasNoBareExeExampleLine | VERIFIED |
| H4 | Every switch Program.cs parses appears in help | `RunAsHelper/HelpText.cs` vs `RunAsHelper/Program.cs` | HelpTextTests Cli_DocumentsEveryParsedSwitch | VERIFIED |
| H5 | "/timeout: the child is left running (see /jobs) and the CLI exits 124" | `PipeServer.cs` timeout path (no termination), `CliLaunchResult.cs:10` | B2, and the leftover ping visible to Get-ServiceChildren until it ends | VERIFIED |
| H6 | "/jobs, /kill, /joblog and /trusted need the installed RunAsHelper running elevated" | `PipeServer.cs` verb guards (isTrayElevated) | A4, B6, B10, R5, R6, R13 | VERIFIED |
