# GoveeBridge: three-mode control + manually configured multi-lamp foundation

**Baseline:** GitHub `david-browning/GoveeBridge`, branch `david/bridgeruntimerefactor`, commit `d66a87437c483ef901f5380075bd5ad75f7e5de8` (`Cleanup runtime command processing`). These are replacement/new files relative to that commit, **not a complete Git checkout**. Nothing has been pushed to GitHub.

**Important:** This is a coordinated refactor that changes configuration, logical output semantics and one existing response field. Apply all the files together. Back up your uncommitted changes first; use Git diff/Visual Studio's compare if your branch has advanced. There is no .NET SDK or PowerShell runtime in this environment, so the package has **not been compiled or executed**. Offline structural/JSON checks only; build and run the tests locally before physical hardware testing.

## Design

One Host process binds `127.0.0.1:41005` for management commands and frames. `BridgeWorker` owns IPC and timers. `BridgeRuntime` owns a `Dictionary<string,LampSession>` keyed by a stable, configured `lampId`. Each `LampSession` contains its own `SemaphoreSlim` state gate, frame mailbox, requested power, send/retry history, latest physical observation, and physical client. `GoveeLanNetwork` shares the one **UDP 4002** reply socket among all `GoveeLanClient` instances and routes status responses by source IP address. Discovery and IP-reconciliation are not implemented.

`TransportMode` is removed. `Diagnostic` is now an independent Host setting. `ControlMode` is per lamp and means **Host-selected authority**, not hardware-confirmed state:

- `Manual`: no unsolicited Host lighting output; only observation and control transitions.
- `Pc`: direct commands; static RGB `set-color` retains bounded verification retries; no frames output.
- `PcStreaming`: only Razer/multizone frame output plus global power/brightness; no direct RGB or temperature.

Permission data is a static, immutable `FrozenDictionary<string,ModeMask>` in `BridgeRuntime.Commands.cs`, not a new command hierarchy. The table grants permission; handlers still check capability, payload and state-transition invariants.

| Action | Manual | Pc | PcStreaming |
|---|---|---|---|
| `status` | Yes | Yes | Yes |
| `lamp-status` | Yes* | Yes* | Yes* |
| `resume` | → Pc | No-op | No-op |
| `release` | No-op/retry DISARM | → Manual | → Manual |
| `arm` | Reject | → PcStreaming | No-op |
| `disarm` | No-op/retry DISARM | No-op/retry DISARM | → Pc |
| `set-power` | Reject | Yes | Yes |
| `set-brightness` | Reject | Yes | Yes |
| `set-color` | Reject | Yes | Reject |
| `set-color-temperature` | Reject | Yes | Reject |
| `rgb-frame` physical output | No | No | Yes |

\* `lamp-status` may return an error when diagnostic, polling is prohibited while Razer is believed armed, or the device is unavailable. Unsuccessful physical sends are not acknowledgments.

### Power intent and ARM/DISARM

- `resume` **only** selects `Pc` and clears stale frames; it no longer enables Razer implicitly.
- `arm` requires `Pc`, configured `SupportsRealtime=true`, and no pending failed DISARM. It sends one physical ARM packet and enters `PcStreaming` after successful transport submission. It discards stale frames.
- `set-power(false)` works in `Pc` and `PcStreaming`, suppresses any future automatic power-on, and discards old frames. Continued *incoming* frames can keep the streaming lease alive, but no frames are sent while explicitly off.
- `set-power(true)` lifts the suppression. A new frame causes a new physical ARM submission if the lamp was power-cycled; Host bookkeeping cannot prove physical ARM succeeded.
- `set-brightness(0)` maps to explicit power-off, because the physical `brightness` command supports only 1..100. Values 1..100 call the lamp brightness API normally.
- `disarm` stops emitting frames before attempting the physical DISARM. An unsuccessful send leaves `razerArmed=true` even when the logical mode has returned to `Pc`; direct color/temperature are rejected until a successful DISARM retry.
- `release` immediately enters `Manual`, clears frames and makes a best-effort physical DISARM without powering the lamp off. A failed DISARM remains retryable by `release`/`disarm` and blocks a new `resume` until resolved.
- An idle streaming session is released after `FrameStaleAfterMs`, including a session with no frames after ARM. While explicitly off, incoming frames still refresh the lease but are not transmitted.

