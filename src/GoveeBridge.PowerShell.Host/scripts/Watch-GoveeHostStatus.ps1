<# Watches Host ownership/reconciliation in a separate terminal. #>
[CmdletBinding()]
param(
    [ValidateRange(1,1000)][int]$Count = 15,
    [ValidateRange(1,60)][int]$IntervalSeconds = 2,
    [string]$HostAddress = '127.0.0.1',
    [ValidateRange(1024,65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Watch-GoveeHostStatus -Count $Count -IntervalSeconds $IntervalSeconds -HostAddress $HostAddress -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
