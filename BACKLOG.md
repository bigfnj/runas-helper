# RunAS Helper backlog

Every item has a disposition with evidence. `Open` lists what is actionable now and
must be empty at the end of a release; everything else is closed with proof.

Dispositions:

- **FIXED** - a code change, with the test that proves it and the mutation that test
  catches (tests/MUTATIONS.md). Where no test can reach the code (a Win32 lifetime path with
  no user-mode trigger), the row says "code read" and names the live case that covers the
  surrounding behavior.
- **CLOSED-VERIFIED** - not a problem; evidence that it was already correct.
- **DECLINED-MEASURED** - real but not worth doing now; the measurement stays on the record.
- **ACCEPTED-RECORDED** - real, deliberately not done now, with the trigger that reopens it.
- **OPEN** - actionable now.

Columns: `ID | Item | Where (file:line) | Source | Disposition | Evidence | Notes/Trigger`.

## Open

_(empty)_

## Dispositions (v2.3.0)

Evidence names cases from tests/ (A*, B*, R*, I*, U*, T*, M*, P*, H*) run against the
installed builds on 2026-09-29/30, unit tests (105 at v2.3.1, all green), and the 2.2.0
baseline runs recorded in tests/MUTATIONS.md. "Probe result" means the Phase 0 section of
docs/console-launcher-plan.md; "decision N" means the numbered list in the same file.

