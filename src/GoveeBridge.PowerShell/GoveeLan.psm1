# Govee LAN test module (official LAN commands + experimental Razer channel).
# Windows PowerShell 5.1 / PowerShell 7+. No external dependencies.
Set-StrictMode -Version Latest

function Resolve-GoveeEndpoint {
    param([Parameter(Mandatory = $true)][string]$LampIp)

    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($LampIp, [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LampIp must be a valid IPv4 address.'
    }

    return [System.Net.IPEndPoint]::new($address, 4003)
}

function New-GoveeUdpClient {
    param([switch]$ReceiveStatus)

    if ($ReceiveStatus) {
        # Govee replies to UDP/4002 even when the request originated on another port.
        return [System.Net.Sockets.UdpClient]::new(
            [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 4002))
    }

    return [System.Net.Sockets.UdpClient]::new()
}

function Send-GoveeMessage {
    param(
        [Parameter(Mandatory = $true)][System.Net.Sockets.UdpClient]$Client,
        [Parameter(Mandatory = $true)][System.Net.IPEndPoint]$Target,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Data
    )

    $envelope = @{ msg = @{ cmd = $Command; data = $Data } }
    $json = ConvertTo-Json -InputObject $envelope -Depth 8 -Compress
    [byte[]]$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $sent = $Client.Send($bytes, $bytes.Length, $Target)
    if ($sent -ne $bytes.Length) {
        throw "Govee UDP send returned $sent of $($bytes.Length) bytes."
    }
    Write-Verbose ("Sent {0} ({1} bytes) to {2}" -f $Command, $sent, $Target)
    # UDP send success is not proof that the lamp applied the command.
}

function ConvertTo-GoveeRgb {
    param([Parameter(Mandatory = $true)][AllowNull()][object]$Color)

    if ($null -eq $Color) { throw 'Color cannot be null.' }

    if ($Color -is [string]) {
        if ($Color -match '^#?([0-9A-Fa-f]{6})$') {
            $hex = $Matches[1]
        }
        else {
            throw "Invalid color '$Color'. Use #RRGGBB or RRGGBB."
        }
        $values = @(
            [Convert]::ToInt32($hex.Substring(0, 2), 16),
            [Convert]::ToInt32($hex.Substring(2, 2), 16),
            [Convert]::ToInt32($hex.Substring(4, 2), 16))
    }
    elseif ($Color -is [System.Collections.IDictionary]) {
        $values = @($Color['R'], $Color['G'], $Color['B'])
    }
    elseif ($Color -is [array]) {
        if ($Color.Length -ne 3) { throw 'An RGB array must contain exactly three components.' }
        $values = @($Color[0], $Color[1], $Color[2])
    }
    else {
        $properties = $Color.PSObject.Properties
        if ($null -eq $properties['R'] -or $null -eq $properties['G'] -or $null -eq $properties['B']) {
            throw 'Color must be #RRGGBB, a three-component array, or an object with R/G/B properties.'
        }
        $values = @($Color.R, $Color.G, $Color.B)
    }

    $components = [int[]]::new(3)
    for ($i = 0; $i -lt 3; $i++) {
        if ($null -eq $values[$i]) { throw 'RGB components cannot be null.' }
        $raw = $values[$i]
        if ($raw -is [bool] -or -not ([string]$raw -match '^[0-9]{1,3}$')) {
            throw "RGB component '$raw' must be an integer between 0 and 255."
        }
        $value = [int]$raw
        if ($value -gt 255) { throw "RGB component $value exceeds 255." }
        $components[$i] = $value
    }

    return [pscustomobject]@{
        R = $components[0]
        G = $components[1]
        B = $components[2]
        Hex = ('#{0:X2}{1:X2}{2:X2}' -f $components[0], $components[1], $components[2])
    }
}

function New-GoveeRazerPacket {
    param(
        [Parameter(Mandatory = $true)][byte]$Opcode,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Payload
    )

    if ($Payload.Length -gt 65535) { throw 'The binary payload exceeds the 16-bit protocol length.' }

    [byte[]]$packet = [byte[]]::new(5 + $Payload.Length)
    $packet[0] = 0xBB
    $packet[1] = [byte](($Payload.Length -shr 8) -band 0xFF)
    $packet[2] = [byte]($Payload.Length -band 0xFF)
    $packet[3] = $Opcode
    [Array]::Copy($Payload, 0, $packet, 4, $Payload.Length)

    $checksum = 0
    for ($i = 0; $i -lt $packet.Length - 1; $i++) {
        $checksum = $checksum -bxor [int]$packet[$i]
    }
    $packet[$packet.Length - 1] = [byte]$checksum

    # Prevent PowerShell from unrolling the byte array into pipeline objects.
    return ,$packet
}

function Send-GoveeRazerPacket {
    param(
        [Parameter(Mandatory = $true)][System.Net.Sockets.UdpClient]$Client,
        [Parameter(Mandatory = $true)][System.Net.IPEndPoint]$Target,
        [Parameter(Mandatory = $true)][byte[]]$Packet
    )

    $pt = [Convert]::ToBase64String($Packet)
    Send-GoveeMessage -Client $Client -Target $Target -Command 'razer' -Data @{ pt = $pt }
}

function Get-GoveeStatus {
    <#
    .SYNOPSIS
    Queries power, brightness, RGB and color-temperature state over Govee LAN.
    .DESCRIPTION
    Uses local UDP port 4002 for responses. Stop the GoveeBridge Host and other
    applications owning this port. Results are polled and may not reflect a DIY
    scene or active real-time segment frame.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [ValidateRange(200, 15000)][int]$TimeoutMs = 2500
    )

    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    $client = New-GoveeUdpClient -ReceiveStatus
    try {
        Send-GoveeMessage -Client $client -Target $target -Command 'devStatus' -Data @{}
        $watch = [System.Diagnostics.Stopwatch]::StartNew()

        while ($watch.ElapsedMilliseconds -lt $TimeoutMs) {
            $remaining = [Math]::Max(1, $TimeoutMs - [int]$watch.ElapsedMilliseconds)
            $client.Client.ReceiveTimeout = $remaining
            $sender = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)

            try {
                [byte[]]$bytes = $client.Receive([ref]$sender)
            }
            catch {
                # PowerShell can wrap socket exceptions in MethodInvocationException.
                $cause = $_.Exception
                while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
                if ($cause -is [System.Net.Sockets.SocketException] -and
                    $cause.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) {
                    break
                }
                throw
            }

            if (-not $sender.Address.Equals($target.Address)) {
                Write-Verbose "Ignoring datagram from $sender (not the configured lamp)."
                continue
            }

            try {
                $response = ConvertFrom-Json -InputObject ([System.Text.Encoding]::UTF8.GetString($bytes)) -ErrorAction Stop
                if ($null -eq $response.msg -or $response.msg.cmd -ne 'devStatus') { continue }
                $data = $response.msg.data
                if ($null -eq $data -or $null -eq $data.onOff -or
                    $null -eq $data.brightness -or $null -eq $data.color -or
                    $null -eq $data.color.r -or $null -eq $data.color.g -or $null -eq $data.color.b) {
                    Write-Verbose 'Ignoring an incomplete status response.'
                    continue
                }
                $color = ConvertTo-GoveeRgb -Color $data.color
                $temperature = 0
                if ($null -ne $data.PSObject.Properties['colorTemInKelvin']) {
                    $temperature = [int]$data.colorTemInKelvin
                }
                $power = [int]$data.onOff
                $brightness = [int]$data.brightness
                if ($power -notin @(0, 1) -or $brightness -lt 0 -or $brightness -gt 100) {
                    Write-Verbose 'Ignoring invalid status values.'
                    continue
                }

                Write-Verbose "Received devStatus from $sender after $($watch.ElapsedMilliseconds) ms."
                return [pscustomobject]@{
                    LampIp = $target.Address.ToString()
                    IsOn = ($power -eq 1)
                    Brightness = $brightness
                    Color = $color
                    ColorTemperatureKelvin = $temperature
                }
            }
            catch {
                Write-Verbose "Ignoring malformed status payload: $($_.Exception.Message)"
            }
        }
        throw "No valid devStatus response from $LampIp within $TimeoutMs ms (check UDP 4002 and LAN Control)."
    }
    finally {
        $client.Dispose()
    }
}

