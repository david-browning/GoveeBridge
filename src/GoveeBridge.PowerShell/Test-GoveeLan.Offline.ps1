# Deterministic no-hardware checks. These call non-exported helpers inside the module.
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'GoveeLan.psd1'
Import-Module $modulePath -Force
$module = Get-Module GoveeLan

& $module {
    function Assert-Same {
        param([object]$Actual, [object]$Expected, [string]$TestName)
        if ($Actual -cne $Expected) { throw "FAILED $TestName`: '$Actual' != '$Expected'" }
        Write-Host "PASS $TestName"
    }

    $arm = New-GoveeRazerPacket -Opcode 0xB1 -Payload ([byte[]]@(1))
    Assert-Same -Actual ([System.BitConverter]::ToString($arm).Replace('-', '')) -Expected 'BB0001B1010A' -TestName 'Arm frame'
    Assert-Same -Actual ([Convert]::ToBase64String($arm)) -Expected 'uwABsQEK' -TestName 'Arm base64'

    $disarm = New-GoveeRazerPacket -Opcode 0xB1 -Payload ([byte[]]@(0))
    Assert-Same -Actual ([System.BitConverter]::ToString($disarm)) -Expected 'BB-00-01-B1-00-0B' -TestName 'Disarm frame'

    $red = ConvertTo-GoveeRgb -Color '#FF0000'
    Assert-Same -Actual $red.Hex -Expected '#FF0000' -TestName 'Hex parse'
    $blue = ConvertTo-GoveeRgb -Color @{R=0; G=0; B=255}
    Assert-Same -Actual $blue.Hex -Expected '#0000FF' -TestName 'Hashtable parse'

    # Length of B0 frame data is 2 + 3*N, big-endian: N=128 => 386 => 0x0182.
    $payload = [byte[]]::new(2 + 128 * 3)
    $payload[0] = 0
    $payload[1] = 128
    for ($i = 0; $i -lt 128; $i++) {
        $payload[2 + $i*3] = if ($i % 2 -eq 0) { 255 } else { 0 }
        $payload[4 + $i*3] = if ($i % 2 -eq 0) { 0 } else { 255 }
    }
    $packet = New-GoveeRazerPacket -Opcode 0xB0 -Payload $payload
    Assert-Same -Actual ([int]$packet[1]) -Expected 1 -TestName '128-color length high byte'
    Assert-Same -Actual ([int]$packet[2]) -Expected 130 -TestName '128-color length low byte'
    $checksum = 0
    foreach ($b in $packet) { $checksum = $checksum -bxor [int]$b }
    Assert-Same -Actual $checksum -Expected 0 -TestName '128-color checksum'
}

$expected = @('Get-GoveeStatus', 'Set-GoveeColor', 'Set-GoveeBrightness', 'Get-GoveeColor',
    'Get-GoveeBrightness', 'Set-GoveePattern', 'Set-GoveeColorTemperature',
    'Get-GoveeColorTemperature', 'Set-GoveePower')
foreach ($name in $expected) {
    if (-not (Get-Command $name -Module GoveeLan -ErrorAction SilentlyContinue)) {
        throw "FAILED export: $name"
    }
}
Write-Host 'PASS all exported commands'
