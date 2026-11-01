@{
    RootModule = 'GoveeLan.psm1'
    ModuleVersion = '0.1.0'
    GUID = '0aac5940-915d-4cd3-98c6-2725217096e6'
    Author = 'GoveeBridge project'
    CompanyName = '4F Software'
    Copyright = '(c) 2026. All rights reserved.'
    Description = 'PowerShell diagnostics for Govee LAN commands and experimental Razer RGB frames.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-GoveeStatus',
        'Get-GoveeColor',
        'Get-GoveeBrightness',
        'Get-GoveeColorTemperature',
        'Set-GoveePower',
        'Set-GoveeColor',
        'Set-GoveeBrightness',
        'Set-GoveeColorTemperature',
        'Set-GoveePattern'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Govee', 'LAN', 'RGB', 'PowerShell', 'Diagnostics')
        }
    }
}
