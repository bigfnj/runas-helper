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
witness is missing is a harness failure, not a product result (see PROBE-RESULTS.md).

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
