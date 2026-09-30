#Requires -Version 7
# Reads the RunAsHelper Application event-log entries a case needs to check. The
# service writes its events under the source "RunAsHelper"; ids used by the design:
# 1001 launch, 1002 result, 1003 denied/unknown, 1005 install, 1006 kill, 1099 crash.
Set-StrictMode -Version Latest

function Get-RunAsHelperEvents {
    param(
        [int[]]$Id,
        [datetime]$Since,
        [string]$Source = 'RunAsHelper',
        [int]$Max = 50
    )
    $filter = @{ LogName = 'Application'; ProviderName = $Source }
    if ($Id) { $filter.Id = $Id }
    if ($Since) { $filter.StartTime = $Since }
    try {
        @(Get-WinEvent -FilterHashtable $filter -MaxEvents $Max -ErrorAction Stop |
            Sort-Object TimeCreated -Descending)
    } catch {
        # No matching events is not an error for our purposes.
        if ($_.Exception.Message -match 'No events were found') { return @() }
        throw
    }
}
