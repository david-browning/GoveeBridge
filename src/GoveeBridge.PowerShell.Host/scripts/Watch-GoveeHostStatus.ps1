<# Watches Host ownership/reconciliation in a separate terminal. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [ValidateRange(1,1000)][int]$Count = 15,
    [ValidateRange(1,60)][int]$IntervalSeconds = 2,
    [ValidateRange(1024,65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Watch-GoveeHostStatus -LampId $LampId -Count $Count -IntervalSeconds $IntervalSeconds -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
