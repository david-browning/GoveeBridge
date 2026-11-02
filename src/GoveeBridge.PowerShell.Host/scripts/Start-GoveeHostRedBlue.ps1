<#
.SYNOPSIS Alternates red and blue every five seconds (default 60 seconds).
.DESCRIPTION Resumes PC control ONCE, never again during animation. Use another terminal to request release or use the physical remote. RGB datagrams continue after release but should NOT move the lamp.
#>
[CmdletBinding()]
param(
    [ValidateRange(1,3600)][int]$ChangeEverySeconds = 5,
    [ValidateRange(100,4000)][int]$FrameIntervalMs = 500,
    [ValidateRange(-1,3600)][int]$DurationSeconds = 60,
    [ValidateRange(1,256)][int]$LogicalLedCount = 8,
    [switch]$NoResume,
    [switch]$ReleaseOnExit,
    [string]$HostAddress = '127.0.0.1',
    [ValidateRange(1024,65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Start-GoveeHostAnimation -FirstColor Red -SecondColor Blue -ChangeEverySeconds $ChangeEverySeconds -FrameIntervalMs $FrameIntervalMs -DurationSeconds $DurationSeconds -LogicalLedCount $LogicalLedCount -Resume:(-not $NoResume) -ReleaseOnExit:$ReleaseOnExit -HostAddress $HostAddress -Port $Port -Verbose:($VerbosePreference -eq 'Continue')
