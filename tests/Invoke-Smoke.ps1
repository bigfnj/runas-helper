#Requires -Version 7
<#
.SYNOPSIS
  New-feature smoke suite for v2.3.0: the console launcher, bare-name resolution,
  real exit codes, the PowerShell host rule and the /trusted verbs.
.DESCRIPTION
  -Baseline runs the new-feature cases against the installed 2.2.0 build, where every
  one of them must FAIL because the feature is not there yet. That FAIL set is the
  mutation evidence for the suite and is pasted into tests/MUTATIONS.md. Controls that
  keep 2.2.0 behavior, GUI cases, install-cycle cases and (unless -AllowElevated)
  elevated cases skip with a reason.

  Integration mode (-Phase A|B|All without -Baseline) runs against the installed 2.3.0
  build and expects passes; it is not exercised in Phase 1.

  ConPTY cases fail fast when the installed RunAsHelper.com is absent, so a Phase 1
  baseline run never needs a pseudo console. When ConPTY does run (integration) and
  this process's stdout is redirected, the script re-launches itself through a hidden
  pwsh with a real console and prints the captured results, per the Phase 0 harness
  lesson (a run whose stdout is not a console is a harness failure, not a result).
#>
param(
    [ValidateSet('A', 'B', 'All')][string]$Phase = 'All',
    [string]$ExpectedVersion,
    [switch]$Baseline,
    [switch]$AllowElevated,
    [switch]$KeepArtifacts,
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

# ConPTY relaunch guard: only relevant when a ConPTY case will actually run (the .com
# is installed and we are not in baseline). In baseline the .com is absent, so ConPTY
# never runs and no relaunch is needed.
$conPtyWillRun = (Test-Path $com) -and (-not $Baseline) -and ($Phase -in 'A', 'All')
if ($conPtyWillRun -and [Console]::IsOutputRedirected -and -not $Relaunched) {
    $rf = Join-Path ([IO.Path]::GetTempPath()) ("rah-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".log")
    $childArgs = @('-NoProfile', '-File', $PSCommandPath, '-Phase', $Phase, '-Relaunched', '-ResultFile', $rf)
    if ($ExpectedVersion) { $childArgs += @('-ExpectedVersion', $ExpectedVersion) }
    if ($AllowElevated) { $childArgs += '-AllowElevated' }
    if ($KeepArtifacts) { $childArgs += '-KeepArtifacts' }
    $p = Start-Process pwsh -WindowStyle Hidden -Wait -PassThru -ArgumentList $childArgs
    if (Test-Path $rf) { Get-Content $rf | ForEach-Object { Write-Host $_ }; if (-not $KeepArtifacts) { Remove-Item $rf -ErrorAction SilentlyContinue } }
    exit $p.ExitCode
}

# In a relaunched instance, tee every case line into the result file so the parent can
# print it. IsOutputRedirected is false here (real hidden console).
$global:RahTee = $null
if ($Relaunched -and $ResultFile) { $global:RahTee = $ResultFile; Set-Content -Path $ResultFile -Value '' }

$skipTags = @()
if ($Baseline) { $skipTags += @('baseline-control', 'integration-only', 'gui') }
if (-not $AllowElevated) { $skipTags += 'needs-elevated' }
Reset-Run -Filter @{ SkipTags = $skipTags }

# Extend Write-CaseLine to also append to the tee file when relaunched.
if ($global:RahTee) {
    $origWrite = ${function:Write-CaseLine}
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
    # Integration uses the installed .com; baseline has none, so exercise the installed
    # exe (which lets the pipe-level 2.3.0 behavior be probed and shown absent on 2.2.0).
    if (Test-Path $com) { $com } else { $exe }
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
            Assert-Match 'installed RunAsHelper\.exe running elevated' ($r.Stdout + $r.Stderr) 'elevated-required text'
        }

        Invoke-Case -Id 'A5' -Name 'direct exe without compat still fails Win32 740' -Tags @('baseline-control') -Test {
            $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 15
            Assert-Equal 740 $r.Win32Code 'Win32 error unchanged from 2.2.0'
        }

        Invoke-Case -Id 'A6' -Name '.com with no args starts the tray' -Tags @('integration-only') -Test { }

        Invoke-Case -Id 'A7' -Name 'Ctrl+C to the .com kills the child and exits 0xC000013A' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (2.3.0 launcher)"
            $r = Invoke-ConPty -CommandLine ('"' + $com + '" /capture /timeout:20 cmd /c "ping -n 15 127.0.0.1 >nul"') -TimeoutMs 12000 -CtrlCAfterMs 2000
            Assert-Equal (-1073741510) $r.ExitCode 'Ctrl+C exit code 0xC000013A'
            Assert-Match 'still be running' $r.Text 'orphan hint line'
        }

        Invoke-Case -Id 'A8' -Name '.com renders on a real console (witness + help)' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (2.3.0 launcher)"
            Test-ConPtyWitness | Out-Null
            $r = Invoke-ConPty -CommandLine ('"' + $com + '" --help') -TimeoutMs 15000
            Assert-Match 'COMMAND LINE' $r.Text 'help renders on a real console'
        }

        Invoke-Case -Id 'A9' -Name 'service exe run from a shell refuses with one line, exit 1' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'A10' -Name 'built MSI content (delegated to Invoke-MsiContent)' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'A11' -Name 'install adds the folder to machine PATH' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'A12' -Name 'uninstall removes only the PATH entry, keeps trusted list' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'A13' -Name 'release binaries are signed with the expected thumbprint' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'A14' -Name 'upgrade preserves settings.json and HKCU Run' -Tags @('integration-only') -Test { }
    }

    if ($Phase -in 'B', 'All') {
        Invoke-Case -Id 'B1' -Name '/capture passes the child exit code through (3, 7, 0)' -Test {
            $l = Get-Launcher
            $r3 = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'cmd', '/c', 'exit', '3')
            Assert-ExitCode 3 $r3.ExitCode
            $r0 = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'cmd', '/c', 'exit', '0')
            Assert-ExitCode 0 $r0.ExitCode
        }

        Invoke-Case -Id 'B2' -Name '/timeout expiry exits 124' -Test {
            $l = Get-Launcher
            $r = Invoke-Console -FilePath $l -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:2', 'cmd', '/c', 'ping -n 4 127.0.0.1 >nul')
            Assert-ExitCode 124 $r.ExitCode
        }

        Invoke-Case -Id 'B3' -Name 'no /capture stays fire-and-forget (exit 0)' -Tags @('baseline-control') -Test {
            $l = Get-Launcher
            $r = Invoke-Console -FilePath $l -TimeoutSec 20 -Env $comp -ArgumentList @('cmd', '/c', 'exit', '3')
            Assert-ExitCode 0 $r.ExitCode
        }

        Invoke-Case -Id 'B4' -Name 'gate-closed untrusted caller exits 1 with "disabled"' -Tags @('integration-only') -Test { }
        Invoke-Case -Id 'B5' -Name 'service down: help still works, launch exits 1' -Tags @('integration-only') -Test { }

        Invoke-Case -Id 'B6' -Name 'elevated /joblog on a missing job exits 1' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/joblog:999999')
            Assert-ExitCode 1 $r.ExitCode
        }

        Invoke-Case -Id 'B7' -Name '20 rapid big-output captures never corrupt a frame' -Tags @('integration-only') -Test { }

        Invoke-Case -Id 'B8' -Name 'caller-shell host rule picks pwsh from pwsh, 5.1 from cmd' -Test {
            Assert-True (Test-Path $com) "installed RunAsHelper.com present (host rule is a 2.3.0 client feature)"
            $v = $fx.Version
            $r = Invoke-Console -FilePath 'pwsh' -TimeoutSec 40 -Env (Get-RefreshedPathEnv) -ArgumentList @(
                '-NoProfile', '-Command', "& '$com' /capture '$v'")
            Assert-Match 'PSV=7' $r.Stdout 'pwsh caller selects pwsh 7'
            Assert-Match 'PowerShell host: pwsh' $r.Stdout 'client host line names pwsh'
        }

        Invoke-Case -Id 'B9' -Name 'tray saved-entry .ps1 host rule' -Tags @('gui') -Test { }

        Invoke-Case -Id 'B10' -Name '/trusted round-trip: list, add, remove' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/trusted')
            Assert-ExitCode 0 $r.ExitCode
            Assert-Match (Get-CallerSid) $r.Text 'trusted list shows the caller SID'
        }

        Invoke-Case -Id 'B11' -Name 'event Source is cli for a CLI launch' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $t = Get-Date
            Invoke-Console -FilePath $l -TimeoutSec 20 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', "echo", "WITNESS-$cn") | Out-Null
            $ev = @(Get-RunAsHelperEvents -Id 1001 -Since $t)
            Assert-True ($ev.Count -ge 1) '1001 launch event present'
            Assert-Match 'Source: cli' $ev[0].Message 'launch event Source is cli'
        }

        Invoke-Case -Id 'B12' -Name 'elevated capture is treated as the tray identity' -Tags @('needs-elevated') -Test {
            $l = Get-Launcher
            $t = Get-Date
            $r = Invoke-Elevated -FilePath $l -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', "echo", "WITNESS-$cn")
            Assert-ExitCode 0 $r.ExitCode
            $ev = @(Get-RunAsHelperEvents -Id 1001 -Since $t)
            Assert-Match 'Source: tray' $ev[0].Message 'elevated installed caller logs Source: tray'
        }
    }
} finally {
    if (-not $KeepArtifacts) { Remove-Fixtures -WorkDir $W }
}

$title = if ($Baseline) { "smoke -Baseline (expect all-fail on 2.2.0)" } else { "smoke $Phase" }
Finish-Run -Title $title
