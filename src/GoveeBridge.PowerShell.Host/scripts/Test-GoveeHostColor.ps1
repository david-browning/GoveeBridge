<#
.SYNOPSIS Tests the new, persistent set-color IPC (requires an updated Host dispatcher).
#>
[CmdletBinding()]
param(
    [object]$Color = 'Red',
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Set-GoveeHostColor -Color $Color -Port $Port -PreviewJson:$PreviewJson -Verbose:($VerbosePreference -eq 'Continue')