function Get-GoveeColor {
    <# .SYNOPSIS Returns RGB triplet from the device's current devStatus response. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [ValidateRange(200, 15000)][int]$TimeoutMs = 2500
    )
    return (Get-GoveeStatus -LampIp $LampIp -TimeoutMs $TimeoutMs).Color
}

function Get-GoveeBrightness {
    <# .SYNOPSIS Returns brightness (percentage) from devStatus. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [ValidateRange(200, 15000)][int]$TimeoutMs = 2500
    )
    return (Get-GoveeStatus -LampIp $LampIp -TimeoutMs $TimeoutMs).Brightness
}

function Get-GoveeColorTemperature {
    <# .SYNOPSIS Returns color temperature in Kelvin (0 means RGB mode). #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [ValidateRange(200, 15000)][int]$TimeoutMs = 2500
    )
    return (Get-GoveeStatus -LampIp $LampIp -TimeoutMs $TimeoutMs).ColorTemperatureKelvin
}

function Set-GoveePower {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [Parameter(Mandatory = $true)][ValidateSet('On', 'Off')][string]$State
    )
    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    if (-not $PSCmdlet.ShouldProcess($LampIp, "Turn lamp $State")) { return }
    $client = New-GoveeUdpClient
    try {
        $value = if ($State -eq 'On') { 1 } else { 0 }
        Send-GoveeMessage -Client $client -Target $target -Command 'turn' -Data @{ value = $value }
    }
    finally { $client.Dispose() }
}

