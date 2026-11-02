<# .SYNOPSIS Displays Host ownership and lamp statistics every two seconds. #>
[CmdletBinding()]
param(
    [ValidateRange(1, 10000)][int]$Count = 30,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 2,
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Watch-GoveeHostStatus -Count $Count -IntervalSeconds $IntervalSeconds -Port $Port `
    -Verbose:($VerbosePreference -eq 'Continue')
