# GoveeLan PowerShell test module

Standalone module for direct LAN diagnostics with a Govee H6072. Includes official single-color/brightness/status commands and an **experimental**, tested-on-H6072 multicolor `razer` packet encoder. Works without SignalRGB and without GoveeBridge.Host.

**Before running:** Stop GoveeBridge.Host and any iCUE/SignalRGB Govee LAN integration. `Get-*` operations bind **UDP port 4002**, which can only have one owner. All commands send to the lamp at **UDP port 4003**. The `razer` test doesn't intentionally power on the lamp; turn it on with the remote first.

## Import

```powershell
Import-Module "$PWD/GoveeLan/GoveeLan.psd1" -Force
Get-Command -Module GoveeLan
$ip = '192.168.0.5'  # replace with your lamp's actual local IP
```

If you unzip the module into another directory, change the import path to the manifest file. No module installation or `Install-Module` is necessary.

## Supported commands

```powershell
Get-GoveeStatus -LampIp $ip -Verbose
Get-GoveeColor -LampIp $ip
Get-GoveeBrightness -LampIp $ip
Get-GoveeColorTemperature -LampIp $ip

Set-GoveePower -LampIp $ip -State On -Verbose
Set-GoveeBrightness -LampIp $ip -Brightness 60
Set-GoveeColor -LampIp $ip -Color '#4080FF'
Set-GoveeColor -LampIp $ip -Color @{ R = 64; G = 128; B = 255 }
Set-GoveeColorTemperature -LampIp $ip -Kelvin 4500

Set-GoveePattern -LampIp $ip -Colors @('#FF0000', '#0000FF') -Seconds 5 -Verbose
$frame = 0..127 | ForEach-Object { if ($_ % 2) { '#0000FF' } else { '#FF0000' } }
Set-GoveePattern -LampIp $ip -Colors $frame -LEDCount 128 -Seconds 10 -FrameIntervalMs 250
Set-GoveePattern -LampIp $ip -Colors @('#FF0000', '#0000FF') -Gradient -Seconds -1
```

- All exported functions accept **`-LampIp`** and the common PowerShell **`-Verbose`** parameter. Setters also implement **`-WhatIf`** and **`-Confirm`** via `ShouldProcess`.
- **`-LEDCount` only applies to `Set-GoveePattern`.** If present, it must match the length of `-Colors` (no automatic resampling). Otherwise the submitted count is inferred from `-Colors`. Allowed range: **1–255** color entries. This is a *logical frame length*, not a measured physical LED count.
- **`-Seconds` only applies to pattern streaming.** Default 5. `-Seconds -1` continuously repeats the frame until interrupted with Ctrl+C, then attempts `B1 00` in a `finally` block. It does **not** send one frame and deliberately leave real-time mode armed. Abrupt termination can prevent disarm; firmware inactivity-timeout behavior is not guaranteed.
- Pattern `-Colors` may be a string array of `#RRGGBB`, a collection of hashtables containing `R`, `G` and `B`, or RGB triples. A **single** RGB triple must be passed as a nested array; hex strings are simpler.
- `-Gradient` requests `0x01`; without it uses discrete mode `0x00`. Your lamp may still visibly blend neighboring regions due to its firmware mapping or diffuser.
- The tested H6072 maps pattern index **0 to the bottom**, and index **count - 1 to the top**. No fixed physical zone count is assumed.
- `Get-GoveeColor` reports the RGB value from the **whole-device** `devStatus`. It cannot recover all per-segment colors from a real-time pattern. When in color-temperature mode, the reported RGB fields may be zero.
- Brightness uses a **separate official command** and is independent at the protocol level from solid RGB / color temperature selection. How brightness affects `razer`-controlled frames should be measured.
- `Set-GoveeColor` and `Set-GoveeColorTemperature` **both** use `colorwc` with mutually exclusive mode values (`0` for RGB, `2000–9000` K for white). The module separates them for ease of use.
- UDP commands have **no acknowledgment**. Sending successfully does not prove the lamp applied an operation. Use a `Get-*` command afterwards to verify official state, where applicable.

## Testing and limitations

- These are **direct hardware tools**, deliberately bypassing the future GoveeBridge Host IPC security boundary. Do **not** run them simultaneously with the Host in real-device mode; a separate process bypassing ownership arbitration can interfere with the Host.
- `Get-*` functions use port `4002` for device replies, so any other process owning that port causes a socket bind error. No fallback to another local port is possible in the documented protocol.
- Status changes and all `razer` frames might not be represented in `devStatus`. Use visual confirmation for segment control.
- The long-running pattern test sends a periodic re-arm at 30-second intervals as a **conservative experimental keepalive**. Its necessity on this particular firmware is unverified.
- No network authentication/encryption is provided by the physical Govee LAN protocol. Restrict access to trusted networks.
- Module targets Windows PowerShell 5.1 and PowerShell 7+, and uses only .NET/PowerShell built-ins.

Protocol references: [Govee WLAN guide](https://app-h5.govee.com/user-manual/wlan-guide), [govee-toolkit LAN protocol research](https://github.com/damient/govee-toolkit/blob/main/docs/protocol/lan.md), [govee_razer_led protocol](https://github.com/bochelork/govee_razer_led/blob/main/PROTOCOL.md).
