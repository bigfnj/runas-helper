#Requires -Version 7
<#
.SYNOPSIS
  Install an MSI on this box with administrator rights, verify the result, and (with
  -Cycle) uninstall, verify the removal, and install again. Optionally start the
  installed tray afterwards and check its window title. -UninstallOnly removes the
  installed product (snapshot, stop the tray, msiexec /x, verify removal) and stops,
  for taking a dev build off the box before installing a release of the same version.
.DESCRIPTION
  This changes machine state (the installed product, the service, the machine PATH),
  so it runs only from the integration owner's serial cycle, never from a parallel
  slice. -DryRun prints the steps and changes nothing.

  Every step is a case with a PASS/FAIL/SKIP line. msiexec runs through a runner
  script started with -Verb RunAs (UAC auto-consents on this box), because a RunAs
  start cannot redirect output; the runner records msiexec's exit code to a file.
  Exit 0 is required from msiexec: 3010 (restart needed, usually a file in use) and
  1603 are failures and the tail of the verbose log is printed.
#>
param(
    # -MsiPath and -ExpectedVersion are required for an install; -UninstallOnly needs
    # neither (the ProductCode comes from the Uninstall registry key).
    [string]$MsiPath,
    [string]$ExpectedVersion,
    [switch]$Cycle,
    [switch]$StartTray,
    [switch]$UninstallOnly,
    [switch]$DryRun,
    [string]$LogDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Msi.ps1')

if ($UninstallOnly) {
    if ($Cycle -or $StartTray) { throw '-UninstallOnly cannot combine with -Cycle or -StartTray' }
    $steps = @(
        "Snapshot: machine PATH entries, AllowedCallerSids, HKCU Run, settings.json hash, service status, tray count",
        "End any running tray instance (an open RunAsHelper.exe keeps msiexec from removing the file)",
        "Uninstall: msiexec /x {ProductCode} /qn /norestart /l*v <log>, with administrator rights; exit 0 required",
        "Verify removal: service gone, binaries gone, PATH entry gone and every other entry intact, InstallFolder value gone, AllowedCallerSids still present"
    )
} else {
    if (-not $MsiPath -or -not $ExpectedVersion) { throw '-MsiPath and -ExpectedVersion are required unless -UninstallOnly is given' }
    $steps = @(
        "Snapshot: machine PATH entries, AllowedCallerSids, HKCU Run, settings.json hash, service status, tray count",
        "End any running tray instance (an open RunAsHelper.exe keeps msiexec from replacing the file)",
        "Install: msiexec /i `"$MsiPath`" /qn /norestart /l*v <log>, with administrator rights; exit 0 required",
        "Verify: service Running, pipe present, FileVersion of .com/.exe/.Service.exe = $ExpectedVersion, install folder on the machine PATH, InstallFolder registry value, AllowedCallerSids unchanged, HKCU Run unchanged, settings.json unchanged"
    )
}
if ($Cycle) {
    $steps += @(
        "Uninstall: msiexec /x {ProductCode} /qn /norestart /l*v <log>, with administrator rights",
        "Verify removal: service gone, binaries gone, PATH entry gone and every other entry intact, InstallFolder value gone, AllowedCallerSids still present",
        "Install again and verify again"
    )
}
if ($StartTray) {
    $steps += "Put the HKCU Run value in the opposite of the state the tray should leave (per settings.json StartWithWindows)"
    $steps += "Start the installed tray with administrator rights (no arguments) and check its title carries v$ExpectedVersion, then that it wrote (or removed) the Run value; the profile's value is restored if it did not"
}

if ($DryRun) {
    Write-Host "DRY RUN Invoke-InstallCycle (no machine state changed):"
    $i = 1
    foreach ($s in $steps) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    exit 0
}

# Every check below is against the folder the MSI installs to; a staged RAH_INSTALL_DIR
# would point them at a different folder while msiexec writes the real one.
if ($env:RAH_INSTALL_DIR) { throw 'RAH_INSTALL_DIR is set: unset it before running the install cycle' }
if (-not $UninstallOnly) {
    if (-not (Test-Path $MsiPath)) { throw "MSI not found: $MsiPath" }
    $MsiPath = (Resolve-Path $MsiPath).Path
}
if (-not $LogDir) { $LogDir = New-WorkDir }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$installDir = Get-InstallDir
$exe = Get-InstalledExe
$com = Get-InstalledCom
$svcExe = Get-InstalledService
$want = if ($ExpectedVersion) { ConvertTo-FourPartVersion $ExpectedVersion } else { $null }
$policyKey = 'HKLM:\SOFTWARE\RunAsHelper'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsPath = Join-Path $env:APPDATA 'RunAsHelper\settings.json'

function Get-Snapshot {
    $reg = Get-ItemProperty $policyKey -ErrorAction SilentlyContinue
    [pscustomobject]@{
        PathEntries       = @(Get-MachinePathEntries)
        AllowedCallerSids = @(if ($reg -and ($reg.PSObject.Properties.Name -contains 'AllowedCallerSids')) { $reg.AllowedCallerSids } else { @() })
        HkcuRun           = $(
            $run = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
            if ($run -and ($run.PSObject.Properties.Name -contains 'RunAsHelper')) { [string]$run.RunAsHelper } else { $null }
        )
        SettingsHash      = if (Test-Path $settingsPath) { (Get-FileHash $settingsPath -Algorithm SHA256).Hash } else { $null }
        # On a box with nothing installed Get-Service returns nothing, and StrictMode refuses
        # a property read on null; the cycle must be able to start from that state.
        ServiceStatus     = $( $svc = Get-Service RunASHelper -ErrorAction SilentlyContinue; if ($svc) { $svc.Status.ToString() } else { 'absent' } )
        TrayCount         = @(Get-Process RunAsHelper -ErrorAction SilentlyContinue).Count
    }
}

function Invoke-AdminRunner {
    # Runs a PowerShell script block with administrator rights and returns the exit code
    # it recorded. The block receives no arguments; bake values in with string formatting.
    # The work folder goes on every path, a timeout included. A runner that recorded no
    # number is an error: its own exit code only says the runner ran.
    param([Parameter(Mandatory)][string]$Body, [int]$TimeoutSec = 600)
    $dir = New-WorkDir
    try {
        $rcFile = Join-Path $dir 'rc.txt'
        $runner = Join-Path $dir 'runner.ps1'
        $script = $Body.Replace('__RC__', $rcFile)
        Set-Content -Path $runner -Value $script -Encoding UTF8
        # Quoted: Start-Process joins -ArgumentList with plain spaces, and %TEMP% can hold one.
        $p = Start-Process pwsh -Verb RunAs -WindowStyle Hidden -PassThru `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $runner + '"')
        if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "administrator runner timed out after $TimeoutSec s" }
        $rcText = if (Test-Path $rcFile) { [IO.File]::ReadAllText($rcFile).Trim() } else { '' }
        if ($rcText -notmatch '^-?\d+$') { throw "administrator runner recorded no exit code (runner exit $($p.ExitCode))" }
        [int]$rcText
    } finally {
        try { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue } catch { }
    }
}