## Manual configuration

The default `src/GoveeBridge.Host/appsettings.json` runs `Diagnostic=true` with no physical lamps (one implicit `diagnostic` session). For real hardware, set `Diagnostic=false` and configure **all lamps**. A two-device example is provided in `src/GoveeBridge.Host/appsettings.local.example.json`:

```json
{
  "Bridge": {
    "Diagnostic": false,
    "LogicalLedCount": 8,
    "Lamps": [
      {
        "Id": "floor", "DeviceId": null, "Ip": "192.168.1.50",
        "Capabilities": {
          "SupportsPower": true, "SupportsBrightness": true,
          "SupportsRgb": true, "SupportsColorTemperature": true,
          "SupportsRealtime": true, "RealtimeZoneCount": 8
        }
      },
      {
        "Id": "desk", "DeviceId": null, "Ip": "192.168.1.51",
        "Capabilities": { "SupportsRealtime": false }
      }
    ]
  }
}
```

**Replace example IPs and capability claims with your actual device information.** `Id` is a stable operator alias for IPC; `DeviceId` is optional metadata for future discovery; the current `Ip` is not an identity and can change. Duplicate IDs and duplicate physical IPs are rejected. Unknown model support defaults `SupportsRealtime=false`, requiring explicit opt-in. `RealtimeZoneCount` should match actual logical Razer positions. `LogicalLedCount` is the maximum *incoming* frame width (up to 256); a Razer physical frame is limited to 255 positions.

**Migrate existing `appsettings.local.json`:** remove old `Bridge.TransportMode`/`Bridge.LampIp`, set `Bridge.Diagnostic=false`, and supply `Bridge.Lamps` explicitly. If the local file is not migrated, the new base configuration keeps Diagnostic enabled; no physical output will occur. If `appsettings.local.json` is untracked/local, do not overwrite it without merging these settings.

## PowerShell and SignalRGB

The included `GoveeHost.psm1` and `.psd1` add `-LampId floor` to status/control/frame functions, and export `Arm-GoveeHostStreaming` / `Disarm-GoveeHostStreaming`. Use the newly compiled `GoveeBridge.Core.dll` with PowerShell 7.6+.

```powershell
Resume-GoveeHostControl -LampId floor
Arm-GoveeHostStreaming -LampId floor
Set-GoveeHostBrightness -BrightnessPercent 60 -LampId floor
Send-GoveeHostSolidFrame -Color Red -LogicalLedCount 8 -LampId floor
Set-GoveeHostPower -State Off -LampId floor
Set-GoveeHostPower -State On -LampId floor
Disarm-GoveeHostStreaming -LampId floor
Set-GoveeHostColorTemperature -Kelvin 3500 -LampId floor
Release-GoveeHostControl -LampId floor
```

The add-on's root `GoveeBridge.js` now has a `LAMP_ID` constant. Leave it null to use the configured default, or set it to a stable lamp Id for targeted frames. The add-on intentionally never sends `resume` or `arm`; manually enable streaming (or use the PowerShell animation helper with `-Resume`, which performs both operations explicitly). Its current single virtual-device geometry is not yet an automatic multi-lamp SignalRGB layout.

## Changes to apply

**Replace existing files:**

