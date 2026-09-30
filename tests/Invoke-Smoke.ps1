#Requires -Version 7
<#
.SYNOPSIS
  New-feature smoke suite for v2.3.0: the console launcher, bare-name resolution,
  real exit codes, the PowerShell host rule, the /trusted verbs and the service fixes.
.DESCRIPTION
  -Baseline runs the new-feature cases against the installed 2.2.0 build, where every
  one of them must FAIL because the feature is not there yet. That FAIL set is the
  mutation evidence for the suite and is pasted into tests/MUTATIONS.md.

  -Integration runs against the installed 2.3.0 build and expects passes. It enables
  the integration-only cases (service stop/start, policy edits, the tray start through
  the .com, the framing soak) and the elevated ones; use it only from the integration
  owner's serial run, never from a parallel worktree.

  ConPTY cases need this process's stdout to be a real console. When it is redirected,
  the script re-launches itself through a hidden pwsh with a real console and prints
  the captured results, per the Phase 0 harness lesson.
#>
param(
    [ValidateSet('A', 'B', 'All')][string]$Phase = 'All',
    [string]$ExpectedVersion,
    [switch]$Baseline,
    [switch]$Integration,
    [switch]$AllowElevated,
    [switch]$KeepArtifacts,
    [string[]]$Only,
    [switch]$Relaunched,
    [string]$ResultFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\Elevated.ps1')
. (Join-Path $here 'lib\Fixtures.ps1')
. (Join-Path $here 'lib\EventLog.ps1')
. (Join-Path $here 'lib\ConPty.ps1')

$exe = Get-InstalledExe
$com = Get-InstalledCom
$svc = Get-InstalledService
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$cn = Get-ComputerNameLocal
$expectedThumbprint = '0EEBB64DCE430D98D2CA19DC3DC715DB9999BAD5'

# ConPTY relaunch guard: only when a ConPTY case will run (the .com is installed and
# this is not a baseline run) and this process's stdout is not a console.
$conPtyWillRun = (Test-Path $com) -and (-not $Baseline) -and ($Phase -in 'A', 'All')
if ($conPtyWillRun -and [Console]::IsOutputRedirected -and -not $Relaunched) {
    $rf = Join-Path ([IO.Path]::GetTempPath()) ("rah-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".log")
    $childArgs = @('-NoProfile', '-File', $PSCommandPath, '-Phase', $Phase, '-Relaunched', '-ResultFile', $rf)
    if ($ExpectedVersion) { $childArgs += @('-ExpectedVersion', $ExpectedVersion) }
    if ($Integration) { $childArgs += '-Integration' }
    if ($AllowElevated) { $childArgs += '-AllowElevated' }
    if ($KeepArtifacts) { $childArgs += '-KeepArtifacts' }
    if ($Only) { $childArgs += @('-Only', ($Only -join ',')) }
    # WaitForExit on the process itself, not Start-Process -Wait: -Wait also waits for
    # every descendant, and A6 starts the tray, which would keep this parent waiting
    # until the tray closes.
    $p = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList $childArgs
    $p.WaitForExit()
    if (Test-Path $rf) { Get-Content $rf | ForEach-Object { Write-Host $_ }; if (-not $KeepArtifacts) { Remove-Item $rf -ErrorAction SilentlyContinue } }
    exit $p.ExitCode
}

# In a relaunched instance, tee every case line into the result file for the parent.
$global:RahTee = $null
if ($Relaunched -and $ResultFile) { $global:RahTee = $ResultFile; Set-Content -Path $ResultFile -Value '' }

$skipTags = @()
if ($Baseline) { $skipTags += @('baseline-control', 'integration-only', 'gui') }
if (-not ($AllowElevated -or $Integration)) { $skipTags += 'needs-elevated' }
$filter = @{ SkipTags = $skipTags; Integration = [bool]$Integration }
if ($Only) { $filter.Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
Reset-Run -Filter $filter

if ($global:RahTee) {
    function Write-CaseLine {
        param([string]$Verdict, [string]$Id, [string]$Name, [string]$Detail = '')
        $line = if ($Detail) { "$Verdict $Id ${Name}: $Detail" } else { "$Verdict $Id $Name" }
        Write-Host $line
        Add-Content -Path $global:RahTee -Value $line
        $global:RahRun.Rows += [pscustomobject]@{ Verdict = $Verdict; Id = $Id; Name = $Name; Detail = $Detail }
    }
}

$W = New-WorkDir
$fx = New-Fixtures -WorkDir $W

function Get-Launcher {
    # Integration uses the installed .com; baseline has none, so the installed exe stands
    # in (which shows the 2.3.0 pipe-level behavior absent on 2.2.0).
    if (Test-Path $com) { $com } else { $exe }
}

function Invoke-AdminPwsh {
    # Run one PowerShell command with administrator rights (silent on this box).
    param([Parameter(Mandatory)][string]$Command)
    Invoke-Elevated -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-Command', $Command)
}

try {
    if ($Phase -in 'A', 'All') {
        Invoke-Case -Id 'A1' -Name 'RunAsHelper.com exists, console subsystem, exe is GUI' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present at $com"
            Assert-Equal 3 (Get-PeSubsystem $com) '.com PE subsystem (3 = console)'
            Assert-Equal 2 (Get-PeSubsystem $exe) '.exe PE subsystem (2 = GUI)'
            if ($ExpectedVersion) { Assert-Equal $ExpectedVersion (Get-FileVersionOf $com) '.com FileVersion' }
        }

        Invoke-Case -Id 'A2' -Name '.com --help returns the help text, exit 0' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (2.3.0 launcher)"
            Test-ConPtyWitness | Out-Null
            $r = Invoke-ConPty -CommandLine ('"' + $com + '" --help') -TimeoutMs 15000
            Assert-ExitCode 0 $r.ExitCode
            Assert-Match 'COMMAND LINE' $r.Text 'help text'
        }

        Invoke-Case -Id 'A3' -Name 'bare RunAsHelper resolves to the .com' -Test {
            Update-SessionPath
            $cmd = Get-Command RunAsHelper -ErrorAction SilentlyContinue
            Assert-True ($null -ne $cmd) 'RunAsHelper resolves on PATH'
            Assert-Match 'RunAsHelper\.com$' $cmd.Source 'resolved path ends in .com'
        }

        Invoke-Case -Id 'A4' -Name 'Medium /jobs via .com returns 1 with the elevated-required text' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present"
            $r = Invoke-Console -FilePath $com -ArgumentList @('/jobs') -TimeoutSec 20 -Env $comp
            Assert-ExitCode 1 $r.ExitCode
            Assert-Match 'running elevated' ($r.Stdout + $r.Stderr) 'elevated-required text'
        }

        Invoke-Case -Id 'A5' -Name 'direct exe without compat still fails Win32 740' -Tags @('baseline-control') -Test {
            $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 15
            Assert-Equal 740 $r.Win32Code 'Win32 error unchanged from 2.2.0'
        }

        Invoke-Case -Id 'A6' -Name '.com with no arguments starts the tray and returns at once' -Tags @('integration-only') -Test {
            # A second tray instance exits silently on the single-instance mutex, so this
            # only proves something when no tray is running.
            if (@(Get-Process RunAsHelper -ErrorAction SilentlyContinue).Count -gt 0) {
                Skip-Case -Reason 'a tray is already running; the exe start is covered by Invoke-InstallCycle T1'
            }
            $r = Invoke-Console -FilePath $com -ArgumentList @() -TimeoutSec 15
            Assert-ExitCode 0 $r.ExitCode
            Assert-True ($r.DurationMs -lt 8000) "returned in $($r.DurationMs) ms without waiting for the tray"
            $up = Wait-Until -TimeoutSec 20 -Condition { @(Get-Process RunAsHelper -ErrorAction SilentlyContinue).Count -gt 0 }
            Assert-True $up 'a RunAsHelper process appeared within 20 s'
        }

        Invoke-Case -Id 'A7' -Name 'Ctrl+C to the .com ends the child and exits 0xC000013A' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (2.3.0 launcher)"
            $r = Invoke-ConPty -CommandLine ('"' + $com + '" /capture /timeout:20 cmd /c "ping -n 15 127.0.0.1 >nul"') -TimeoutMs 12000 -CtrlCAfterMs 2000
            Assert-Equal (-1073741510) $r.ExitCode 'Ctrl+C exit code 0xC000013A'
            Assert-Match 'still be running' $r.Text 'hint line about the elevated target'
        }

        Invoke-Case -Id 'A8' -Name '.com renders on a real console (witness + help)' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (2.3.0 launcher)"
            Test-ConPtyWitness | Out-Null
            $r = Invoke-ConPty -CommandLine ('"' + $com + '" --help') -TimeoutMs 15000
            Assert-Match 'COMMAND LINE' $r.Text 'help renders on a real console'
        }

        Invoke-Case -Id 'A9' -Name 'service exe run from a shell refuses with one line, exit 1' -Tags @('integration-only') -Test {
            $r = Invoke-Console -FilePath $svc -ArgumentList @() -TimeoutSec 15
            Assert-True (-not $r.TimedOut) 'the host exited on its own'
            Assert-ExitCode 1 $r.ExitCode
            Assert-Match 'Service Control Manager' ($r.Stdout + $r.Stderr) 'refusal text'
            Assert-Equal 'Running' ((Get-Service RunASHelper).Status.ToString()) 'installed service still Running'
        }

        Invoke-Case -Id 'A10' -Name 'built MSI content' -Tags @('integration-only') -Test {
            Skip-Case -Reason 'covered by Invoke-MsiContent.ps1 (run against the built MSI)'
        }

        Invoke-Case -Id 'A11' -Name 'install folder is on the machine PATH; value kind unchanged' -Tags @('integration-only') -Test {
            $entries = @(Get-MachinePathEntries)
            Assert-True ($entries -contains (Get-InstallDir).TrimEnd('\')) 'machine PATH contains the install folder'
            Assert-Equal 'ExpandString' (Get-MachinePathValueKind) 'machine PATH registry value kind'
        }

        Invoke-Case -Id 'A12' -Name 'uninstall removes only the PATH entry, keeps trusted list' -Tags @('integration-only') -Test {
            Skip-Case -Reason 'covered by Invoke-InstallCycle -Cycle (U4/U5)'
        }

        Invoke-Case -Id 'A13' -Name 'installed binaries are signed with the expected thumbprint' -Tags @('integration-only') -Test {
            if ((Get-AuthenticodeSignature $com).Status.ToString() -eq 'NotSigned') { Skip-Case -Reason 'unsigned dev build' }
            foreach ($f in $com, $exe, $svc) {
                $s = Get-AuthenticodeSignature $f
                Assert-Equal 'Valid' $s.Status.ToString() "$([IO.Path]::GetFileName($f)) signature status"
                Assert-Equal $expectedThumbprint $s.SignerCertificate.Thumbprint "$([IO.Path]::GetFileName($f)) thumbprint"
                Assert-True ($null -ne $s.TimeStamperCertificate) "$([IO.Path]::GetFileName($f)) is timestamped"
            }
        }

        Invoke-Case -Id 'A14' -Name 'upgrade preserves settings.json and HKCU Run' -Tags @('integration-only') -Test {
            Skip-Case -Reason 'covered by Invoke-InstallCycle (I8/I9)'
        }
    }

    if ($Phase -in 'B', 'All') {
        Invoke-Case -Id 'B1' -Name '/capture passes the child exit code through (3, 7, 0)' -Test {
            $l = Get-Launcher
            $r3 = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'cmd', '/c', 'exit', '3')
            Assert-ExitCode 3 $r3.ExitCode
            $r7 = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'powershell', '-NoProfile', '-Command', 'exit 7')
            Assert-ExitCode 7 $r7.ExitCode
            $r0 = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'cmd', '/c', 'exit', '0')
            Assert-ExitCode 0 $r0.ExitCode
            Assert-Match 'Process exited with code 3' $r3.Stdout 'exit log line'
        }

        Invoke-Case -Id 'B2' -Name '/timeout expiry exits 124' -Test {
            $l = Get-Launcher
            $r = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:2', 'cmd', '/c', 'ping -n 4 127.0.0.1 >nul')
            Assert-ExitCode 124 $r.ExitCode
            Assert-Match '\[timeout\]' $r.Stdout 'timeout log line'
        }

        Invoke-Case -Id 'B3' -Name 'no /capture stays fire-and-forget (exit 0)' -Tags @('baseline-control') -Test {
            $l = Get-Launcher
            $r = Invoke-Console -FilePath $l -TimeoutSec 20 -Env $comp -ArgumentList @('cmd', '/c', 'exit', '3')
            Assert-ExitCode 0 $r.ExitCode
        }

        Invoke-Case -Id 'B4' -Name 'gate-closed untrusted caller exits 1 with "disabled"; trust restored' -Tags @('needs-elevated', 'integration-only') -Test {
            $sid = Get-CallerSid
            $add = $null
            try {
                $rm = Invoke-Elevated -FilePath $com -ArgumentList @('/trusted:remove', $sid)
                Assert-ExitCode 0 $rm.ExitCode
                Assert-Match 'Removed' $rm.Text 'remove confirmation'
                $den = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', 'echo', "WITNESS-$cn")
                Assert-ExitCode 1 $den.ExitCode
                Assert-Match 'Command line is disabled' $den.Stdout 'denial text'
                Assert-NotMatch "WITNESS-$cn" $den.Stdout 'the child did not run'
            } finally {
                $add = Invoke-Elevated -FilePath $com -ArgumentList @('/trusted:add', $sid)
            }
            Assert-ExitCode 0 $add.ExitCode
            Assert-Match 'Trusted:' $add.Text 'add confirmation'
            $ok = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', 'echo', "WITNESS-$cn")
            Assert-ExitCode 0 $ok.ExitCode
            Assert-Match "WITNESS-$cn" $ok.Stdout 'launch allowed again after re-add'
        }

        Invoke-Case -Id 'B5' -Name 'service down: help still works, launch exits 1; service restarts' -Tags @('needs-elevated', 'integration-only') -Test {
            try {
                $stop = Invoke-AdminPwsh 'Stop-Service RunASHelper -Force; (Get-Service RunASHelper).Status.ToString()'
                Assert-Match 'Stopped' $stop.Text 'service reported Stopped'
                $gone = Wait-Until -TimeoutSec 20 -Condition { -not (Test-PipePresent) }
                Assert-True $gone 'pipe disappeared within 20 s'
                $h = Invoke-Console -FilePath $com -ArgumentList @('--help') -TimeoutSec 20 -Env $comp
                Assert-ExitCode 0 $h.ExitCode
                $l = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', 'echo', "WITNESS-$cn")
                Assert-ExitCode 1 $l.ExitCode
                Assert-Match 'Could not connect' $l.Stdout 'unreachable-service text'
            } finally {
                Invoke-AdminPwsh 'Start-Service RunASHelper' | Out-Null
            }
            Assert-True (Wait-ForPipe -TimeoutSec 30) 'pipe back within 30 s'
            Assert-Equal 'Running' ((Get-Service RunASHelper).Status.ToString()) 'service Running again'
        }

        Invoke-Case -Id 'B6' -Name 'elevated /joblog on a missing job exits 1' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/joblog:999999')
            Assert-ExitCode 1 $r.ExitCode
        }

        Invoke-Case -Id 'B7' -Name '10 streaming captures with /timeout:1 all exit 124 with intact framing' -Tags @('integration-only') -Test {
            # A steady stream (one line every 100 ms for 4 s) keeps the service's stdout
            # pump writing while the 1 s timeout fires, which is the race the serialized
            # writer closes. Volume stays small on purpose: Invoke-Console reads through
            # PowerShell event handlers and cannot drain hundreds of thousands of lines,
            # and a stalled reader holds the whole chain back (that is backpressure, not
            # a defect).
            $codes = @()
            $bad = 0
            foreach ($i in 1..10) {
                $r = Invoke-Console -FilePath $com -TimeoutSec 40 -Env $comp -ArgumentList @(
                    '/capture', '/timeout:1', '/as:system', 'powershell', '-NoProfile', '-Command',
                    "1..40 | ForEach-Object { 'L' + `$_; Start-Sleep -Milliseconds 100 }")
                $codes += $r.ExitCode
                if (($r.Stdout + $r.Stderr) -match 'Pipe communication error') { $bad++ }
            }
            Assert-Equal 0 $bad 'runs that reported a pipe communication error'
            Assert-Equal 10 @($codes | Where-Object { $_ -eq 124 }).Count "runs that exited 124 (codes: $($codes -join ','))"
            Wait-Until -TimeoutSec 60 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        }

        Invoke-Case -Id 'B8' -Name 'caller-shell host rule picks pwsh from pwsh, 5.1 from cmd' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (host rule is a 2.3.0 client feature)"
            $v = $fx.Version
            $env2 = Get-RefreshedPathEnv
            $r = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-Command', "& '$com' /capture /timeout:30 '$v'")
            Assert-Match 'PSV=7' $r.Stdout 'pwsh caller selects pwsh 7'
            Assert-Match 'PowerShell host: pwsh' $r.Stdout 'client host line names pwsh'
            $r2 = Invoke-Console -FilePath 'powershell' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "& '$com' /capture /timeout:30 '$v'")
            Assert-Match 'PSV=5' $r2.Stdout 'Windows PowerShell caller selects 5.1'
            $r3 = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-Command', "& '$com' /capture /timeout:30 /ps:5 '$v'")
            Assert-Match 'PSV=5' $r3.Stdout '/ps:5 pins 5.1 from pwsh'
            $r4 = Invoke-Console -FilePath 'powershell' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "& '$com' /capture /timeout:30 '$($fx.Requires7)'")
            Assert-Match 'PSV=7' $r4.Stdout '#Requires -Version 7 picks pwsh from Windows PowerShell'
            $r5 = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-Command', "& '$com' /capture /timeout:30 '$($fx.Desktop)'")
            Assert-Match 'PSV=5' $r5.Stdout '#Requires -PSEdition Desktop picks 5.1 from pwsh'
            # cmd /c mangles a command line that starts with a quote and holds more quotes
            # (it strips the first and last), and ArgumentList would escape the inner
            # quotes with backslashes, so the cmd row runs a small .cmd wrapper from the
            # space-free work dir instead. The caller of the .com is then cmd.exe.
            $wrap = Join-Path $W 'via-cmd.cmd'
            Set-Content -LiteralPath $wrap -Encoding ASCII -Value ('@"' + $com + '" /capture /timeout:30 "' + $v + '"')
            $r6 = Invoke-Console -FilePath 'cmd' -TimeoutSec 40 -Env $env2 -ArgumentList @('/d', '/c', $wrap)
            Assert-Match 'PSV=5' $r6.Stdout 'cmd caller falls back to 5.1'
            Assert-Match '\(default\)' $r6.Stdout 'reason is default for a cmd caller'
            $r7 = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-Command', "& '$com' /capture /timeout:30 '$($fx.Args)' alpha 'b c'")
            Assert-Match 'ARGC=2' $r7.Stdout 'two arguments reach the script'
            Assert-Match 'ARG=\[b c\]' $r7.Stdout 'a quoted argument survives the rewrite'
            $r8 = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env $env2 -ArgumentList @('-NoProfile', '-Command', "& '$com' /capture /timeout:30 powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$v'")
            Assert-Match 'PSV=5' $r8.Stdout 'an explicit host runs as given'
            Assert-NotMatch 'PowerShell host:' $r8.Stdout 'no host line when the target is not a .ps1'
        }

        Invoke-Case -Id 'B9' -Name 'tray saved-entry .ps1 host rule' -Tags @('gui') -Test { }

        Invoke-Case -Id 'B10' -Name '/trusted: elevated list shows the caller, group refused, Medium denied' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/trusted')
            Assert-ExitCode 0 $r.ExitCode
            Assert-Match ([regex]::Escape((Get-CallerSid))) $r.Text 'trusted list shows the caller SID'
            $g = Invoke-Elevated -FilePath $l -ArgumentList @('/trusted:add', 'BUILTIN\Administrators')
            Assert-ExitCode 1 $g.ExitCode
            $m = Invoke-Console -FilePath $l -ArgumentList @('/trusted') -TimeoutSec 20 -Env $comp
            Assert-ExitCode 1 $m.ExitCode
            Assert-Match 'elevated' ($m.Stdout + $m.Stderr) 'Medium caller is told an elevated shell is needed'
        }

        Invoke-Case -Id 'B11' -Name 'event Source is cli for a CLI launch' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $t = Get-Date
            Invoke-Console -FilePath $l -TimeoutSec 20 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', "echo", "WITNESS-$cn") | Out-Null
            $ev = @(Get-RunAsHelperEvents -Id 1001 -Since $t)
            Assert-True ($ev.Count -ge 1) '1001 launch event present'
            Assert-Match 'Source: cli' (Get-RunAsHelperEventText $ev[0]) 'launch event Source is cli'
        }

        Invoke-Case -Id 'B12' -Name 'elevated capture is treated as the tray identity' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $t = Get-Date
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', "echo", "WITNESS-$cn")
            Assert-ExitCode 0 $r.ExitCode
            $ev = @(Get-RunAsHelperEvents -Id 1001 -Since $t)
            Assert-True ($ev.Count -ge 1) '1001 launch event present'
            Assert-Match 'Source: tray' (Get-RunAsHelperEventText $ev[0]) 'elevated installed caller logs Source: tray'
        }
    }
} finally {
    if (-not $KeepArtifacts) { Remove-Fixtures -WorkDir $W }
}

$title = if ($Baseline) { "smoke -Baseline (expect all-fail on 2.2.0)" } else { "smoke $Phase" }
Finish-Run -Title $title
