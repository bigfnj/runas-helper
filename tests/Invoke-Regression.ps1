#Requires -Version 7
<#
.SYNOPSIS
  The 2.2.0 behavior suite: what must not change in v2.3.0. Every case not tagged
  [changed-in-2.3.0] must PASS against the installed 2.2.0 build.
.DESCRIPTION
  Read-only cases run by default. Cases that would write persistent machine state
  (R4 writes an HKLM scratch key) are suppressed unless -AllowMachineWrites, and
  cases needing an elevated tray identity are suppressed unless -AllowElevated, so
  a Phase 1 run honors the "no machine state outside the worktree" rule. GUI and
  install-cycle cases are integration-only and skip with a reason.

  Non-elevated capture cases use the installed RunAsHelper.exe through Invoke-Console
  with __COMPAT_LAYER=RunAsInvoker and stdout piped (the 2.2.0-supported form).
#>
param(
    [string]$ExpectedVersion,
    [switch]$AllowElevated,
    [switch]$AllowMachineWrites
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

$exe = Get-InstalledExe
$svc = Get-InstalledService
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$cn = Get-ComputerNameLocal
$runStart = Get-Date

$skipTags = @()
if (-not $AllowMachineWrites) { $skipTags += 'machine-write' }
if (-not $AllowElevated) { $skipTags += 'needs-elevated' }
Reset-Run -Filter @{ OnTwoTwoZero = $true; SkipTags = $skipTags }

$W = New-WorkDir
$fx = New-Fixtures -WorkDir $W
try {
    Invoke-Case -Id 'R1' -Name 'exe --help via RunAsInvoker returns help, exit 0' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match 'TrustedInstaller' $r.Stdout 'help text'
        Assert-True ($r.Lines.Count -ge 100) "help line count $($r.Lines.Count) >= 100"
    }

    Invoke-Case -Id 'R2' -Name 'direct exe --help without compat fails Win32 740' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 15
        Assert-Equal 740 $r.Win32Code 'Win32 error'
    }

    Invoke-Case -Id 'R3' -Name 'capture echo witness as system, warns without timeout' -Test {
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/as:system', 'cmd', '/c', "echo", "WITNESS-$cn")
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match "WITNESS-$cn" $r.Stdout 'child witness'
        Assert-Match '\[warning\] /capture used without /timeout' $r.Stdout 'no-timeout warning'
    }

    Invoke-Case -Id 'R4' -Name '.reg host imports and .cmd host runs' -Tags @('machine-write') -Test {
        # Writes an HKLM scratch key via the regedit host, then reads it back. Suppressed
        # in Phase 1 by the machine-write tag; runs at integration where cleanup follows.
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @($fx.Reg)
        Assert-ExitCode 0 $r.ExitCode
        $ok = Wait-Until -TimeoutSec 10 -Condition {
            (Get-ItemProperty 'HKLM:\SOFTWARE\RunAsHelperSmoke' -Name Marker -ErrorAction SilentlyContinue).Marker -eq $fx.Guid
        }
        Assert-True $ok 'HKLM marker equals the fixture guid'
        $r2 = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', $fx.Cmd)
        Assert-Match "CMDHOST-$cn" $r2.Stdout 'cmd host output'
    }

    Invoke-Case -Id 'R5' -Name 'elevated /jobs lists an in-flight capture' -Tags @('needs-elevated') -Test {
        $job = Start-Job -ScriptBlock {
            param($exe, $comp)
            $psi = [Diagnostics.ProcessStartInfo]::new($exe)
            'RunAsInvoker' | Out-Null
            $env:__COMPAT_LAYER = 'RunAsInvoker'
            & $exe /capture /timeout:20 /as:system cmd /c "ping -n 12 127.0.0.1 >nul" | Out-Null
        } -ArgumentList $exe, $comp
        try {
            Start-Sleep -Seconds 3
            $r = Invoke-Elevated -FilePath $exe -ArgumentList @('/jobs')
            Assert-ExitCode 0 $r.ExitCode
            Assert-Match 'Slots in use:' $r.Text 'jobs header'
            Assert-Match 'ping -n 12' $r.Text 'job command'
        } finally {
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            Wait-Until -TimeoutSec 20 -Condition { @(Get-ServiceChildren).Count -eq 0 } | Out-Null
        }
    }

    Invoke-Case -Id 'R6' -Name 'Medium /jobs returns 1 with the elevated-required message' -Test {
        $r = Invoke-Console -FilePath $exe -ArgumentList @('/jobs') -TimeoutSec 20 -Env $comp
        Assert-ExitCode 1 $r.ExitCode
        # The denial is written to stderr, so match the combined streams.
        Assert-Match 'installed RunAsHelper\.exe running elevated' ($r.Stdout + $r.Stderr) 'elevated-required text'
    }

    Invoke-Case -Id 'R7' -Name 'trusted-callers list matrix (elevated)' -Tags @('needs-elevated') -Test {
        $r = Invoke-Elevated -FilePath $exe -ArgumentList @('/listtrustedcallers') 2>$null
        # 2.2.0 exposes listtrustedcallers via the elevated installed exe; the caller SID
        # was added in Phase 0, so it must appear.
        Assert-Match (Get-CallerSid) $r.Text 'caller SID present in trusted list'
    }

    Invoke-Case -Id 'R10' -Name 'as:ti and as:system both run as SYSTEM; TI group only for ti' -Test {
        # whoami /all carries both the User section (S-1-5-18 for the LocalSystem account)
        # and the Groups section (the TrustedInstaller group, present only for /as:ti).
        # whoami /groups alone would not show S-1-5-18, which is a user SID, not a group.
        $ti = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/as:ti', 'whoami', '/all')
        $sys = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/as:system', 'whoami', '/all')
        Assert-Match 'S-1-5-18' $ti.Stdout 'ti run runs as the SYSTEM account'
        Assert-Match 'S-1-5-18' $sys.Stdout 'system run runs as the SYSTEM account'
        $tiGroup = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
        Assert-Match $tiGroup $ti.Stdout 'ti run carries the TrustedInstaller group'
        Assert-NotMatch $tiGroup $sys.Stdout 'system run has no TrustedInstaller group'
    }

    Invoke-Case -Id 'R11' -Name 'priority /p:2 yields an Idle child' -Test {
        $r = Invoke-Console -FilePath $exe -TimeoutSec 30 -Env $comp -ArgumentList @(
            '/capture', '/p:2', '/as:system', 'powershell', '-NoProfile', '-Command',
            '(Get-Process -Id $PID).PriorityClass')
        Assert-Match 'Idle' $r.Stdout 'child priority class'
    }

    Invoke-Case -Id 'R12' -Name 'three captures run in parallel, not serialized' -Test {
        $jobs = 1..3 | ForEach-Object {
            Start-Job -ScriptBlock {
                param($exe)
                $env:__COMPAT_LAYER = 'RunAsInvoker'
                & $exe /capture /timeout:15 /as:system cmd /c "ping -n 6 127.0.0.1 >nul" | Out-Null
            } -ArgumentList $exe
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $jobs | Wait-Job -Timeout 30 | Out-Null
        $sw.Stop()
        $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
        Assert-InRange $sw.Elapsed.TotalSeconds 0 14 'wall clock for three parallel 6s pings'
    }

    Invoke-Case -Id 'R13' -Name 'elevated /kill terminates a job and logs 1006' -Tags @('needs-elevated') -Test {
        Skip-Case -Reason 'runs at integration: needs a stable job id round-trip through elevated /jobs then /kill'
    }

    Invoke-Case -Id 'R14' -Name 'old 2.2.0 client copy stays compatible with the service' -Test {
        $old = Join-Path (Split-Path $exe) 'RunAsHelper.exe'
        $backup = 'D:\.ai-work\_backups\runas-helper-2.2.0-baseline\RunAsHelper.exe'
        $client = if (Test-Path $backup) { $dst = Join-Path $W 'old-client.exe'; Copy-Item $backup $dst -Force; $dst } else { $old }
        $r = Invoke-Console -FilePath $client -TimeoutSec 30 -Env $comp `
            -ArgumentList @('/capture', '/as:system', 'cmd', '/c', "echo", "WITNESS-$cn")
        Assert-ExitCode 0 $r.ExitCode
        Assert-Match "WITNESS-$cn" $r.Stdout 'old client witness'
        $j = Invoke-Console -FilePath $client -ArgumentList @('/jobs') -TimeoutSec 20 -Env $comp
        Assert-ExitCode 1 $j.ExitCode
    }

    Invoke-Case -Id 'R15' -Name 'no 1099 crash events during the run and no crash.log' -Test {
        $events = @(Get-RunAsHelperEvents -Id 1099 -Since $runStart)
        Assert-Equal 0 $events.Count '1099 crash events since run start'
        $crash = Join-Path $env:AppData 'RunAsHelper\crash.log'
        Assert-True (-not (Test-Path $crash)) 'crash.log absent'
    }

    Invoke-Case -Id 'R8' -Name 'tray window title carries the version' -Tags @('integration-only') -Test { }
    Invoke-Case -Id 'R9' -Name 'HKCU Run entry restored after a tray launch' -Tags @('integration-only') -Test { }
    Invoke-Case -Id 'R16' -Name 'validate dialog, tray panes, dark mode' -Tags @('gui') -Test { }
} finally {
    Remove-Fixtures -WorkDir $W
}

Finish-Run -Title "regression 2.2.0"
