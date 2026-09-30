# RunAS Helper backlog

Every item has a disposition with evidence. `Open` lists what is actionable now and
must be empty at the end of a release; everything else is closed with proof.

Dispositions:

- **FIXED** - a code change, with a test and the mutation that test catches. In this
  release many fixes are `FIXED (pending integration)`: the code lands on a slice
  branch and the fix is real, but the proof completes when the integrated build is
  installed and the suites run against it. The owning slice is named.
- **CLOSED-VERIFIED** - not a problem; evidence that it was already correct.
- **DECLINED-MEASURED** - real but not worth doing now; the measurement stays on the record.
- **ACCEPTED-RECORDED** - real, deliberately not done now, with the trigger that reopens it.
- **OPEN** - actionable now.

Columns: `ID | Item | Where (file:line) | Source | Disposition | Evidence | Notes/Trigger`.

## Open

_(empty)_

## Dispositions (v2.3.0)

| ID | Item | Where | Source | Disposition | Evidence | Notes/Trigger |
|---|---|---|---|---|---|---|
| BL-01 | No publicly trusted code-signing certificate (self/Serenity cert) | README Project status | owner | ACCEPTED-RECORDED | Purchase pending; project_code_signing memory | Reopens when a public cert is bought |
| BL-02 | Unexplained 0xe0434352 crash reported historically | README Project status | owner | CLOSED-VERIFIED | Get-WinEvent RunAsHelper id 1099: exactly two events, both 2026-08-17 (the v1.8.0 ImageList/Icon and ListView bugs, already fixed), none since; no %AppData%\RunAsHelper\crash.log | Reopens on any new 1099 |
| BL-03 | ActivateElevation does not confirm the elevated copy started | MainForm.cs ActivateElevation | owner | ACCEPTED-RECORDED | Plan decision 19: touches the single-instance mutex, only manually testable | Reopens if the lead adds the 5 s wait to slice B |
| BL-04 | No console launcher (.com) | console-launcher-plan.md | plan | FIXED (pending integration) | slice A: RunAsHelper.Launcher; A1/A2 | Integration proves the installed .com |
| BL-05 | Install folder not on PATH | console-launcher-plan.md | plan | FIXED (pending integration) | slice A: Package.wxs Environment row; A11 | |
| BL-06 | The .com is not signed | console-launcher-plan.md | plan | FIXED (pending integration) | slice A: SignPublishedApps + release.yml; A13 | |
| BL-07 | Help and README still say RunAsHelper.exe | console-launcher-plan.md | plan | FIXED (pending integration) | slice C README + slice B HelpText; HelpTextTests | HelpTextTests RED now, green at integration |
| BL-08 | No .ps1 host rule | console-launcher-plan.md | plan | FIXED (pending integration) | slice B: PowerShellHost/CallerShell; B8, U4 | |
| BL-09 | 5.1 caller gotchas ($PSScriptRoot in param defaults, PS7 syntax) | console-launcher-plan.md:106-110 | plan | FIXED (pending integration) | slice C README note | |
| BL-10 | Framing race: pump vs timeout line unsynchronized | PipeServer.cs / PipeProtocol.cs WriteFrameAsync | audit | FIXED (pending integration) | slice B: SerializedFrameWriter + one-buffer WriteFrame; U3 + control | |
| BL-11 | /timeout returns Success/exit 0 | PipeServer.cs timeout path | audit | FIXED (pending integration) | slice B: 124; B2 (2.2.0 returns 0, B2 fails baseline) | |
| BL-12 | /joblog exits 0 on a missing job | Program.cs:208-211 | audit | FIXED (pending integration) | slice B; B6 | |
| BL-13 | Unknown verbs fall through to a launch | PipeServer.cs verb dispatch | audit | FIXED (pending integration) | slice B: reject + result Failed + Event 1003 | |
| BL-14 | Identity mismatch not event-logged | PipeServer.cs:495-500 | audit | FIXED (pending integration) | slice B: Event 1003 with reason | EventLogHelper already claimed it |
| BL-15 | Dead P/Invokes CreatePipe / SetHandleInformation | RunAsHelper.Service/Core/NativeMethods.cs | audit | FIXED (pending integration) | slice B: delete-and-build | |
| BL-16 | Stale "self-contained" / "1001-1005" text | Package.wxs, release.yml, THIRD_PARTY_NOTICES.md, CrashLogger.cs | audit | FIXED (pending integration) | slice A (wxs/yml), slice C (notices) | CrashLogger.cs is slice B |
| BL-17 | Orphan RunAsHelper.Shared.csproj; README calls it a library | sln has no entry | audit | FIXED | slice C deleted the csproj (build-succeeds proof this session); README says linked source | |
| BL-18 | README refers to a heading "Signed build" that does not exist | README:901-902 | audit | FIXED | slice C: points to "Signing" under "Build from source" | |
| BL-19 | Capture decodes OEM output as UTF-8 (garbled accented text) | PipeServer.cs capture StreamReader; box OEMCP 437 | audit | FIXED (pending integration) | slice B: CaptureDecoder (UTF-8 strict, else OEM); unit test | Lead may instead ACCEPT-RECORD with the chcp 65001 workaround |
| BL-20 | targetSessionId never supplied (always active console session) | ElevationLauncher.cs | audit | ACCEPTED-RECORDED | Single-session box | Reopens on an RDP/multi-session report |
| BL-21 | WaitForSingleObject blocks a pool thread per capture | PipeServer.cs capture wait | audit | DECLINED-MEASURED | Bounded by 10 launch slots; saturation probe in Invoke-AuditProbes | Reopens if slot count is raised a lot |
| BL-22 | Service exe becomes typeable once the folder is on PATH | RunAsHelper.Service/Program.cs | audit | FIXED (pending integration) | slice A guard; A9 | |
| BL-23 | No tests, no CI test step | - | owner | FIXED (pending integration) | slice C: tests/, RunAsHelper.Tests, ci.yml; this session's runs | ci.yml runs once the lead pushes |
| BL-24 | uninstall.py does not remove the machine PATH entry on a manual scrub | uninstall.py | audit | FIXED (pending integration) | slice A: PATH removal in the scrub | |
| BL-25 | /capture cannot distinguish "child exited 1" from RunAsHelper's own failure | design 4 | owner | ACCEPTED-RECORDED | Plan decision 7: own failure stays 1, documented | Reopens if a distinct code is adopted |
| BL-26 | No CLI gate verb (/gate:on|off); the "gate open, interactive caller" row is GUI-only to automate | trusted-callers-test-plan.md | idea | ACCEPTED-RECORDED | Idea only | Reopens if headless gate control is wanted |
| BL-27 | Em dash in service log lines is written as one byte 0x97 (Windows-1252) to a pipe, shown as a replacement char in a UTF-8 reader | service log prose; PROBE-RESULTS.md encoding finding | Phase 0 | FIXED (pending integration) | slice B: ASCII-only service log prose; observed in R3 capture this session | Cheapest durable fix per the plan |
| BL-28 | ConPTY children inherit a redirected stdout instead of the pseudo console when the harness process's own stdout is a pipe | tests/lib/ConPty.ps1; PROBE-RESULTS.md harness lesson | Phase 0 | CLOSED-VERIFIED | Harness records harness-stdout-is-console and re-launches through a hidden console; verified True this session (rendered CONPTY-OK, exit 7) | A run with False there is a harness failure |

