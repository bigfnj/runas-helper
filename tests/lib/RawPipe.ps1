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
        [int]$ReadTimeoutSec = 30
    )
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.', 'RunAsHelper', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
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
        while ((Get-Date) -lt $deadline) {
            $len = Read-Exactly -Stream $pipe -Count 4
            if ($null -eq $len) { break }
            $n = [BitConverter]::ToInt32($len, 0)
            if ($n -le 0 -or $n -gt 4MB) { throw "raw pipe: bad frame length $n" }
            $data = Read-Exactly -Stream $pipe -Count $n
            if ($null -eq $data) { break }
            $msg = [Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json
            $frames += [pscustomobject]@{ Type = [string]$msg.Type; Content = [string]$msg.Content }
            if ($msg.Type -eq 'result') { break }
        }
        return @($frames)
    } finally {
        $pipe.Dispose()
    }
}

function Read-Exactly {
    # Reads exactly $Count bytes, or returns $null when the peer closes first.
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count)
    $buf = [byte[]]::new($Count)
    $got = 0
    while ($got -lt $Count) {
        $r = $Stream.Read($buf, $got, $Count - $got)
        if ($r -le 0) { return $null }
        $got += $r
    }
    $buf
}
