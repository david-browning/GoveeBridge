<# Directs a PC-owned Host to turn the lamp on or off, without contacting it directly. #>
[CmdletBinding()]
param(
    [ValidateSet('On', 'Off')][string]$State = 'On',
    [string]$HostAddress = '127.0.0.1',
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Set-GoveeHostPower -State $State -HostAddress $HostAddress -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
