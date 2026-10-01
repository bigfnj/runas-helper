# Mutation ledger

Every check here was seen failing once under a deliberate mutation before it was
trusted. A check whose failure cannot be produced does not ship. Format: check,
mutation, observed failure line. Runs were on this box against the installed 2.2.0
build unless marked otherwise.

## Unit tests (RunAsHelper.Tests, dotnet test -c Release)

| Check | Mutation | Observed |
|---|---|---|
| PipeProtocolFramingTests.RoundTrip_PipeMessage_PreservesValue | Ran against PipeProtocol.cs as-is | PASS (green); flipping a byte of the length prefix in the test stream returns null and the Equal assertion fails |
| OversizeLength_ReturnsNull | 4 MiB + 1 length prefix | PASS; changing the test constant to 4 MiB - 1 with a matching body makes ReadFrame return non-null and the Null assertion fails |
| HelpTextTests.Cli_MentionsNewFeature (RunAsHelper.com, /trusted, /ps:, 124) | Linked against the 2.2.0 HelpText.cs | RED, 4 failures: each token "not found" (this is the standing mutation evidence; slice B turns it green) |
| HelpTextTests.Cli_HasNoBareExeExampleLine | 2.2.0 HelpText.cs | RED: "help text still shows RunAsHelper.exe example lines: ..." (13 lines) |
| HelpTextTests.Cli_DocumentsEveryParsedSwitch (/trusted, /ps:) | 2.2.0 HelpText.cs | RED, 2 failures (the seven 2.2.0 switches stay green) |

Full run this session: 11 pass / 7 fail, the 7 red all in HelpTextTests, exactly as
designed for slice C's pre-integration worktree.

## Smoke -Baseline (new-feature cases must fail on 2.2.0)

`pwsh -File tests/Invoke-Smoke.ps1 -Baseline` on the installed 2.2.0 build:
`0 pass / 9 fail / 17 skip`, exit 1. The nine failures ARE the mutation proof for the
new-feature cases (the feature is absent on 2.2.0, so the check fails):

| Case | Observed failure |
|---|---|
| A1 .com exists / subsystem | FAIL: installed RunAsHelper.com present at C:\Program Files\RunAsHelper\RunAsHelper.com |
| A2 .com --help | FAIL: installed RunAsHelper.com present (2.3.0 launcher) |
| A3 bare-name resolves to .com | FAIL: RunAsHelper resolves on PATH |
| A4 Medium /jobs via .com | FAIL: installed RunAsHelper.com present |
| A7 Ctrl+C to the .com | FAIL: installed RunAsHelper.com present (2.3.0 launcher) |
| A8 .com on a real console | FAIL: installed RunAsHelper.com present (2.3.0 launcher) |
| B1 /capture passes child exit code | FAIL: exit code expected 3 got 0 (2.2.0 does not relay the child code) |
| B2 /timeout expiry exits 124 | FAIL: exit code expected 124 got 0 (2.2.0 returns 0 on timeout) |
| B8 caller-shell host rule | FAIL: installed RunAsHelper.com present (host rule is a 2.3.0 client feature) |

Controls skipped in baseline (they keep 2.2.0 behavior): A5 (740), B3 (fire-and-forget
0). Integration-only, GUI and elevated cases skipped with reasons.

## Regression (2.2.0 behavior that must not change)

`pwsh -File tests/Invoke-Regression.ps1`: `9 pass / 0 fail / 7 skip`, exit 0. Each
case carries its own mutation in the design table; those exercised this session:

| Case | Mutation | Observed |
|---|---|---|
| R1 exe --help via RunAsInvoker | expected-text swap | PASS as-is; a NO-SUCH-TEXT expectation fails (seen in Invoke-Mutations MUT1) |
| R2 direct exe --help without compat | run same against a launcher that sets RunAsInvoker | PASS (Win32 740); with compat it would not throw |
| R6 Medium /jobs -> 1 | expect exit 0 | PASS as-is; the /jobs denial is on stderr (fixed during bring-up: the check now reads both streams) |
| R10 as:ti vs as:system whoami /all | swap TI-group expectation | PASS; design said whoami /groups + S-1-5-18, but /groups never shows S-1-5-18 (a user SID), so the case uses whoami /all -- corrected during bring-up |
| R11 priority /p:2 | expect High | PASS (Idle); High would fail |
| R12 three parallel captures | expect wall > 15 s | PASS (< 14 s); serialized behavior would fail |

## Harness self-test (Invoke-Mutations.ps1)

`pwsh -File tests/Invoke-Mutations.ps1`: 6 mutations fired, 2 controls passed, exit 0.
Each mutation feeds one wrong expectation to a real operation on this box:

| Mutation | Observed |
|---|---|
| MUT1 wrong help text | FAIL: help text did not match /NO-SUCH-TEXT-XYZZY/ |
| MUT2 wrong exit code | FAIL: exit code expected 99 got 0 |
| MUT3 PATHEXT reorder | FAIL: default PATHEXT resolves .com before .exe, so the "resolves to .exe" expectation fails |
| MUT4 wrong MSI version | FAIL: RunAsHelper.exe version expected [9.9.9.0] got [2.2.0.0] |
| MUT5 bogus HKCU Run value | FAIL: got the real value "C:\Program Files\RunAsHelper\RunAsHelper.exe" --tray |
| MUT6 wrong event Source | FAIL: sample body carries Source: cli, not Source: tray |
| CTRL1 correct help | PASS |
| CTRL2 correct MSI row count | PASS |

## MSI content (Invoke-MsiContent.ps1)

`pwsh -File tests/Invoke-MsiContent.ps1 -MsiPath <2.2.0 MSI>`: `2 pass / 3 fail`, exit 1.
This IS the mutation for the CI MSI-content step (an integrated 2.3.0 MSI passes it):

