<# Requests 50% global brightness by default; requires the Host IPC extension. #>
[CmdletBinding()]
param(
    [ValidateRange(1, 100)][int]$Brightness = 50,
    [string]$HostAddress = '127.0.0.1',
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Set-GoveeHostBrightness -Brightness $Brightness -HostAddress $HostAddress -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
