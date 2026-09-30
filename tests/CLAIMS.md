# Claims ledger

Every sentence in README.md and HelpText.cs that asserts a behavior maps here to the
`file:line` that makes it true. A claim with no anchor, or an anchor that does not say
what the claim says, is a bug. Rows for text slice A or B owns are marked TODO with the
owning slice; the lead fills them at integration once A/B land their wording.

Legend: VERIFIED (anchor checked against code this session), TODO-A / TODO-B (text or
code owned by another slice), INTEGRATION (holds only once the integrated build exists).

## README.md claims (slice C owns the README prose)

| # | Claim | Anchor | Status |
|---|---|---|---|
| C1 | RunAsHelper.exe is a GUI-subsystem binary; a shell does not wait for it and gets no output | RunAsHelper/RunAsHelper.csproj OutputType WinExe; PE subsystem 2 (Get-PeSubsystem, A1) | VERIFIED |
| C2 | RunAsHelper.com is a console-subsystem launcher; the shell waits and $LASTEXITCODE is set | RunAsHelper.Launcher (design 1); PE subsystem 3 (A1) | TODO-A (project) / INTEGRATION |
| C3 | The launcher sets __COMPAT_LAYER=RunAsInvoker so the child runs at the caller's level | design 1; PROBE-RESULTS A4/A5 (740 without, runs with) | TODO-A / VERIFIED (probe) |
| C4 | A direct exe call without the launcher fails Win32 740 from a non-elevated shell | RunAsHelper/app.manifest (highestAvailable); R2/A5 | VERIFIED |
| C5 | Installing puts the folder on the machine PATH; a new terminal is needed | Package.wxs PathComponent Environment row (design 2); A11 | TODO-A / INTEGRATION |
| C6 | Uninstall removes the PATH entry and keeps the trusted list | Package.wxs Environment Action; A12 | TODO-A / INTEGRATION |
| C7 | With /capture the exit code is the child's own; timeout is 124; RunAsHelper failures are 1 | CliExitCode.Compute (design 4); B1/B2 | TODO-B / VERIFIED-BASELINE (2.2.0 returns 0, B1/B2 fail) |
| C8 | For a .ps1 target the host is /ps:, then #Requires, then caller shell, then 5.1 | PowerShellHost/CallerShell (design 3); B8, U4 | TODO-B |
| C9 | The same command picks a different host from pwsh than from cmd; /ps:5 pins 5.1 | design 3 / decision 1; B8 rows 1,7,3 | TODO-B |
| C10 | /trusted, /trusted:add, /trusted:remove manage trusted users from an elevated shell | Program.cs (design 5); B10 | TODO-B |
| C11 | The service refuses to start from a shell (exit 1, one line) | RunAsHelper.Service/Program.cs guard (design 2); A9 | TODO-A / INTEGRATION |
| C12 | Unknown verbs are rejected, not run as a launch | PipeServer.cs (design 6, BL-13); B (server) | TODO-B |
| C13 | Project status is tracked in BACKLOG.md | BACKLOG.md (this slice) | VERIFIED |
| C14 | Signing covers all three binaries | signing/Build-Signed.ps1, release.yml (design 2); A13 | TODO-A / INTEGRATION |
| C15 | RunAsHelper.Shared is linked source, not a built assembly | RunAsHelper.csproj / Service csproj Compile Include of Protocol/*.cs; no csproj in the folder | VERIFIED |

## HelpText.cs claims (slice B owns HelpText.cs)

| # | Claim | Anchor | Status |
|---|---|---|---|
| H1 | Help names RunAsHelper.com and explains .com vs .exe | HelpText.cs (rewrite) | TODO-B; HelpTextTests asserts it (RED now) |
| H2 | Help documents /trusted, /ps:, the exit codes incl. 124 | HelpText.cs | TODO-B; HelpTextTests asserts it (RED now) |
| H3 | Help has no example line telling a shell user to type RunAsHelper.exe | HelpText.cs EXAMPLES | TODO-B; HelpTextTests Cli_HasNoBareExeExampleLine asserts it (RED now) |
| H4 | Every switch Program.cs parses appears in help | HelpText.cs vs Program.cs switch parsing | TODO-B; HelpTextTests Cli_DocumentsEveryParsedSwitch (7 of 9 green now) |
