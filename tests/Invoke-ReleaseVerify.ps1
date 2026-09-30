#Requires -Version 7
<#
.SYNOPSIS
  Verify a published GitHub release end to end: the workflow run, the single MSI asset,
  its Authenticode signature, the MSI tables, the signatures of the three binaries
  inside it, then install it on this box and run the smoke and regression suites.
.DESCRIPTION
  Downloads and installs, so it runs only at release time from the integration owner.
  -DryRun prints the steps and changes nothing. Each step is a PASS/FAIL case; the
  install cycle and the suites run as child processes and their exit codes are cases
  too, so one RESULT line covers the whole release.
#>
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repo = 'bigfnj/runas-helper',
    [string]$ExpectedThumbprint = '0EEBB64DCE430D98D2CA19DC3DC715DB9999BAD5',
    [switch]$SkipInstall,
    [switch]$DryRun,
    [string]$WorkDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'lib\Assert.ps1')
. (Join-Path $here 'lib\Env.ps1')
. (Join-Path $here 'lib\Msi.ps1')

$ver = $Tag.TrimStart('v')
$asset = "RunAsHelper-Setup-$ver.msi"

$steps = @(
    "gh run list --workflow=release.yml for ${Tag}: conclusion success",
    "gh release view ${Tag}: exactly one asset, $asset; download it; record SHA-256",
    "Get-AuthenticodeSignature on the MSI: Valid, thumbprint $ExpectedThumbprint, timestamped",
    "Invoke-MsiContent.ps1 -ExpectedVersion $ver on the downloaded MSI",
    "Administrative image (msiexec /a ... TARGETDIR=...): the three binaries inside are Valid and timestamped with the same thumbprint",
    "Invoke-InstallCycle.ps1 -MsiPath <downloaded> -ExpectedVersion $ver -StartTray (upgrade over the dev build)",
    "Invoke-Smoke.ps1 -Phase All -ExpectedVersion $ver -Integration -ExpectedThumbprint $ExpectedThumbprint; Invoke-Regression.ps1 -NewBuild -AllowElevated -AllowMachineWrites -ExpectedVersion $ver"
)
if ($DryRun) {
    Write-Host "DRY RUN Invoke-ReleaseVerify for $Tag (no download, no install):"
    $i = 1
    foreach ($s in $steps) { Write-Host ("  {0}. {1}" -f $i, $s); $i++ }
    exit 0
}

if (-not $WorkDir) { $WorkDir = New-WorkDir }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$msi = Join-Path $WorkDir $asset

Reset-Run
Write-Host "Release verify: $Repo $Tag (work dir $WorkDir)"

Invoke-Case -Id 'V1' -Name "release.yml run for $Tag concluded success" -Test {
    $runs = @(gh run list --repo $Repo --workflow=release.yml --limit 5 --json databaseId,conclusion,headBranch,event | ConvertFrom-Json)
    $mine = @($runs | Where-Object { $_.headBranch -eq $Tag })
    # A pipeline, not $runs.headBranch: member access on an empty array throws under
    # StrictMode, which would report a missing run as a harness error.
    Assert-True ($mine.Count -ge 1) "a release.yml run for $Tag exists (recent runs: $(@($runs | ForEach-Object { $_.headBranch }) -join ','))"
    Assert-Equal 'success' $mine[0].conclusion 'run conclusion'
    Write-Host "  run id $($mine[0].databaseId)"
}

Invoke-Case -Id 'V2' -Name "release $Tag has exactly one asset, $asset; downloaded" -Test {
    $rel = gh release view $Tag --repo $Repo --json assets,tagName | ConvertFrom-Json
    Assert-True ($null -ne $rel) "gh release view $Tag returned no release (exit $LASTEXITCODE)"
    Assert-Equal $Tag $rel.tagName 'release tag'
    $names = @($rel.assets | ForEach-Object { $_.name })
    Assert-Equal 1 $names.Count "asset count (got: $($names -join ','))"
    Assert-Equal $asset $names[0] 'asset name'
    gh release download $Tag --repo $Repo --pattern '*.msi' --dir $WorkDir --clobber | Out-Null
    Assert-True (Test-Path $msi) "downloaded to $msi"
    Write-Host "  SHA-256 $((Get-FileHash $msi -Algorithm SHA256).Hash)  size $((Get-Item $msi).Length)"
}

