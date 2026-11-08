<# Uniform streaming-color test. Resumes and arms once unless -NoResume. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [object]$Color = 'Red',
    [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
    [switch]$NoResume,
    [ValidateRange(1024, 65535)][int]$Port = 41005
)
Import-Module (Join-Path $PSScriptRoot '..\GoveeHost.psd1') -Force
if (-not $NoResume) {
    [void](Resume-GoveeHostControl -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
    [void](Arm-GoveeHostStreaming -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
}
Send-GoveeHostSolidFrame -LampId $LampId -Color $Color -LogicalLedCount $LogicalLedCount -Port $Port -PassThru -Verbose:($VerbosePreference -eq 'Continue')
