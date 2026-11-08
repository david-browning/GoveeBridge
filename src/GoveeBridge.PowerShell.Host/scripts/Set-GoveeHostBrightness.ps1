<# Requests 50% global brightness by default; requires the Host IPC extension. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [ValidateRange(1, 100)][int]$Brightness = 50,
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Set-GoveeHostBrightness -LampId $LampId -BrightnessPercent $Brightness -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
