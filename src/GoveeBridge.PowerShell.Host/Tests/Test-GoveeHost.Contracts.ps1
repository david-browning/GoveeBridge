# Core IPC serialization tests.
# Pure contract test. Uses the compiled C# Core DLL; does not open a socket.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path (Split-Path $PSScriptRoot -Parent) 'GoveeHost.psd1') -Force -ErrorAction Stop
$lampId = 'floor'

function Assert-Contract {
    param([bool]$Condition, [string]$Description)
    if (-not $Condition) { throw "FAILED: $Description" }
    Write-Host "PASS: $Description"
}

$cases = @(
    @{ Name='Host status'; Json=(Get-GoveeHostStatus -LampId $lampId -PreviewJson); Action='status' },
    @{ Name='Lamp status'; Json=(Get-GoveeHostLampStatus -LampId $lampId -PreviewJson); Action='lamp-status' },
    @{ Name='Resume'; Json=(Resume-GoveeHostControl -LampId $lampId -PreviewJson); Action='resume' },
    @{ Name='Release'; Json=(Release-GoveeHostControl -LampId $lampId -PreviewJson); Action='release' },
    @{ Name='Power off'; Json=(Set-GoveeHostPower -LampId $lampId -State Off -PreviewJson); Action='set-power' },
    @{ Name='Brightness'; Json=(Set-GoveeHostBrightness -LampId $lampId -BrightnessPercent 35 -PreviewJson); Action='set-brightness' },
    @{ Name='Solid RGB'; Json=(Set-GoveeHostColor -LampId $lampId -Color '#123456' -PreviewJson); Action='set-color' },
    @{ Name='Kelvin'; Json=(Set-GoveeHostColorTemperature -LampId $lampId -Kelvin 3500 -PreviewJson); Action='set-color-temperature' }
)
foreach ($case in $cases) {
    $request = ConvertFrom-Json -InputObject $case.Json -ErrorAction Stop
    Assert-Contract ($request.type -eq 'command') "$($case.Name): type"
    Assert-Contract ($request.action -eq $case.Action) "$($case.Name): action"
    Assert-Contract ($request.lampId -eq $lampId) "$($case.Name): explicit lamp ID"
}
$power = ConvertFrom-Json (Set-GoveeHostPower -LampId $lampId -State Off -PreviewJson)
Assert-Contract ($power.data.isOn -eq $false) 'Power payload isOn false'
$brightness = ConvertFrom-Json (Set-GoveeHostBrightness -LampId $lampId -BrightnessPercent 35 -PreviewJson)
Assert-Contract ($brightness.data.brightnessPercent -eq 35) 'Brightness payload 35'
$rgb = ConvertFrom-Json (Set-GoveeHostColor -LampId $lampId -Color '#123456' -PreviewJson)
Assert-Contract ($rgb.data.color.r -eq 18 -and $rgb.data.color.g -eq 52 -and $rgb.data.color.b -eq 86) 'RGB payload bytes'
$kelvin = ConvertFrom-Json (Set-GoveeHostColorTemperature -LampId $lampId -Kelvin 3500 -PreviewJson)
Assert-Contract ($kelvin.data.kelvin -eq 3500) 'Temperature payload Kelvin'
$frame = ConvertFrom-Json (Send-GoveeHostFrame -LampId $lampId -Colors @('Red', 'Blue') -PreviewJson)
Assert-Contract ($frame.type -eq 'rgb-frame' -and $frame.count -eq 2) 'Frame shape/count'
Assert-Contract ($frame.lampId -eq $lampId) 'Frame has explicit lamp ID'
Assert-Contract (([string]::Join(',', [int[]]$frame.colors)) -eq '255,0,0,0,0,255') 'Frame RGB byte order'
$bad = $false
try { [void](Set-GoveeHostBrightness -LampId $lampId -BrightnessPercent 101 -PreviewJson) } catch { $bad = $true }
Assert-Contract $bad 'Invalid brightness rejected'
$missing = $false
try { [void](Get-GoveeHostStatus -LampId '   ' -PreviewJson -ErrorAction Stop) } catch { $missing = $true }
Assert-Contract $missing 'Whitespace lamp ID rejected before serialization'
Write-Host 'All Core IPC contract tests passed.'
