<# Default static-color test. Resumes once, then sends one red frame. #>
[CmdletBinding()]
param(
    [object]$Color = 'Red',
    [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
    [switch]$NoResume,
    [string]$HostAddress = '127.0.0.1',
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
Set-GoveeHostColor -Color $Color -LogicalLedCount $LogicalLedCount -Resume:(-not $NoResume) -HostAddress $HostAddress -Port $Port -PassThru -Verbose:($VerbosePreference -eq 'Continue')
