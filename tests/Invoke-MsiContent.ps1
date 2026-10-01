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
    # MSI pads File.Version to four parts, so compare as versions, not strings.
    $want = ConvertTo-FourPartVersion $ExpectedVersion
    foreach ($name in 'RunAsHelper.com', 'RunAsHelper.exe', 'RunAsHelper.Service.exe') {
        $row = @($vers | Where-Object { $_.FileName -eq $name })
        Assert-True ($row.Count -ge 1) "$name has a File.Version row"
        Assert-Equal $want (ConvertTo-FourPartVersion $row[0].Version) "$name version"
    }
}

Invoke-Case -Id 'M7' -Name 'ProductVersion property matches ExpectedVersion' -Tags @('needs-expected') -Test {
    if (-not $ExpectedVersion) { Skip-Case -Reason 'no -ExpectedVersion given' }
    # The package version binds to the service exe's FileVersion, which is four parts
    # ("2.2.90.0"); a release built with -p:ProductVersion=2.3.0 reports "2.3.0.0" too.
    $got = Get-MsiProperty -Database $db -Name 'ProductVersion'
    Assert-True ($null -ne $got) 'ProductVersion property present'
    Assert-Equal (ConvertTo-FourPartVersion $ExpectedVersion) (ConvertTo-FourPartVersion $got) 'ProductVersion'
}

Invoke-Case -Id 'M8' -Name 'event source registers a message file' -Test {
    # Without EventMessageFile, Event Viewer prefixes every 1001-1006 entry with "The
    # description for Event ID ... cannot be found" and a reader of the rendered message
    # gets no text (BL-50). The 2.3.2 and earlier MSIs have only TypesSupported here, so
    # this case is what makes that difference visible.
    $rows = @(Get-MsiEventSourceValues -Database $db)
    Assert-True ($rows.Count -ge 1) "event-source registry rows (got $($rows.Count))"
    $msg = @($rows | Where-Object { $_.Name -eq 'EventMessageFile' })
    Assert-Equal 1 $msg.Count 'EventMessageFile rows'
    Assert-Match 'EventLogMessages\.dll$' $msg[0].Value 'EventMessageFile points at a message DLL'
}

Finish-Run -Title 'msi-content'
