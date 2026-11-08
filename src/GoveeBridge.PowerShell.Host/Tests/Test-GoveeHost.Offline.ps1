<#
.SYNOPSIS Runs loopback-only contract tests using a temporary fake Host.
.DESCRIPTION Requires neither the physical lamp nor a running GoveeBridge.Host. Uses a background PowerShell job to capture datagrams.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manifest = Join-Path $PSScriptRoot '..\GoveeHost.psd1'
Import-Module $manifest -Force
$module = Test-ModuleManifest -Path $manifest -ErrorAction Stop
Write-Host "Loaded GoveeHost $($module.Version)"

function Assert-HostTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "TEST FAILED: $Message" }
    Write-Host "PASS: $Message"
}

$functions = @('Get-GoveeHostStatus', 'Get-GoveeHostLampStatus', 'Resume-GoveeHostControl',
    'Release-GoveeHostControl', 'Set-GoveeHostColor', 'Set-GoveeHostPower',
    'Set-GoveeHostBrightness', 'Set-GoveeHostColorTemperature', 'Start-GoveeHostAnimation')
foreach ($name in $functions) {
    Assert-HostTest -Condition ($null -ne (Get-Command -Name $name -ErrorAction SilentlyContinue)) -Message "$name is exported"
}

$rejected = $false
try { Get-GoveeHostStatus -LampId $LampId -HostAddress '192.168.0.5' -TimeoutMs 100 | Out-Null }
catch { $rejected = $true }
Assert-HostTest -Condition $rejected -Message 'Rejects nonloopback addresses'

$rejected = $false
try { Set-GoveeHostColor -LampId $LampId -Color '#WRONG!' -WhatIf | Out-Null }
catch { $rejected = $true }
Assert-HostTest -Condition $rejected -Message 'Rejects invalid RGB colors'

# Reserve an unused ephemeral UDP loopback port, then release it for the mock Host.
$probe = [System.Net.Sockets.UdpClient]::new(
    [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 0))
$port = ([System.Net.IPEndPoint]$probe.Client.LocalEndPoint).Port
$probe.Dispose()

$job = Start-Job -ArgumentList $port -ScriptBlock {
    param([int]$Port)
    $ErrorActionPreference = 'Stop'
    $listener = [System.Net.Sockets.UdpClient]::new(
        [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, $Port))
    $listener.Client.ReceiveTimeout = 8000
    $observed = [System.Collections.Generic.List[string]]::new()
    try {
        for ($index = 0; $index -lt 5; $index++) {
            $sender = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            [byte[]]$received = $listener.Receive([ref]$sender)
            $json = [System.Text.Encoding]::UTF8.GetString($received)
            $observed.Add($json)
            $request = ConvertFrom-Json -InputObject $json
            if ($request.type -eq 'command') {
                $reply = @{ ownership = 'PC'; mode = 'Diagnostic'; echo = $request.action } | ConvertTo-Json -Compress
                [byte[]]$replyBytes = [System.Text.Encoding]::UTF8.GetBytes($reply)
                $listener.Send($replyBytes, $replyBytes.Length, $sender) | Out-Null
            }
        }
        foreach ($item in $observed) { Write-Output $item }
    }
    finally { $listener.Dispose() }
}

try {
    Start-Sleep -Milliseconds 600
    $result = Get-GoveeHostStatus -LampId $LampId -Port $port -TimeoutMs 2000
    Assert-HostTest -Condition ($result.echo -eq 'status') -Message 'Host status command round trip'
    $result = Resume-GoveeHostControl -LampId $LampId -Port $port -TimeoutMs 2000
    Assert-HostTest -Condition ($result.echo -eq 'resume') -Message 'Resume command round trip'
    $result = Set-GoveeHostBrightness -LampId $LampId -BrightnessPercent 65 -Port $port -TimeoutMs 2000
    Assert-HostTest -Condition ($result.echo -eq 'set-brightness') -Message 'Brightness command round trip'
    Send-GoveeHostSolidFrame -LampId $LampId -Color '#A1B2C3' -LogicalLedCount 2 -Port $port
    $result = Release-GoveeHostControl -LampId $LampId -Port $port -TimeoutMs 2000
    Assert-HostTest -Condition ($result.echo -eq 'release') -Message 'Release command round trip'

    $finished = Wait-Job -Job $job -Timeout 8
    Assert-HostTest -Condition ($null -ne $finished) -Message 'Mock Host captured all five packets'
    $received = @(Receive-Job -Job $job -ErrorAction Stop)
    Assert-HostTest -Condition ($received.Count -eq 5) -Message 'Exactly five datagrams transmitted'
    foreach ($message in $received) {
        Assert-HostTest -Condition ((ConvertFrom-Json $message).lampId -eq $LampId) -Message 'Packet targets requested lamp'
    }
    $brightness = ConvertFrom-Json -InputObject $received[2]
    Assert-HostTest -Condition ($brightness.data.brightnessPercent -eq 65) -Message 'Brightness value serialized as 65'
    $frame = ConvertFrom-Json -InputObject $received[3]
    Assert-HostTest -Condition ($frame.type -eq 'rgb-frame' -and $frame.count -eq 2) -Message 'RGB frame has correct type and count'
    Assert-HostTest -Condition ((@($frame.colors) -join ',') -eq '161,178,195,161,178,195') -Message 'RGB frame flattened in R/G/B order'
}
finally {
    if ($null -ne $job) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
}
Write-Host 'Offline tests completed.'
