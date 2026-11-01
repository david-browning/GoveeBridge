
# Experimental Govee LAN real-time lighting test.
# Stop GoveeBridge.Host and other Govee integrations first.
# Turn the lamp on before running.

param(
    [Parameter(Mandatory = $true)]
    [string]$LampIp,

    [ValidateRange(1, 255)]
    [int]$Count = 8,

    [ValidateRange(0, 254)]
    [int]$Index = 0,

    [ValidateSet('Spot', 'Alternating')]
    [string]$Pattern = 'Alternating',

    [switch]$Gradient,

    [ValidateRange(1, 20)]
    [int]$Seconds = 5
)

if ($Pattern -eq 'Spot' -and $Index -ge $Count) {
    throw "Index must be less than Count."
}

function New-RazerPacket {
    param(
        [byte]$Opcode,
        [byte[]]$Payload
    )

    # BB | length (big-endian) | opcode | payload | XOR
    [byte[]]$packet = [byte[]]::new(5 + $Payload.Length)

    $packet[0] = 0xBB
    $packet[1] = [byte](($Payload.Length -shr 8) -band 0xFF)
    $packet[2] = [byte]($Payload.Length -band 0xFF)
    $packet[3] = $Opcode

    [Array]::Copy($Payload, 0, $packet, 4, $Payload.Length)

    [int]$checksum = 0

    for ($i = 0; $i -lt $packet.Length - 1; $i++) {
        $checksum = $checksum -bxor [int]$packet[$i]
    }

    $packet[$packet.Length - 1] = [byte]$checksum

    return ,$packet
}

function Send-RazerPacket {
    param(
        [System.Net.Sockets.UdpClient]$Client,
        [System.Net.IPEndPoint]$Target,
        [byte[]]$Packet
    )

    $message = @{
        msg = @{
            cmd = 'razer'
            data = @{
                pt = [Convert]::ToBase64String($Packet)
            }
        }
    }

    $json = ConvertTo-Json -InputObject $message `
        -Depth 5 -Compress

    [byte[]]$wire = [Text.Encoding]::UTF8.GetBytes($json)

    [void]$Client.Send($wire, $wire.Length, $Target)
}

# Build B0 payload:
# gradient flag | color count | RGB triplets

[byte[]]$body = [byte[]]::new(2 + 3 * $Count)

$body[0] = if ($Gradient) { 1 } else { 0 }
$body[1] = [byte]$Count

for ($i = 0; $i -lt $Count; $i++) {
    $offset = 2 + 3 * $i

    if ($Pattern -eq 'Spot') {
        # One red spot, everything else almost dark.
        $body[$offset] = if ($i -eq $Index) { 180 } else { 0 }
        $body[$offset + 1] = 0
        $body[$offset + 2] = if ($i -eq $Index) { 0 } else { 6 }
    }
    else {
        # Alternating red / blue sections.
        $body[$offset] = if ($i % 2 -eq 0) { 160 } else { 0 }
        $body[$offset + 1] = 0
        $body[$offset + 2] = if ($i % 2 -eq 0) { 0 } else { 160 }
    }
}

$address = [System.Net.IPAddress]::Parse($LampIp)

if ($address.AddressFamily -ne
    [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw "Use an IPv4 lamp address."
}

$target = [System.Net.IPEndPoint]::new($address, 4003)
$client = [System.Net.Sockets.UdpClient]::new()

try {
    # B1, payload 01: enable real-time mode.
    $enable = New-RazerPacket -Opcode 0xB1 `
        -Payload ([byte[]]@(1))

    $frame = New-RazerPacket -Opcode 0xB0 -Payload $body

    Write-Host "Enabling real-time lighting..."
    Send-RazerPacket -Client $client `
        -Target $target -Packet $enable

    # Give the lamp time to enter real-time mode.
    Start-Sleep -Milliseconds 1200

    Write-Host "Transmitting $Count colors for $Seconds seconds."

    Write-Host (
        "Frame header: " +
        (($frame[0..5] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
    )

    $until = [DateTime]::UtcNow.AddSeconds($Seconds)

    while ([DateTime]::UtcNow -lt $until) {
        Send-RazerPacket -Client $client `
            -Target $target -Packet $frame

        Start-Sleep -Milliseconds 250
    }

    Write-Host "Finished transmitting."
}
finally {
    try {
        # B1, payload 00: disable real-time mode.
        $disable = New-RazerPacket -Opcode 0xB1 `
            -Payload ([byte[]]@(0))

        Send-RazerPacket -Client $client `
            -Target $target -Packet $disable

        Write-Host "Real-time mode disarm sent."
    }
    finally {
        $client.Dispose()
    }
}
