#Requires -Version 7
# A raw client for the service pipe, independent of the shipped RunAsHelper.exe, so a
# case can send a frame the real client never sends (an unknown verb, a malformed
# request). Wire format per RunAsHelper.Shared/Protocol/PipeProtocol.cs: a 4-byte
# little-endian length prefix, then UTF-8 JSON; the service answers with the same
# framing, one PipeMessage {Type, Content} per frame, "result" always last.
Set-StrictMode -Version Latest

function Send-RawPipeRequest {
    <#
      Connects to \\.\pipe\RunAsHelper, writes one request frame carrying $Json verbatim,
      then reads reply frames until a "result" frame or the service closes the pipe.
      Returns the frames as objects with Type and Content, in arrival order.
    #>
    param(
        [Parameter(Mandatory)][string]$Json,
        [int]$ConnectTimeoutMs = 5000,
        [int]$ReadTimeoutSec = 30,
        [string]$PipeName = 'RunAsHelper'
    )
    # Asynchronous, so a read can be abandoned at the deadline. A synchronous Read on a pipe
    # whose server takes the request and then stays silent blocks until the server closes
    # the pipe, which may be never, and the whole run would hang with nothing printed.
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect($ConnectTimeoutMs)
        $body = [Text.Encoding]::UTF8.GetBytes($Json)
        $frame = [byte[]]::new(4 + $body.Length)
        [BitConverter]::GetBytes([int]$body.Length).CopyTo($frame, 0)   # little-endian on Windows
        $body.CopyTo($frame, 4)
        $pipe.Write($frame, 0, $frame.Length)
        $pipe.Flush()

        $frames = @()
        $deadline = (Get-Date).AddSeconds($ReadTimeoutSec)
        while ($true) {
            $len = Read-Exactly -Stream $pipe -Count 4 -Deadline $deadline
            if ($null -eq $len) { break }
            $n = [BitConverter]::ToInt32($len, 0)
            if ($n -le 0 -or $n -gt 4MB) { throw "raw pipe: bad frame length $n" }
            $data = Read-Exactly -Stream $pipe -Count $n -Deadline $deadline
            if ($null -eq $data) { break }
            $msg = [Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json
            $frames += [pscustomobject]@{ Type = [string]$msg.Type; Content = [string]$msg.Content }
            if ($msg.Type -eq 'result') { break }
        }
        if ((Get-Date) -ge $deadline) { Write-Host "  raw pipe: no complete reply within $ReadTimeoutSec s ($($frames.Count) frame(s) received)" }
        return @($frames)
    } finally {
        $pipe.Dispose()
    }
}

function Get-PipeDaclViolations {
    <#
      Reads the service pipe's DACL through a connected client stream (Get-Acl cannot open a
      pipe path) and returns one line per departure from PipeServer.cs CreatePipe. The first
      ACE must deny NETWORK. Allow ACEs may name only SYSTEM and Administrators (any rights),
      or INTERACTIVE and the trusted user SIDs with read/write and Synchronize only, and each
      trusted SID has its own ACE. Nothing may allow NETWORK. No lines means as designed.
    #>
    param([string[]]$TrustedSids = @(), [string]$PipeName = 'RunAsHelper')
    $c = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect(5000)
        $rules = @([IO.Pipes.PipesAclExtensions]::GetAccessControl($c).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    } finally { $c.Dispose() }
    if ($rules.Count -eq 0) { return @('no ACEs could be read') }
    $out = @()
    if ($rules[0].IdentityReference.Value -ne 'S-1-5-2' -or $rules[0].AccessControlType -ne 'Deny') {
        $out += "first ACE is $($rules[0].AccessControlType) $($rules[0].IdentityReference.Value), not Deny S-1-5-2 (NETWORK)"
    }
    $limited = [int]([IO.Pipes.PipeAccessRights]::ReadWrite -bor [IO.Pipes.PipeAccessRights]::Synchronize)
    foreach ($r in $rules) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        $sid = $r.IdentityReference.Value
        if ($sid -in 'S-1-5-18', 'S-1-5-32-544') { continue }
        if ($sid -eq 'S-1-5-2') { $out += 'an ACE allows NETWORK'; continue }
        if ($sid -ne 'S-1-5-4' -and $TrustedSids -notcontains $sid) { $out += "unexpected allowed principal $sid ($($r.PipeAccessRights))"; continue }
        if (([int]$r.PipeAccessRights -band -bnot $limited) -ne 0) { $out += "$sid holds more than read/write ($($r.PipeAccessRights))" }
    }
    foreach ($t in $TrustedSids) {
        if (@($rules | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -eq $t }).Count -eq 0) { $out += "trusted SID $t has no ACE of its own" }
    }
    $out
}

function Read-Exactly {
    # Reads exactly $Count bytes. Returns $null when the peer closes first or the deadline
    # passes first; the caller disposes the pipe, which cancels a read still pending.
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count, [Parameter(Mandatory)][datetime]$Deadline)
    $buf = [byte[]]::new($Count)
    $got = 0
    while ($got -lt $Count) {
        $ms = [int][Math]::Max(0, ($Deadline - (Get-Date)).TotalMilliseconds)
        if ($ms -le 0) { return $null }
        $t = $Stream.ReadAsync($buf, $got, $Count - $got)
        if (-not $t.Wait($ms)) { return $null }
        $r = $t.Result
        if ($r -le 0) { return $null }
        $got += $r
    }
    $buf
}