function Set-GoveeBrightness {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [Parameter(Mandatory = $true)][ValidateRange(1, 100)][int]$Brightness
    )
    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    if (-not $PSCmdlet.ShouldProcess($LampIp, "Set brightness to $Brightness%")) { return }
    $client = New-GoveeUdpClient
    try {
        Send-GoveeMessage -Client $client -Target $target -Command 'brightness' -Data @{ value = $Brightness }
    }
    finally { $client.Dispose() }
}

function Set-GoveeColor {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [Parameter(Mandatory = $true)][object]$Color
    )
    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    $rgb = ConvertTo-GoveeRgb -Color $Color
    if (-not $PSCmdlet.ShouldProcess($LampIp, "Set solid color to $($rgb.Hex)")) { return }
    $client = New-GoveeUdpClient
    try {
        Send-GoveeMessage -Client $client -Target $target -Command 'colorwc' -Data @{
            color = @{ r = $rgb.R; g = $rgb.G; b = $rgb.B }
            colorTemInKelvin = 0
        }
    }
    finally { $client.Dispose() }
}

function Set-GoveeColorTemperature {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [Parameter(Mandatory = $true)][ValidateRange(2000, 9000)][int]$Kelvin
    )
    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    if (-not $PSCmdlet.ShouldProcess($LampIp, "Set white color temperature to $Kelvin K")) { return }
    $client = New-GoveeUdpClient
    try {
        Send-GoveeMessage -Client $client -Target $target -Command 'colorwc' -Data @{
            color = @{ r = 0; g = 0; b = 0 }
            colorTemInKelvin = $Kelvin
        }
    }
    finally { $client.Dispose() }
}

