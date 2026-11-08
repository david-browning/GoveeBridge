<#
.SYNOPSIS Tests the set-power action.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [ValidateSet('On', 'Off')][string]$State = 'On',
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostPower -LampId $LampId -State $State -Port $Port -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
