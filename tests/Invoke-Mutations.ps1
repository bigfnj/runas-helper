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
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Process.ps1')
. (Join-Path $here 'lib\Msi.ps1')

$exe = Get-InstalledExe
$comp = @{ __COMPAT_LAYER = 'RunAsInvoker' }
$msi = 'D:\.ai-work\_backups\runas-helper-2.2.0-baseline\RunAsHelper-Setup-2.2.0.msi'

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
    $db = Open-MsiDatabase -MsiPath $msi
    $vers = @(Get-MsiFileVersions -Database $db)
    $exeVer = @($vers | Where-Object { $_.FileName -eq 'RunAsHelper.exe' })
    Assert-True ($exeVer.Count -ge 1) 'exe has a version row'
    Assert-Equal '9.9.9.0' $exeVer[0].Version 'RunAsHelper.exe version (deliberately wrong)'
}

Invoke-Case -Id 'MUT5-bogus-hkcu' -Name 'bogus expected HKCU Run value fires one fail' -Test {
    # Read-only: read the real HKCU Run entry (if any) and assert a bogus expectation.
    $run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RunAsHelper -ErrorAction SilentlyContinue).RunAsHelper
    $actual = if ($null -eq $run) { '<absent>' } else { $run }
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
    $db = Open-MsiDatabase -MsiPath $msi
    $files = @(Get-MsiFileNames -Database $db)
    Assert-Equal 1 @($files | Where-Object { $_ -eq 'RunAsHelper.exe' }).Count 'exe file rows'
}

# --- Reconciliation ---
$expectedFail = @('MUT1-wrong-text', 'MUT2-wrong-exit', 'MUT3-pathext', 'MUT4-wrong-version', 'MUT5-bogus-hkcu', 'MUT6-wrong-source')
$expectedPass = @('CTRL1-help-ok', 'CTRL2-version-ok')
$rows = $global:RahRun.Rows
$actualFail = @($rows | Where-Object { $_.Verdict -eq 'FAIL' } | ForEach-Object { $_.Id })
$actualPass = @($rows | Where-Object { $_.Verdict -eq 'PASS' } | ForEach-Object { $_.Id })

$missingFail = @($expectedFail | Where-Object { $actualFail -notcontains $_ })
$unexpectedFail = @($actualFail | Where-Object { $expectedFail -notcontains $_ })
$missingPass = @($expectedPass | Where-Object { $actualPass -notcontains $_ })

Write-Host ''
Write-Host "Mutations that fired (expected $($expectedFail.Count)): $($actualFail.Count) -> $($actualFail -join ', ')"
Write-Host "Controls that passed (expected $($expectedPass.Count)): $($actualPass.Count) -> $($actualPass -join ', ')"

$ok = ($missingFail.Count -eq 0) -and ($unexpectedFail.Count -eq 0) -and ($missingPass.Count -eq 0)
if (-not $ok) {
    if ($missingFail.Count) { Write-Host "MUTATIONS THAT DID NOT FIRE: $($missingFail -join ', ')" }
    if ($unexpectedFail.Count) { Write-Host "UNEXPECTED FAILURES: $($unexpectedFail -join ', ')" }
    if ($missingPass.Count) { Write-Host "CONTROLS THAT DID NOT PASS: $($missingPass -join ', ')" }
    Write-Host 'RESULT: mutation self-test FAILED'
    exit 1
}
Write-Host 'RESULT: every mutation fired exactly once and both controls passed'
exit 0
