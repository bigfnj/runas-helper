#Requires -Version 7
# MSI inspection via the WindowsInstaller.Installer COM object. Three rules learned the
# hard way: no LIKE in MSI SQL (COM throws on it, so substring filtering happens in
# PowerShell), [void] on every InvokeMember whose value is not wanted, and @() at every
# call site because PowerShell unrolls a one-element array on return.
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

function Get-InstalledProductCode {
    # The ProductCode of the installed RunAS Helper, from the per-machine Uninstall key,
    # or $null when it is not installed. Needed for msiexec /x.
    $p = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' -and $_.DisplayName -eq 'RunAS Helper' } |
        Select-Object -First 1
    if ($null -eq $p) { return $null }
    $p.PSChildName
}

function ConvertTo-FourPartVersion {
    # MSI stores ProductVersion and File.Version as up to four numeric parts, and the
    # FileVersion bind pads to four ("2.2.90" becomes "2.2.90.0"), so compare as [Version]
    # padded to four parts rather than as strings.
    param([Parameter(Mandatory)][string]$Text)
    $parts = @(($Text.Trim() -split '\.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    [Version]::new($parts[0], $parts[1], $parts[2], $parts[3])
}

function Test-MsiHasTable {
    param([Parameter(Mandatory)]$Database, [Parameter(Mandatory)][string]$Table)
    @(Get-MsiRows -Database $Database -Sql 'SELECT `Name` FROM `_Tables`') -contains $Table
}

function Get-MsiEventSourceValues {
    # Returns the Registry rows under the RunAsHelper event-source key as
    # [pscustomobject]@{ Name; Value }, or an empty array when the MSI has no Registry
    # table at all. Column names are backquoted because `Key` and `Value` are reserved
    # words in MSI SQL: unquoted, OpenView throws. That throw used to be swallowed by a
    # try/catch here, which made an absent row and a malformed query look identical, so
    # the absence check below asks _Tables and any other failure propagates.
    param([Parameter(Mandatory)]$Database)
    if (-not (Test-MsiHasTable -Database $Database -Table 'Registry')) { return @() }
    $sql = 'SELECT `Registry`, `Key`, `Name`, `Value` FROM `Registry`'
    $keys = @(Get-MsiRows -Database $Database -Sql $sql -Column 2)
    $names = @(Get-MsiRows -Database $Database -Sql $sql -Column 3)
    $values = @(Get-MsiRows -Database $Database -Sql $sql -Column 4)
    $out = @()
    for ($i = 0; $i -lt $keys.Count; $i++) {
        if ($keys[$i] -match 'EventLog\\Application\\RunAsHelper$') {
            $out += [pscustomobject]@{ Name = $names[$i]; Value = $values[$i] }
        }
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
