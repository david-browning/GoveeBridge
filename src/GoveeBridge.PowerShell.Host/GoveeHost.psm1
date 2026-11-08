# GoveeHost: client of GoveeBridge.Host, never of the physical lamp.
# Requires PowerShell 7.6+ (.NET 10) and a compiled GoveeBridge.Core.dll.
#
# Optional: set the path to the EXACT DLL you wish to test, for example:
# $script:GoveeBridgeCoreDllPath = 'C:\Source\GoveeBridge\src\GoveeBridge.Core\bin\Debug\net10.0\GoveeBridge.Core.dll'
$script:GoveeBridgeCoreDllPath = ''

Set-StrictMode -Version Latest

function Import-GoveeHostCoreAssembly {
    <#
    .SYNOPSIS
    Loads the compiled Core assembly, which defines every IPC request and wire name.
    .DESCRIPTION
    Does not compile or duplicate C# source. The loaded DLL must contain the
    unpublished BridgeIpcRequests types used by this module.
    #>
    if ([System.Environment]::Version.Major -lt 10) {
        throw 'GoveeBridge.Core targets .NET 10. Use PowerShell 7.6+ (pwsh), not Windows PowerShell 5.1 or PowerShell 7.5.'
    }

    $requested = $null
    if (-not [string]::IsNullOrWhiteSpace($script:GoveeBridgeCoreDllPath)) {
        $requested = $script:GoveeBridgeCoreDllPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GOVEEBRIDGE_CORE_DLL)) {
        $requested = $env:GOVEEBRIDGE_CORE_DLL
    }

    if ($null -ne $requested) {
        # An explicit path is authoritative. Do not silently fall back to
        # a different build and accidentally test the wrong DTO version.
        if (-not (Test-Path -LiteralPath $requested -PathType Leaf)) {
            throw "Specified GoveeBridge.Core.dll was not found: $requested"
        }
        $dll = (Resolve-Path -LiteralPath $requested -ErrorAction Stop).ProviderPath
    }
    else {
        $sourceDirectory = Split-Path -Path $PSScriptRoot -Parent
        $candidates = @(
            (Join-Path -Path $PSScriptRoot -ChildPath 'GoveeBridge.Core.dll'),
            (Join-Path -Path $sourceDirectory -ChildPath 'GoveeBridge.Core/bin/Debug/net10.0/GoveeBridge.Core.dll'),
            (Join-Path -Path $sourceDirectory -ChildPath 'GoveeBridge.Core/bin/Release/net10.0/GoveeBridge.Core.dll')
        )
        $dll = $null
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $dll = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).ProviderPath
                break
            }
        }
        if ($null -eq $dll) {
            throw ('GoveeBridge.Core.dll not found. Build Core, set $script:GoveeBridgeCoreDllPath ' +
                'at the top of GoveeHost.psm1, or set GOVEEBRIDGE_CORE_DLL.')
        }
    }

    $loaded = [System.AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -eq 'GoveeBridge.Core' } |
        Select-Object -First 1

    if ($null -ne $loaded) {
        if (-not [string]::Equals($loaded.Location, $dll,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "GoveeBridge.Core is already loaded from '$($loaded.Location)'. Restart pwsh to use '$dll'."
        }
    }
    else {
        Add-Type -LiteralPath $dll -ErrorAction Stop
    }

    if ($null -eq [System.Type]::GetType('GoveeBridge.Core.Ipc.BridgeIpcRequests, GoveeBridge.Core', $false)) {
        throw "DLL '$dll' does not contain GoveeBridge.Core.Ipc.BridgeIpcRequests. Rebuild Core with the new IPC sources."
    }
    Write-Verbose "Loaded Core IPC contract: $dll"
}

