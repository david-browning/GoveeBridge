@{
    RootModule = 'GoveeHost.psm1'
    ModuleVersion = '0.2.0'
    GUID = '428e29d7-491b-4dc7-ab49-dc6452ac2856'
    Author = 'GoveeBridge project'
    CompanyName = '4F Software'
    Description = 'Local Host IPC testing through GoveeBridge.Core.dll request factories; never controls Govee devices directly.'
    PowerShellVersion = '7.6'
    CompatiblePSEditions = @('Core')
    FunctionsToExport = @(
        'ConvertTo-GoveeHostJson',
        'Get-GoveeHostStatus', 'Get-GoveeHostLampStatus',
        'Resume-GoveeHostControl', 'Release-GoveeHostControl',
        'Set-GoveeHostPower', 'Set-GoveeHostBrightness',
        'Set-GoveeHostColor', 'Set-GoveeHostColorTemperature',
        'Send-GoveeHostFrame', 'Send-GoveeHostSolidFrame',
        'Start-GoveeHostAnimation', 'Watch-GoveeHostStatus'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Govee', 'IPC', 'Loopback', 'Testing', '.NET10')
        }
    }
}