| Check | Observed on the 2.2.0 MSI |
|---|---|
| M1 RunAsHelper.com present | FAIL: file rows expected [1] got [0] |
| M4 each binary in its own component | FAIL: component rows >= 3 (got 2) |
| M5 Environment PATH row | FAIL: Environment PATH rows (got 0) |
| M2/M3 exe + service present | PASS (the two 2.2.0 binaries are there) |

## ConPTY harness (adopted from Phase 0)

Proven on this box via the hidden-console relaunch the harness lesson prescribes:
`harness-stdout-is-console=True`, rendered `CONPTY-OK`, relayed exit 7. A run whose
witness is missing is a harness failure, not a product result (see
docs/console-launcher-plan.md, "Phase 0 probe result").

## Phase 0 encoding finding (recorded, fixed by slice B)

The 2.2.0 service wrote the em dash in its log lines as one byte 0x97 (Windows-1252) when
stdout was a pipe, which shows as a replacement character in a UTF-8 reader. Observed in
R3's captured output on 2.2.0. Fixed by ASCII-only service log prose (BL-27); the
integrated build's R3/B1 output is ASCII.

## Integration on the installed 2.2.90 build (2026-09-29)

Merged tree = slices A + B + C on `integrate/v2.3.0`, built with `-p:ProductVersion=2.2.90`.

Unit tests: 86 pass (19 launcher, 49 client/service, 18 protocol/help), including the
HelpTextTests that were red on the 2.2.0 text.