function ConvertTo-GoveeHostRgb {
    <# Normalize a friendly PowerShell color to the Core RgbColor value type. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowNull()][object]$Color)

    if ($null -eq $Color) { throw 'Color cannot be null.' }
    if ($Color -is [GoveeBridge.Core.RgbColor]) { return $Color }

    if ($Color -is [string]) {
        $names = @{
            Red = '#FF0000'; Green = '#00FF00'; Blue = '#0000FF'
            White = '#FFFFFF'; Black = '#000000'; Amber = '#FFBF00'
        }
        $hex = $Color
        if ($names.ContainsKey($hex)) { $hex = $names[$hex] }
        if ($hex -notmatch '^#?([0-9a-fA-F]{6})$') {
            throw "Invalid color '$Color'. Use #RRGGBB, RRGGBB, or Red/Green/Blue/White/Black/Amber."
        }
        $hex = $Matches[1]
        $values = @(
            [Convert]::ToInt32($hex.Substring(0, 2), 16),
            [Convert]::ToInt32($hex.Substring(2, 2), 16),
            [Convert]::ToInt32($hex.Substring(4, 2), 16)
        )
    }
    elseif ($Color -is [System.Collections.IDictionary]) {
        $values = @($Color['R'], $Color['G'], $Color['B'])
    }
    elseif ($Color -is [array]) {
        $values = @($Color)
    }
    else {
        $properties = $Color.PSObject.Properties
        if ($null -eq $properties['R'] -or $null -eq $properties['G'] -or $null -eq $properties['B']) {
            throw 'Color must contain R, G and B components.'
        }
        $values = @($Color.R, $Color.G, $Color.B)
    }

    if ($values.Count -ne 3) { throw 'Each RGB color requires exactly three components.' }
    [byte[]]$rgb = [byte[]]::new(3)
    for ($index = 0; $index -lt 3; $index++) {
        $raw = $values[$index]
        if ($null -eq $raw -or $raw -is [bool] -or
            ([string]$raw) -notmatch '^\d{1,3}$' -or [int]$raw -gt 255) {
            throw "RGB component '$raw' must be an integer from 0 through 255."
        }
        $rgb[$index] = [byte][int]$raw
    }
    return [GoveeBridge.Core.RgbColor]::new($rgb[0], $rgb[1], $rgb[2])
}

function New-GoveeHostRgbFrame {
    <# Convert input colors to the Core RgbFrame model; factory creates wire DTO. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Colors)

    if ($Colors.Count -lt 1 -or $Colors.Count -gt 255) {
        throw 'Frames must contain between 1 and 255 logical RGB positions.'
    }
    $values = [System.Collections.Generic.List[GoveeBridge.Core.RgbColor]]::new()
    foreach ($color in $Colors) {
        $values.Add((ConvertTo-GoveeHostRgb -Color $color))
    }
    return [GoveeBridge.Core.RgbFrame]::new($values)
}

function ConvertTo-GoveeHostJson {
    <#
    .SYNOPSIS
    Serializes a Core IPC DTO using System.Text.Json, respecting JsonPropertyName.
    .DESCRIPTION
    Useful for offline inspection: no UDP activity. PowerShell's ConvertTo-Json
    is deliberately NOT used for outgoing packets, because we want the exact
    JSON attributes and object shape from GoveeBridge.Core.dll.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Request)
    return [System.Text.Json.JsonSerializer]::Serialize(
        [object]$Request,
        $Request.GetType(),
        [System.Text.Json.JsonSerializerOptions]$null)
}

function Send-GoveeHostRequest {
    <# Common loopback transport for strongly typed requests. Never uses lamp LAN. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Request,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [bool]$ExpectReply = $true,
        [switch]$PassThru
    )

    $json = ConvertTo-GoveeHostJson -Request $Request
    [byte[]]$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes.Length -gt 8192) {
        throw "IPC datagram has $($bytes.Length) bytes; current Host limit is 8192."
    }

    $endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, $Port)
    $client = [System.Net.Sockets.UdpClient]::new(
        [System.Net.Sockets.AddressFamily]::InterNetwork)
    try {
        # Binding explicitly to loopback also keeps the reply address local.
        $client.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 0))
        $sent = $client.Send($bytes, $bytes.Length, $endpoint)
        if ($sent -ne $bytes.Length) {
            throw "UDP accepted only $sent of $($bytes.Length) bytes."
        }
        Write-Verbose "Submitted $sent bytes to $endpoint. JSON: $json"

        if (-not $ExpectReply) {
            # Frames have no Host reply. Return only on explicit request and
            # never misrepresent UDP submission as a confirmed lamp update.
            if ($PassThru) {
                return [pscustomobject]@{
                    Submission = 'UDP only (unacknowledged)'
                    MessageType = $Request.Type
                    ByteCount = $sent
                    Port = $Port
                }
            }
            return
        }

        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
            $remaining = [Math]::Max(1, $TimeoutMs - [int]$clock.ElapsedMilliseconds)
            $client.Client.ReceiveTimeout = $remaining
            $sender = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            try {
                [byte[]]$replyBytes = $client.Receive([ref]$sender)
            }
            catch {
                $cause = $_.Exception
                while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
                if ($cause -is [System.Net.Sockets.SocketException] -and
                    $cause.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) {
                    break
                }
                throw
            }

            if (-not $sender.Address.Equals($endpoint.Address) -or $sender.Port -ne $endpoint.Port) {
                Write-Verbose "Ignoring response from unexpected sender $sender."
                continue
            }
            $replyJson = [System.Text.Encoding]::UTF8.GetString($replyBytes)
            Write-Verbose "Host reply after $($clock.ElapsedMilliseconds) ms: $replyJson"
            try {
                $reply = ConvertFrom-Json -InputObject $replyJson -ErrorAction Stop
            }
            catch {
                throw "Host returned invalid JSON: $replyJson"
            }
            if ($null -ne $reply -and $null -ne $reply.PSObject.Properties['error']) {
                throw "Host rejected '$($Request.Action)': $($reply.error)"
            }
            return $reply
        }
        throw "No reply from GoveeBridge.Host on 127.0.0.1:$Port within $TimeoutMs ms."
    }
    finally {
        $client.Dispose()
    }
}

