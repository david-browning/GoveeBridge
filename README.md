# GoveeBridge

**GoveeBridge** is an experimental .NET 10 application that gives one background Host exclusive responsibility for communicating with a configured Govee lamp. Other local applications, including SignalRGB, send lighting data or control requests to the Host instead of opening competing connections to the lamp.

The initial target is the **Govee H6072**. The Host can poll its status and report changes through `ILogger`. The current lighting implementation supports whole-lamp RGB control through Govee's standard LAN API; individually addressable segments are not implemented.

> **Project status:** Physical-lamp status monitoring has been exercised with an H6072. The SignalRGB network add-on and its virtual-device registration still require end-to-end testing. Local IPC is experimental and **not yet a secure, authenticated interface for arbitrary clients**. Do not expose it beyond the local machine.

## Design at a glance

    SignalRGB lighting canvas
      -> GoveeBridge.js (SignalRGB network add-on)
      -> localhost UDP 127.0.0.1:41005
                                |
    Other local programs -------+  (current local command interface)
                                v
                       GoveeBridge.Host
                          |       |
                     control   status polling -> ILogger
                          |       |
                          +--- GoveeLanClient
                                      |
                           Govee LAN UDP 4003
                                      |
                            Configured H6072

* **One Host, one configured lamp.** The Host owns the lamp connection, status cache, and PC-versus-manual control policy. It can run interactively today and is structured as a Worker Service for eventual Windows Service deployment.
* **SignalRGB is an adapter, not the lamp driver.** The JavaScript add-on captures logical LED colors and sends frames to the Host. It does not need the physical lamp's IP address or Govee's wire format.
* **Monitoring is independent of control.** In real-lamp mode, the Host queries the lamp at startup and at a configured interval, even while manual control is active. Successful state changes and connectivity transitions are logged through `ILogger`.
* **The physical remote retains startup priority.** The Host starts in Manual mode. It will not apply incoming lighting frames until PC control is explicitly resumed. Automatic release on observed remote power-off is best effort, not guaranteed remote-event detection.
* **Future integrations require a secure control interface.** The current UDP listener accepts local frames and commands, but has no client authentication or Windows-user authorization. A protected management channel and status subscriptions remain planned work.

## Repository layout

| Path                        | Responsibility                                                                                            |
| --------------------------- | --------------------------------------------------------------------------------------------------------- |
| `src/GoveeBridge.Host/`     | Worker, local receiver, frame pump, status monitor, logger, control arbitration, and event-publisher stub |
| `src/GoveeBridge.Protocol/` | Govee LAN UDP client, command identifiers, JSON payload DTOs, serialization, and status parsing           |
| `src/GoveeBridge.Core/`     | Shared RGB/frame models, local frame validation, and Host configuration                                   |
| `GoveeBridge.js`            | Experimental SignalRGB network add-on; registers a logical device and sends colors to the local Host      |
| `GoveeBridge.qml`           | Optional SignalRGB add-on configuration view; **not** LED geometry or the lighting effect                 |
| `tests/`                    | Initial smoke checks; a more complete automated suite is planned                                          |
| `docs/architecture.md`      | Protocol boundaries, concurrency, ownership, security requirements, and roadmap                           |

## Requirements

* Windows with the **.NET 10 SDK** or Visual Studio configured for .NET 10 development.
* For physical-lamp monitoring: an H6072 on a reachable LAN with **LAN Control enabled** in Govee's application.
* For lighting integration: SignalRGB with network add-on support. The synthetic device implementation in this repository is not yet verified against the installed SignalRGB version.
* Disable competing Govee LAN integrations that attempt to bind the same UDP response port (notably the stock SignalRGB Govee integration and any enabled iCUE Govee integration).

## Build and run

From the repository root:
    dotnet build GoveeBridge.sln
    dotnet run --project src/GoveeBridge.Host

Alternatively, open `GoveeBridge.sln` in Visual Studio and launch **GoveeBridge.Host** as the startup project.

### Diagnostic mode: no physical lamp access

The committed `src/GoveeBridge.Host/appsettings.json` sets `Bridge:TransportMode` to `Diagnostic`. This is the safe default:

* The Host binds its local IPC listener to `127.0.0.1:41005`.
* It does **not** open the Govee status-response port or query/control a physical lamp.
* When valid RGB frames arrive, it logs a representative averaged color (rate-limited diagnostic output).

