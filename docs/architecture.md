# GoveeBridge architecture

This document describes the **current implementation** of GoveeBridge and the constraints agreed upon for its evolution. Planned behavior is called out explicitly rather than described as already working.

## 1. Scope and principles

GoveeBridge has one authoritative **Host** process that owns communication with **one configured Govee lamp**. Other programs submit desired lighting data or control requests to the Host. The Host decides which operations are allowed, serializes physical-device I/O, polls observed state, and exposes diagnostics.

The initial target is the Govee **H6072**. The Host is a .NET 10 Worker Service and currently runs as an ordinary interactive process; it is prepared for future Windows Service hosting. **No kernel-mode or user-mode driver is currently needed or implemented.** A service already provides the required user-mode ownership boundary.

Architectural invariants:

1. **Single device owner:** Other applications do not bind Govee's UDP reply port or address the lamp directly.
2. **Protocol isolation:** Govee JSON commands and the physical lamp's IP are confined to Host configuration and the Protocol assembly, not SignalRGB or another client.
3. **Monitoring does not imply control:** Reading physical state does not give the Host permission to overwrite it.
4. **Manual ownership by default:** Restarting the Host must not reclaim the lamp.
5. **Nonblocking frame ingress:** A slow physical device must not stall the local lighting-frame receiver.
6. **Explicit security boundary:** The present localhost IPC is for initial development; authentication/authorization must be addressed before expanding client access or deploying a privileged persistent service.

## 2. Components and data flow

```text
SignalRGB effect and layout
       |
       v
GoveeBridge.js (network add-on, synthetic logical LEDs)
       |  JSON RGB frames, localhost UDP 41005
       v
+--------------------------- GoveeBridge.Host ---------------------------+
|                                                                       |
|  BridgeWorker                                                         |
|    - local datagram receiver                                           |
|    - bounded administrative-command queue                             |
|    - periodic frame pump                                               |
|    - startup + periodic lamp monitor                                   |
|           |                         |                                  |
|           v                         v                                  |
|  BridgeRuntime                 ILogger                                  |
|    - desired frame / observed status                                  |
|    - Manual vs PC ownership                                            |
|    - output throttling and basic remote-override detection            |
|    - BridgeEvent publication -> NullBridgeEventPublisher (stub)       |
|           |                                                           |
+-----------|-----------------------------------------------------------+
            v
  GoveeBridge.Protocol
    - typed JSON DTOs, packet building, parsing
    - GoveeLanClient: exclusive local UDP 4002
            |
            | Govee LAN requests to configured lamp UDP 4003
            v
        H6072 lamp
```

The Host also accepts the currently supported administrative command messages through its localhost UDP receiver. **A secure control channel for other applications and an event-subscription transport are planned, not implemented.** No extra production executable is required for either capability.

### Assemblies and root integration files

- **Core (`src/GoveeBridge.Core`)** defines `RgbColor`, `RgbFrame`, `BridgeSettings`, `TransportMode`, and the local frame parser/averaging logic.
- **Protocol (`src/GoveeBridge.Protocol`)** owns Govee LAN command identifiers, payload-property constants, strongly typed serialization DTOs, status parsing, and the UDP client.
- **Host (`src/GoveeBridge.Host`)** owns connection lifetime, concurrency, arbitration, scheduling, logging, and the future notification boundary.
- **`GoveeBridge.js`** is the SignalRGB network adapter. It has no physical lamp address and should not implement Govee device control itself.
- **`GoveeBridge.qml`** is an optional Qt Quick add-on view, not a lighting script or LED layout definition.

## 3. Current transports and messages

| Direction | Protocol | Endpoint | Current state |
| --- | --- | --- | --- |
| SignalRGB / local programs -> Host | JSON datagrams over IPv4 UDP | `127.0.0.1:41005` | Implemented; no authentication |
| Host -> requesting local program | JSON reply over IPv4 UDP | Original sender's loopback endpoint | Implemented for recognized command requests |
| Host -> lamp | Govee LAN JSON over IPv4 UDP | `<LampIp>:4003` | Implemented in `GoveeStatic` mode |
| Lamp -> Host | Govee status UDP replies | Host binds `0.0.0.0:4002` | Implemented in `GoveeStatic` mode |
| Host -> status subscribers | Not selected | Not selected | **Stub only** |
| Future secure management channel | Named pipe with Windows ACLs is a candidate | Not selected | **Not implemented** |

The inbound local listener binds specifically to `IPAddress.Loopback`, not to the LAN interface. The physical response socket binds to `IPAddress.Any` on port `4002`, as required by the current transport design. The Govee client filters status packets by the configured lamp's **source IP**, but this is not cryptographic authentication, and the current implementation does not validate the response source port.

