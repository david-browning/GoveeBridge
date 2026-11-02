<#
.SYNOPSIS Tests planned set-color-temperature action (requires updated Host dispatcher).
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100000)][int]$Kelvin = 3500,
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostColorTemperature -Kelvin $Kelvin -Port $Port `
    -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