function Get-GoveeHostStatus {
    <# .SYNOPSIS Gets cached Host status; does not query the lamp. #>
    [CmdletBinding()]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::Status($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Get-GoveeHostLampStatus {
    <# .SYNOPSIS Requests fresh device status via Host (may be unavailable during Razer). #>
    [CmdletBinding()]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::LampStatus($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Resume-GoveeHostControl {
    <# .SYNOPSIS Grants PC ownership; requires a fresh frame for streaming output. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", 'Grant PC ownership')) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::Resume($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Release-GoveeHostControl {
    <# .SYNOPSIS Returns ownership to Manual; never sends direct lamp commands. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", 'Return ownership to Manual')) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::Release($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Arm-GoveeHostStreaming {
    <# .SYNOPSIS Explicitly enters PC Streaming; must already hold PC Direct ownership. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", 'Arm PC Streaming')) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::Arm($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Disarm-GoveeHostStreaming {
    <# .SYNOPSIS Stops streaming and retries DISARM if the prior attempt failed. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", 'Disarm PC Streaming')) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::Disarm($LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Set-GoveeHostPower {
    <# .SYNOPSIS Requests desired power state via planned set-power Host IPC. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateSet('On', 'Off')][string]$State = 'On',
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Request lamp power $State")) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::SetPower(($State -eq 'On'), $LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Set-GoveeHostBrightness {
    <# .SYNOPSIS Requests desired global brightness via planned Host IPC. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(0, 100)][int]$BrightnessPercent = 50,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Request brightness $BrightnessPercent%")) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::SetBrightness($BrightnessPercent, $LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Set-GoveeHostColor {
    <#
    .SYNOPSIS Sends a persistent solid-color request using the NEW set-color action.
    .NOTES Requires PC Direct control; color is a discrete, verified target, not a streaming frame.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([object]$Color = 'Red',
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    $rgb = ConvertTo-GoveeHostRgb -Color $Color
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Request solid RGB $rgb")) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::SetColor($rgb, $LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Set-GoveeHostColorTemperature {
    <# .SYNOPSIS Requests white Kelvin mode via planned Host IPC. #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([ValidateRange(1, 100000)][int]$Kelvin = 3500,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [ValidateRange(100, 30000)][int]$TimeoutMs = 5000,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Request white temperature $Kelvin K")) { return }
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::SetColorTemperature($Kelvin, $LampId)
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -TimeoutMs $TimeoutMs -Verbose:($VerbosePreference -eq 'Continue')
}

function Send-GoveeHostFrame {
    <#
    .SYNOPSIS Sends one logical RGB frame using Core's SendFrame DTO factory.
    .DESCRIPTION Frame datagrams have no acknowledgment. The caller is responsible for ownership and continuing frames as needed.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([Parameter(Mandatory = $true)][object[]]$Colors,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [switch]$PassThru,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    $frame = New-GoveeHostRgbFrame -Colors $Colors
    $request = [GoveeBridge.Core.Ipc.BridgeIpcRequests]::SendFrame($frame, $LampId)
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Send $($Colors.Count)-position frame")) { return }
    if ($PreviewJson) { return ConvertTo-GoveeHostJson -Request $request }
    return Send-GoveeHostRequest -Request $request -Port $Port -ExpectReply:$false -PassThru:$PassThru -Verbose:($VerbosePreference -eq 'Continue')
}

function Send-GoveeHostSolidFrame {
    <#
    .SYNOPSIS Sends a uniform RGB frame over the EXISTING rgb-frame IPC channel.
    .DESCRIPTION This is a streaming RGB frame and requires explicit Arm-GoveeHostStreaming; it never claims control automatically.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([object]$Color = 'Red',
        [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [switch]$PassThru,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$PreviewJson)
    $colors = [object[]]::new($LogicalLedCount)
    for ($i = 0; $i -lt $LogicalLedCount; $i++) { $colors[$i] = $Color }
    return Send-GoveeHostFrame -Colors $colors -LampId $LampId -Port $Port -PassThru:$PassThru -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue') -WhatIf:$WhatIfPreference
}

function Start-GoveeHostAnimation {
    <#
    .SYNOPSIS Alternates red and blue over Host IPC while refreshing the current frame.
    .DESCRIPTION Default five-second color changes, refreshing every 500 ms. -Resume enters PC Direct and explicitly arms streaming once. It does not reclaim control after remote release.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param([object]$FirstColor = 'Red',
        [object]$SecondColor = 'Blue',
        [ValidateRange(1, 3600)][int]$IntervalSeconds = 5,
        [ValidateRange(50, 4000)][int]$FrameRefreshMs = 500,
        [ValidateRange(-1, 3600)][int]$DurationSeconds = 60,
        [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
        [switch]$Resume,
        [switch]$ReleaseOnExit)

    if ($DurationSeconds -eq 0) { throw 'DurationSeconds must be -1 (until interrupted) or positive.' }
    $first = ConvertTo-GoveeHostRgb -Color $FirstColor
    $second = ConvertTo-GoveeHostRgb -Color $SecondColor
    if (-not $PSCmdlet.ShouldProcess("127.0.0.1:$Port", "Stream alternating frames for $DurationSeconds seconds")) { return }

    if ($Resume) {
        [void](Resume-GoveeHostControl -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
        # Enter streaming explicitly; resume alone only selects PC Direct.
        [void](Arm-GoveeHostStreaming -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastIndex = -1
    try {
        while ($DurationSeconds -eq -1 -or $watch.Elapsed.TotalSeconds -lt $DurationSeconds) {
            $index = ([int][Math]::Floor($watch.Elapsed.TotalSeconds / $IntervalSeconds)) % 2
            $rgb = if ($index -eq 0) { $first } else { $second }
            if ($lastIndex -ne $index) {
                Write-Verbose "Animation color changed to $rgb at $([Math]::Round($watch.Elapsed.TotalSeconds, 1)) s"
                $lastIndex = $index
            }
            $colors = [object[]]::new($LogicalLedCount)
            for ($i = 0; $i -lt $LogicalLedCount; $i++) { $colors[$i] = $rgb }
            [void](Send-GoveeHostFrame -Colors $colors -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
            Start-Sleep -Milliseconds $FrameRefreshMs
        }
    }
    finally {
        if ($ReleaseOnExit) {
            [void](Release-GoveeHostControl -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
        }
    }
}

function Watch-GoveeHostStatus {
    <# .SYNOPSIS Samples cached Host state over loopback, useful during remote relinquishment tests. #>
    [CmdletBinding()]
    param([ValidateRange(1, 3600)][int]$IntervalSeconds = 2,
        [ValidateRange(1, 10000)][int]$Count = 30,
        [ValidateRange(1024, 65535)][int]$Port = 41005,
        [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId)
    for ($index = 0; $index -lt $Count; $index++) {
        $result = Get-GoveeHostStatus -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
        [pscustomobject]@{
            Timestamp = [DateTimeOffset]::Now
            LampId = $result.lampId
            Ownership = $result.ownership
            Mode = $result.mode
            FramesReceived = $result.framesReceived
            FramesSent = $result.framesSent
            RazerArmed = $result.razerArmed
            LampOnline = $result.lampOnline
        }
        if ($index -lt ($Count - 1)) { Start-Sleep -Seconds $IntervalSeconds }
    }
}

# Loading at module import yields an immediate, actionable error when the DLL
# is unavailable. Do not fall back to custom hand-authored JSON messages.
Import-GoveeHostCoreAssembly

Export-ModuleMember -Function @(
    'ConvertTo-GoveeHostJson',
    'Get-GoveeHostStatus', 'Get-GoveeHostLampStatus',
    'Resume-GoveeHostControl', 'Release-GoveeHostControl',
    'Arm-GoveeHostStreaming', 'Disarm-GoveeHostStreaming',
    'Set-GoveeHostPower', 'Set-GoveeHostBrightness',
    'Set-GoveeHostColor', 'Set-GoveeHostColorTemperature',
    'Send-GoveeHostFrame', 'Send-GoveeHostSolidFrame',
    'Start-GoveeHostAnimation', 'Watch-GoveeHostStatus'
)