function Set-GoveePattern {
    <#
    .SYNOPSIS
    Streams an arbitrary ordered array of RGB values over the experimental Razer LAN channel.
    .DESCRIPTION
    Arms with opcode B1, streams B0 frames, and attempts to disarm in finally.
    Colors are in bottom-to-top order on the tested H6072. LEDCount is the
    submitted color count, NOT a proven physical pixel count; if supplied it
    must match Colors.Count. -Seconds -1 streams until interrupted, not a
    one-shot 'arm and leave running' operation. Does not auto-power-on.
    .EXAMPLE
    Set-GoveePattern -LampIp 192.168.0.5 -Colors @('#FF0000','#0000FF') -Seconds 5
    .EXAMPLE
    $colors = 0..31 | ForEach-Object { if ($_ % 2) { '#0000FF' } else { '#FF0000' } }
    Set-GoveePattern -LampIp 192.168.0.5 -Colors $colors -LEDCount 32 -Seconds -1 -Verbose
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)][string]$LampIp,
        [Parameter(Mandatory = $true)][object[]]$Colors,
        [ValidateRange(0, 255)][int]$LEDCount = 0,
        [ValidateRange(-1, 3600)][int]$Seconds = 5,
        [ValidateRange(50, 5000)][int]$FrameIntervalMs = 250,
        [ValidateRange(0, 5000)][int]$ArmDelayMs = 1200,
        [switch]$Gradient
    )

    if ($Seconds -eq 0) { throw 'Seconds must be -1 (continuous) or 1..3600.' }
    if ($null -eq $Colors -or $Colors.Count -lt 1 -or $Colors.Count -gt 255) {
        throw 'Colors must contain between 1 and 255 RGB values.'
    }
    if ($PSBoundParameters.ContainsKey('LEDCount') -and $LEDCount -eq 0) {
        throw 'LEDCount must be between 1 and 255 when supplied.'
    }
    if ($LEDCount -ne 0 -and $LEDCount -ne $Colors.Count) {
        throw "LEDCount ($LEDCount) must equal Colors.Count ($($Colors.Count)); the module does not resample your pattern."
    }

    # Format is: gradient flag | byte count | RGB triplets (2 + 3*N bytes).
    [byte[]]$body = [byte[]]::new(2 + 3 * $Colors.Count)
    $body[0] = if ($Gradient) { 1 } else { 0 }
    $body[1] = [byte]$Colors.Count
    for ($i = 0; $i -lt $Colors.Count; $i++) {
        $rgb = ConvertTo-GoveeRgb -Color $Colors[$i]
        $offset = 2 + 3 * $i
        $body[$offset] = [byte]$rgb.R
        $body[$offset + 1] = [byte]$rgb.G
        $body[$offset + 2] = [byte]$rgb.B
    }

    $target = Resolve-GoveeEndpoint -LampIp $LampIp
    if (-not $PSCmdlet.ShouldProcess($LampIp, "Arm, stream $($Colors.Count) colors, then disarm")) { return }

    [byte[]]$arm = New-GoveeRazerPacket -Opcode 0xB1 -Payload ([byte[]]@(1))
    [byte[]]$frame = New-GoveeRazerPacket -Opcode 0xB0 -Payload $body
    [byte[]]$disarm = New-GoveeRazerPacket -Opcode 0xB1 -Payload ([byte[]]@(0))
    $client = New-GoveeUdpClient
    $armAttempted = $false
    $frameCount = 0
    $lastArm = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $armAttempted = $true
        Send-GoveeRazerPacket -Client $client -Target $target -Packet $arm
        $lastArm.Restart()
        Write-Verbose "Razer channel armed (requested); waiting $ArmDelayMs ms before first frame."
        if ($ArmDelayMs -gt 0) { [System.Threading.Thread]::Sleep($ArmDelayMs) }

        $duration = [System.Diagnostics.Stopwatch]::StartNew()
        while ($Seconds -eq -1 -or $duration.Elapsed.TotalSeconds -lt $Seconds) {
            # Long-running tests re-arm periodically in case firmware requires a keep-alive.
            # This is a conservative probe policy, not a verified H6072 requirement.
            if ($lastArm.Elapsed.TotalSeconds -ge 30) {
                Send-GoveeRazerPacket -Client $client -Target $target -Packet $arm
                $lastArm.Restart()
                [System.Threading.Thread]::Sleep(30)
            }
            Send-GoveeRazerPacket -Client $client -Target $target -Packet $frame
            $frameCount++
            [System.Threading.Thread]::Sleep($FrameIntervalMs)
        }
        Write-Verbose "Transmitted $frameCount pattern frames."
    }
    finally {
        try {
            if ($armAttempted) {
                # Best effort. PowerShell Stop-Process, crashes, or power loss can prevent this.
                Send-GoveeRazerPacket -Client $client -Target $target -Packet $disarm
                Write-Verbose 'Razer channel disarm requested.'
            }
        }
        catch {
            Write-Warning "Failed to disarm Razer channel: $($_.Exception.Message)"
        }
        finally { $client.Dispose() }
    }
}

Export-ModuleMember -Function @(
    'Get-GoveeStatus',
    'Get-GoveeColor',
    'Get-GoveeBrightness',
    'Get-GoveeColorTemperature',
    'Set-GoveePower',
    'Set-GoveeColor',
    'Set-GoveeBrightness',
    'Set-GoveeColorTemperature',
    'Set-GoveePattern'
)
