<#
.SYNOPSIS Tests the planned set-power action (requires updated Host dispatcher).
#>
[CmdletBinding()]
param(
    [ValidateSet('On', 'Off')][string]$State = 'On',
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostPower -State $State -Port $Port -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
