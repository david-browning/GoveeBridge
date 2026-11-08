<#
.SYNOPSIS
Checks live Host management responses and optionally exercises its frame handler.
.DESCRIPTION
Only loopback IPC. Does not access the lamp directly. Set -DriveFrames to
request PC control, send a short stream, then explicitly release in finally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$DriveFrames,
    [ValidateRange(1, 256)][int]$LogicalLedCount = 8
)
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop

$initial = Get-GoveeHostStatus -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
Write-Host "Connected to Host. mode=$($initial.mode); ownership=$($initial.ownership)"
if ($DriveFrames) {
    try {
        $owner = Resume-GoveeHostControl -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
        if ($owner.ownership -ne 'PC') { throw 'Host did not grant PC ownership.' }
        [void](Arm-GoveeHostStreaming -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
        $colors = [object[]]::new($LogicalLedCount)
        for ($i = 0; $i -lt $LogicalLedCount; $i++) { $colors[$i] = 'Red' }
        for ($j = 0; $j -lt 6; $j++) {
            Send-GoveeHostFrame -LampId $LampId -Colors $colors -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
            Start-Sleep -Milliseconds 500
        }
        $current = Get-GoveeHostStatus -LampId $LampId -Port $Port
        Write-Host "framesReceived=$($current.framesReceived); framesSent=$($current.framesSent)"
    }
    finally {
        $released = Release-GoveeHostControl -LampId $LampId -Port $Port
        Write-Host "After release: ownership=$($released.ownership)"
    }
}
Write-Host 'Host integration smoke test completed.'
