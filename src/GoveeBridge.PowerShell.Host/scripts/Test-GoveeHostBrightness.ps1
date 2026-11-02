<#
.SYNOPSIS Tests planned set-brightness action (requires updated Host dispatcher).
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 100)][int]$BrightnessPercent = 50,
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostBrightness -BrightnessPercent $BrightnessPercent -Port $Port `
    -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
