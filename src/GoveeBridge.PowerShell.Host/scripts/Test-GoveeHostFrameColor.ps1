<#
.SYNOPSIS Sends a single uniform frame over the existing rgb-frame IPC.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrWhiteSpace()][string]$LampId,
    [object]$Color = 'Red',
    [ValidateRange(1, 256)][int]$LogicalLedCount = 8,
    [ValidateRange(1024, 65535)][int]$Port = 41005,
    [switch]$PreviewJson,
    [switch]$Resume
)
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
if ($Resume -and -not $PreviewJson) {
    [void](Resume-GoveeHostControl -LampId $LampId -Port $Port -Verbose:($VerbosePreference -eq 'Continue'))
}
Send-GoveeHostSolidFrame -LampId $LampId -Color $Color -LogicalLedCount $LogicalLedCount -Port $Port `
    -PreviewJson:$PreviewJson -PassThru -Verbose:($VerbosePreference -eq 'Continue')
