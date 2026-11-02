<#
.SYNOPSIS
Exercises Host ownership and streaming with 5-second red/blue changes.
.DESCRIPTION
Uses only localhost IPC. Continues refreshing the current frame so that the
Host's frame-staleness timeout is not accidentally triggered between color changes.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 3600)][int]$DurationSeconds = 60,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 5,
    [ValidateRange(50, 4000)][int]$FrameRefreshMs = 500,
    [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$ReleaseOnExit
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
Start-GoveeHostAnimation -DurationSeconds $DurationSeconds -IntervalSeconds $IntervalSeconds `
    -FrameRefreshMs $FrameRefreshMs -LogicalLedCount $LogicalLedCount -Port $Port `
    -Resume -ReleaseOnExit:$ReleaseOnExit -Verbose:($VerbosePreference -eq 'Continue')
