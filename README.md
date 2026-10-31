# GoveeBridge — .NET 10 + SignalRGB network add-on

A developer-oriented, **experimental** integration for a Govee H6072. SignalRGB renders eight **logical** LEDs, a JavaScript network add-on forwards RGB frames over localhost UDP, and a .NET 10 worker receives them. The worker can operate in safe **Diagnostic** mode or opt-in **GoveeStatic** mode.

> **Current scope and honesty:** The physical lamp has only been verified with Govee's standard LAN `devStatus` endpoint in prior manual testing. The bridge implements official status/power/single-color commands, **not** individually addressable streaming. Its eight SignalRGB logical LEDs are averaged into a **single lamp color** until a multizone protocol is verified. The synthetic SignalRGB controller has not yet been integration-tested. No automatic detection of *all* remote scene/color commands is promised.

## What's in this repo

| Location | Role |
|---|---|
| `GoveeBridge.js` | SignalRGB network add-on; registers virtual H6072, reads canvas colors, forwards UDP frames. Must be in Git repository **root** for add-on URL installation. |
| `GoveeBridge.qml` | Optional Qt Quick **add-on configuration panel**, not LED geometry or the Lightscript canvas. |
| `GoveeBridge.sln` | Open this in Visual Studio with .NET 10 SDK. Includes all .NET projects and root add-on files as Solution Items. |
| `src/GoveeBridge.Core` | RGB types, IPC frame validation, settings. |
| `src/GoveeBridge.Protocol` | Govee LAN command serialization and exclusive UDP 4002 transport. |
| `src/GoveeBridge.Host` | .NET 10 Worker Service, configurable diagnostic/real transport, manual override state, local UDP receiver. |
| `src/GoveeBridge.Cli` | Command-line `status`, `lamp-status`, `release`, `resume` over loopback UDP. |
| `tests/GoveeBridge.SmokeTests` | Dependency-free offline checks (run as Console App; no xUnit packages). |
| `docs/` | Architecture, setup and security/remote notes. |

## First run (safe; does not contact physical lamp)

1. In Visual Studio, open `GoveeBridge.sln`; install .NET **10 SDK** if required. Restore NuGet packages for Worker Service hosting.
2. Set **GoveeBridge.Host** as Startup Project and start debugging. Initial `TransportMode` is `Diagnostic`. It binds only `127.0.0.1:41005`; no Govee command is transmitted, no port 4002 is used.
3. From another shell run `dotnet run --project src/GoveeBridge.Cli -- status`. You should get JSON showing `mode: Diagnostic`.
4. Commit and push this repository to GitHub. SignalRGB's **Devices → Add-ons → Add** accepts a public Git URL. The JS/QML files are already at the root, alongside the solution. Installation of this minimal synthetic controller is **not yet guaranteed**, so watch SignalRGB logs.
5. Keep the **stock SignalRGB Govee add-on disabled**. In the virtual device, add an eight-LED generic strip to channel `Lamp RGB`, position it in the Govee region of your Lightscript layout, and watch Host log incoming representative colors.
6. Run offline checks: `dotnet run --project tests/GoveeBridge.SmokeTests`.

> No separate public addon repository or duplicated file-publishing flow is necessary. If SignalRGB's installer rejects a repository with additional files, you can later publish just the two root addon files to a dedicated repository; we have not observed that issue yet.

## Private lamp IP configuration (NOT checked into Git)

Copy `src/GoveeBridge.Host/appsettings.local.example.json` to **`src/GoveeBridge.Host/appsettings.local.json`** and replace the example documentation IP with your lamp's address. The latter file is `.gitignore`d. It overrides the committed `appsettings.json`.

```powershell
Copy-Item src/GoveeBridge.Host/appsettings.local.example.json src/GoveeBridge.Host/appsettings.local.json
# Edit LampIp in that private file. Use the real device's assigned address.
```

For published service deployment, copy `appsettings.local.json` alongside the published executable. Host loads config relative to `AppContext.BaseDirectory`, not the process working directory. The committed settings always default to `Diagnostic`, so simply cloning the repository cannot commandeer a lamp.

**Once intentionally ready to send real commands:**