`Invoke-MsiContent.ps1 -ExpectedVersion 2.2.90`: first run FAILED M7 (`expected [2.2.90]
got [2.2.90.0]`), a harness bug: MSI stores four-part versions. Fixed by comparing as
versions; rerun 7 pass / 0 fail. The 2.2.0 MSI still fails M1/M4/M5 (the CI step's mutation).

`Invoke-InstallCycle.ps1 -Cycle -StartTray` (install over 2.2.0, uninstall, reinstall,
tray start): 23 pass / 3 fail on the first run. All three were harness defects, found
because the cycle ran for the first time here:

| Case | First run | Cause | Fix and rerun |
|---|---|---|---|
| I5/R5 install folder on machine PATH | FAIL, snapshot said `pathEntries=1` | `Get-MachinePath -split ';'` passed `-split` to the function and returned the whole PATH as one entry | `(Get-MachinePath) -split ';'`; A11 PASS and MUT8 fires |
| T1 tray title | FAIL `got []` | title read from a variable set inside the wait's script block (different scope) | re-read after the wait; the tray title was `RunAS Helper - v2.2.90` all along (screenshot) |

Machine facts checked directly at the same time: machine PATH kind still `ExpandString`
(REG_EXPAND_SZ), 43 entries, `C:\Program Files\RunAsHelper\` present; AllowedCallerSids
kept through the uninstall; every msiexec run exited 0.

`Invoke-Smoke.ps1 -Phase All -ExpectedVersion 2.2.90 -Integration`: 23 pass / 3 fail /
5 skip on the first run, then 4 pass on the rerun of the fixed cases:

| Case | First run | Cause | Fix |
|---|---|---|---|
| B8 cmd row | FAIL `got []` | `cmd /c` strips the first and last quote of a command line that starts with a quote, and ArgumentList escapes inner quotes with backslashes | the cmd row runs a `.cmd` wrapper written to the space-free work dir |
| B11, B12 event Source | FAIL `got []` | `Get-WinEvent` leaves `Message` empty for the RunAsHelper source (no message file) | `Get-RunAsHelperEventText` reads the string properties |
| (parent run never returned) | hang | `Start-Process -Wait` waits for descendants, and A6 starts the tray through the .com | `WaitForExit()` on the child process |

The five skips: A10, A12, A14 (covered by Invoke-MsiContent and the install cycle), A13
(unsigned dev build; runs on the released MSI), B9 (GUI). B7 passed in both its forms
(the original 400,000-line stream and the steady 100 ms stream that shipped).

`Invoke-Regression.ps1 -NewBuild -AllowElevated -AllowMachineWrites -ExpectedVersion 2.2.90`:
13 pass / 2 fail / 1 skip, then 2 pass on the rerun:

| Case | First run | Cause | Fix |
|---|---|---|---|
| R5, R13 | `harness error: You cannot call a method on a null-valued expression` | the elevated runner's `&` does not wait for a GUI exe and records no exit code, so rc.txt was empty | elevated calls use RunAsHelper.com. (This row first said the runner also falls back to its own exit code. For R5 and R13 it never did, because the empty rc.txt threw first; see BL-45 in the hand-off section.) |
| R7 | (would have failed) | the design used `/listtrustedcallers` as a CLI switch, which never existed | rewritten on `/trusted`, tagged changed-in-2.3.0 |

`Invoke-AuditProbes.ps1 -SoakLaunches 30`: 5 pass. Handles 440 -> 463 (+23) -> 427 (-36)
across two rounds of 60 launches + 30 denials each (no repeating growth); the 11th
concurrent capture is told busy after 30.5 s; `--help` median 315 ms via the .com vs 205 ms
via the exe (110 ms overhead, under the 150 ms threshold; Phase 0 measured 71 ms and slice
A 40 ms on a quieter box); 1001 = 1002 = 5 for 5 launches; no leftover children.

`Invoke-Mutations.ps1 -Integration`: 13 mutations fired exactly once, 3 controls passed.
The seven integration mutations feed a wrong expectation to the operation an integration
case performs:

| Mutation | Observed |
|---|---|
| MUT7 wrong installed FileVersion (I4) | expected [9.9.9.0] got [2.2.90.0] |
| MUT8 folder not on PATH (A11/I5) | machine PATH contains a folder that is not there |
| MUT9 wrong tray title (R8/T1) | did not match /RunAS Helper - v9\.9\.9/; got [RunAS Helper - v2.2.90] |
| MUT10 Medium launch expecting Source: tray (B11) | got `Source: cli  ClientPID: ...` |
| MUT11 service host from a shell expecting exit 0 (A9) | exit code expected 0 got 1 |
| MUT12 /capture exit 3 expecting 4 (B1) | exit code expected 4 got 3 |
| MUT13 /timeout expecting 0 (B2) | exit code expected 0 got 124 |

Real-app check: the installed tray, started through `RunAsHelper.com` with no arguments
(A6), shows `RunAS Helper - v2.2.90` in dark theme with the service running (PrintWindow
screenshot taken from an elevated helper, because a Medium process cannot render an
elevated window).

## Release v2.3.0 (2026-09-30, published from the tag)

CI: `ci.yml` green on `integrate/v2.3.0` and on `main`; a throwaway branch with one
deliberately failing unit test went red at the "Unit tests" step and was deleted (the CI
gate's own mutation). `release.yml` run 36659942899 green: the signed MSI, the two exes
and the `.com` all verified on the runner.

`Invoke-ReleaseVerify.ps1 -Tag v2.3.0`: 8 pass / 0 fail. Asset `RunAsHelper-Setup-2.3.0.msi`
(2,068,480 bytes, SHA-256 F3EAB58FFE0E736C78A9F9DD0444018A85CFC8C36ABE2B0CFF54F32DCF897A02),
signature Valid with thumbprint 0EEBB64D...BAD5 and a timestamp; MSI content 7/7 at 2.3.0;
the three binaries in the administrative image Valid and timestamped; install cycle over
2.2.90 12/12 with the tray titled `RunAS Helper - v2.3.0`; smoke 22 pass / 4 skip (A13 now
PASS on the signed build); regression 15 pass / 1 skip.

Its first run failed to parse: `"$Tag:"` inside a double-quoted string reads as a scoped
variable. Fixed with `${Tag}`, and every `tests/*.ps1` now passes `Parser.ParseFile`.

## v2.3.1 audit fixes (2026-09-29, client and harness slice)

Runs on this box against the installed 2.3.0 service. Client cases used the built 2.3.1
`RunAsHelper.exe` staged next to a copy of the installed `RunAsHelper.com`
(`RAH_INSTALL_DIR`); the installed 2.3.0 client is the mutation for the client fixes.

Unit tests (`dotnet test -c Release`, 89 tests):

| Check | Mutation | Observed |
|---|---|---|
| PowerShellHostTests.Rewrite_ExpandsEnvironmentInArguments (L1-01) | args expansion removed from TryRewrite | FAIL: Assert.EndsWith, expected end `-File "C:\scripts\fix.ps1" -Out C:\data\a.log` |
| HelpTextTests.Cli_DocumentsEveryParsedSwitch (C0-10, derived list) | `else if (a.Equals("/mutationzz", ...))` added to the Program.cs flag loop | FAIL: "Program.cs parses switches the help does not mention: /mutationzz" |
| HelpTextTests.Cli_DerivedSwitchList_HasTheKnownTokens (control) | extraction regex reduced to StartsWith only | FAIL: "lost known tokens: /capture, /jobs, /trusted (derived: /as:, /joblog:, /kill:, /p:, /ps:, /timeout:, /trusted:)" |
| HelpTextTests wording pins (L1-02, L4-01, C0-04, L1-06) | HelpText.cs restored to its v2.3.0 text | 6 FAIL: "Overrides a #Requires" not found; "/trusted:remove <SID\|DOMAIN\user>", "no longer tracked", "Without /capture" not found; "(see /jobs)" found; Cli_DocumentsEveryParsedSwitch "/h, /validate" |
| SourceHygieneTests.WireStrings_AreAscii (L4-12) | em dash in `PipeClient.cs` `Log($"Pipe communication error ...")` | FAIL: "RunAsHelper\Core\PipeClient.cs:299 U+2014 in ..." |
| SourceHygieneTests.WireStrings_AreAscii, v2.3.0 service text | none needed: the v2.3.0 `PipeServer.cs:716` killjob frame still carries an em dash | FAIL: "RunAsHelper.Service\Worker\PipeServer.cs:716 U+2014 in "No such job ..."" (green once the service slice lands) |

Smoke (`Invoke-Smoke.ps1 -Phase B -Only B13,B14,B15`):

| Case | Mutation | Observed |
|---|---|---|
| B13 raw frame with an unknown verb (C0-05, BL-13) | the same frame with `Verb: launch` | the service ran it: 11 frames, result Success, log "Process created", one 1001 event, no 1003, no "Unknown request" |
| B14 malformed /timeout: and /p: (L4-11) | installed 2.3.0 client | FAIL: "/timeout:abc usage line on stderr did not match /Usage: RunAsHelper/; got []" |
| B15 /timeout without /capture (L1-06) | installed 2.3.0 client | FAIL: "the ignored-timeout line did not match /applies with \/capture/; got [Worker thread reverted ...]" |
| B9 / R16 GUI placeholders (L2-08) | `gui` tag removed | body `Skip-Case` records SKIP; the v2.3.0 empty body recorded PASS (the vacuous pass the fix removes) |

Staged client, all three: 3 pass / 0 fail. Installed 2.3.0 client: B13 PASS (service
behaviour), B14 and B15 FAIL as above.

Harness self-test (`Invoke-Mutations.ps1`, C0-02):

| Run | Observed |
|---|---|
| default (baseline MSI present) | 6 fired, 2 controls, exit 0 |
| `-BaselineMsi C:\nonexistent\...msi` | MUT4 and CTRL2 SKIP "no baseline MSI at ... (pass -BaselineMsi)"; 5 fired, 1 control, exit 0 |
| `-BaselineMsi LICENSE` (a file that is not an MSI) | both cases `harness error: ... OpenDatabase`; "Harness errors (not counted as fired): MUT4-wrong-version, CTRL2-version-ok", "MUTATIONS THAT DID NOT FIRE: MUT4-wrong-version", exit 1 (the 2.3.0 reconciliation counted that MUT4 as fired) |
| `-Integration` | 12 fired, 3 controls, exit 0 (MUT9 skipped: the running tray has no window open) |

MSI content (`Invoke-MsiContent.ps1`, the ci.yml step with the derived version, C0-03):
against the worktree's Release MSI `-ExpectedVersion 2.3.0` (from `git describe`) 7 pass;
`-ExpectedVersion 9.9.9` fails M6 and M7.

uninstall.py (L4-17, BL-24): `remove_path_entry()` imported and run with `DRY_RUN=True`
printed "DRY-RUN would strip the install dir from the machine PATH." and changed nothing;
a read-only replay of its matching rule against the live machine PATH (43 entries,
REG_EXPAND_SZ) would remove exactly one entry, `C:\Program Files\RunAsHelper\`, and keep
the other 42; `HKLM\SOFTWARE\RunAsHelper\InstallFolder` names the same folder. Not run
for real (the MSI is installed and the box is in use).

## v2.3.1 audit fixes (service slice, unit level)

`RunAsHelper.Service/Core/CaptureDecoder.cs` gained two pure types, `LineSplitter` and
`CaptureRelay`, with 16 new tests (LineSplitterTests, CaptureRelayTests). Each mutation was
applied to the source once and restored:

| Mutation | Observed |
|---|---|
| LF-only split (`else if (b == '\r')` disabled) | 6 of 11 LineSplitterTests failed, first BareCr_EndsALine |
| a CR ends the line at once (run not absorbed) | 5 failed, first CrCrLf_IsOneTerminator |
| line cap check disabled | LineAboveTheCap_IsEmittedInPieces, SmallCap_IsHonoured failed |
| a failed send classified ReadFailed instead of ClientGone | ClientDisconnects_MidStream_EndsTheSessionAndDisposesTheReadEnd failed at its 10 s guard |
| `CancelAfter(drainGrace)` removed | ChildExits_ButDescendantHoldsThePipe_DrainIsCutShortAfterTheGrace failed at its 10 s guard |
| `output.DisposeAsync()` removed from the relay finally | 3 CaptureRelayTests failed |
| final `splitter.Flush()` at EOF skipped | ChildExits_AndThePipeReachesEof_DrainCompletes failed |

## v2.3.1 integration (2026-09-30)

Merged tree = slices S + D on `integrate/v2.3.1`; Release build green; 105 unit tests pass
(the SourceHygieneTests case that was red on the v2.3.0 PipeServer text is green once the
service slice's ASCII fix is in). MSI content 7/7 at 2.3.1.

`tests/Invoke-ServiceHardening.ps1` (H1 to H6) against the installed 2.3.0, before the
upgrade, as the suite's mutation evidence: 1 pass / 5 fail. H1 left jobs listed until /kill,
H2 grew the handle count by 28 over five runs, H3 waited the full 20 s ping, H4's silent
connection was never closed (60 s guard), H6 exited 1 with nothing printed. H5 passed on 2.3.0
because the harness's process reader itself splits on CR and masked the embedded CRs; it now
reads the launcher's raw stdout bytes through a cmd file redirect, which fails on 2.3.0.

Install cycle with the 2.3.1 dev MSI over 2.3.0: 12/12, tray titled `RunAS Helper - v2.3.1`.
Smoke `-Integration`: 24 pass / 5 skip of 29 cases (this line first said 27, a hand count
made while the relaunched runner's RESULT line was being lost; see the v2.3.1 release
section) after one harness fix: B14 first failed on an event
count because `Get-WinEvent`'s StartTime filter truncates to whole seconds and picked up
B13's 1003 from the same second; the event helper now filters on the exact timestamp and
B14 matches the malformed token in the event text. B13, B14, B15 rerun: 3 pass. Regression
`-NewBuild`: 15 pass / 1 skip.

Hardening on 2.3.1: H2 to H6 pass on the first run (H3 returned in 3.8 s, H4 closed at
30.0 s, H5 three CRLF lines, H6 five 1 MiB pieces). H1 cleared its jobs but failed its
handle threshold twice (+3, then +37 over five runs) while H2 showed no growth: the service's
TOTAL handle count is not a leak instrument (a type histogram from an elevated `handle.exe`
showed the growth was Event +17, Thread +7, Mutant +2: thread-pool threads spun up by the
runs), so H1 and H2 now count Process-type handles, which is what 2.3.0 leaked: 0 before and
0 after on 2.3.1 for both. Harness self-test `-Integration`: 12 fired, 3 controls. The same
histogram taken a few minutes later had settled from 443 to 438 (Event 152, Thread 39,
Mutant 2, Process 0): the thread-pool growth unwinding, not a leak.

## Release v2.3.1 (2026-09-30, published from the tag)

`release.yml` run 36685012087 green. `Invoke-ReleaseVerify.ps1 -Tag v2.3.1`: 8 pass / 0 fail
on its third run. Asset `RunAsHelper-Setup-2.3.1.msi` (2,076,672 bytes, SHA-256
F7D9165D0AE7F31ED0CEEA94C7435754EA79FFC0FDAB0AC9961EF7B311D84BE5), signature Valid with
thumbprint 0EEBB64D...BAD5 and a timestamp; MSI content 7/7 at 2.3.1; the three binaries in
the administrative image Valid and timestamped; install cycle 12/12 with the tray titled
`RunAS Helper - v2.3.1`; smoke 24 pass / 5 skip (29 cases); regression 15 pass / 1 skip.

The install was a fresh one, not an upgrade: the 2.3.1 dev build had been removed with
`-UninstallOnly` so its unsigned files could not sit under the release's identical version.
That exposed a harness assumption. Run 1 died in `Env.ps1` (`Get-InstallDir`): the
`HKLM\SOFTWARE\RunAsHelper` key survives an uninstall (AllowedCallerSids stays) while
`InstallFolder` is removed, and `Set-StrictMode -Version Latest` turns the read of a missing
property into an error. Run 2 died in `Invoke-InstallCycle.ps1` `Get-Snapshot` on
`(Get-Service ...).Status.ToString()` with no service present. Both reads are guarded now,
along with the same shape in the I2 service poll, I6, R4's marker poll, R9's HKCU Run read
and the self-test's HKCU case: in pwsh 7 under that StrictMode a `$null.Property` read
throws (checked directly, "The property 'Foo' cannot be found on this object"), so a
`Wait-Until` condition written that way errors out instead of polling again. Run 3 passed
V6 from `service=absent tray=0`, which is the evidence for the guards.

Second finding from the same log: the smoke suite's RESULT line never reached the release
verify. With stdout redirected the suite relaunches itself in a hidden console and the parent
prints only the case lines the child tees into the result file; `Finish-Run` wrote the total
to the hidden console alone, so the parent showed every case and no total, and the exit code
was the only summary. `Finish-Run` now tees the RESULT line as well. Proof: `Invoke-Smoke.ps1
-Phase A -Only A1` with redirected stdout ends in `RESULT [smoke A]: 1 pass / 0 fail / 13
skip`; the release-verify3 log taken before the fix has no RESULT line between B15 and V7.

Service hardening on the released 2.3.1: 6 pass / 0 fail. H1 and H2 process handles 0 before
and 0 after (total handles 408, 400, 398 across the two, information only); H3 returned in
3.4 s; H4 dropped the silent connection at 30.0 s; H5 three CRLF-terminated redraw lines and
no embedded CR; H6 five pieces totalling 5,242,880 bytes, the longest 1,048,576.

## Hand-off (2026-09-30)

BL-45, `Invoke-Elevated` in tests/lib/Elevated.ps1. Nine `rah-tests-*` work folders were
left in %TEMP% after the release verify. The helper read rc.txt with
`[string](Get-Content -Raw ...)`, which is `$null` for an empty file in pwsh 7 (checked
directly), so `.Trim()` threw before the folder was removed; the timeout path also threw
before its cleanup. rc.txt is empty whenever the target records no exit code: a
GUI-subsystem target such as the installed exe, or a target that never started (the
release-verify2 suites ran with nothing installed). The comment above the code promised a
fallback to the runner's own exit code. For an empty rc.txt it never ran, because .Trim()
on $null threw first; when rc.txt was never written it did run, and returned the runner's
own code as the target's (S-K2 later measured 64 for a target that exits 7). The helper now
removes its folder on every path and throws "recorded no exit code" instead of returning a number.

Proof (`p7\elevated-proof.ps1`; it and every other `p7\` proof in this file are kept, as records, in `D:\.ai-work\ops\scripts\runas-helper\proofs\p7\`, outside the repo): E1, a console target running `exit 7`,
returns 7; E2, the installed exe, and E3, a missing path, both throw the named error and
report runner exit 0; no case leaves a folder. The same proof against the HEAD copy of the
helper: E1 PASS, E2 and E3 FAIL on "work folders left behind" with the old error "You cannot
call a method on a null-valued expression".

BL-46, the release prune. `.github/scripts/Remove-OldReleases.ps1` (proof
`p7\prune-proof.ps1`, never with `-Apply`): P1 a dry run on the real repo lists v2.1.5 and
v2.1.4 and deletes nothing; P2 `-Keep 10` prints "nothing to delete"; P3 a release whose tag
is not on the remote is skipped. 3 pass. Mutant M1 without the tag guard fails P3; mutant M2
without the dry-run guard (its delete command replaced by a print, and refused if it still
held one) fails P1. Each mutant: 2 pass / 1 fail. Every release tag is on the remote.

### Harness sweep

Dispositions are BACKLOG HS-01 to HS-32. Proofs (`p7\sweep-proofs.ps1`,
the function under test taken verbatim from each tree), run against the fixed harness and
against a worktree at 0b3e40e:

| Proof | Fixed harness | 0b3e40e |
|---|---|---|
| S-K2 Invoke-Elevated, TEMP holding a space | exit 7 | exit 64 |
| S-C13a Invoke-AdminRunner timeout | throws, no folder left | throws, rah-tests-68c642fb left |
| S-C13b runner that recorded no exit code | throws "recorded no exit code (runner exit 0)" | returned 0 |
| S-C5 Get-ActiveJobIds on a refused /jobs | throws "did not answer (exit 1)" | no ids, no error |
| S-C6 service identity | stable across reads; 49632 to 54004 across a restart | SKIP (no pin) |
| S-C12 help latency, missing launcher | throws "failed (exit -1)" | "median 6 ms" |
| S-K3 default folder, ProgramW6432 = D:\Program Files | `D:\Program Files\RunAsHelper` | `C:\Program Files\RunAsHelper` |
| S-K4 silent private pipe server, 3 s deadline | back after 3.0 s, 0 frames | SKIP (no -PipeName) |
| S-C2C3 release verify, a repo gh cannot find | V1 and V2 fail as assertions | V1 and V2 "harness error: The property ... cannot be found" |
| S-K1 smoke from a clone path with a space | A1 PASS and the RESULT line | exit 64, no output |
| S-K5 B8, TEMP with a space, an apostrophe, u-umlaut | PASS | FAIL at the pwsh row |
| S-K6 A13, wrong then real -ExpectedThumbprint | FAIL, then PASS | SKIP (no parameter) |
| S-W1 old marker form, child prints nothing | PASS on the echo alone (the weakness) | same |
| S-W2 child-computed marker | expanded in the child line, literal in the echo | same |

Fixed harness 14 pass / 0 fail; 0b3e40e 2 pass / 9 fail / 3 skip, the nine failures being
exactly the nine mutation proofs. The first fixed run failed S-K5 on the fix itself: the cmd row
called its wrapper by bare name, and agent shells set `NoDefaultCurrentDirectoryInExePath=1`,
which stops cmd searching its working directory. `.\via-cmd.cmd` passed in four folder
shapes (plain, space, apostrophe, u-umlaut), then S-K5 passed.

T2 on the real box (`p7\t2-proof.ps1`): uninstall, remove the HKCU Run value, install from
the bare profile. The 0b3e40e harness: 11 pass / 1 fail, T2 "HKCU Run value expected [] got
["C:\Program Files\RunAsHelper\RunAsHelper.exe" --tray]". After a second uninstall the fixed
harness: 12 pass / 0 fail. Both uninstalls ran through the rewritten Invoke-AdminRunner (5
pass each), and the Run value ended as it started.

Final rerun on the fixed harness against that fresh 2.3.1 install: smoke 25 pass / 0 fail / 5
skip (30 cases with B16; A6 skips because T1 left a tray open), regression 15 / 0 / 1,
hardening 6 / 0 / 0, self-test 16 mutations fired of 16 expected and 5 controls passed of 5
(new: MUT14, MUT15, MUT16, CTRL4, CTRL5). No `rah-*` entry left in %TEMP%.

### Fix review

A second read-only workflow reviewed the sweep's own diff: 23 findings, 22 confirmed and 1
refuted (BACKLOG HR-01 to HR-23). One was a product defect, BL-48.

BL-48, the pipe DACL after a trust change. On 2.3.1 (proof
`p7\v4-verify.ps1`: the change B4 makes, plus one restart): after `/trusted:remove` the live
DACL already lacked the SID's ACE, but after `/trusted:add`, following a restart, the SID had
no ACE of its own until the next restart. Private pipes, current user only
(`p7\npfs-experiment.ps1`): with one instance open, a second instance created with a wider
DACL left clients seeing the first DACL; SetAccessControl on a new instance created with
ChangePermissions applied the wider one. CreatePipe now does that on every new listener. On
the 2.3.2 dev build the v4-verify sequence was clean at every step.

B4 now checks the DACL after the remove and after the re-add. Its first version passed on
2.3.1, because both refreshes won the race in that run, so it could not be trusted. It now
holds an idle connection open across both changes, which takes the race away. With it: 2.3.1
FAIL "pipe DACL follows /trusted:remove within 5 s: unexpected allowed principal <this
account's SID> (ReadWrite, Synchronize)"; the 2.3.2 dev build PASS.

Other review fixes, proven: MUT17 (a private pipe whose NETWORK deny covers only
ChangePermissions) fires with "the NETWORK deny covers ChangePermissions, not FullControl";
S-K5b (TEMP holding U+2019): working tree PASS, a150167 FAIL at "pwsh caller selects pwsh
7"; T2 with the Run value cleared first: the dev install passed 12/12.

The 2.3.2 dev build (unsigned, `-p:ProductVersion=2.3.2`; its published service binary is
2.3.2 and carries one more SetAccessControl reference than 2.3.1's; 105 unit tests; MSI
content 7/7 at 2.3.2): install cycle 12/12 over 2.3.1, smoke 24 pass / 0 fail / 6 skip (A13
skips on an unsigned build), regression 15 / 0 / 1 (R12 now also requires three exit codes of
0), hardening 6 / 0 / 0, self-test 17 of 17 mutations fired and 5 of 5 controls passed, no
`rah-*` entry left in %TEMP%. That suite run used B4 before the held connection; the held
version passed on the same build afterwards. The dev build was then removed and the signed
2.3.1 release reinstalled (install cycle 10/10).

## Release v2.3.2 (2026-09-30, published from the tag)

`release.yml` run 36747013597 green. Its prune step, on its first live run: "prune: 6
release(s) in bigfnj/runas-helper; keeping the 3 most recent (deleting)", then "deleted
release ... (tag kept)" for v2.2.0, v2.1.5 and v2.1.4, and "3 of 3 old release(s) deleted".
The Releases page now holds v2.3.2, v2.3.1 and v2.3.0; all seven tags, v2.1.3 to v2.3.2,
are still on the remote.

`Invoke-ReleaseVerify.ps1 -Tag v2.3.2`: 8 pass / 0 fail. Asset `RunAsHelper-Setup-2.3.2.msi`
(2,076,672 bytes, SHA-256 80B321CA6A85708E54CD236983D8DC990F26B7C183DB23E60187831E3FE896E0),
signature Valid with thumbprint 0EEBB64D...BAD5 and a timestamp; MSI content 7/7 at 2.3.2; the
three binaries in the administrative image Valid and timestamped; install cycle over the 2.3.1
release 12/12 (T2 with the Run value cleared first); smoke 25 pass / 0 fail / 5 skip, with B4
(the held-connection DACL check) and B16 green on the published build; regression 15 / 0 / 1.
Then on the same install: service hardening 6 / 0 / 0 (H1 and H2 Process handles 0 before and
0 after), self-test 17 of 17 mutations fired and 5 of 5 controls passed, no `rah-*` entry left
in %TEMP%.

## History rewrite (2026-09-30)

The owner asked for the 63 commits that carried the work-domain identity to be rewritten
(BACKLOG L4-03). Measured first on a mirror clone of the remote: 147 commits reachable from two
branches and seven tags; 63 with the work domain as both author and committer, under three
spellings of the name; no commit message, tag message or file content anywhere in history
contained the employer string (message search, tag messages, content pickaxe: 0 each). 0 forks,
no branch protection, no rulesets.

Backups before anything changed: a mirror clone at
`D:\.ai-work\_backups\runas-helper-pre-rewrite-2026-09-30.git` (its GitHub remote removed, so no
fetch can overwrite it) and a verified bundle beside it.

Rehearsal on a scratch mirror: `git filter-repo --mailmap` mapping the one work-domain address to
the personal identity already on 37 commits, with the noreply identity left as it is. 147 commits
before and after; every branch and tag kept its tree and commit count, and each annotated tag
its tagger and message; 0 work-domain fields left; filter-repo translated the old hashes cited in
two commit messages. An independent review reproduced the result byte for byte, compared all 147
commits one by one (trees, mapped parents, dates, other identities: unchanged), and tried the
push's leases against a local copy of the remote, where every broken variant pushed nothing.

The push: release.yml disabled, then one atomic push straight to the URL, each of the nine refs
leased on the value the backup recorded; all nine were force-updated in place. The verifier,
first shown to fail against the backup (126 work-domain fields, every ref off), then passed
against GitHub: a fresh clone has 0 work-domain fields and 147 commits, all nine refs as pushed
with unchanged trees, `describe` gives v2.3.2, the three Releases kept their MSI digests and
updated_at, v2.3.2 is still latest, and release.yml started no run (39 before and after). It was
then re-enabled.

What the rewrite cannot reach: GitHub keeps serving the old commits by hash, and its
documentation says Support does not remove data that is not sensitive. The shipped 2.3.x
binaries' version strings carry pre-rewrite commit IDs. The commit hashes cited in BACKLOG.md and
in this file were remapped to the rewritten history (22 replacements). 18 old Release runs
(v1.1.0 to v2.1.5) returned the address through the Actions API; the owner approved deleting
them, and they were deleted the same day (each id now answers 404, and no remaining run's head
commit carries the work domain).

Measured after the push, from a fresh mirror and anonymous requests: the old commits are not
merely reachable by someone who already holds a hash. The repo's public activity feed names 110
pre-rewrite SHAs, including the before-SHAs of all three force pushes, and 31 of the 39
remaining Actions runs sit on pre-rewrite heads. Fetching the SHAs those records name brings
back 220 old commits, and 105 of them carry the work domain as author and committer: 63 from
this rewrite and 42 left by the 2026-09-03 one (a floor, since older objects that no record
names cannot be counted from outside). PLAN.md, AGENTS.md and CLAUDE.md, removed on 2026-09-03,
are fetchable by hash too; no version of them contains the employer name or the work domain.

The re-sync advice first given for other clones (fetch --force --tags, reset --hard
origin/main, re-point the docs branch) is not enough. In a sandbox clone that held the 13
retired release tags and a deleted branch, all three commands succeeded, and a dry-run push of
the tags would still have published 98 old commits, and of the branches 125, without force.
The corrected advice: re-clone, or run git fetch --prune --prune-tags --force origin, git reset
--hard origin/main, delete every local branch origin lacks, and re-point the docs branch. After
that, both dry-run pushes report everything up to date.

## Actions off Node 20 (BL-47, 2026-09-30)

One commit per action, each read against its changelog first and each with its own green CI
run: actions/checkout v4 to v7 (e0ef11a, CI 36762479003), actions/setup-dotnet v4 to v6
(9fc3daf, CI 36762739300), actions/upload-artifact v4 to v7 (7115bed, CI 36763042042; v5 still
declares Node 20, so it was skipped), and softprops/action-gh-release v2 to v3.0.3, pinned to
commit efb35369e0ad2afab669f228072c1b0d510eae64 (6dd09d8, CI 36764701678). From 7115bed on,
CI's Node 20 annotation names no action.

CI reaches neither release.yml step that changed: the upload runs only on a manual dispatch and
the publish only on a tag push. Dispatch dry run 36764740582, at 6dd09d8, was checked by a
script that requires the four new pins' download lines in "Set up job" (which lists every
action in the job, skipped steps included), the upload's "Artifact RunAsHelper-Setup has been
successfully uploaded" line and the artifact itself, the publish and prune steps skipped and
the upload step successful, as many Releases after the run as before, and no Node 20
annotation. It passed with 0 problems, and the signature step printed Valid for the MSI and
the three binaries. Mutation: the same checks run against the v2.3.2 tag run 36747013597,
which used the old pins, report 9 misses (the five log lines, the artifact, and the three step
outcomes). The checker is kept at `D:\.ai-work\ops\scripts\runas-helper\release-dryrun.ps1`
(`-RunId` validates an existing run instead of dispatching one).

The publish step had not run on v3 yet when this was written; the next tag push was to be its
first live run, with "Release ready at" in its log and no action named in a Node 20
annotation. It ran at the v2.3.3 tag push (release run 36821234383), clean on both counts; see
Release v2.3.3 below.

## How to Use on the installed 2.3.2 (2026-09-30)

The plan's real-app check asked for the How to Use window to show the new text, and the
integration record covered only the tray title. Run elevated against the installed 2.3.2 with
no tray running: the tray was started, its window title read "RunAS Helper - v2.3.2", Tools >
How to Use was opened through UI Automation scoped to the tray's own window, and the dialog was
captured with PrintWindow. It shows the 2.3.x text in the dark theme: RunAsHelper.com named as
the command-line launcher, and the /capture, /timeout, /ps:5|7 and /trusted syntax. The two
longest COMMAND LINE lines are cut off at the right edge, because the text box scrolls
vertically only (BL-49). Afterwards the tray was stopped, no RunAsHelper.exe was left running,
and the HKCU Run entry and settings.json were unchanged. The script and the screenshot are kept
at `D:\.ai-work\ops\scripts\runas-helper\howto-check.ps1` and `...\evidence\`.

## Release v2.3.3 (2026-09-30, dev build verified before the tag)

Scope, by owner decision: BL-49 and BL-50 only; BL-51 and BL-52 are ACCEPTED-RECORDED with
their triggers. No service or protocol source changed: `git diff v2.3.2 -- RunAsHelper.Service
RunAsHelper.Shared` is empty, and the product diff is one property in HelpForm.cs and one
registry value in Package.wxs.

BL-50 test: Invoke-MsiContent M8 requires an EventMessageFile row under the RunAsHelper
event-source key. Mutation: against the shipped 2.3.2 MSI it FAILS, "EventMessageFile rows
expected [1] got [0]" (5 pass / 1 fail / 2 skip, exit 1); against the 2.3.3 build it passes
(8 / 0 / 0). The first version of M8 failed on 2.3.2 for the wrong reason: `Key` and `Value`
are reserved words in MSI SQL, the unquoted query threw, and a try/catch read the throw as zero
rows, so a malformed query and an absent row looked the same. Found by dumping the Registry
table before trusting the result (the 2.3.2 MSI has two rows under that key, TypesSupported
and the uninstall marker). The helper now backquotes the columns, asks `_Tables` whether the
table exists, and lets any other failure propagate.

Dev build 2.3.3 (local, unsigned, `-p:ProductVersion=2.3.3`): install cycle 24 / 0 / 0 (install,
uninstall, reinstall); smoke 25 pass / 0 fail / 5 skip; regression 15 / 0 / 1; 105 unit tests.
The same totals as the published 2.3.2.

Live witnesses, each run on the installed 2.3.3 dev build and then on the published 2.3.2 after
it was reinstalled from the archive:

- BL-49, `howto-check.ps1 -ExpectHorizontalScrollbar yes` (elevated; opens Tools > How to Use on
  a tray it starts and stops, and reads the Edit child's window styles): on 2.3.3 WS_HSCROLL
  True, WS_VSCROLL True, RESULT PASS, and the capture shows the horizontal bar; on 2.3.2
  WS_HSCROLL False, RESULT FAIL, exit 9. The dialog text (11,026 characters through WM_GETTEXT)
  names RunAsHelper.com. The HKCU Run entry and settings.json were unchanged on both runs, and no
  tray was left running.
- BL-50, `eventlog-check.ps1`: on 2.3.3 the key holds EventMessageFile (REG_EXPAND_SZ, the file
  exists) and the newest event, 1005 "RunAsHelper service started.", renders as text
  (`-Expect yes` PASS); on 2.3.2 the key holds TypesSupported only and the same event id renders
  as the lookup-failure preface, with Get-WinEvent's Message null (`-Expect no` PASS).

The installed dev build's ProductVersion read 2.3.3+f8cdb0b..., the checkout's HEAD, so the
release build will name the tagged commit. Both witness scripts and the release-run checker
live in `D:\.ai-work\ops\scripts\runas-helper\`. The box was left on the published 2.3.2 for the
release verify to upgrade.

Published from the tag: release run 36821234383 on 00d486b, after CI run 36820998877 (which ran
M8 against its own build: 8 / 0 / 0). `release-run-verify.ps1 -Tag v2.3.3 -PrunedTag v2.3.0`
reported 0 problems: the publish step header names the pinned action-gh-release commit (its
first live run), the log shows "Creating new GitHub release", "Uploading", "Uploaded",
"Finalizing release", "Getting assets list" and "Release ready at", then "prune: deleted release
v2.3.0 (tag kept)" and "1 of 1", with none of the draft-reuse or retry lines and no Node 20
annotation; three Releases (v2.3.3 latest, v2.3.2, v2.3.1), eight tags on the remote, one asset
of 2,076,672 bytes whose SHA-256 (E250F924...151C) equals the downloaded file's, signature Valid
and timestamped. Checked against the v2.3.2 run as the negative control, the same script reports
the old action header and no prune of v2.3.0.

`Invoke-ReleaseVerify.ps1 -Tag v2.3.3 -WorkDir D:\.ai-work\_backups\runas-helper-release-verify\v2.3.3`:
8 pass / 0 fail / 0 skip (V1 to V8: run green, one asset downloaded, MSI signature Valid with the
expected thumbprint and a timestamp, MSI content at 2.3.3, the three binaries in the admin image
signed and timestamped, install cycle over the published 2.3.2, smoke and regression on the
released build). The suites' own totals were not captured separately this time; the same
suites on the 2.3.3 dev build the same evening gave 25 / 0 / 5 and 15 / 0 / 1. The durable
-WorkDir is what Windows Installer recorded as the product's source, so a repair no longer
depends on a temp folder. On the installed published build: all three binaries read
2.3.3+00d486b326c94001673a81f2a20dae675362d491 (`git tag --contains` gives v2.3.3), signatures
Valid; `howto-check.ps1 -ExpectHorizontalScrollbar yes` PASS and `eventlog-check.ps1 -Expect yes`
PASS (the newest event, 1002 "Launch succeeded", renders as text). The tray that the release
verify starts (-StartTray) was still running afterwards and was closed by hand, as the box was
found; no `rah-*` folder left in %TEMP% (the three from the direct install-cycle runs held only
msiexec logs and were removed); service Running.