| ID | Item | Where | Source | Disposition | Evidence | Notes/Trigger |
|---|---|---|---|---|---|---|
| BL-01 | No publicly trusted code-signing certificate (self-signed Serenity cert) | README Project status | owner | ACCEPTED-RECORDED | Purchase pending; project_code_signing memory | Reopens when a public cert is bought |
| BL-02 | Unexplained 0xe0434352 crash reported historically | README Project status | owner | CLOSED-VERIFIED | Event 1099: exactly two events, both 2026-08-17 (the v1.8.0 ImageList/Icon and ListView bugs, fixed before 1.8.0 shipped), none since; no %AppData%\RunAsHelper\crash.log; R15 green on every run | Reopens on any new 1099 |
| BL-03 | ActivateElevation does not confirm the elevated copy started | MainForm.cs ActivateElevation | owner | ACCEPTED-RECORDED | Owner decision 2026-09-29 (not one of the numbered plan decisions): touches the single-instance mutex, only manually testable | Reopens if a user reports the tray vanishing on Activate |
| BL-04 | No console launcher (.com) | console-launcher-plan.md | plan | FIXED | RunAsHelper.Launcher; A1, A2, A6, A7, A8; probe result | |
| BL-05 | Install folder not on PATH | console-launcher-plan.md | plan | FIXED | Package.wxs PathComponent; A3, A11, I5, U4 (removed on uninstall) | Machine PATH stays REG_EXPAND_SZ (checked) |
| BL-06 | The .com is not signed | console-launcher-plan.md | plan | FIXED | wixproj SignPublishedApps + release.yml verify list; V3/V5 and A13 on the released 2.3.0 | |
| BL-07 | Help and README still say RunAsHelper.exe | console-launcher-plan.md | plan | FIXED | HelpTextTests green; README examples in the bare form | |
| BL-08 | No .ps1 host rule | console-launcher-plan.md | plan | FIXED | PowerShellHost.cs, CallerShell.cs; B8 (eight rows), PowerShellHostTests | |
| BL-09 | 5.1 caller gotchas ($PSScriptRoot in param defaults, PS7 syntax) | console-launcher-plan.md | plan | FIXED | README and HelpText note 4 | |
| BL-10 | Framing race: pump vs timeout line unsynchronized | PipeServer.cs / PipeProtocol.cs | audit | FIXED | PipeMessageWriter + one-buffer WriteFrame; PipeMessageWriterTests (with the unserialized control); B7 soak clean | |
| BL-11 | /timeout returns Success/exit 0 | PipeServer.cs timeout path | audit | FIXED | timeout frame + 124; B2 (2.2.0 baseline returned 0) | |
| BL-12 | /joblog exits 0 on a missing job | Program.cs | audit | FIXED | B6 | |
| BL-13 | Unknown verbs fall through to a launch | PipeServer.cs verb dispatch | audit | FIXED | smoke B13 (tests/lib/RawPipe.ps1 sends Verb "nonsense": log "Unknown request", result Failed, Event 1003, no 1001); mutation Verb "launch" runs the command (tests/MUTATIONS.md, v2.3.1) | |
| BL-14 | Identity mismatch not event-logged | PipeServer.cs identity check | audit | ACCEPTED-RECORDED | Code writes Event 1003 through EventLogHelper.IdentityMismatch (v2.3.1 wording says the request continues); no user-mode way to produce a pipe/process SID mismatch, so it is code read only | Reopens when a mismatch can be produced from user mode |
| BL-15 | Dead P/Invokes CreatePipe / SetHandleInformation | RunAsHelper.Service/Core/NativeMethods.cs | audit | FIXED | deleted; Release build green | |
| BL-16 | Stale "self-contained" / "1001-1005" text | Package.wxs, release.yml, THIRD_PARTY_NOTICES.md, CrashLogger.cs | audit | FIXED | all four updated; audit re-check clean | |
| BL-17 | Orphan RunAsHelper.Shared.csproj; README calls it a library | sln has no entry | audit | FIXED | csproj deleted, Release build green; README says linked source | |
| BL-18 | README refers to a heading "Signed build" that does not exist | README | audit | FIXED | prose points to "Signing" under "Build from source" | |
| BL-19 | Capture decodes OEM output as UTF-8 (garbled accented text) | PipeServer.cs capture pump | audit | FIXED | CaptureDecoder (strict UTF-8, else the OEM code page); CaptureDecoderTests | |
| BL-20 | targetSessionId never supplied (always the active console session) | ElevationLauncher.cs | audit | ACCEPTED-RECORDED | Single-session box; audit L2-13 re-confirmed | Reopens on an RDP or multi-session report |
| BL-21 | WaitForSingleObject blocks a pool thread per capture | PipeServer.cs capture wait | audit | FIXED | v2.3.1 uses a registered thread-pool wait (CaptureRelay); P2 saturation still passes | Was DECLINED-MEASURED at 2.3.0 |
| BL-22 | Service exe becomes typeable once the folder is on PATH | RunAsHelper.Service/Program.cs | audit | FIXED | A9 (exit 1 with the SCM text, service untouched) | |
| BL-23 | No tests, no CI test step | - | owner | FIXED | RunAsHelper.Tests (105), tests/ harness, ci.yml green on main, release.yml test step, a deliberately red branch proved the gate | |
| BL-24 | uninstall.py does not remove the machine PATH entry on a manual scrub | uninstall.py | audit | FIXED | remove_path_entry() added; dry run 2026-09-29 (tests/MUTATIONS.md, v2.3.1): with DRY_RUN it changes nothing, and a read-only replay of its rule against the live machine PATH (43 entries, REG_EXPAND_SZ) would remove exactly "C:\Program Files\RunAsHelper\" and keep the other 42; a real scrub has not been run | Reopen if a real scrub misbehaves; caveats: the InstallFolder value is deleted before the PATH step (the step uses the constant), and %ProgramFiles% under 32-bit Python |
| BL-25 | /capture cannot distinguish "child exited 1" from RunAsHelper's own failure | exit-code design | owner | ACCEPTED-RECORDED | Plan decision 6: own failure stays 1, documented in README and help | Reopens if a distinct code is adopted |
| BL-26 | No CLI gate verb (/gate:on\|off); the "gate open, interactive caller" row is GUI-only to automate | trusted-callers-test-plan.md | idea | ACCEPTED-RECORDED | Idea only | Reopens if headless gate control is wanted |
| BL-27 | Em dash in service log lines written as one byte 0x97 (Windows-1252) to a pipe | service log prose; probe result | Phase 0 | FIXED | ASCII-only service log prose; v2.3.1 SourceHygieneTests keeps every pipe and console string ASCII | |
| BL-28 | ConPTY children inherit a redirected stdout when the harness process's stdout is a pipe | tests/lib/ConPty.ps1 | Phase 0 | CLOSED-VERIFIED | Harness re-launches through a hidden console; A2/A7/A8 pass that way | A run with harness-stdout-is-console False is a harness failure |
| BL-29 | Start-Process -Wait also waits for descendants, so a test that starts the tray never returns | tests/Invoke-Smoke.ps1 relaunch | integration | FIXED | WaitForExit() on the child process | Recorded in tests/README.md |
| BL-30 | The elevated runner cannot wait on a GUI exe or read its exit code | tests/lib/Elevated.ps1 | integration | FIXED | elevated cases call RunAsHelper.com; the runner falls back to its own exit code; R5/R13 green | |
| BL-31 | MSI ProductVersion and File.Version are four-part; string comparison with "2.2.90" fails | tests/Invoke-MsiContent.ps1 | integration | FIXED | ConvertTo-FourPartVersion; M6/M7 green | |
| BL-32 | Capture mode sends the pid frame after the child has exited, so AllowSetForegroundWindow is granted to a dead PID | PipeServer.cs pid frame | slice B | ACCEPTED-RECORDED | Harmless (CREATE_NO_WINDOW child) | Reopens if a captured GUI child ever needs foreground rights |
| BL-33 | Event 1002 does not carry the child's exit code | EventLogHelper.cs | slice B | ACCEPTED-RECORDED | The ILogger line carries it; a 1007 event would change the 1001-1006 schema | Reopens on a SIEM request for exit codes |
| BL-34 | Client and service NativeMethods both declare OpenProcess/CloseHandle/QueryFullProcessImageNameW | both NativeMethods.cs | slice B | DECLINED-MEASURED | Three duplicated declarations (~40 lines) for two projects that share only the protocol | Reopens if a third project needs them |
| BL-35 | A stalled reader of the launcher's stdout holds the service's pump | PipeServer.cs writer + client pipe buffers | integration | CLOSED-VERIFIED | Backpressure by design, unchanged from 2.2.0; B7 clean; v2.3.1 additionally detaches when the client is gone (H1) | Document, do not "fix" |
| BL-36 | Regression R7 used a verb that never existed (/listtrustedcallers as a CLI switch) | tests/Invoke-Regression.ps1 | integration | FIXED | rewritten on /trusted; R7 green | |

## Audit of v2.3.0 (nine agents, 67 findings; dispositions shipped in v2.3.1)

