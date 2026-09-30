#Requires -Version 7
# MSI inspection via the WindowsInstaller.Installer COM object, following the proven
# pattern from ops\scripts\runas-helper\msi-certcheck.ps1: no LIKE in MSI SQL (COM
# throws on it, so substring filtering happens in PowerShell), [void] on every
# InvokeMember whose value is not wanted, and @() at every call site because
# PowerShell unrolls a one-element array on return.
Set-StrictMode -Version Latest

function Open-MsiDatabase {
    param([Parameter(Mandatory)][string]$MsiPath)
    if (-not (Test-Path $MsiPath)) { throw "MSI not found: $MsiPath" }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path $MsiPath).Path, 0))
}

function Get-MsiRows {
    param([Parameter(Mandatory)]$Database, [Parameter(Mandatory)][string]$Sql, [int]$Column = 1)
    $rows = New-Object System.Collections.ArrayList
    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database, @($Sql))
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        [void]$rows.Add([string]$rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @($Column)))
    }
    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    return [string[]]$rows.ToArray()
}

function Get-MsiProperty {
    param([Parameter(Mandatory)]$Database, [Parameter(Mandatory)][string]$Name)
    $rows = @(Get-MsiRows -Database $Database -Sql "SELECT Value FROM Property WHERE Property='$Name'")
    if ($rows.Count -eq 0) { return $null }
    $rows[0]
}

function Get-MsiFileNames {
    # File.FileName is "short|long"; take the long half when present, as msi-sizes.ps1 does.
    param([Parameter(Mandatory)]$Database)
    @(Get-MsiRows -Database $Database -Sql "SELECT FileName FROM File" | ForEach-Object {
            if ($_ -match '\|') { ($_ -split '\|', 2)[1] } else { $_ }
        })
}

function Get-MsiFileVersions {
    param([Parameter(Mandatory)]$Database)
    # Pair FileName (col 3) with Version (col 5) from the File table.
    $names = @(Get-MsiRows -Database $Database -Sql "SELECT File, FileName, Version FROM File" -Column 2)
    $vers = @(Get-MsiRows -Database $Database -Sql "SELECT File, FileName, Version FROM File" -Column 3)
    $out = @()
    for ($i = 0; $i -lt $names.Count; $i++) {
        $long = if ($names[$i] -match '\|') { ($names[$i] -split '\|', 2)[1] } else { $names[$i] }
        $out += [pscustomobject]@{ FileName = $long; Version = $vers[$i] }
    }
    $out
}

function Test-MsiEnvironmentPath {
    # Returns the matching Environment rows (Name, Value) that set a PATH entry to the
    # install folder, or an empty array. An absent Environment table throws inside the
    # COM view; that is caught and read as zero rows.
    param([Parameter(Mandatory)]$Database)
    try {
        $names = @(Get-MsiRows -Database $Database -Sql "SELECT Environment, Name, Value FROM Environment" -Column 2)
        $values = @(Get-MsiRows -Database $Database -Sql "SELECT Environment, Name, Value FROM Environment" -Column 3)
    } catch {
        return @()
    }
    $out = @()
    for ($i = 0; $i -lt $names.Count; $i++) {
        # WiX Name prefixes: '=' set, '-' remove-on-uninstall, '*' system.
        if ($names[$i] -match '^[=*\-]*Path$' -and $values[$i] -match '\[INSTALLFOLDER\]') {
            $out += [pscustomobject]@{ Name = $names[$i]; Value = $values[$i] }
        }
    }
    $out
}