1. Turn off *both* iCUE's Govee integration and SignalRGB's stock Govee add-on. The bridge must exclusively own the PC's local UDP **4002** response port.
2. Enable Govee LAN Control; keep lamp/PC on a reachable subnet. `GoveeStatic` requires a private IP address in `appsettings.local.json`.
3. Restart Host. It **does not automatically take control at startup**. The remote remains in charge.
4. Run `dotnet run --project src/GoveeBridge.Cli -- lamp-status` to read the lamp's status safely.
5. To explicitly give PC control to the bridge: `dotnet run --project src/GoveeBridge.Cli -- resume`. It will begin applying colors upon the **next SignalRGB frame**. This can power the lamp on. `release` stops sending colors; it doesn't power the lamp off.
6. To give remote priority: `dotnet run --project src/GoveeBridge.Cli -- release`.

`GoveeStatic` intentionally uses at most **one color write per second**, coalesces all virtual LED colors into one representative RGB value, and avoids repeated identical writes. This is for safe functional testing, not smooth animation. Do not switch to high-frame-rate streaming without proving the H6072's supported realtime protocol.

## Architecture

```text
Idle Machinery / other Lightscript
    ↓ SignalRGB samples the virtual LED positions on its canvas
GoveeBridge.js + GoveeBridge.qml (management UI only)
    ↓ localhost UDP 127.0.0.1:41005 (small JSON RGB frames)
GoveeBridge.Host  [.NET 10 Worker, explicit Program.Main()]
    ├─ BridgeRuntime: PC vs Manual ownership; throttling, diagnostics
    ├─ Core: validates logical RGB frames and averages colors
    └─ Protocol: binds UDP 4002, sends Govee JSON to lamp UDP 4003
             ↓
          H6072 (only in GoveeStatic mode)
```

**QML is NOT the lighting model.** It describes an optional SignalRGB Add-ons UI panel. `device.addChannel()` and SignalRGB's generic component/layout editor define how the LEDs overlay the canvas. The JS add-on must remain separate from the Lightscript. **Loopback is not a fake physical lamp**: it's a private IPC boundary so the renderer can talk to the real-device service without embedding the lamp's IP or protocol in SignalRGB. It is useful in production as well as testing.

## Manual override expectations

- Starts in **Manual** ownership after every service launch. `resume` explicitly takes control; `release` returns it to remote, without changing lamp power.
- The status polling feature attempts to latch **Manual** if the remote turns the lamp *off* while PC mode is active. **It cannot guarantee priority** if a later PC color command overwrites the change before polling sees it, and other native mode changes may be invisible in `devStatus`.
- For absolutely reliable remote operation, use the explicit `release` command, or disable the virtual Govee device. Better arbitration needs confirmed remote events or a reliable state-change signal.
- On abnormal exit, the lamp may retain its last color or remain in its last firmware mode. The service never turns it off automatically on shutdown.

## Useful commands

```powershell
dotnet build GoveeBridge.sln
dotnet run --project src/GoveeBridge.Host
dotnet run --project src/GoveeBridge.Cli -- status
dotnet run --project src/GoveeBridge.Cli -- lamp-status
dotnet run --project src/GoveeBridge.Cli -- release
dotnet run --project src/GoveeBridge.Cli -- resume
dotnet run --project tests/GoveeBridge.SmokeTests
```

## Visual Studio Worker Service / Windows Service

Host is a Worker Service using `Microsoft.Extensions.Hosting.WindowsServices`, but until registered with Windows it runs as a normal debug executable. Once tested, publish Host (preferably self-contained), copy your private settings into the publishing directory, then register the **exe** with SCM (`sc.exe create`). Start/stop with Services.msc or `sc.exe`; Windows service registration is an installation step, not a separate project type. A Windows service's working directory is often System32; config loading already avoids this. See `docs/architecture.md` for constraints.

## References

- SignalRGB Govee network add-on reference: https://gitlab.com/signalrgb/Govee/-/raw/main/Govee.js
- SignalRGB channel sampling: https://docs.signalrgb.com/developer/plugins/device-functions/
- Govee LAN protocol and port design: https://app-h5.govee.com/user-manual/wlan-guide
- .NET Windows service hosting: https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service
