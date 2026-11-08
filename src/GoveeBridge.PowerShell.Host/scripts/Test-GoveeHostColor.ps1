<#
.SYNOPSIS Tests the persistent set-color IPC.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [object]$Color = 'Red',
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostColor -LampId $LampId -Color $Color -Port $Port -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