Finder lanes L1 (regressions, non-operable), L2 (dead code, calls that go nowhere), L3
(handle lifetime, optimization), L4 (security, docs truth, CI and repo hygiene) and C0 (the
completeness critic). Every row was checked by an adversarial verifier; 56 CONFIRMED, 1
PLAUSIBLE (L3-08), 10 critic additions. Evidence for FIXED rows: the unit test or harness case,
with the mutation in tests/MUTATIONS.md (v2.3.1 section); H1 to H6 fail on the installed
2.3.0 and pass on 2.3.1 (tests/Invoke-ServiceHardening.ps1).

| ID | Item | Where | Sev | Disposition | Evidence |
|---|---|---|---|---|---|
| L1-01 | .ps1 rewrite dropped %VAR% expansion in script arguments (2.2.0 expanded them) | RunAsHelper/Core/PowerShellHost.cs TryRewrite | med | FIXED | arguments expanded with the injected expander; Rewrite_ExpandsEnvironmentInArguments (mutation: expansion removed) |
| L1-02 / L4-02 | Help said /ps: is ignored when #Requires is present; code, README and the help's own list say /ps: wins | RunAsHelper/HelpText.cs | low | FIXED | wording pinned by HelpTextTests |
| L1-03 | Capture pump split on LF only and stripped one trailing CR (2.2.0 split on CR, LF, CRLF) | RunAsHelper.Service/Core/CaptureDecoder.cs LineSplitter | low | FIXED | 11 LineSplitterTests (mutations: LF-only split, CR run not absorbed, EOF flush skipped); H5 |
| L1-04 / L4-12 | One pipe-bound log line (killjob "No such job") and one client line still carried U+2014 | PipeServer.cs, PipeClient.cs | low | FIXED | SourceHygieneTests.WireStrings_AreAscii (mutation: an em dash in PipeClient.cs) |
| L1-05 / L4-10 | Identity mismatch wrote Event 1003 "Launch denied" although the request continues | PipeServer.cs, EventLogHelper.cs | low | FIXED | EventLogHelper.IdentityMismatch wording; id and behavior unchanged (no user-mode trigger, see BL-14) |
| L1-06 | /timeout:N without /capture accepted but never enforced; docs promised 124 | RunAsHelper/Program.cs, HelpText.cs, README | low | FIXED | one client line says the ceiling is ignored; B15; docs corrected |
| L1-07 | /capture exits 0 when the capture pipe falls back to fire-and-forget | ElevationLauncher.cs TryCreateCapturePipe fallback | low | ACCEPTED-RECORDED | rare fallback, logged as such; reopens if the fallback is ever observed on a real box |
| L1-08 | Launcher edge cases (empty tail, GUI flag plus extra argument, missing exe, early Ctrl+C) | RunAsHelper.Launcher/Program.cs | low | CLOSED-VERIFIED | behave as designed; LauncherLogicTests, A7 |
| L1-09 | Wire compatibility 2.2.0 <-> 2.3.0 | RunAsHelper.Shared/Protocol | low | CLOSED-VERIFIED | LaunchRequest byte-identical; R14 old client green |
| L1-10 | Tray launch paths through the .ps1 rewrite (working directory, quoted paths, JobsPanel) | RunAsHelper/Core/PipeClient.cs | low | CLOSED-VERIFIED | traced; no tray feature changed |
| L2-01 | PipeClient.LaunchElevatedAsync 4-argument overload had no caller | RunAsHelper/Core/PipeClient.cs | low | FIXED | deleted; build |
| L2-02 | NativeMethods.PriorityClassName had no caller | RunAsHelper/Core/NativeMethods.cs | low | FIXED | deleted; build |
| L2-03 | READ_CONTROL, SYNCHRONIZE, LOGON_WITH_PROFILE had no references | RunAsHelper.Service/Core/NativeMethods.cs | low | FIXED | deleted; build |
| L2-04 | Launcher CTRL_CLOSE_EVENT declared and never referenced | RunAsHelper.Launcher | low | FIXED | named in OnCtrl |
| L2-05 | "gate" frame comment claimed a deadline; the payload echoes GateMinutes; no client reads it | PipeServer.cs, PipeMessages.cs | low | FIXED (comment) | frame kept for the tray; comments say what the payload is |
| L2-06 | ClientProcessSnapshot.ExecutablePath written and never read | PipeServer.cs | low | FIXED | now logged as image= on the connection line, which is what an operator needs on a denial |
| L2-07 | LookupSid declared an unused log parameter | ElevationLauncher.cs | low | FIXED | removed; build |
| L2-08 | B9 and R16 had empty -Test blocks that would pass vacuously without their gui tag | tests/ | low | FIXED | explicit Skip-Case |
| L2-09 | Smoke A10, A12, A14 contain only Skip-Case (pointers to other suites) | tests/Invoke-Smoke.ps1 | low | CLOSED-VERIFIED | they record SKIP, never PASS; the covering cases exist |
| L2-10 | Assert-NoTrayStart had no caller | tests/lib/Process.ps1 | low | FIXED | deleted |
| L2-11 | No unit test has an empty body | RunAsHelper.Tests | low | CLOSED-VERIFIED | every method asserts |
| L2-12 | LaunchRequest.Source is logged but never used for authorization | PipeServer.cs | low | CLOSED-VERIFIED | by design: identity comes from the pinned process |
| L2-13 | targetSessionId never supplied | ElevationLauncher.cs | low | CLOSED-VERIFIED | already BL-20 |
| L2-14 | Win32 interop enums carry unused members | RunAsHelper.Service/Core/NativeMethods.cs | low | CLOSED-VERIFIED | API completeness; harmless |
| L2-15 | EventLogHelper comment said 1001 covers validate requests | EventLogHelper.cs | low | FIXED | comment corrected |
| L3-01 | Client disconnect during LaunchElevated orphaned the launch result (hProcess leak, untracked child) | PipeServer.cs | med | FIXED | launchTask awaited in a finally; H1 (code read for the Win32 glue) |
| L3-02 | Timeout path wrote a frame before CloseHandle; with the client gone it leaked a process handle per run | PipeServer.cs | med | FIXED | no frame between outcome and CloseHandle; best-effort sends; CaptureRelayTests; H2 |
| L3-03 | /timeout not a ceiling when a grandchild inherits the pipe (pump join had no deadline) | PipeServer.cs / CaptureRelay | med | FIXED | 3 s drain grace after child exit, child's own exit code plus a [timeout] note; CaptureRelayTests (mutation: CancelAfter removed); H3 |
| L3-04 | Unbounded per-line accumulation; a line above the 4 MiB frame cap dropped the client | CaptureDecoder.cs LineSplitter | med | FIXED | 1 MiB cap, pieces emitted; LineSplitterTests (mutation: cap removed); H6 |
| L3-05 | LaunchElevated read and duplicated the cached TI token outside _initLock while ValidateToken reacquired it | ElevationLauncher.cs | low | FIXED | duplicate under the lock (TryDuplicatePrimary); code read, build |
| L3-06 | First frame read had no deadline; a silent client pinned a pipe instance | PipeServer.cs | low | FIXED | 30 s FirstFrameTimeout; H4 |
| L3-07 | UpdateProcThreadAttribute return value ignored | ElevationLauncher.cs | low | FIXED | both attribute-list calls checked; launch refused with the Win32 error on failure; code read |
| L3-08 | WinForms initialization, settings load and SetColorMode run on every CLI invocation | RunAsHelper/Program.cs Main | low | ACCEPTED-RECORDED | measured: exe --help 171 to 247 ms; reordering Main risks the 2.0.2 hand-off crash; reopens if CLI latency becomes a complaint |
| L3-09 | Two avoidable copies per captured line | CaptureDecoder / PipeServer | low | DECLINED-MEASURED | gen0 churn of about 2N bytes per N-byte line; superseded in shape by the v2.3.1 splitter, still one copy per line |
| L3-10 | CallerShell parent walk handle pairing | RunAsHelper/Core/CallerShell.cs | low | CLOSED-VERIFIED | every OpenProcess closed on every path; depth bound 8 |
| L3-11 | Launcher StartGui left the Process object undisposed | RunAsHelper.Launcher/Program.cs | low | FIXED | using var |
| L3-12 | PowerShellRequires regex construction and the 64 KB head read | RunAsHelper/Core/PowerShellHost.cs | low | CLOSED-VERIFIED | correctly sized for a one-shot process |
| L3-13 | PipeServer CTS, SemaphoreSlim, Channel and pipe-instance lifetimes | PipeServer.cs | low | CLOSED-VERIFIED | correct on the soaked paths; P1 no repeating growth |
| L3-14 | After a client disconnect the pump died but the INFINITE child wait continued; the child blocked on a full pipe and held its slot until /kill | PipeServer.cs / CaptureRelay | med | FIXED | WhenAny(pump, wait); read end disposed, slot released, child keeps running; CaptureRelayTests (mutation: ClientGone misclassified); H1 |
| L4-01 | Timed-out capture child is dropped from /jobs while three doc sites said "see /jobs" | HelpText.cs, README, launcher Ctrl+C line | med | FIXED (docs) | wording: the child keeps running and is no longer tracked |
| L4-03 | Public repo history carries the maintainer's work-domain email and real name on 63 older commits | git history | med | ACCEPTED-RECORDED | owner decision; nothing from 2.3.0 or 2.3.1 adds to it (all 23+ commits use the noreply identity); a rewrite would force-push a public default branch |
| L4-04 | Tray UI and help branded elevation with a vendor name | MainForm.Designer.cs, MainForm.cs, HelpText.cs | low | FIXED | "run elevated (UAC)"; comments keep the history |
| L4-05 | The v2.3.0 tag shipped a release-verify script that did not parse | tests/Invoke-ReleaseVerify.ps1 | med | FIXED | fixed in 70562e8; v2.3.1 tag carries the fix; every tests/*.ps1 passes Parser.ParseFile |
| L4-06 / C0-03 | No MSI-content or version check ran on the artifact that ships; ci.yml never passed -ExpectedVersion | ci.yml, release.yml | low | FIXED | ci.yml derives the tag version; release.yml runs Invoke-MsiContent on the built MSI |
| L4-07 | RunAsInvoker in the launcher grants nothing new | RunAsHelper.Launcher | low | CLOSED-VERIFIED | authorization never reads the layer; a Medium caller could already set it |
| L4-08 | Pipe DACL and trusted-SID model unchanged since 2.2.0 | PipeServer.cs CreatePipe | low | CLOSED-VERIFIED | no diff hunk in CreatePipe |
| L4-09 | Service host comment misdescribed the tray identity check | RunAsHelper.Service/Program.cs | low | FIXED | comment says what IsRunAsHelperTray checks |
| L4-11 | /timeout:0, non-numeric or negative /timeout and a bare /p: silently became the launch target | RunAsHelper/Program.cs | low | FIXED | usage line and exit 1 before touching the pipe; /p:9 is refused (2.3.0 mapped it to Normal); /timeout:0 now means no ceiling; B14 |
| L4-13 | Stale anchors and text written during 2.3.0 | THIRD_PARTY_NOTICES.md, Package.wxs, tests/CLAIMS.md | low | FIXED | re-anchored |
| L4-14 | TestResults not ignored; stale Exists() conditions in the test csproj | .gitignore, RunAsHelper.Tests.csproj | low | FIXED | |
| L4-15 | ICE61 is expected with AllowSameVersionUpgrades and documented; no warning can fail a workflow | Package.wxs | low | CLOSED-VERIFIED | README documents the same-version reinstall |
| L4-16 | release.yml still spliced derived step outputs into pwsh text | release.yml | low | FIXED | passed through env |
| L4-17 | BL-24 marked FIXED without a test | uninstall.py | low | RECORDED | dry-run evidence (see BL-24) |
| L4-18 | Ledger edits for the release were uncommitted | BACKLOG.md, tests/*.md | low | FIXED | committed as b0ad61d |
| C0-01 | Ledgers cited a scratch file and unnumbered plan decisions | BACKLOG.md, tests/CLAIMS.md, tests/MUTATIONS.md | low | FIXED | docs/console-launcher-plan.md decisions numbered; citations point at it |
| C0-02 | Invoke-Mutations hard-coded a dev-box path; off the box a harness error counted as a fired mutation | tests/Invoke-Mutations.ps1 | med | FIXED | -BaselineMsi parameter, skips when absent, harness errors count as not fired |
| C0-04 | /trusted:remove placeholder disagreed across help, usage and README | HelpText.cs, Program.cs | low | FIXED | `<SID\|DOMAIN\user>` everywhere |
| C0-05 | BL-13 and BL-14 were FIXED on code read alone | BACKLOG.md | low | FIXED | B13 proves BL-13 live; BL-14 moved to ACCEPTED-RECORDED |
| C0-06 | README host mapping still said .ps1 -> powershell | README | low | FIXED | |
| C0-07 | tests/README listed setcli as a CLI verb and called harness cases unit tests | tests/README.md | low | FIXED | |
| C0-08 | uninstall.py PATH removal matches the MSI's Environment row | uninstall.py | low | CLOSED-VERIFIED | same folder, normalized compare |
| C0-09 | Launcher project receives the Directory.Build.props stamp and resolves the exe from its own folder | RunAsHelper.Launcher | low | CLOSED-VERIFIED | A1 FileVersion equals the build version |
| C0-10 | HelpTextTests used a hand-kept switch list | RunAsHelper.Tests/HelpTextTests.cs | low | FIXED | list derived from Program.cs at test time (it found /h and /validate missing) |

Follow-ups raised while fixing (recorded, not open):

| ID | Item | Where | Source | Disposition | Evidence | Notes/Trigger |
|---|---|---|---|---|---|---|
| BL-37 | Three tray-only AppendLog strings keep an em dash | RunAsHelper/MainForm.cs | slice D | ACCEPTED-RECORDED | they render in the tray's own text box, never a pipe | Three one-character edits if a fully ASCII client is wanted |
| BL-38 | A child whose client disconnected mid-capture keeps running (detached, slot released) rather than being ended | PipeServer.cs CaptureEnd.ClientGone | slice S | ACCEPTED-RECORDED | same contract as /timeout; the verifier called ending it "also defensible" | Owner decision: one TerminateByPid call in the ClientGone branch |
| BL-39 | A service stop that arrives while LaunchElevated waits for the TrustedInstaller service (up to 30 s cold) now delays that handler's unwinding | PipeServer.cs launchTask finally | slice S | ACCEPTED-RECORDED | bounded by the host's shutdown timeout; B5 stop/start green | Reopens if the release-verify stop time grows |
| BL-40 | CaptureDecoder.cs holds three types (CaptureDecoder, LineSplitter, CaptureRelay) because the test csproj links that one file | RunAsHelper.Service/Core/CaptureDecoder.cs | slice S | DECLINED-MEASURED | 3 types, ~400 lines, one file; splitting needs two more test links | Do it the next time the file grows |
| BL-41 | %USERPROFILE%, %APPDATA%, %TEMP% in a .ps1 target's path and arguments resolve in the caller's environment since 2.3.0, not the service's systemprofile as in 2.2.0 | PowerShellHost.cs | slice D | ACCEPTED-RECORDED | deliberate part of the client-side rewrite; 2.3.1 made path and arguments consistent | Reopens if a saved entry relied on the SYSTEM profile paths |
| BL-42 | ci.yml triggers on push to every branch and on pull_request, so a same-repo PR builds twice; upload-artifact asks 7 days where the repo caps at 3 | .github/workflows/ci.yml | critic / CI annotation | ACCEPTED-RECORDED | cosmetic; both are GitHub's own notices | Trim when PRs become the workflow |
| BL-43 | The harness assumed an installed product: `Get-InstallDir` read `InstallFolder` from a key that survives an uninstall, and the install-cycle snapshot called `.Status.ToString()` on an absent service, so a release verify from a bare box died before its first install | tests/lib/Env.ps1, tests/Invoke-InstallCycle.ps1 (same shape in I2, I6, R4, R9, the self-test) | release verify v2.3.1 runs 1 and 2 | FIXED | run 3 installed the released 2.3.1 from `service=absent tray=0` and passed V1-V8 (tests/MUTATIONS.md, Release v2.3.1) | A registry or service read in the harness checks presence first; `$null.Property` throws under StrictMode in pwsh 7 |
| BL-44 | The smoke suite's RESULT line was lost whenever it relaunched itself for ConPTY (redirected stdout), so the release verify printed every case and no total; the "27 pass" recorded at integration was a hand count | tests/lib/Assert.ps1 Finish-Run | release-verify3 log | FIXED | `-Phase A -Only A1` under redirection now ends in `RESULT [smoke A]: 1 pass / 0 fail / 13 skip`; the suite has 29 cases, 24 pass / 5 skip on 2.3.1 | A count in a ledger comes from a RESULT line, never from counting rows by hand |
| BL-45 | `Invoke-Elevated` threw before removing its work folder when rc.txt was empty (`[string]` of `Get-Content -Raw` is `$null` in pwsh 7) or the run timed out, so each elevated call to a GUI or missing target leaked a %TEMP% folder; its comment promised a fallback to the runner's exit code that never ran and would have returned 0 for an unmeasured target | tests/lib/Elevated.ps1 | nine leaked `rah-tests-*` folders after the release verify | FIXED | fixed helper E1-E3 3/3; HEAD copy E2 and E3 FAIL on the leaked folder (tests/MUTATIONS.md, Hand-off) | A helper that cannot measure throws; it never substitutes a number |
| BL-46 | release.yml had no prune step, so the Releases page held five releases against the cross-repo keep-three policy; plan decision 17 flagged it and no row recorded it until the hand-off re-read | .github/workflows/release.yml, .github/scripts/Remove-OldReleases.ps1 | plan decision 17 | FIXED | local dry run lists v2.1.5 and v2.1.4 and deletes nothing (P1-P3 3/3); mutant without the tag guard fails P3, mutant without the dry-run guard fails P1; all release tags are on the remote; the v2.2.0, v2.1.5 and v2.1.4 MSIs and notes are archived at D:\.ai-work\_backups\runas-helper-releases-2026-09-30 with digests matching GitHub's | First live run is the next tag push: its log must show "prune: 6 release(s)" and three "deleted release" lines. The step shares the publish step's condition, so a dispatch dry run prunes nothing |

## Harness sweep (2026-09-30)

A read-only audit of the harness itself, after BL-43 to BL-45 showed it had only ever run
with the product installed: four lenses (StrictMode null traps, checks that cannot fail,
leaks, claims) produced 26 raw findings, of which adversarial verifiers confirmed 24 and
refuted 2, and a completeness critic added 6. Proofs S-* ran against the fixed harness and
against a worktree at d2610ad (tests/MUTATIONS.md, Harness sweep).

| ID | Item | Where | Source | Disposition | Evidence | Notes/Trigger |
|---|---|---|---|---|---|---|
| HS-01 | T2 compared the Run value after the tray start with the value before the cycle, so a profile where no tray had run, or a value naming another copy of the exe, failed on a correct build; reachable once BL-43 let the cycle start from a bare box | tests/Invoke-InstallCycle.ps1 T2 | sweep | FIXED | bare-profile install on this box: d2610ad harness 11 pass / 1 fail (T2 "expected [] got [...]"), fixed harness 12 / 0 | the expectation now comes from settings.json StartWithWindows |
| HS-02 | V2 read `$rel.tagName` when no release came back, so a missing release was reported as a harness error | tests/Invoke-ReleaseVerify.ps1 V2 | sweep | FIXED | S-C2C3: fixed "gh release view v0.0.0 returned no release (exit 1)"; d2610ad "harness error: The property 'tagName' cannot be found" | |
| HS-03 | V1 built its message from `$runs.headBranch`, which throws on an empty list | tests/Invoke-ReleaseVerify.ps1 V1 | sweep | FIXED | S-C2C3: fixed assertion FAIL; d2610ad "harness error: The property 'headBranch' cannot be found" | |
| HS-04 | V5 built its message from `$files.Name`, the same shape, when the image holds none of the binaries | tests/Invoke-ReleaseVerify.ps1 V5 | sweep | FIXED | same pipeline form as HS-03; code read, since the state needs a defective MSI | |
| HS-05 | Get-ActiveJobIds parsed a refused `/jobs` as an empty job table, so H1's "no job listed" could pass on nothing | tests/Invoke-ServiceHardening.ps1 | sweep | FIXED | S-C5, a refusing `/jobs` stand-in: fixed throws "did not answer (exit 1)", d2610ad returns no ids; H1 PASS in the final run | the finally block reports a failed listing instead of throwing |
| HS-06 | H1 and H2 re-resolved the service by name, so a service that crashed and was restarted mid-case would pass on a fresh process | tests/Invoke-ServiceHardening.ps1 H1, H2 | sweep | FIXED | S-C6: PID and creation time stable across reads, changed across a service restart (49632 to 54004) | read through CIM: Get-Process StartTime is null from a non-elevated shell |
| HS-07 | R14's witness was a marker the harness expanded, which the service echo also carries, so it passed with no child output | tests/Invoke-Regression.ps1 R14 | sweep | FIXED | S-W1: the old form passes on the echo alone; S-W2: `WITNESS-%COMPUTERNAME%` arrives expanded while the echo keeps the literal; self-test MUT15 fires | |
| HS-08 | R3's witness, the same shape | tests/Invoke-Regression.ps1 R3 | sweep | FIXED | as HS-07; R3 PASS in the final run | |
| HS-09 | H3's `parent-done` matched the echo of the command | tests/Invoke-ServiceHardening.ps1 H3 | sweep | FIXED | anchored `^parent-done$`, which the echo line (it starts with "Args detected") cannot match | |
| HS-10 | R12 asserted only the wall clock and discarded every job's output and exit code | tests/Invoke-Regression.ps1 R12 | sweep | FIXED | three anchored R12-DONE child lines are required; R12 PASS in the final run | |
| HS-11 | B10 sent the group by name, which the client refuses before the pipe, so the service's own user-only check was never exercised | tests/Invoke-Smoke.ps1 B10 | sweep | FIXED | S-1-5-32-544 now reaches the service, which answers "The SID does not resolve to a user account"; B10 PASS in the final run | a finally removes the SID if a faulty service ever accepted it |
| HS-12 | Measure-HelpLatency timed failed runs, so a missing launcher passed the 150 ms gate | tests/Invoke-AuditProbes.ps1 | sweep | FIXED | S-C12: fixed throws; d2610ad reported "median 6 ms" for a launcher that does not exist | |
| HS-13 | Invoke-AdminRunner threw on a timeout before removing its folder, and returned the runner's own exit code when rc.txt was missing: BL-45's shape in a second helper | tests/Invoke-InstallCycle.ps1 | sweep | FIXED | S-C13a (a folder left by d2610ad, none by the fix), S-C13b (d2610ad returned 0, the fix throws); four real msiexec runs through it in the T2 proof | Recorded: a non-elevated Kill cannot end the elevated runner, so after a timeout it and its msiexec may still finish |
| HS-14 | The README's witness rule described a check R3 and R14 did not make | tests/README.md | sweep | FIXED | rewritten around the markers now used and MUT15 | |
| HS-15 | B6, B10 and B12 handed the GUI exe to Invoke-Elevated on a 2.2.0 baseline, where it can only error | tests/Invoke-Smoke.ps1 | sweep | FIXED | they skip with a reason when there is no .com; the header says so | |
| HS-16 | The self-test called CTRL2 an MSI-version check (it counts File rows), nothing ran the version reader against a correct expectation, and MUT4 and MUT10 counted a failed precondition as the mutation firing | tests/Invoke-Mutations.ps1 | sweep | FIXED | CTRL4 added (the reader returns 2.2.0.0); the preconditions throw | |
| HS-17 | The audit probes' plan text promised a soak-level 1001/1003 reconciliation that no probe performs | tests/Invoke-AuditProbes.ps1 | sweep | FIXED (text), ACCEPTED-RECORDED (check) | the plan text now describes P4 as it is | The soak-level check (1003 count equals the denials driven) is worth adding the next time the probes are run |
| HS-18 | The ConPTY witness covered stdout only, the relaunch guard looked only at stdout, and A7, which asserts on a stderr line, ran no witness | tests/lib/ConPty.ps1, tests/Invoke-Smoke.ps1 | sweep | FIXED | the witness echoes on both streams, the relaunch covers either redirect, A7 calls the witness; A2, A7, A8 PASS in the final run | |
| HS-19 | The regression header promised every untagged case passes on 2.2.0, where R5 and R13 could only error | tests/Invoke-Regression.ps1 | sweep | FIXED | they skip with a reason without the .com; the header says so | |
| HS-20 | The README said `-NewBuild` enables needs-elevated cases and labelled a non-integration command as integration | tests/README.md | sweep | FIXED | the text matches the filters | |
| HS-21 | The README gave `Start-Process -Wait` for the ConPTY relaunch, the form that hung the first integration run | tests/README.md | sweep | FIXED | `-PassThru` plus `WaitForExit()`, as the code does | |
| HS-22 | The README's "cannot be tested" list credited coverage that did not exist: U4 for the tray host rule, an environment override for a missing pwsh, and an accesschk check of the pipe DACL that the approved plan listed and nobody built | tests/README.md, tests/Invoke-Smoke.ps1 B16 | sweep | FIXED | text corrected; the DACL check now exists as B16 (Get-PipeDaclViolations, PASS in the final run) and self-test MUT16 fires when the trusted list is withheld | |
| HS-23 | The README said every runner ends with a RESULT count line | tests/README.md | sweep | FIXED | the self-test's own reconciliation is described | |
| HS-24 | EventLog.ps1 listed the event ids wrongly (1002 as "result", 1005 as "install", no 1004) | tests/lib/EventLog.ps1 | sweep | FIXED | the comment matches EventLogHelper.cs | |
| HS-25 | The smoke relaunch passed its script and results paths unquoted, so from a clone path with a space it ran nothing and printed nothing | tests/Invoke-Smoke.ps1 | sweep critic | FIXED | S-K1: from "sp test fix" A1 PASS and the RESULT line; d2610ad exit 64 with no output | a missing results file is now reported |
| HS-26 | Invoke-Elevated and Invoke-AdminRunner passed the runner path unquoted, so a TEMP with a space broke every elevated case | tests/lib/Elevated.ps1, tests/Invoke-InstallCycle.ps1 | sweep critic | FIXED | S-K2: fixed exit 7, d2610ad exit 64 | |
| HS-27 | Get-InstallDir fell back to a literal `C:\Program Files\RunAsHelper` | tests/lib/Env.ps1 | sweep critic | FIXED | S-K3: `D:\Program Files\RunAsHelper` under an overridden ProgramW6432; d2610ad `C:\...` | the install cycle also refuses a set RAH_INSTALL_DIR |
| HS-28 | Send-RawPipeRequest's timeout applied only between frames, so a service that took the request and fell silent would hang B13 and a relaunched smoke run | tests/lib/RawPipe.ps1 | sweep critic | FIXED | S-K4: a private silent server, back after 3.0 s with 0 frames (the sweep measured 20.1 s on the old code with a copy) | |
| HS-29 | B8 spliced TEMP paths into `-Command` text unescaped and into an ASCII .cmd file, so an apostrophe or a non-ASCII letter in the profile path failed B8 | tests/Invoke-Smoke.ps1 B8, tests/lib/Process.ps1 | sweep critic | FIXED | S-K5, TEMP holding a space, an apostrophe and u-umlaut: fixed PASS, d2610ad FAIL | The first fix called the wrapper by bare name and failed: agent shells set NoDefaultCurrentDirectoryInExePath, so it runs `.\via-cmd.cmd` |
| HS-30 | A13 hard-coded the signer thumbprint, so a release signed with another certificate (BL-01) would fail V7 | tests/Invoke-Smoke.ps1, tests/Invoke-ReleaseVerify.ps1 | sweep critic | FIXED | S-K6: A13 FAILs on a wrong `-ExpectedThumbprint` and PASSes on the real one, both through the relaunch | |
| HS-31 | A13 skips on an unsigned build | tests/Invoke-Smoke.ps1 A13 | sweep | CLOSED-VERIFIED | refuted by its verifier: the dev-build skip prints its reason, and the release verify runs A13 on the signed MSI | |
| HS-32 | H1 abandons its captures without a per-case `/kill` | tests/Invoke-ServiceHardening.ps1 H1 | sweep | CLOSED-VERIFIED | refuted by its verifier: H1 and H2 run only with `-AllowElevated`, which is exactly when the suite's finally ends every listed job | |

## Not testable on this box (recorded)

| ID | Gap | Why | Record |
|---|---|---|---|
| NT-01 | Standard-user / explicitly denied-user rows | No second account; creating one is a machine change | B4 proves the same authorization predicate for this user: with its SID removed the launch is refused with "Command line is disabled", and allowed again after re-add |
| NT-02 | Remote pipe access | Single machine | Deny-NETWORK ACE is in the pipe DACL (PipeServer.cs CreatePipe) |
| NT-03 | Restricted token, 128-entry limit, domain user, PID-reuse churn | Needs token crafting / many users / a domain | Recorded |
| NT-04 | Machine without pwsh | pwsh is installed | The resolver's fallback (5.1 plus a warning line) is covered by PowerShellHostTests Resolve_FallsBackTo51_WhenPwshMissing |
| NT-05 | UAC-prompting machine, AppCompat disabled by policy | This box auto-consents; no GPO changes | Recorded; the launcher prints the Win32 message and exits with the error code if CreateProcess fails |
| NT-06 | Windows Terminal as the terminal | Not installed | conhost via ConPTY only |
| NT-07 | Tray saved-entry .ps1 host rule, the /validate dialog, dark mode, the reworded Activate button | GUI | Screenshot of the running tray taken at integration (dark theme, version title); the .ps1 rewrite is shared with the CLI path (PipeClient.SendAsync) and covered by B8; the 2.3.1 button text was verified in --help output only |
| NT-08 | A pipe/process identity mismatch (Event 1003 wording) | No user-mode way to produce it | Code read; BL-14 |

## Machine state on the dev box

| MS | Change | Why | Reverse |
|---|---|---|---|
| MS-01 | Codex sandbox SIDs ...-1004 / ...-1005 (CodexSandboxOffline/Online) are trusted CLI callers | Pre-existing, found not changed | Owner decision; `RunAsHelper /trusted:remove <SID>` from an elevated shell |
| MS-02 | This account's SID ...-1000 was added to AllowedCallerSids (Phase 0) | Authorizes non-elevated calls for the suites and for agents | `RunAsHelper /trusted:remove <SID>` from an elevated shell |
| MS-03 | Install folder is on the machine PATH; installed version went 2.2.0 -> 2.2.90 (dev) -> 2.3.0 (published) -> 2.3.1 dev -> removed -> 2.3.1 (published, fresh install); the tray was closed at hand-off, as it was found | Integration and release | Uninstall removes PATH; the 2.2.0 MSI is backed up at D:\.ai-work\_backups\runas-helper-2.2.0-baseline |
| MS-04 | Windows Error Reporting LocalDumps is armed for RunAsHelper.exe (key `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\RunAsHelper.exe`, dump folder `%USERPROFILE%\RunAsHelper-crashdumps`, empty on 2026-09-30) | Set up at v1.6.1 to catch the unexplained 0xe0434352 crash; kept while BL-02 can reopen, since it writes only when the exe crashes | `reg delete` that key from an elevated shell and delete the folder |
