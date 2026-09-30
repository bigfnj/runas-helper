# RunAS Helper test harness

Two layers: xunit unit tests for the pure logic (`RunAsHelper.Tests`), and a
PowerShell 7 harness in this folder that drives the installed build end to end.

## Running

Unit tests (no elevation, no installed build needed):

```
dotnet test RunAsHelper.Tests\RunAsHelper.Tests.csproj -c Release
```

Harness (pwsh 7; run from the repo root):

```
pwsh -File tests\Invoke-Regression.ps1        # 2.2.0 behavior that must not change
pwsh -File tests\Invoke-Smoke.ps1 -Baseline   # new-feature cases; must fail on 2.2.0
pwsh -File tests\Invoke-Smoke.ps1 -Phase All -ExpectedVersion 2.3.0   # integration
pwsh -File tests\Invoke-Mutations.ps1         # harness self-test
pwsh -File tests\Invoke-MsiContent.ps1 -MsiPath <msi> [-ExpectedVersion X.Y.Z]
```

Every runner prints one line per case (`PASS`/`FAIL`/`SKIP <id> <name>`) and a final
`RESULT: n pass / m fail / k skip`, and exits 1 on any fail or when only skips ran.

## Elevation model

- Non-elevated (Medium) cases use the installed `RunAsHelper.exe` (2.2.0 form) or
  `RunAsHelper.com` (2.3.0) through `Invoke-Console`, with `__COMPAT_LAYER=RunAsInvoker`
  in the child environment and stdout piped. The caller's user SID must be a trusted
  command-line user, or the session gate open, for a launch to be authorized.
- Elevated cases use `Invoke-Elevated`, which starts a hidden elevated pwsh that writes
  its own `out.txt`/`rc.txt` (`-Verb RunAs` cannot combine with `-RedirectStandardOutput`).
  On this box UAC auto-consents, so elevation is silent. Elevated cases are tagged
  `needs-elevated` and skipped unless `-AllowElevated`.
- `/jobs`, `/kill`, `/joblog`, `setcli` and `/trusted` require the installed
  `RunAsHelper.exe` running elevated; from a Medium shell they return exit 1.

## The PATH refresh rule

The MSI broadcasts `WM_SETTINGCHANGE`, but already-running shells (including the agent
host and this VS Code session) keep their launch-time PATH. Cases that resolve
`RunAsHelper` by bare name call `Update-SessionPath` / `Get-RefreshedPathEnv`, which
rebuild PATH from the registry. In real use, open a new terminal after installing.

## The witness rule

The service echoes the command line back in its `Args detected` line, so a literal
marker in the command matches that echo, not the child. Assert only on values the
CHILD computes: `%COMPUTERNAME%` expansion, `PSV=n`, SIDs from `whoami /all`. Under
ConPTY a run whose witness (`cmd /c echo CONPTY-OK`) does not render is a harness
failure (`WITNESS-MISSING`), never a `PASS` or `FAIL`. Any script that uses ConPTY
must run with a real console: when its own stdout is redirected it re-launches itself
through `Start-Process pwsh -WindowStyle Hidden -Wait` and prints a results file
(see the relaunch guard in `Invoke-Smoke.ps1`). A run recording
`harness-stdout-is-console = False` is discarded.

## Linked-source purity rule (for slice A/B files)

`RunAsHelper.Tests` compiles the code under test with linked `Compile Include`, the way
the two apps link the protocol files, and never with a `ProjectReference`. Every linked
file must depend only on the base class library and on the other linked files: no Win32,
no WinForms, no `PipeClient`. A new dependency in one of these files breaks the test
build in CI, which is the alarm we want. Keep `PowerShellHost.cs`, `CliLaunchResult.cs`,
`CaptureDecoder.cs`, `LauncherLogic.cs` and the protocol records dependency-free.

## What cannot be tested on this box (recorded, not run)

- Standard-user and explicitly denied-user rows: no second account; creating one is a
  machine change. The SID-removed window (a `/trusted:remove` then re-add) proves the
  same authorization predicate for this user instead.
- Remote pipe access: single machine. The deny-NETWORK ACE is checked locally with
  `accesschk -nobanner \pipe\RunAsHelper`.
- Restricted tokens, the 128-entry trusted-list limit, a domain user, PID-reuse churn.
- A machine without pwsh: pwsh is installed; simulated only via an environment override.
- UAC-prompting machines and AppCompat disabled by policy: this box auto-consents.
- Windows Terminal: not installed; conhost is the only terminal backstop.
- Tray saved-entry `.ps1` host rule, the `/validate` dialog, dark mode: GUI, covered by
  the manual screenshot pass in the audit and by unit tests U4/B8 for the shared logic.

## Phase 1 scope

Slice C in Phase 1 runs only the read-only suites against the installed 2.2.0 build:
regression (passes), smoke `-Baseline` (fails on the new-feature cases), the mutation
self-test and the MSI-content check on the 2.2.0 MSI (fails). Install cycle and release
verify change machine state and run only at integration; in Phase 1 they accept
`-DryRun` and refuse otherwise.
