# RunAS Helper backlog

Every item has a disposition with evidence. `Open` lists what is actionable now and
must be empty at the end of a release; everything else is closed with proof.

Dispositions:

- **FIXED** - a code change, with the test that proves it and the mutation that test
  catches (tests/MUTATIONS.md).
- **CLOSED-VERIFIED** - not a problem; evidence that it was already correct.
- **DECLINED-MEASURED** - real but not worth doing now; the measurement stays on the record.
- **ACCEPTED-RECORDED** - real, deliberately not done now, with the trigger that reopens it.
- **OPEN** - actionable now.

Columns: `ID | Item | Where (file:line) | Source | Disposition | Evidence | Notes/Trigger`.

## Open

_(empty)_

## Dispositions (v2.3.0)

Evidence names cases from tests/ (A*, B*, R*, I*, U*, T*, M*, P*) run against the
installed integrated build 2.2.90 on 2026-09-29, unit tests (86, all green), and the
2.2.0 baseline runs recorded in tests/MUTATIONS.md.

| ID | Item | Where | Source | Disposition | Evidence | Notes/Trigger |
|---|---|---|---|---|---|---|
| BL-01 | No publicly trusted code-signing certificate (self-signed Serenity cert) | README Project status | owner | ACCEPTED-RECORDED | Purchase pending; project_code_signing memory | Reopens when a public cert is bought |
| BL-02 | Unexplained 0xe0434352 crash reported historically | README Project status | owner | CLOSED-VERIFIED | Get-WinEvent RunAsHelper id 1099: exactly two events, both 2026-08-17 (the v1.8.0 ImageList/Icon and ListView bugs, fixed before 1.8.0 shipped), none since; no %AppData%\RunAsHelper\crash.log; R15 green | Reopens on any new 1099 |
| BL-03 | ActivateElevation does not confirm the elevated copy started | MainForm.cs ActivateElevation | owner | ACCEPTED-RECORDED | Plan decision 19: touches the single-instance mutex, only manually testable | Reopens if a user reports the tray vanishing on Activate |
| BL-04 | No console launcher (.com) | console-launcher-plan.md | plan | FIXED | RunAsHelper.Launcher; A1, A2, A6, A7, A8; PROBE-RESULTS | |
| BL-05 | Install folder not on PATH | console-launcher-plan.md | plan | FIXED | Package.wxs PathComponent; A3, A11, I5, U4 (removed on uninstall) | Machine PATH stays REG_EXPAND_SZ (checked) |
| BL-06 | The .com is not signed | console-launcher-plan.md | plan | FIXED | wixproj SignPublishedApps + release.yml verify list; slice A's local signed build: Valid, timestamped .com | A13 confirms on the released MSI |
| BL-07 | Help and README still say RunAsHelper.exe | console-launcher-plan.md | plan | FIXED | HelpTextTests green; README examples in the bare form | |
| BL-08 | No .ps1 host rule | console-launcher-plan.md | plan | FIXED | PowerShellHost.cs, CallerShell.cs; B8 (eight rows), PowerShellHostTests | |
| BL-09 | 5.1 caller gotchas ($PSScriptRoot in param defaults, PS7 syntax) | console-launcher-plan.md:110-114 | plan | FIXED | README and HelpText note 4 | |
| BL-10 | Framing race: pump vs timeout line unsynchronized | PipeServer.cs / PipeProtocol.cs | audit | FIXED | PipeMessageWriter + one-buffer WriteFrame; PipeMessageWriterTests (with the unserialized control); B7 soak 10/10 clean | |
| BL-11 | /timeout returns Success/exit 0 | PipeServer.cs timeout path | audit | FIXED | timeout frame + 124; B2 (2.2.0 baseline returned 0) | |
| BL-12 | /joblog exits 0 on a missing job | Program.cs | audit | FIXED | B6 | |
| BL-13 | Unknown verbs fall through to a launch | PipeServer.cs:731-733 | audit | FIXED | reject + result Failed + Event 1003; verified by code read (the shipped client sends no unknown verb) | |
| BL-14 | Identity mismatch not event-logged | PipeServer.cs:519 | audit | FIXED | Event 1003 "pipe and process identity differ"; code read (no live way to force a mismatch from the shipped client) | |
| BL-15 | Dead P/Invokes CreatePipe / SetHandleInformation | RunAsHelper.Service/Core/NativeMethods.cs | audit | FIXED | deleted; Release build green | |
| BL-16 | Stale "self-contained" / "1001-1005" text | Package.wxs, release.yml, THIRD_PARTY_NOTICES.md, CrashLogger.cs | audit | FIXED | all four updated | |
| BL-17 | Orphan RunAsHelper.Shared.csproj; README calls it a library | sln has no entry | audit | FIXED | csproj deleted, Release build green; README says linked source | |
| BL-18 | README refers to a heading "Signed build" that does not exist | README | audit | FIXED | prose points to "Signing" under "Build from source" | |
| BL-19 | Capture decodes OEM output as UTF-8 (garbled accented text) | PipeServer.cs capture pump | audit | FIXED | CaptureDecoder (strict UTF-8, else the OEM code page); CaptureDecoderTests | |
| BL-20 | targetSessionId never supplied (always the active console session) | ElevationLauncher.cs | audit | ACCEPTED-RECORDED | Single-session box | Reopens on an RDP or multi-session report |
| BL-21 | WaitForSingleObject blocks a pool thread per capture | PipeServer.cs capture wait | audit | DECLINED-MEASURED | Bounded by 10 slots; audit probe P2: ten concurrent captures complete and the 11th is told busy | Reopens if the slot count is raised a lot |
| BL-22 | Service exe becomes typeable once the folder is on PATH | RunAsHelper.Service/Program.cs:13 | audit | FIXED | A9 (exit 1 with the SCM text, service untouched) | |
| BL-23 | No tests, no CI test step | - | owner | FIXED | RunAsHelper.Tests (86), tests/ harness, ci.yml, release.yml test step | ci.yml proven on the first push |
| BL-24 | uninstall.py does not remove the machine PATH entry on a manual scrub | uninstall.py | audit | FIXED | remove_path_entry() added (not run: the MSI uninstall removes the entry, U4) | |
| BL-25 | /capture cannot distinguish "child exited 1" from RunAsHelper's own failure | exit-code design | owner | ACCEPTED-RECORDED | Plan decision 7: own failure stays 1, documented in README and help | Reopens if a distinct code is adopted |
| BL-26 | No CLI gate verb (/gate:on\|off); the "gate open, interactive caller" row is GUI-only to automate | trusted-callers-test-plan.md | idea | ACCEPTED-RECORDED | Idea only | Reopens if headless gate control is wanted |
| BL-27 | Em dash in service log lines written as one byte 0x97 (Windows-1252) to a pipe | service log prose; PROBE-RESULTS | Phase 0 | FIXED | ASCII-only service log prose (slice B); live output in B1/R3 is ASCII | |
| BL-28 | ConPTY children inherit a redirected stdout when the harness process's stdout is a pipe | tests/lib/ConPty.ps1 | Phase 0 | CLOSED-VERIFIED | Harness re-launches through a hidden console; A2/A7/A8 pass that way | A run with harness-stdout-is-console False is a harness failure |
| BL-29 | Start-Process -Wait also waits for descendants, so a test that starts the tray never returns | tests/Invoke-Smoke.ps1 relaunch | integration | FIXED | WaitForExit() on the child process; the first integration run hung until the parent was ended by hand | Recorded in tests/README.md |
| BL-30 | The elevated runner cannot wait on a GUI exe or read its exit code | tests/lib/Elevated.ps1 | integration | FIXED | elevated cases call RunAsHelper.com; the runner falls back to its own exit code instead of failing on an empty rc file; R5/R13 green after the fix | |
| BL-31 | MSI ProductVersion and File.Version are four-part; string comparison with "2.2.90" fails | tests/Invoke-MsiContent.ps1 | integration | FIXED | ConvertTo-FourPartVersion; M6/M7 green | |
| BL-32 | Capture mode sends the pid frame after the child has exited, so AllowSetForegroundWindow is granted to a dead PID | PipeServer.cs pid frame | slice B | ACCEPTED-RECORDED | Harmless (CREATE_NO_WINDOW child); moving it before the wait is a two-line change | Reopens if a captured GUI child ever needs foreground rights |
| BL-33 | Event 1002 does not carry the child's exit code | EventLogHelper.cs | slice B | ACCEPTED-RECORDED | The ILogger line carries it; a 1007 "job finished" event would change the 1001-1006 schema | Reopens on a SIEM request for exit codes |
| BL-34 | Client and service NativeMethods both declare OpenProcess/CloseHandle/QueryFullProcessImageNameW | RunAsHelper/Core/NativeMethods.cs, RunAsHelper.Service/Core/NativeMethods.cs | slice B | DECLINED-MEASURED | Three duplicated declarations; a shared linked native file would save ~40 lines and couple two projects that today share only the protocol | Reopens if a third project needs them |
| BL-35 | A stalled reader of the launcher's stdout holds the service's pump, so the timeout frame waits for the reader | PipeServer.cs writer + client pipe buffers | integration | CLOSED-VERIFIED | Backpressure by design, unchanged from 2.2.0; B7 with a steady stream is clean, and the harness reads at line rate | Document, do not "fix" |
| BL-36 | tests/Invoke-Regression R7 used a verb that never existed (/listtrustedcallers as a CLI switch) | tests/Invoke-Regression.ps1 | integration | FIXED | rewritten on /trusted, tagged changed-in-2.3.0; R7 green | Found because needs-elevated cases first ran at integration |