## Not testable on this box (recorded)

| ID | Gap | Why | Record |
|---|---|---|---|
| NT-01 | Standard-user / explicitly denied-user rows | No second account; creating one is a machine change | Substitute: the SID-removed window (/trusted:remove then re-add) proves the same authorization predicate |
| NT-02 | Remote pipe access | Single machine | Deny-NETWORK ACE checked locally with accesschk -nobanner \pipe\RunAsHelper |
| NT-03 | Restricted token, 128-entry limit, domain user, PID-reuse churn | Needs token crafting / many users / a domain | Recorded |
| NT-04 | Machine without pwsh | pwsh is installed | Simulated only via a %ProgramFiles% env override (B8 row 10) |
| NT-05 | UAC-prompting machine, AppCompat disabled by policy | This box auto-consents; no GPO changes | Recorded; the launcher must print a clear message on a 740 |
| NT-06 | Windows Terminal as the terminal | Not installed | conhost backstop only |

## Machine state on the dev box

| MS | Change | Why | Reverse |
|---|---|---|---|
| MS-01 | Codex sandbox SIDs ...-1004 / ...-1005 are already trusted CLI callers | Pre-existing, found not changed | Surface to the owner; not removed |
| MS-02 | This account's SID ...-1000 was added to AllowedCallerSids (Phase 0) | Authorizes non-elevated calls for the suites | RunAsHelper /trusted:remove S-1-5-21-...-1000 from an elevated shell |
| MS-03 | Install folder goes on the machine PATH; version 2.2.0 -> 2.2.9x -> 2.3.0; service restarts; tray stops/starts | Testing and release (integration, not Phase 1) | Uninstall removes PATH; 2.2.0 MSI is backed up; tray left not running |
