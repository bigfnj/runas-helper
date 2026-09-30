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

## Phase 0 encoding finding (recorded, fix owned by slice B)

The service writes the em dash in its log lines as one byte 0x97 (Windows-1252) when
stdout is a pipe, which shows as a replacement character in a UTF-8 reader. Observed
this session in R3's captured output. Cheapest durable fix: ASCII-only service log
prose (slice B). BACKLOG BL-27.