Invoke-Case -Id 'V3' -Name 'MSI signature is Valid, expected thumbprint, timestamped' -Test {
    $s = Get-AuthenticodeSignature $msi
    Assert-Equal 'Valid' $s.Status.ToString() 'signature status'
    Assert-Equal $ExpectedThumbprint $s.SignerCertificate.Thumbprint 'signer thumbprint'
    Assert-True ($null -ne $s.TimeStamperCertificate) 'timestamp present'
}

Invoke-Case -Id 'V4' -Name 'MSI tables carry the 2.3.0 contract at the release version' -Test {
    pwsh -NoProfile -File (Join-Path $here 'Invoke-MsiContent.ps1') -MsiPath $msi -ExpectedVersion $ver | ForEach-Object { Write-Host "  $_" }
    Assert-ExitCode 0 $LASTEXITCODE
}

Invoke-Case -Id 'V5' -Name 'the three binaries inside the MSI are signed and timestamped' -Test {
    $adm = Join-Path $WorkDir 'adm'
    $log = Join-Path $WorkDir 'admin-image.log'
    $p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$adm`"", '/l*v', "`"$log`"") -Wait -PassThru -WindowStyle Hidden
    Assert-ExitCode 0 $p.ExitCode
    $files = @(Get-ChildItem $adm -Recurse -File | Where-Object { $_.Name -in 'RunAsHelper.com', 'RunAsHelper.exe', 'RunAsHelper.Service.exe' })
    Assert-Equal 3 $files.Count "binaries found in the administrative image (got: $(@($files | ForEach-Object { $_.Name }) -join ','))"
    foreach ($f in $files) {
        $s = Get-AuthenticodeSignature $f.FullName
        Assert-Equal 'Valid' $s.Status.ToString() "$($f.Name) signature status"
        Assert-Equal $ExpectedThumbprint $s.SignerCertificate.Thumbprint "$($f.Name) thumbprint"
        Assert-True ($null -ne $s.TimeStamperCertificate) "$($f.Name) timestamped"
        Assert-Equal (ConvertTo-FourPartVersion $ver) (ConvertTo-FourPartVersion (Get-FileVersionOf $f.FullName)) "$($f.Name) FileVersion"
    }
}

if (-not $SkipInstall) {
    Invoke-Case -Id 'V6' -Name "install cycle with the released MSI ($ver)" -Test {
        pwsh -NoProfile -File (Join-Path $here 'Invoke-InstallCycle.ps1') -MsiPath $msi -ExpectedVersion $ver -StartTray -LogDir (Join-Path $WorkDir 'install-logs') | ForEach-Object { Write-Host "  $_" }
        Assert-ExitCode 0 $LASTEXITCODE
    }
    Invoke-Case -Id 'V7' -Name 'smoke suite passes on the released build' -Test {
        pwsh -NoProfile -File (Join-Path $here 'Invoke-Smoke.ps1') -Phase All -ExpectedVersion $ver -Integration -ExpectedThumbprint $ExpectedThumbprint | ForEach-Object { Write-Host "  $_" }
        Assert-ExitCode 0 $LASTEXITCODE
    }
    Invoke-Case -Id 'V8' -Name 'regression suite passes on the released build' -Test {
        pwsh -NoProfile -File (Join-Path $here 'Invoke-Regression.ps1') -NewBuild -AllowElevated -AllowMachineWrites -ExpectedVersion $ver | ForEach-Object { Write-Host "  $_" }
        Assert-ExitCode 0 $LASTEXITCODE
    }
}

Finish-Run -Title "release verify $Tag"
