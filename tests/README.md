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
pwsh -File tests\Invoke-Mutations.ps1 [-BaselineMsi <2.2.0 msi>]   # harness self-test
pwsh -File tests\Invoke-MsiContent.ps1 -MsiPath <msi> [-ExpectedVersion X.Y.Z]
```

`Invoke-Mutations.ps1` needs a copy of the 2.2.0 release MSI for its two MSI cases
(MUT4, CTRL2); without one they SKIP with a reason and the rest of the self-test still
runs. Its reconciliation counts only assertion failures as fired mutations: a case that
failed with `harness error:` is listed separately and fails the run.

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
- `/jobs`, `/kill`, `/joblog` and `/trusted` require the installed `RunAsHelper.exe`
  running elevated; from a Medium shell they return exit 1. (The `setcli` pipe verb has
  the same gate, but only the tray sends it; there is no CLI switch for it.)
- A raw pipe client (`lib/RawPipe.ps1`, `Send-RawPipeRequest`) sends one hand-built
  frame to `\\.\pipe\RunAsHelper` and returns the reply frames, for cases the shipped
  client cannot express (B13 sends an unknown verb).

## Where the installed build is

`lib/Env.ps1` resolves the install folder from `RAH_INSTALL_DIR` in the environment,
then the `InstallFolder` value the MSI writes under `HKLM\SOFTWARE\RunAsHelper`, then
`C:\Program Files\RunAsHelper`. Set `RAH_INSTALL_DIR` to a scratch folder holding a
freshly built `RunAsHelper.exe` next to a copy of the installed `RunAsHelper.com` to smoke
a client change against the installed service without installing anything (the launcher
looks for the exe in its own folder).

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
  the manual screenshot pass in the audit; the shared host logic is covered by the
  PowerShellHostTests unit tests and by harness cases U4 (install cycle) and B8 (smoke).
  The GUI placeholders B9 and R16 always record SKIP (their body is `Skip-Case`), never a
  vacuous PASS.

## Integration and release runs

The suites have two modes. Against a 2.2.0 install, `Invoke-Regression.ps1` must pass and
`Invoke-Smoke.ps1 -Baseline` must fail on every new-feature case. Against the new build:

```
pwsh -File tests\Invoke-InstallCycle.ps1 -MsiPath <msi> -ExpectedVersion X.Y.Z -Cycle -StartTray
pwsh -File tests\Invoke-Smoke.ps1 -Phase All -ExpectedVersion X.Y.Z -Integration
pwsh -File tests\Invoke-Regression.ps1 -NewBuild -AllowElevated -AllowMachineWrites -ExpectedVersion X.Y.Z
pwsh -File tests\Invoke-AuditProbes.ps1
pwsh -File tests\Invoke-ReleaseVerify.ps1 -Tag vX.Y.Z      # after the release workflow is green
```

`-Integration` (smoke) and `-NewBuild` (regression) enable the cases tagged
`integration-only` and `needs-elevated`, including the ones that edit the trusted-caller
policy, stop and start the service, and start the tray through the launcher. `-Only A1,B8`
runs a subset. The install cycle, the audit probes and the release verify change machine
state (the installed product, the service, the machine PATH); `-DryRun` prints their steps.
`Invoke-InstallCycle.ps1 -UninstallOnly` removes the installed product (snapshot, stop the
tray, `msiexec /x`, verify the removal) and stops, for taking a dev build off the box
before installing a release of the same version.

Two lessons from the first integration run, both now built into the harness: a script
that starts the tray must wait on its child process with `WaitForExit()` rather than
`Start-Process -Wait`, because `-Wait` also waits for every descendant and the tray is
one; and `Get-WinEvent` leaves `Message` empty for the RunAsHelper source (it registers
no message file), so read the text with `Get-RunAsHelperEventText`, which falls back to
the event's string properties.
