<# Directs a PC-owned Host to turn the lamp on or off, without contacting it directly. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [ValidateSet('On', 'Off')][string]$State = 'On',
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Set-GoveeHostPower -LampId $LampId -State $State -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