Diagnostic mode still needs a frame producer, such as the SignalRGB add-on, to show frame messages. Merely starting the Host produces startup logs, not simulated lamp power changes.

### Real-lamp mode: monitor and optionally control the lamp

Copy the example configuration to a machine-local override:
    Copy-Item src/GoveeBridge.Host/appsettings.local.example.json src/GoveeBridge.Host/appsettings.local.json

Edit `appsettings.local.json` and set `LampIp` to the actual lamp address. The JSON below uses a reserved documentation address as a placeholder; the checked-in example file may contain a different value:
    {
      "Bridge": {
        "TransportMode": "GoveeStatic",
        "LampIp": "192.0.2.5",
        "RemotePollIntervalMs": 2000
      },
      "Host": {
        "EnableLampMonitoring": true,
        "LogLampStatusChanges": true
      }
    }

The local override is excluded by `.gitignore`. The Host loads it **after** the default settings, with the override resolved relative to `AppContext.BaseDirectory` (the executable/output directory). The Host project is configured to copy an existing `appsettings.local.json` to build and publish output. **Restart the Host after changing configuration**; reload-on-change is disabled.

Start the Host. It should query the lamp immediately and continue polling at the selected interval. Turning the physical lamp on or off should produce `ILogger` messages when a changed status is observed. For example:
    info: GoveeBridge.Host.BridgeRuntime[0]
          Lamp online
    info: GoveeBridge.Host.BridgeRuntime[0]
          Lamp state changed: On=True, Brightness=75%, RGB=#FF8800, Kelvin=0 (...)

This is illustrative output, not a recorded hardware trace. The Host logs **changes**, rather than printing every successful poll. Logging verbosity is controlled by the normal `Logging:LogLevel` settings; the console provider is active when running interactively. A future Windows Service deployment can use other `ILogger` providers.

Real mode does **not** automatically take PC control. Incoming frames are observed, but are not applied until the Host receives an explicit `resume` request. `release` stops further PC-controlled color writes without powering off the lamp. The current output path averages logical LEDs into a single RGB color and limits writes to the configured minimum interval (default **1000 ms**).

## SignalRGB integration: next integration milestone

The repository-root `GoveeBridge.js` is a **network add-on**, not a SignalRGB Lightscript. It is intended to register a virtual H6072 with **eight logical LED positions**, sample SignalRGB's lighting canvas, and transmit RGB frames to the Host over localhost UDP. The current intended installation route is **SignalRGB → Devices → Add-ons → Add** using this repository's public Git URL; the exact menu and synthetic registration behavior need verification on the installed version.

The logical LEDs do **not** imply that the real H6072 exposes eight addressable LAN segments. In the present `GoveeStatic` mode they are averaged into one color. The optional `GoveeBridge.qml` provides an add-on view only; LED placement is configured through SignalRGB's channel/component layout.

**The add-on's synthetic controller registration has not yet been tested end to end.** The next milestone is to load the add-on, verify that the logical device appears in SignalRGB, assign a generic eight-LED component to the `Lamp RGB` channel, position it in SignalRGB Layout, and confirm that frame changes reach the Host in Diagnostic mode before attempting full-lamp control.

## Safety and known limitations

* **Localhost is not authentication.** Any local process capable of sending UDP to port `41005` can currently submit frames or supported commands. Do not treat the current IPC as a trusted public API. Secure, access-controlled management IPC is an explicit prerequisite for broader use or a persistent privileged service.
* **No real-time multizone control yet.** Only standard whole-device LAN RGB control is implemented. High-rate or per-segment streaming requires separate protocol verification.
* **Remote takeover is best effort.** Govee's standard status API is polled; it does not identify which actor changed a setting or provide dependable push notifications. An observed external power-off can release PC ownership, but short-lived changes and native scenes may not be detected. Color-mismatch takeover detection is experimental and disabled by default.
* **One device per Host instance.** Multi-device discovery and coordination are outside the current design.
* **No status event transport yet.** `IBridgeEventPublisher` and event types exist, but the registered implementation is a no-op. State changes are currently observable through `ILogger` and the Host's cached status responses.
* **Windows Service hosting is not deployment.** The Host supports Windows Service integration, but must still be published, installed, and configured with appropriate permissions before running as a service.

See [Architecture](docs/architecture.md) for current IPC formats, scheduling and ownership behavior, the security-hardening plan, and planned testing.