function Invoke-Msiexec {
    param([Parameter(Mandatory)][string[]]$MsiArgs, [Parameter(Mandatory)][string]$LogFile)
    $quoted = ($MsiArgs + @('/qn', '/norestart', '/l*v', "`"$LogFile`"")) -join ' '
    $body = @'
$p = Start-Process msiexec.exe -ArgumentList '__ARGS__' -Wait -PassThru -WindowStyle Hidden
[IO.File]::WriteAllText("__RC__", "$($p.ExitCode)")
'@
    # The parser's escaper, not a plain '' doubling: U+2018 to U+201B also close a
    # single-quoted string, and an MSI or log path may hold one.
    Invoke-AdminRunner -Body $body.Replace('__ARGS__', [Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($quoted))
}

function Stop-TrayInstances {
    if (@(Get-Process RunAsHelper -ErrorAction SilentlyContinue).Count -eq 0) { return 0 }
    $body = @'
Get-Process RunAsHelper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
[IO.File]::WriteAllText("__RC__", "$(@(Get-Process RunAsHelper -ErrorAction SilentlyContinue).Count)")
'@
    Invoke-AdminRunner -Body $body -TimeoutSec 60
}

function Show-LogTail {
    param([string]$LogFile, [int]$Lines = 40)
    if (Test-Path $LogFile) {
        Write-Host "---- last $Lines lines of $LogFile ----"
        Get-Content $LogFile -Tail $Lines | ForEach-Object { Write-Host "  $_" }
        $inUse = @(Select-String -Path $LogFile -Pattern 'RESTART MANAGER|in use|FilesInUse' -SimpleMatch:$false)
        if ($inUse.Count -gt 0) { Write-Host "  (log mentions files in use / restart manager: $($inUse.Count) lines)" }
    }
}

function Test-Installed {
    param([Parameter(Mandatory)][string]$Prefix, [Parameter(Mandatory)]$Before)

    Invoke-Case -Id "$($Prefix)2" -Name 'service RunASHelper is Running' -Test {
        $ok = Wait-Until -TimeoutSec 30 -Condition { $s = Get-Service RunASHelper -ErrorAction SilentlyContinue; $s -and $s.Status -eq 'Running' }
        Assert-True $ok 'service reached Running within 30 s'
    }
    Invoke-Case -Id "$($Prefix)3" -Name 'service pipe is present' -Test {
        Assert-True (Wait-ForPipe -TimeoutSec 30) 'pipe \\.\pipe\RunAsHelper present within 30 s'
    }
    Invoke-Case -Id "$($Prefix)4" -Name "installed binaries report FileVersion $ExpectedVersion" -Test {
        foreach ($f in $com, $exe, $svcExe) {
            Assert-True (Test-Path $f) "$f exists"
            Assert-Equal $want (ConvertTo-FourPartVersion (Get-FileVersionOf $f)) "$([IO.Path]::GetFileName($f)) FileVersion"
        }
    }
    Invoke-Case -Id "$($Prefix)5" -Name 'install folder is on the machine PATH' -Test {
        $entries = @(Get-MachinePathEntries)
        Assert-True ($entries -contains $installDir.TrimEnd('\')) "machine PATH contains $installDir"
        $n = @($entries | Where-Object { $_ -ieq $installDir.TrimEnd('\') }).Count
        Assert-Equal 1 $n 'exactly one PATH entry for the install folder'
    }
    Invoke-Case -Id "$($Prefix)6" -Name 'InstallFolder registry value names the install folder' -Test {
        $reg = Get-ItemProperty $policyKey -ErrorAction SilentlyContinue
        $v = if ($reg -and ($reg.PSObject.Properties.Name -contains 'InstallFolder')) { $reg.InstallFolder } else { $null }
        Assert-True ($null -ne $v) 'InstallFolder value present'
        Assert-Equal $installDir.TrimEnd('\') ([string]$v).TrimEnd('\') 'InstallFolder value'
    }
    Invoke-Case -Id "$($Prefix)7" -Name 'AllowedCallerSids unchanged by the install' -Test {
        $now = (Get-Snapshot).AllowedCallerSids
        Assert-Equal ($Before.AllowedCallerSids -join ';') ($now -join ';') 'AllowedCallerSids'
    }
    Invoke-Case -Id "$($Prefix)8" -Name 'HKCU Run entry unchanged by the install' -Test {
        Assert-Equal ([string]$Before.HkcuRun) ([string](Get-Snapshot).HkcuRun) 'HKCU Run value'
    }
    Invoke-Case -Id "$($Prefix)9" -Name 'settings.json unchanged by the install' -Test {
        Assert-Equal ([string]$Before.SettingsHash) ([string](Get-Snapshot).SettingsHash) 'settings.json hash'
    }
}

function Test-Removed {
    # The removal checks, shared by -Cycle (after its uninstall) and -UninstallOnly.
    param([Parameter(Mandatory)]$Before)
    Invoke-Case -Id 'U2' -Name 'service is gone after uninstall' -Test {
        $gone = Wait-Until -TimeoutSec 30 -Condition { $null -eq (Get-Service RunASHelper -ErrorAction SilentlyContinue) }
        Assert-True $gone 'service RunASHelper absent within 30 s'
    }
    Invoke-Case -Id 'U3' -Name 'binaries are gone after uninstall' -Test {
        foreach ($f in $com, $exe, $svcExe) { Assert-True (-not (Test-Path $f)) "$f removed" }
    }
    Invoke-Case -Id 'U4' -Name 'PATH entry is gone and every other entry is intact' -Test {
        $entries = @(Get-MachinePathEntries)
        Assert-True ($entries -notcontains $installDir.TrimEnd('\')) 'install folder no longer on the machine PATH'
        $others = @($Before.PathEntries | Where-Object { $_ -ine $installDir.TrimEnd('\') })
        foreach ($e in $others) { Assert-True ($entries -contains $e) "PATH entry still present: $e" }
    }
    Invoke-Case -Id 'U5' -Name 'InstallFolder value is gone, AllowedCallerSids kept' -Test {
        $reg = Get-ItemProperty $policyKey -ErrorAction SilentlyContinue
        $hasFolder = $reg -and ($reg.PSObject.Properties.Name -contains 'InstallFolder')
        Assert-True (-not $hasFolder) 'InstallFolder value removed'
        $sids = @(if ($reg -and ($reg.PSObject.Properties.Name -contains 'AllowedCallerSids')) { $reg.AllowedCallerSids } else { @() })
        Assert-Equal ($Before.AllowedCallerSids -join ';') ($sids -join ';') 'AllowedCallerSids after uninstall'
    }
}

Reset-Run
$before = Get-Snapshot
Write-Host ("Before: service={0} tray={1} pathEntries={2} trusted={3}" -f $before.ServiceStatus, $before.TrayCount, $before.PathEntries.Count, $before.AllowedCallerSids.Count)

if ($UninstallOnly) {
    Write-Host "Uninstall only (logs in $LogDir)"
    $code = Get-InstalledProductCode
    $uninstallLog = Join-Path $LogDir 'uninstall-only.log'
    Invoke-Case -Id 'U1' -Name 'msiexec /x exits 0' -Test {
        Assert-True ($null -ne $code) 'ProductCode found in the Uninstall registry'
        $left = Stop-TrayInstances
        Assert-Equal 0 $left 'RunAsHelper.exe instances after the stop'
        $rc = Invoke-Msiexec -MsiArgs @('/x', $code) -LogFile $uninstallLog
        if ($rc -ne 0) { Show-LogTail $uninstallLog }
        Assert-ExitCode 0 $rc
    }
    Test-Removed -Before $before
    Finish-Run -Title 'uninstall only'
}

Write-Host "Install cycle: $MsiPath -> $ExpectedVersion (logs in $LogDir)"

Invoke-Case -Id 'I0' -Name 'no tray instance is running before the install' -Test {
    $left = Stop-TrayInstances
    Assert-Equal 0 $left 'RunAsHelper.exe instances after the stop'
}

$installLog = Join-Path $LogDir "install-$ExpectedVersion.log"
Invoke-Case -Id 'I1' -Name 'msiexec /i exits 0' -Test {
    $rc = Invoke-Msiexec -MsiArgs @('/i', "`"$MsiPath`"") -LogFile $installLog
    if ($rc -ne 0) { Show-LogTail $installLog }
    Assert-ExitCode 0 $rc
}
Test-Installed -Prefix 'I' -Before $before

if ($Cycle) {
    $code = Get-InstalledProductCode
    $uninstallLog = Join-Path $LogDir "uninstall-$ExpectedVersion.log"
    Invoke-Case -Id 'U1' -Name 'msiexec /x exits 0' -Test {
        Assert-True ($null -ne $code) 'ProductCode found in the Uninstall registry'
        $left = Stop-TrayInstances
        Assert-Equal 0 $left 'RunAsHelper.exe instances after the stop'
        $rc = Invoke-Msiexec -MsiArgs @('/x', $code) -LogFile $uninstallLog
        if ($rc -ne 0) { Show-LogTail $uninstallLog }
        Assert-ExitCode 0 $rc
    }
    Test-Removed -Before $before

    $reinstallLog = Join-Path $LogDir "reinstall-$ExpectedVersion.log"
    Invoke-Case -Id 'R1' -Name 'msiexec /i (reinstall) exits 0' -Test {
        $rc = Invoke-Msiexec -MsiArgs @('/i', "`"$MsiPath`"") -LogFile $reinstallLog
        if ($rc -ne 0) { Show-LogTail $reinstallLog }
        Assert-ExitCode 0 $rc
    }
    Test-Installed -Prefix 'R' -Before $before
}

if ($StartTray) {
    # T2 must prove the tray itself writes (or deletes) the Run value, so the value is put
    # in the opposite state before T1: a profile that already held the expected value
    # passed T2 whether or not the tray did anything. The tray writes the value on load
    # when Start with Windows is on (the default, and what a settings.json without
    # StartWithWindows means) and deletes it when it is off.
    $s = if (Test-Path $settingsPath) { Get-Content -Raw $settingsPath | ConvertFrom-Json } else { $null }
    $startWithWindows = -not ($s -and ($s.PSObject.Properties.Name -contains 'StartWithWindows') -and -not $s.StartWithWindows)
    $wantRun = if ($startWithWindows) { '"' + $exe + '" --tray' } else { '<absent>' }
    $runBefore = (Get-Snapshot).HkcuRun
    if ($startWithWindows) { Remove-ItemProperty -Path $runKey -Name RunAsHelper -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $runKey -Name RunAsHelper -Value ('"' + $exe + '" --tray') }

    Invoke-Case -Id 'T1' -Name "tray started with administrator rights shows 'RunAS Helper - v$ExpectedVersion'" -Test {
        Start-Process $exe -Verb RunAs | Out-Null
        $ok = Wait-Until -TimeoutSec 20 -Condition {
            @(Get-Process RunAsHelper -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -match 'RunAS Helper - v' }).Count -gt 0
        }
        Assert-True $ok 'a tray window titled "RunAS Helper - v..." appeared within 20 s'
        # Re-read after the wait: a value set inside the wait's script block would live
        # in a different scope from this one.
        $title = @(Get-Process RunAsHelper -ErrorAction SilentlyContinue | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ })[0]
        Assert-Match ('RunAS Helper - v' + [regex]::Escape($ExpectedVersion)) $title 'tray title'
    }
    Invoke-Case -Id 'T2' -Name 'the started tray writes (or removes) the HKCU Run entry itself' -Test {
        # Against the value the tray should leave, not the value from before the cycle:
        # that comparison failed on a profile where no tray had run yet, or where the
        # value named another copy of the exe.
        $got = [string](Get-Snapshot).HkcuRun
        if (-not $got) { $got = '<absent>' }
        Assert-Equal $wantRun $got "HKCU Run value after the tray start (StartWithWindows $startWithWindows)"
    }
    # A tray that did not act must not cost the profile its entry.
    $after = [string](Get-Snapshot).HkcuRun
    if (-not $after) { $after = '<absent>' }
    if ($after -ne $wantRun -and $null -ne $runBefore) { Set-ItemProperty -Path $runKey -Name RunAsHelper -Value $runBefore }
}

Finish-Run -Title "install cycle $ExpectedVersion"
