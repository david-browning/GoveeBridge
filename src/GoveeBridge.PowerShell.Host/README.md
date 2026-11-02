# GoveeHost PowerShell IPC module (Core DLL-backed)

This module communicates **only with GoveeBridge.Host over IPv4 loopback UDP** (default `127.0.0.1:41005`). It never sends a packet to the Govee lamp, never binds Govee's device-response port 4002, and never constructs proprietary Govee LAN commands.

The request format comes directly from the **compiled `GoveeBridge.Core.dll`**. Every command calls the `BridgeIpcRequests` C# factory and serializes its returned object with `System.Text.Json` to honor `JsonPropertyName`. We deliberately do not reimplement the IPC constants or body schema in PowerShell.

## Prerequisites and assembly resolution

1. Use **PowerShell 7.6+ (`pwsh`)**, based on .NET 10. Windows PowerShell 5.1 and PowerShell 7.5 cannot reliably load this `net10.0` library.
2. Copy `BridgeIpcConstants.cs`, `BridgeIpcDtos.cs`, and `BridgeIpcRequests.cs` into `src/GoveeBridge.Core/Ipc` and build your Core project (the new sources are not yet pushed).
3. Place this module under `src/GoveeBridge.PowerShell.Host` alongside the `GoveeBridge.Core` project, or configure an explicit DLL path.

Set this variable **at the top of `GoveeHost.psm1`** to override discovery:

```powershell
$script:GoveeBridgeCoreDllPath = 'C:\Source\GoveeBridge\src\GoveeBridge.Core\bin\Debug\net10.0\GoveeBridge.Core.dll'
```

Alternatively, in the session before import:

```powershell
$env:GOVEEBRIDGE_CORE_DLL = 'C:\Source\GoveeBridge\src\GoveeBridge.Core\bin\Debug\net10.0\GoveeBridge.Core.dll'
```

When neither is provided, the module looks for:

- `GoveeBridge.Core.dll` adjacent to `GoveeHost.psm1`
- `../GoveeBridge.Core/bin/Debug/net10.0/GoveeBridge.Core.dll`
- `../GoveeBridge.Core/bin/Release/net10.0/GoveeBridge.Core.dll`

It does **not** search the GAC, arbitrary working directories, or your entire drive. A specified but missing DLL produces an error rather than silently choosing another build. After rebuilding a loaded DLL, **restart `pwsh`** to load its new types; reimporting a module cannot unload and replace a .NET assembly in the same session.

## Import and preview the messages before enabling Host support

```powershell
Import-Module .\src\GoveeBridge.PowerShell.Host\GoveeHost.psd1 -Force

Get-GoveeHostStatus -PreviewJson
Resume-GoveeHostControl -PreviewJson
Set-GoveeHostPower -State Off -PreviewJson
Set-GoveeHostBrightness -BrightnessPercent 35 -PreviewJson
Set-GoveeHostColor -Color '#123456' -PreviewJson
Set-GoveeHostColorTemperature -Kelvin 3500 -PreviewJson
Send-GoveeHostFrame -Colors @('Red', 'Blue') -PreviewJson
```

`-PreviewJson` uses the *real Core DTO* but opens **no socket**. It is useful while the Host dispatcher does not yet understand the new actions.

## Which operations the current Host supports

| Cmdlet | IPC request | Current checked-in Host |
|---|---|---|
| `Get-GoveeHostStatus` | `command/status` | Yes |
| `Get-GoveeHostLampStatus` | `command/lamp-status` | Yes, except while some real-time modes are armed |
| `Resume-GoveeHostControl` | `command/resume` | Yes |
| `Release-GoveeHostControl` | `command/release` | Yes |
| `Send-GoveeHostFrame` | `rgb-frame` | Yes, no reply |
| `Send-GoveeHostSolidFrame` | `rgb-frame` | Yes, no reply |
| `Set-GoveeHostPower` | `command/set-power` | **Not yet** |
| `Set-GoveeHostBrightness` | `command/set-brightness` | **Not yet** |
| `Set-GoveeHostColor` | `command/set-color` | **Not yet** |
| `Set-GoveeHostColorTemperature` | `command/set-color-temperature` | **Not yet** |

The new `set-*` methods require Host-side dispatch and ownership checks. At present they correctly return a Host error such as `Unknown command: set-power`; they do **not** fall back to direct lamp LAN traffic. The requested brightness range is 0..100, matching `LampState`, but how to map brightness 0 to hardware power is future Host policy. Kelvin is only required to be positive by Core; device-specific ranges are Host policy.

**Persistent solid color vs. frame:** `Set-GoveeHostColor` uses `set-color`, intended to be a persistent state change once implemented. `Send-GoveeHostSolidFrame` uses the existing `rgb-frame` path and is subject to the Host frame-staleness policy.

## Online tests

In one PowerShell window:

```powershell
Get-GoveeHostStatus -Verbose
Resume-GoveeHostControl -Verbose
Send-GoveeHostSolidFrame -Color Red -LogicalLedCount 8 -PassThru -Verbose
Release-GoveeHostControl -Verbose
```

A single frame may be sent before the output pump processes it; repeated frames are safer for a visible test. Use the wrapper for a longer run:

```powershell
.\src\GoveeBridge.PowerShell.Host\scripts\Test-GoveeHostRedBlue.ps1 -DurationSeconds 60 -Verbose
```

This changes the requested color **every 5 seconds**, but continues resending the current frame every **500 ms** so the Host does not release ownership merely because the source is idle for five seconds. It resumes **once**, not after every color change. You can turn the lamp off with the remote while it runs and verify that output stops after Runtime yields ownership. It does not intentionally release on exit unless `-ReleaseOnExit` is specified.

In a second session:

```powershell
Watch-GoveeHostStatus -Count 30 -IntervalSeconds 2
```

For a short stream with guaranteed cleanup, use the integration test with `-DriveFrames`.

## Offline checks

The script `Tests/Test-GoveeHost.Contracts.ps1` requires only the compiled Core DLL and `pwsh`, **not** a running Host or lamp. It checks the JSON message types, action names, typed payloads, RGB byte order and invalid brightness rejection.

```powershell
.\src\GoveeBridge.PowerShell.Host\Tests\Test-GoveeHost.Contracts.ps1
```

Run the live smoke test against an active Host:

```powershell
.\src\GoveeBridge.PowerShell.Host\Tests\Test-GoveeHost.Integration.ps1
.\src\GoveeBridge.PowerShell.Host\Tests\Test-GoveeHost.Integration.ps1 -DriveFrames
```

`-DriveFrames` explicitly grants PC ownership and releases it in `finally`. Live frame submission is UDP best-effort, not proof of physical output; management replies mean only that the Host processed a command, not necessarily that the device applied it.

## Notes

- You may override `-Port` when testing on a non-default **local loopback** Host. No public network addresses or lamp IP arguments are accepted.
- Functions that could change state support `-WhatIf` and `-Verbose`.
- Nothing here changes `BridgeRuntime`, `BridgeWorker`, SignalRGB, Govee's LAN PowerShell module, or GitHub.
- Loopback restricts remote network exposure but does not authenticate other local processes. Treat this as a development/testing IPC surface.