- `src/GoveeBridge.Core/BridgeSettings.cs`
- `src/GoveeBridge.Core/GoveeHelpers.cs`
- `src/GoveeBridge.Core/Ipc/BridgeIpcConstants.cs`
- `src/GoveeBridge.Core/Ipc/BridgeIpcDtos.cs`
- `src/GoveeBridge.Core/Ipc/BridgeIpcRequests.cs`
- `src/GoveeBridge.Core/Ipc/BridgeIpcResponses.cs`
- `src/GoveeBridge.Host/BridgeEvents.cs`
- `src/GoveeBridge.Host/BridgeRuntime.cs`
- `src/GoveeBridge.Host/BridgeRuntime.Commands.cs`
- `src/GoveeBridge.Host/BridgeRuntime.Monitoring.cs`
- `src/GoveeBridge.Host/BridgeRuntime.Output.cs`
- `src/GoveeBridge.Host/BridgeRuntime.State.cs`
- `src/GoveeBridge.Host/BridgeWorker.cs`
- `src/GoveeBridge.Host/appsettings.json`
- `src/GoveeBridge.Protocol/GoveeLanClient.cs`
- `src/GoveeBridge.PowerShell.Host/GoveeHost.psm1`
- `src/GoveeBridge.PowerShell.Host/GoveeHost.psd1`
- `tests/GoveeBridge.Host.Tests/BridgeCommandBoundaryTests.cs`
- `GoveeBridge.js`, `GoveeBridge.qml`

**Add these files:**

- `src/GoveeBridge.Core/ControlMode.cs`
- `src/GoveeBridge.Core/LampCapabilities.cs`
- `src/GoveeBridge.Core/BridgeLampSettings.cs`
- `src/GoveeBridge.Protocol/GoveeLanNetwork.cs`
- `src/GoveeBridge.Host/AssemblyInfo.cs` (exposes internal test constructor to Host.Tests)
- `src/GoveeBridge.Host/appsettings.local.example.json`
- `tests/GoveeBridge.Host.Tests/BridgeControlModeTests.cs`

**Delete exactly this existing file:**

- `src/GoveeBridge.Core/TransportModes.cs`

No project file updates are needed for the standard SDK-style source includes. Keep your real `appsettings.local.json` private and migrate its values manually. The original commented-out `tests/GoveeBridge.Host.Tests/BridgeRuntimeTests.cs` is not compiled and need not be deleted, though it references the removed TransportMode in comments.

## Known limits and follow-up work

1. The **single Worker management-command FIFO** still processes management actions sequentially across lamps. Per-device gates separate physical state and allow independent frame/poll I/O, but a slow `lamp-status` in the Worker queue can delay another lamp's management command. Per-lamp command queues are a future scaling option, not implemented here.
2. The periodic monitor/pump use `Task.WhenAll` over lamps; timing depends on the slowest outstanding attempt. No device discovery, dynamic IP updates, hot-plug, auto-retry of a new address, or LAN capability negotiation yet.
3. Status messages have no request identifiers. The shared LAN receiver matches responses by source IP. A **late** status response could be mistaken for a newer query to that IP. Never interpret receipt as proof of control ownership.
4. Capability flags are manually configured, not verified automatically. No Govee desktop compatibility checks or probing are included.
5. The previous `GoveeStatic` **automatic per-frame average-color fallback** is intentionally gone. In PC Direct, send `set-color` explicitly; streaming means physical Razer frames only. A future explicit frame rendering/mapping strategy could add static fallback without reintroducing TransportMode.
6. Host response JSON `mode` now means `ControlMode` (`Manual`, `Pc`, `PcStreaming`) rather than the removed configuration TransportMode. Added `lampId` and nullable `requestedPowerOn`; retained legacy `ownership` for compatibility.
7. User-supplied IPC is loopback-only but not authenticated. Hardware arbitration against a remote remains heuristic: `devStatus` does not identify the actor that changed the lamp, and polling may be suppressed while Razer is armed.

## Validation on Windows

```powershell
dotnet build .\GoveeBridge.sln
dotnet test .\tests\GoveeBridge.IpcContractTests\GoveeBridge.IpcContractTests.csproj
dotnet test .\tests\GoveeBridge.Host.Tests\GoveeBridge.Host.Tests.csproj
```

Then run Diagnostic mode and inspect status before enabling physical LAN access. With real lamps, test one lamp first, then two independently addressed lamps: resume/arm, brightness and power while streaming, disarm and color-temperature, release; confirm the remote can still override without the Host fighting it. Make sure no other process owns Govee's local UDP 4002 response port.

**No GitHub writes were made.**