### Local lighting frame (implemented)

```json
{"type":"rgb-frame","count":2,"colors":[255,0,0,0,0,255]}
```

`colors` contains interleaved `[R,G,B]` bytes, with `count` logical LED entries. `BridgeWireProtocol.TryParseFrame` rejects invalid types, lengths, LED counts, and out-of-range color channels. The default configured logical limit is eight LEDs. The JS add-on currently attempts to send frames at up to **10 per second**; actual end-to-end SignalRGB registration and forwarding remain unverified.

### Local administrative command (implemented, experimental IPC)

```json
{"type":"command","action":"release"}
```

Recognized actions are `status`, `lamp-status`, `resume`, and `release`. The Host replies to the requesting loopback UDP endpoint with JSON. This command path is **not authenticated, access-controlled, durable, or guaranteed to deliver requests or replies**. It is suitable for local development, not a general-purpose service security contract. The meaning of `resume` is to permit PC output; `release` relinquishes it without changing lamp power.

The protocol should eventually acquire explicit versions, request identifiers, typed commands/responses, error codes, size limits, and authorization semantics. The precise secure transport is a future architectural decision informed by the client environments; do not assume the SignalRGB JavaScript runtime supports Windows named pipes.

## 4. Runtime modes and configuration

`src/GoveeBridge.Host/appsettings.json` is committed with safe defaults and `TransportMode: Diagnostic`. A machine-local `appsettings.local.json` is optional and ignored by Git. The Host loads the default file and then the local override from `AppContext.BaseDirectory`; changes take effect after restart.

### `Diagnostic`

- Listens for local RGB frames and commands, and reports representative incoming colors through `ILogger`.
- Does **not** instantiate `GoveeLanClient`, bind port `4002`, poll the real lamp, or send Govee commands.
- Is an input-path diagnostic mode, not a simulated device with spontaneous state changes.

### `GoveeStatic`

- Requires a configured valid lamp IP and enables real Govee LAN I/O.
- Polls `devStatus` immediately on startup and then every `RemotePollIntervalMs` (default **2000 ms**) while monitoring is enabled.
- Publishes connection and observed-status changes to `ILogger`; ordinary unchanged polls do not each produce an informational status line.
- Starts in **Manual** ownership and will not apply frames until explicitly resumed.
- Averages all logical LED colors to one whole-lamp RGB color; output is limited by `MinimumSendIntervalMs` (default **1000 ms**) and duplicate colors are suppressed.

Key `Host` settings include `EnableLampMonitoring`, `LogLampStatusChanges`, `FramePumpIntervalMs` (default **50 ms**), and `FrameStaleAfterMs` (default **5000 ms**). `AutoReleaseOnColorMismatch` is disabled by default because the device status may be stale or reflect native scenes differently from our requested RGB. `Logging:LogLevel` controls verbosity using the standard .NET logging pipeline.

## 5. Ownership and remote behavior

The Host maintains several distinct concepts:

- **Desired color:** Most recently received logical frame, reduced to one RGB color for the current physical mode.
- **Observed status:** Latest successful `devStatus` response (power, brightness, RGB, and color temperature).
- **Connectivity:** Whether a recent status request received a parsable response.
- **Ownership:** `Manual` or `PC`, a Host-side permission to transmit lighting changes, not a firmware-enforced lock.

Current lifecycle:

1. Startup -> `Manual`. The monitor may query the lamp but the frame pump does not take control.
2. Incoming frames replace the pending desired color, including while in Manual ownership. An old frame is ignored once it passes the configured stale interval.
3. An explicit `resume` request permits PC output. On a subsequent fresh frame, the Host may power on the lamp and send its averaged RGB color.
4. An explicit `release` request disables PC writes and leaves the lamp in its current state.
5. While the Host believes it controls the lamp, a polled **power-off** (outside a short power-on grace interval) can trigger release if `AutoReleaseWhenRemoteTurnsOff` is enabled.

**Limitations:** The documented LAN status interface is polled, not a reliable event stream. It does not identify the actor who changed a value or necessarily reveal native animated scenes. A rapid remote action can be missed between polls or overwritten by PC frames. Optional repeated color-mismatch detection exists but is disabled by default; a mismatch is not proof of a remote press. Remote-priority behavior therefore remains **best effort** and requires hardware testing.

## 6. Concurrency and backpressure

`BridgeWorker` runs independent loops for datagram receipt, command processing, frame pumping, and status polling:

- Valid RGB frames are parsed and reduced to **one latest desired color**, replacing older pending colors without waiting for lamp I/O.
- Other received datagrams enter a bounded channel (capacity **64**); if full, new datagrams receive a queue-full response.
- `BridgeRuntime` serializes physical operations with `_lampGate`; `GoveeLanClient` also serializes access to its UDP socket. The frame pump uses a nonblocking attempt to acquire the runtime gate so a status query does not back up animation input.
- The frame pump runs on a configurable timer but only transmits when permitted, when the frame is fresh, and when the send interval allows it.

This prevents an old animation backlog but does not yet provide **per-client arbitration, command fairness, guaranteed response delivery, or a priority scheduler**. Those will matter if multiple local applications begin issuing control requests concurrently.

## 7. Logging and future status notifications

The physical monitor reports connectivity transitions and changed state through `ILogger<BridgeRuntime>`. The default interactive console is therefore a diagnostic viewer; logging is not currently a programmatic subscription API.

`BridgeEvents.cs` defines `BridgeEventKind`, `BridgeEvent`, and `IBridgeEventPublisher` with an implementation registered as `NullBridgeEventPublisher`. This is a deliberate **extension point only**: publishing currently has no external effect.

A future publisher should provide an initial state snapshot plus subsequent changes, be nonblocking to device I/O, use bounded **per-subscriber** queues, and handle slow/disconnected subscribers without impacting monitoring or lamp control. Choose the actual notification transport when there is a client to implement and test against.

## 8. Security requirements before broader integration

**Current trust model:** A localhost listener is not an authenticated IPC boundary. Another local process can send frames or `resume` / `release` commands. Govee's LAN transport itself is unauthenticated. These are acceptable limitations for isolated experimentation, **not** for a general privileged service.

Before exposing the Host to additional clients or installing it as a persistent elevated Windows Service:

1. **Separate data and control planes.** Keep low-latency frame ingress distinct from reliable operations that alter ownership or query sensitive state. A **named pipe with Windows ACLs** is a strong candidate for management on Windows; otherwise use a transport with equivalent authentication and authorization. Confirm SignalRGB runtime capabilities before changing its data path.
2. **Authorize callers.** Define which Windows users or processes may set colors, `resume`, `release`, read status, and subscribe to events. Avoid treating any packet from `127.0.0.1` as trusted. Choose a service identity with only the rights it needs.
3. **Bound resource usage.** Enforce datagram/message length, command rate, subscriber count, queue size, parse depth, and request deadlines. Log abusive input without leaking private information.
4. **Make management requests reliable.** Add framing, protocol versioning, request/response correlation, acknowledgments or explicit errors, timeouts, and safe retry behavior.
5. **Specify competing-client policy.** Decide whether SignalRGB has an exclusive lease, manual control wins unconditionally, or multiple producers can be arbitrated by priority. Never assume that serialized writes alone imply synchronized ownership.
6. **Harden device communications.** Validate reply metadata as far as the Govee protocol permits, account for stale or spoofed LAN packets, and avoid binding unrelated application endpoints to public interfaces. The current fixed device port may require host firewall considerations.
7. **Protect configuration and deployment.** Keep real addresses, credentials, and any future tokens out of tracked files. Preserve Diagnostic as the safe committed default. Do not install as `LocalSystem` merely for convenience.

The Host is already a user-mode process. Converting it into a Windows Service changes its lifecycle and security identity; it does **not** require designing a Windows driver.

## 9. Next milestones

1. **Stabilize the existing Host** through code review and a proper automated test suite: configuration precedence, DTO serialization, status parsing, disconnected lamps, frame staleness, command races, remote-release edge cases, and invalid datagrams.
2. **Prove the SignalRGB integration.** Load `GoveeBridge.js`, verify synthetic controller registration and LED ordering, and confirm that changes reach the Host in Diagnostic mode. The add-on is not yet integration-verified.
3. **Verify end-to-end static control.** In `GoveeStatic`, confirm a fresh frame after explicit resume changes the physical lamp, and that release/manual operation does not unexpectedly reclaim control.
4. **Design and implement secure management IPC.** Choose a supported Windows authentication boundary, formalize typed commands and replies, and add authorization before general client integration or persistent privileged deployment.
5. **Implement status subscriptions.** Replace the null event publisher with a bounded, nonblocking broadcaster after selecting and securing its transport.
6. **Explore H6072 multizone support separately.** Confirm any undocumented Razer/DreamView protocol and supported segment layout experimentally before changing the stable whole-lamp implementation or frame rates.

The current objective is **one trustworthy Host and a proven SignalRGB-to-Host path**, not a large collection of applications, transports, or speculative device capabilities.