## Not testable on this box (recorded)

| ID | Gap | Why | Record |
|---|---|---|---|
| NT-01 | Standard-user / explicitly denied-user rows | No second account; creating one is a machine change | B4 proves the same authorization predicate for this user: with its SID removed the launch is refused with "Command line is disabled", and allowed again after re-add |
| NT-02 | Remote pipe access | Single machine | Deny-NETWORK ACE is in the pipe DACL (PipeServer.cs CreatePipe) |
| NT-03 | Restricted token, 128-entry limit, domain user, PID-reuse churn | Needs token crafting / many users / a domain | Recorded |
| NT-04 | Machine without pwsh | pwsh is installed | The resolver's fallback (5.1 plus a warning line) is covered by PowerShellHostTests Resolve_FallsBackTo51_WhenPwshMissing |
| NT-05 | UAC-prompting machine, AppCompat disabled by policy | This box auto-consents; no GPO changes | Recorded; the launcher prints the Win32 message and exits with the error code if CreateProcess fails |
| NT-06 | Windows Terminal as the terminal | Not installed | conhost via ConPTY only |
| NT-07 | Tray saved-entry .ps1 host rule, the /validate dialog, dark mode | GUI | Screenshot of the running 2.2.90 tray taken at integration (dark theme, version title); the .ps1 rewrite is shared with the CLI path (PipeClient.SendAsync) and covered by B8 |

## Machine state on the dev box

| MS | Change | Why | Reverse |
|---|---|---|---|
| MS-01 | Codex sandbox SIDs ...-1004 / ...-1005 (CodexSandboxOffline/Online) are trusted CLI callers | Pre-existing, found not changed | Owner decision; `RunAsHelper /trusted:remove <SID>` from an elevated shell |
| MS-02 | This account's SID ...-1000 was added to AllowedCallerSids (Phase 0) | Authorizes non-elevated calls for the suites and for agents | `RunAsHelper /trusted:remove <SID>` from an elevated shell |
| MS-03 | Install folder is on the machine PATH; installed version 2.2.0 -> 2.2.90 (dev) -> 2.3.0 at release; the tray runs elevated after the suites | Integration and release | Uninstall removes PATH; the 2.2.0 MSI is backed up at D:\.ai-work\_backups\runas-helper-2.2.0-baseline; close the tray from its menu |
