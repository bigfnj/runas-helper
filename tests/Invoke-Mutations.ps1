#Requires -Version 7
<#
.SYNOPSIS
  Harness self-test: prove the checks can fail. Each mutation feeds one deliberately
  wrong expectation to a real, deterministic operation on this box and must produce
  exactly one FAIL naming that mutation; a control with the correct expectation must
  PASS, so the machinery is not simply always-failing.
.DESCRIPTION
  The gate is the reconciliation at the end: the set of cases that failed must equal
  the set of mutations, and every control must pass. Exit 0 when they match.
#>
param(
    # With the new build installed, also run the integration mutations (MUT7-MUT17) and
    # controls (CTRL3, CTRL5): each mutation feeds a wrong expectation to the same operation
    # an integration case performs. MUT14 and CTRL5 make elevated calls, so -Integration
    # is also this script's permission to elevate (it has no -AllowElevated).
    [switch]$Integration,
    # A copy of the 2.2.0 release MSI for MUT4 (the MSI-version mutation), CTRL2 (a File-row
    # count) and CTRL4 (the version reader on a known MSI). The default is the maintainer's
    # backup; when the file is absent all three SKIP with a reason and leave the expected
    # sets, so the self-test still passes off this box.
    [string]$BaselineMsi = 'D:\.ai-work\_backups\runas-helper-2.2.0-baseline\RunAsHelper-Setup-2.2.0.msi'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\Msi.ps1')
. (Join-Path $here 'lib\EventLog.ps1')
. (Join-Path $here 'lib\Elevated.ps1')
. (Join-Path $here 'lib\RawPipe.ps1')

$exe = Get-InstalledExe
$com = Get-InstalledCom
$svc = Get-InstalledService
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$cn = Get-ComputerNameLocal
$msi = $BaselineMsi
$haveMsi = [bool]($msi -and (Test-Path $msi))

Reset-Run

# --- Mutations (each must FAIL) and controls (each must PASS) ---

Invoke-Case -Id 'MUT1-wrong-text' -Name 'wrong expected help text fires one fail' -Test {
    $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
    Assert-Match 'NO-SUCH-TEXT-XYZZY' $r.Stdout 'help text'
}

Invoke-Case -Id 'MUT2-wrong-exit' -Name 'wrong expected exit code fires one fail' -Test {
    $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
    Assert-ExitCode 99 $r.ExitCode
}

Invoke-Case -Id 'MUT3-pathext' -Name 'PATHEXT reorder changes bare-name resolution' -Test {
    # Self-contained: two dummy binaries; default PATHEXT resolves .COM before .EXE,
    # a reordered PATHEXT resolves .EXE first. The mutation asserts the default gives
    # .exe, which is false, so it must fail.
    $d = New-WorkDir
    try {
        Set-Content (Join-Path $d 'zzcmd.com') '' ; Set-Content (Join-Path $d 'zzcmd.exe') ''
        $saved = $env:PATH; $savedExt = $env:PATHEXT
        $env:PATH = "$d;$env:PATH"; $env:PATHEXT = '.COM;.EXE;.BAT'
        try {
            $src = (Get-Command zzcmd -ErrorAction SilentlyContinue).Source
            Assert-Match 'zzcmd\.exe$' $src 'default PATHEXT resolves to .exe (deliberately wrong)'
        } finally { $env:PATH = $saved; $env:PATHEXT = $savedExt }
    } finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
}

Invoke-Case -Id 'MUT4-wrong-version' -Name 'wrong expected MSI version fires one fail' -Test {
    if (-not $haveMsi) { Skip-Case -Reason "no baseline MSI at '$msi' (pass -BaselineMsi)" }
    $db = Open-MsiDatabase -MsiPath $msi
    $vers = @(Get-MsiFileVersions -Database $db)
    $exeVer = @($vers | Where-Object { $_.FileName -eq 'RunAsHelper.exe' })
    # A throw, not an assertion: a missing row means the reader broke, and an assertion
    # failure here would count as the mutation firing.
    if ($exeVer.Count -lt 1) { throw 'no RunAsHelper.exe version row; the MSI reader is broken' }
    Assert-Equal '9.9.9.0' $exeVer[0].Version 'RunAsHelper.exe version (deliberately wrong)'
}

Invoke-Case -Id 'MUT5-bogus-hkcu' -Name 'bogus expected HKCU Run value fires one fail' -Test {
    # Read-only: read the real HKCU Run entry (if any) and assert a bogus expectation.
    $run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RunAsHelper -ErrorAction SilentlyContinue
    $actual = if ($null -eq $run) { '<absent>' } else { [string]$run.RunAsHelper }
    Assert-Equal 'C:\bogus\path.exe --tray' $actual 'HKCU Run value (deliberately wrong)'
}

Invoke-Case -Id 'MUT6-wrong-source' -Name 'wrong event Source expectation fires one fail' -Test {
    # A sample event body carrying Source: cli; the mutation asserts Source: tray.
    $sample = 'RunAsHelper launch requested. Source: cli. Account=system.'
    Assert-Match 'Source: tray' $sample 'event Source (deliberately wrong)'
}

Invoke-Case -Id 'CTRL1-help-ok' -Name 'control: correct help expectation passes' -Test {
    $r = Invoke-Console -FilePath $exe -ArgumentList @('--help') -TimeoutSec 30 -Env $comp
    Assert-ExitCode 0 $r.ExitCode
    Assert-Match 'TrustedInstaller' $r.Stdout 'help text'
}

Invoke-Case -Id 'CTRL2-version-ok' -Name 'control: correct MSI-row count passes' -Test {
    if (-not $haveMsi) { Skip-Case -Reason "no baseline MSI at '$msi' (pass -BaselineMsi)" }
    $db = Open-MsiDatabase -MsiPath $msi
    $files = @(Get-MsiFileNames -Database $db)
    Assert-Equal 1 @($files | Where-Object { $_ -eq 'RunAsHelper.exe' }).Count 'exe file rows'
}

Invoke-Case -Id 'CTRL4-msi-version-ok' -Name 'control: the MSI version reader returns 2.2.0.0 for the 2.2.0 exe' -Test {
    # CTRL2 counts File rows and never runs the version reader, so without this a reader
    # that returned nothing useful would still let MUT4 fire and the self-test pass.
    if (-not $haveMsi) { Skip-Case -Reason "no baseline MSI at '$msi' (pass -BaselineMsi)" }
    $db = Open-MsiDatabase -MsiPath $msi
    $exeVer = @(@(Get-MsiFileVersions -Database $db) | Where-Object { $_.FileName -eq 'RunAsHelper.exe' })
    if ($exeVer.Count -lt 1) { throw 'no RunAsHelper.exe version row; the MSI reader is broken' }
    Assert-Equal (ConvertTo-FourPartVersion '2.2.0') (ConvertTo-FourPartVersion $exeVer[0].Version) 'RunAsHelper.exe version'
}

$expectedFail = @('MUT1-wrong-text', 'MUT2-wrong-exit', 'MUT3-pathext', 'MUT5-bogus-hkcu', 'MUT6-wrong-source')
$expectedPass = @('CTRL1-help-ok')
if ($haveMsi) { $expectedFail += 'MUT4-wrong-version'; $expectedPass += @('CTRL2-version-ok', 'CTRL4-msi-version-ok') }

if ($Integration) {
    # The same operations the integration cases perform, each with one wrong expectation.

    Invoke-Case -Id 'MUT7-wrong-installed-version' -Name 'wrong expected installed FileVersion fires one fail (I4 logic)' -Test {
        Assert-Equal (ConvertTo-FourPartVersion '9.9.9') (ConvertTo-FourPartVersion (Get-FileVersionOf $com)) '.com FileVersion (deliberately wrong)'
    }

    Invoke-Case -Id 'MUT8-folder-not-on-path' -Name 'a folder that is not on PATH fails the PATH check (A11/I5 logic)' -Test {
        $entries = @(Get-MachinePathEntries)
        Assert-True ($entries -contains 'C:\NotOnPath\RunAsHelperMutation') 'machine PATH contains a folder that is not there (deliberately wrong)'
    }

    Invoke-Case -Id 'MUT9-wrong-tray-title' -Name 'wrong expected tray title fires one fail (R8/T1 logic)' -Test {
        $titles = @(Get-Process RunAsHelper -ErrorAction SilentlyContinue | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ })
        if ($titles.Count -eq 0) { Skip-Case -Reason 'no tray window is open' }
        Assert-Match 'RunAS Helper - v9\.9\.9' $titles[0] 'tray title (deliberately wrong)'
    }

    Invoke-Case -Id 'MUT10-wrong-event-source' -Name 'a Medium launch does not log Source: tray (B11 logic)' -Test {
        $t = Get-Date
        Invoke-Console -FilePath $com -TimeoutSec 20 -Env $comp -ArgumentList @('/capture', '/timeout:10', 'cmd', '/c', 'echo', "WITNESS-$cn") | Out-Null
        $ev = @(Get-RunAsHelperEvents -Id 1001 -Since $t)
        # A throw, not an assertion: a missing event means the launch or the reader failed,
        # which must not count as the mutation firing.
        if ($ev.Count -lt 1) { throw 'no 1001 event; the launch or the event reader failed' }
        Assert-Match 'Source: tray' (Get-RunAsHelperEventText $ev[0]) 'event Source (deliberately wrong)'
    }

    Invoke-Case -Id 'MUT11-guard-exit-zero' -Name 'the service host run from a shell does not exit 0 (A9 logic)' -Test {
        $r = Invoke-Console -FilePath $svc -ArgumentList @() -TimeoutSec 15
        Assert-ExitCode 0 $r.ExitCode
    }

    Invoke-Case -Id 'MUT12-wrong-child-exit' -Name '/capture of exit 3 does not return 4 (B1 logic)' -Test {
        $r = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:20', 'cmd', '/c', 'exit', '3')
        Assert-ExitCode 4 $r.ExitCode
    }

    Invoke-Case -Id 'MUT13-timeout-not-zero' -Name 'a /timeout expiry does not return 0 (B2 logic)' -Test {
        $r = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:2', 'cmd', '/c', 'ping -n 4 127.0.0.1 >nul')
        Assert-ExitCode 0 $r.ExitCode
    }

    Invoke-Case -Id 'MUT14-elevated-wrong-exit' -Name 'an elevated exit 7 is not reported as 8 (Invoke-Elevated logic)' -Test {
        $r = Invoke-Elevated -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-Command', 'exit 7') -TimeoutSec 60
        # A throw, not an assertion, when the real code is lost: a helper that returned the
        # runner's own 0 would otherwise still count as the mutation firing.
        if ($r.ExitCode -ne 7) { throw "Invoke-Elevated returned $($r.ExitCode) for a target that exits 7" }
        Assert-ExitCode 8 $r.ExitCode
    }

    Invoke-Case -Id 'MUT15-witness-echo-only' -Name 'a child that prints nothing does not satisfy the child-computed witness (R3/R14 logic)' -Test {
        # rem prints nothing, so the only WITNESS text left is the service's echo of the
        # command, which keeps the literal %COMPUTERNAME%. If the echo ever carried the
        # expanded name, this would stop firing and the witness would be void again.
        $r = Invoke-Console -FilePath $com -TimeoutSec 30 -Env $comp -ArgumentList @('/capture', '/timeout:10', '/as:system', 'cmd', '/c', 'rem', 'WITNESS-%COMPUTERNAME%')
        Assert-Match "WITNESS-$cn" $r.Stdout 'child witness (deliberately absent: the child prints nothing)'
    }

    $trustedSids = @(Get-AllowedCallerSids)
    Invoke-Case -Id 'MUT16-dacl-trusted-withheld' -Name 'the pipe DACL check reports trusted-user ACEs when the trusted list is withheld (B16 logic)' -Test {
        if ($trustedSids.Count -eq 0) { Skip-Case -Reason 'no trusted SIDs on this box, so there is nothing to withhold' }
        $bad = @(Get-PipeDaclViolations -TrustedSids @())
        Assert-Equal 0 $bad.Count "DACL departures with the trusted list withheld (deliberately wrong): $($bad -join '; ')"
    }

    Invoke-Case -Id 'MUT17-dacl-narrow-network-deny' -Name 'a NETWORK deny that no longer covers full control is reported (B16 logic)' -Test {
        # A private pipe built like CreatePipe except that its NETWORK deny covers only
        # ChangePermissions, which would leave read/write open to a remote token. The check
        # must report it; this case asserts it does not, so it has to fire.
        $name = 'rah-mut17-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $sec = [IO.Pipes.PipeSecurity]::new()
        $sec.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-2'), [IO.Pipes.PipeAccessRights]::ChangePermissions, 'Deny'))
        foreach ($p in @{ 'S-1-5-4' = 'ReadWrite'; 'S-1-5-18' = 'FullControl'; 'S-1-5-32-544' = 'FullControl' }.GetEnumerator()) {
            $sec.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new($p.Key), [IO.Pipes.PipeAccessRights]$p.Value, 'Allow'))
        }
        $server = [IO.Pipes.NamedPipeServerStreamAcl]::Create($name, [IO.Pipes.PipeDirection]::InOut, 1,
            [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 4096, 4096,
            $sec, [IO.HandleInheritability]::None, [IO.Pipes.PipeAccessRights]0)
        try {
            [void]$server.WaitForConnectionAsync()
            $bad = @(Get-PipeDaclViolations -PipeName $name)
        } finally { $server.Dispose() }
        Assert-Equal 0 $bad.Count "DACL departures on a narrowed NETWORK deny (deliberately wrong): $($bad -join '; ')"
    }

    Invoke-Case -Id 'CTRL3-installed-version-ok' -Name 'control: the three installed binaries share one FileVersion' -Test {
        $v = ConvertTo-FourPartVersion (Get-FileVersionOf $com)
        Assert-Equal $v (ConvertTo-FourPartVersion (Get-FileVersionOf $exe)) 'exe FileVersion'
        Assert-Equal $v (ConvertTo-FourPartVersion (Get-FileVersionOf $svc)) 'service FileVersion'
    }

    Invoke-Case -Id 'CTRL5-elevated-unmeasured' -Name 'control: an elevated call that records no exit code throws and leaves no work folder (BL-45)' -Test {
        # The installed exe is GUI-subsystem, so the elevated runner records no exit code
        # for it; the helper must say so instead of returning a number, and clean up. The
        # call gets a private TEMP: other harness runs share %TEMP% and name their folders
        # the same way, so looking there would blame, and delete, folders still in use.
        $root = (New-Item -ItemType Directory -Path (Join-Path $env:TEMP ('rah-ctrl5-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))).FullName
        $savedTemp = $env:TEMP
        $err = $null
        try {
            $env:TEMP = $root
            try { Invoke-Elevated -FilePath $exe -ArgumentList @('/trusted') -TimeoutSec 60 | Out-Null } catch { $err = $_.Exception.Message }
        } finally { $env:TEMP = $savedTemp }
        $left = @(Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue)
        Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue
        Assert-Equal 0 $left.Count 'work folders left behind'
        Assert-Match 'recorded no exit code' $err 'error from the helper'
    }

    $expectedFail += @('MUT7-wrong-installed-version', 'MUT8-folder-not-on-path', 'MUT10-wrong-event-source', 'MUT11-guard-exit-zero', 'MUT12-wrong-child-exit', 'MUT13-timeout-not-zero', 'MUT14-elevated-wrong-exit', 'MUT15-witness-echo-only', 'MUT17-dacl-narrow-network-deny')
    if ($trustedSids.Count -gt 0) { $expectedFail += 'MUT16-dacl-trusted-withheld' }
    $trayOpen = @(Get-Process RunAsHelper -ErrorAction SilentlyContinue | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ }).Count -gt 0
    if ($trayOpen) { $expectedFail += 'MUT9-wrong-tray-title' }
    $expectedPass += @('CTRL3-installed-version-ok', 'CTRL5-elevated-unmeasured')
}

# --- Reconciliation ---
# Only an ASSERTION failure counts as a fired mutation. Invoke-Case also records a FAIL
# when the body threw something else ("harness error: ..."); that means the check never
# ran, so it is listed separately and counted as "did not fire".
$rows = $global:RahRun.Rows
$harnessErrors = @($rows | Where-Object { $_.Verdict -eq 'FAIL' -and $_.Detail -like 'harness error:*' } | ForEach-Object { $_.Id })
$actualFail = @($rows | Where-Object { $_.Verdict -eq 'FAIL' -and $_.Detail -notlike 'harness error:*' } | ForEach-Object { $_.Id })
$actualPass = @($rows | Where-Object { $_.Verdict -eq 'PASS' } | ForEach-Object { $_.Id })

$missingFail = @($expectedFail | Where-Object { $actualFail -notcontains $_ })
$unexpectedFail = @($actualFail | Where-Object { $expectedFail -notcontains $_ })
$missingPass = @($expectedPass | Where-Object { $actualPass -notcontains $_ })

Write-Host ''
Write-Host "Mutations that fired (expected $($expectedFail.Count)): $($actualFail.Count) -> $($actualFail -join ', ')"
Write-Host "Controls that passed (expected $($expectedPass.Count)): $($actualPass.Count) -> $($actualPass -join ', ')"
if ($harnessErrors.Count) { Write-Host "Harness errors (not counted as fired): $($harnessErrors -join ', ')" }

$ok = ($missingFail.Count -eq 0) -and ($unexpectedFail.Count -eq 0) -and ($missingPass.Count -eq 0) -and ($harnessErrors.Count -eq 0)
if (-not $ok) {
    if ($missingFail.Count) { Write-Host "MUTATIONS THAT DID NOT FIRE: $($missingFail -join ', ')" }
    if ($unexpectedFail.Count) { Write-Host "UNEXPECTED FAILURES: $($unexpectedFail -join ', ')" }
    if ($missingPass.Count) { Write-Host "CONTROLS THAT DID NOT PASS: $($missingPass -join ', ')" }
    if ($harnessErrors.Count) { Write-Host "HARNESS ERRORS: $($harnessErrors -join ', ')" }
    Write-Host 'RESULT: mutation self-test FAILED'
    exit 1
}
Write-Host 'RESULT: every mutation fired exactly once and every control passed'
exit 0
