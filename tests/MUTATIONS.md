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
| R5, R13 | `harness error: You cannot call a method on a null-valued expression` | the elevated runner's `&` does not wait for a GUI exe and records no exit code, so rc.txt was empty | elevated calls use RunAsHelper.com; the runner falls back to its own exit code |
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
Smoke `-Integration`: 27 pass / 5 skip after one harness fix: B14 first failed on an event
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
0 after on 2.3.1 for both. Harness self-test `-Integration`: 12 fired, 3 controls.
