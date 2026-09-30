#Requires -Version 7
<#
.SYNOPSIS
  Verifies that a built MSI carries the v2.3.0 contract: the console launcher file,
  all three binaries at the expected version each in its own component, and an
  Environment-table row that puts the install folder on PATH.
.DESCRIPTION
  Run by ci.yml against the freshly built MSI, and by hand against the 2.2.0 release
  MSI, where it MUST fail (no RunAsHelper.com, no Environment table). Exits 1 on any
  failed check.
#>
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [string]$ExpectedVersion
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Assert.ps1')
. (Join-Path $PSScriptRoot 'lib\Msi.ps1')

Reset-Run
Write-Host "MSI content check: $MsiPath"
$db = Open-MsiDatabase -MsiPath $MsiPath

$files = @(Get-MsiFileNames -Database $db)
$vers = @(Get-MsiFileVersions -Database $db)

Invoke-Case -Id 'M1' -Name 'RunAsHelper.com present' -Test {
    $n = @($files | Where-Object { $_ -eq 'RunAsHelper.com' }).Count
    Assert-Equal 1 $n 'RunAsHelper.com file rows'
}

Invoke-Case -Id 'M2' -Name 'RunAsHelper.exe present' -Test {
    $n = @($files | Where-Object { $_ -eq 'RunAsHelper.exe' }).Count
    Assert-Equal 1 $n 'RunAsHelper.exe file rows'
}

Invoke-Case -Id 'M3' -Name 'RunAsHelper.Service.exe present' -Test {
    $n = @($files | Where-Object { $_ -eq 'RunAsHelper.Service.exe' }).Count
    Assert-Equal 1 $n 'RunAsHelper.Service.exe file rows'
}

Invoke-Case -Id 'M4' -Name 'each binary in its own component' -Test {
    # File table rows keyed by component: three distinct component keys for the three PEs.
    $comps = @(Get-MsiRows -Database $db -Sql "SELECT Component_ FROM File")
    Assert-True ($comps.Count -ge 3) "component rows >= 3 (got $($comps.Count))"
    $distinct = @($comps | Sort-Object -Unique).Count
    Assert-True ($distinct -ge 3) "distinct file components >= 3 (got $distinct)"
}

Invoke-Case -Id 'M5' -Name 'Environment table has a PATH row for the install folder' -Test {
    $rows = @(Test-MsiEnvironmentPath -Database $db)
    Assert-True ($rows.Count -ge 1) "Environment PATH rows (got $($rows.Count))"
}

Invoke-Case -Id 'M6' -Name 'binary versions match ExpectedVersion' -Tags @('needs-expected') -Test {
    if (-not $ExpectedVersion) { Skip-Case -Reason 'no -ExpectedVersion given' }
    $want = "$ExpectedVersion.0"
    foreach ($name in 'RunAsHelper.com', 'RunAsHelper.exe', 'RunAsHelper.Service.exe') {
        $row = @($vers | Where-Object { $_.FileName -eq $name })
        Assert-True ($row.Count -ge 1) "$name has a File.Version row"
        Assert-Equal $want $row[0].Version "$name version"
    }
}

Invoke-Case -Id 'M7' -Name 'ProductVersion property matches ExpectedVersion' -Tags @('needs-expected') -Test {
    if (-not $ExpectedVersion) { Skip-Case -Reason 'no -ExpectedVersion given' }
    Assert-Equal $ExpectedVersion (Get-MsiProperty -Database $db -Name 'ProductVersion') 'ProductVersion'
}

Finish-Run -Title 'msi-content'
