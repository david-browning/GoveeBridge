# GoveeBridge

**An experimental, multi-lamp Windows lighting bridge for Govee LAN devices.**

GoveeBridge provides one authoritative .NET Host between local lighting producers (including a SignalRGB network add-on and a PowerShell IPC client) and individually configured Govee lamps. The Host arbitrates **Manual**, **PC Direct**, and **PC Streaming** control, owns the physical Govee LAN connections, and exposes status and diagnostics.

The initial hardware target is the **Govee H6072**. Static color, power, brightness, and white-temperature operations use Govee's LAN commands. Multicolor streaming uses an **experimental, undocumented Razer-compatible protocol**, not a general-purpose Govee lighting API. Working on the H6072 is not a guarantee of compatibility with other models or firmware.

> **Project status:** Development/testing. The Host, multi-lamp routing, IPC commands, control state machine, and fake-client tests are implemented. The SignalRGB add-on and physical behavior on other lamp models are not fully validated. Local UDP management IPC is not authenticated; production-grade authorization and a possible named-pipe control transport remain future work.

## Architecture

```text
    SignalRGB network add-on          GoveeHost PowerShell module
       GoveeBridge.js                  (compiled Core IPC DTOs)
               |                                  |
               +---------------+------------------+
                               |
                    127.0.0.1:41005 UDP
                    (explicit lampId in
                     every frame/command)
                               |
                         BridgeWorker
                  frame mailbox / command queue
                     / pump / status monitor
                               |
                         BridgeRuntime
               stable lamp ID -> independent session
               mode, state, mailbox, lock, retries
                               |
                      IGoveeRealtimeClient
                      / GoveeLanClient
                               |
                    shared GoveeLanNetwork
                       UDP 4002 receive
                       UDP 4003 send
                         /         \
                     Lamp A       Lamp B ...
```

The important boundaries are:

- **`GoveeBridge.Core`**: colors, frames, lamp/settings models, and typed local IPC contracts. `BridgeIpcRequests` produces the exact JSON-compatible requests used by PowerShell.
- **`GoveeBridge.Host`**: one `BridgeWorker` for localhost IPC, command queuing, frame pumping, and monitoring; one `BridgeRuntime` managing independent lamp sessions. Per-lamp gates prevent one lamp's operations from serializing all other lamps.
- **`GoveeBridge.Protocol`**: Govee LAN packet construction/validation, Razer frame encoding, device clients, and the shared physical UDP status receiver. `GoveeLanPackets` validates outbound physical parameters; `TryReadStatus` rejects invalid inbound observations.
- **`GoveeBridge.PowerShell.Host` (`GoveeHost`)**: client of **the Host**, never of the physical lamp. Requires PowerShell 7.6+ and the compiled Core assembly.
- **`GoveeBridge.PowerShell` (`GoveeLan`)**: independent, direct-to-lamp diagnostics. Bypasses the Host and must not run concurrently with a real-device Host.
- **`GoveeBridge.js`**: SignalRGB network add-on that sends frames to the Host; it does not resume/arm control on its own.
- **`tests/GoveeBridge.Host.Tests`**: MSTest contracts, recording fake, loopback Worker tests, and an explicitly opted-in real-hardware smoke test.

The Host can run interactively and registers Windows Service hosting support. This repository does not currently provide a Windows Service installer or a fully secured service-management interface.

### Multi-lamp identity and transport

Every configured lamp has a **stable, unique `Id`** (for example, `floor` or `desk`). **Every IPC request, including status, must specify `lampId`.** A lamp's IP address is for physical LAN communication and is deliberately not its logical identity. Lamp IDs are case-insensitively unique; in real-device mode, two entries cannot share the same IPv4 address because physical status responses are dispatched by source IP.

The Host creates a separate state machine, frame mailbox, command gate, and device client for each lamp. Physical Govee status replies share **one UDP listener on port 4002**; one outstanding query per IP is allowed. Status packets are not request-correlated by firmware request ID, and UDP submission never proves that an operation was displayed.

## Requirements and quick start

- Windows with the **.NET 10 SDK** for building the `net10.0-windows` projects.
- **PowerShell 7.6+ (`pwsh`)** for the Host IPC PowerShell module. The direct-LAN module also works on Windows PowerShell 5.1.
- A local IPv4 network and a Govee lamp with **LAN Control enabled** for real-device operation.
- No physical lamp is required for diagnostic mode, the recording fake, or most automated tests.

From the repository root:

```powershell
# Build the solution and its C# test project.
dotnet build .\GoveeBridge.sln

# Run the Host after configuring at least one lamp (see below).
dotnet run --project .\src\GoveeBridge.Host\GoveeBridge.Host.csproj
```

**A fresh checkout will not start as-is:** the committed `src/GoveeBridge.Host/appsettings.json` intentionally has `Bridge:Diagnostic = true` and `Bridge:Lamps = []`. `BridgeSettings.Validate()` requires at least one explicitly named lamp, **even in diagnostic mode**. Create a local override before starting.

### Configuration

Copy `src/GoveeBridge.Host/appsettings.local.example.json` to `src/GoveeBridge.Host/appsettings.local.json` and edit it. The local file is ignored by Git and copied to the build output; the Host loads `appsettings.json`, then `appsettings.local.json`, from `AppContext.BaseDirectory` at startup. Changes require a restart.

To start safely with **one diagnostic lamp** and no physical Govee LAN socket, use:

```json
{
  "Bridge": {
    "Diagnostic": true,
    "LogicalLedCount": 8,
    "Lamps": [
      { "Id": "floor", "Capabilities": { "SupportsRealtime": true, "RealtimeZoneCount": 8 } }
    ]
  }
}
```

For a **real H6072 and a second separately configured lamp** (replace the illustrative IP addresses and features):

```json
{
  "Bridge": {
    "Diagnostic": false,
    "LogicalLedCount": 8,
    "Lamps": [
      {
        "Id": "floor",
        "Ip": "192.168.1.101",
        "Capabilities": {
          "SupportsPower": true,
          "SupportsBrightness": true,
          "SupportsRgb": true,
          "SupportsColorTemperature": true,
          "SupportsRealtime": true,
          "RealtimeZoneCount": 8
        }
      },
      {
        "Id": "desk",
        "Ip": "192.168.1.102",
        "Capabilities": {
          "SupportsRealtime": false,
          "RealtimeZoneCount": null
        }
      }
    ]
  }
}
```

`SupportsRealtime` defaults to **false**, so the experimental protocol is explicitly opted into for each device. Other `Supports*` flags currently default to true; configure them to match the device rather than assuming support.

**Counts are not physical LEDs.** A frame contains **1–255 RGB color positions** because the experimental Razer frame encodes the color count in one byte. A lamp may have more than 255 **physical LEDs**. The current configuration still distinguishes:

- `Bridge.LogicalLedCount` — the **configured maximum incoming frame length** passed to the Host's IPC frame parser (default **8**). Despite its name, this is not the lamp's physical LED count. The absolute application/protocol maximum remains **255**. The name and necessity of this setting are under review.
- `Capabilities.RealtimeZoneCount` — an optional **per-lamp frame-size constraint**, currently 1–255 when present. The Runtime allows smaller nonempty frames and rejects frames whose color count exceeds this value. `null` permits any otherwise valid frame size. Whether firmware can downsample frames with more colors than its actual physical zones remains unverified.
- `BridgeSettings.Validate()` currently also requires configured `RealtimeZoneCount <= LogicalLedCount` when `RealtimeZoneCount` is present. Choose a compatible configuration; a physical LED total is **not** what belongs in either field.

In particular, `LogicalLedCount = 8` is a convenient virtual frame resolution, not a claim that an H6072 contains eight physical LEDs.

Useful defaults include `FrameListenPort = 41005`, `MinimumSendIntervalMs = 1000` (static sends), `RemotePollIntervalMs = 2000`, `LampTimeoutMs = 2500`, `Host:FramePumpIntervalMs = 50`, `Host:FrameStaleAfterMs = 5000`, `Host:RealtimeMinimumFrameIntervalMs = 100`, `Host:RealtimeKeepaliveIntervalMs = 2000`, `Host:RealtimeArmDelayMs = 1200`, and `Host:PollWhileRealtimeArmed = false`. Scheduling and retry options are validated at startup. See `appsettings.json` and `BridgeHostOptions.cs` for the full set.

## Control and output behavior

| Host mode | Meaning | Allowed behavior |
| --- | --- | --- |
| `Manual` | Startup/default; PC does not own the lamp | Cached status and monitoring; incoming frames cannot take control |
| `Pc` (PC Direct) | Explicit PC ownership after `resume` | Power, brightness, solid RGB, white Kelvin; no automatic streaming from frames |
| `PcStreaming` | Explicit `arm` after PC ownership | Real-time RGB frames, power/brightness commands, disarm/release |

`resume` enters **PC Direct**, not streaming. `arm` enters **PC Streaming** after checking the configured real-time capability and submitting the experimental ARM command. The first frame after ARM must be new: frames received before ARM are cleared, and frames alone never claim authority. `disarm` returns toward PC Direct; `release` returns toward Manual, attempting DISARM if needed. Failed DISARM submissions remain visible in the Host's arm bookkeeping so that they can be retried.

A frame is an **entire RGB snapshot**, not a partial zone update. The input mailbox is *latest-frame-wins*, allowing producers to submit faster than the device without building an obsolete queue. The pump throttles repeated output, deduplicates unchanged frames, and periodically resubmits unchanged frames as an experimental keepalive. If no fresh frames arrive within `FrameStaleAfterMs` (default five seconds), streaming releases control.

A crucial hardware finding: **ordinary `turn/on` commands sent during armed Razer streaming can cause the lamp to leave real-time mode**, even if the Host still believes it is armed. The Runtime therefore handles initial power-on **before ARM** and does **not** automatically send another power-on command for each streaming frame merely because cached `devStatus` has become stale. Explicit user power changes are handled separately; a commanded power cycle may require a re-ARM.

`set-brightness 0` is Host policy for **power off**. A physical Govee `brightness` command accepts **1–100**. The physical packet factory currently accepts color temperature **2000–9000 K**. Solid RGB and white temperature commands are only permitted in PC Direct, not while streaming. Packet creation rejects invalid physical parameters with `LampParameterException`; invalid local IPC payload shapes return errors through Host dispatch.

Status monitoring is **best effort**. By default, `devStatus` polling pauses while Razer streaming is armed because firmware queries can interfere with real-time operation. Cached status can consequently become stale. An observed remote power-off can cause the Host to release ownership, but missed polls, firmware mode behavior, and UDP loss prevent guarantees about detecting every remote action. Static RGB output has bounded verification/retry behavior; color-mismatch takeover detection is optional and disabled by default. `RazerArmed` and send counters describe **submitted commands**, not confirmed firmware state.

### IPC and ports

| Port | Direction / owner | Purpose |
| --- | --- | --- |
| `127.0.0.1:41005/UDP` (configurable) | Local producers ↔ `BridgeWorker` | App IPC: commands, status replies, full RGB frames |
| `4002/UDP` | Physical Govee Host receiver | Govee `devStatus` replies, shared by all configured lamps |
| `4003/UDP` | Physical device destination | Official Govee LAN commands and experimental Razer commands |

Host IPC is localhost-only, **not authenticated**. It has a bounded management-command queue (64) and an 8192-byte incoming datagram ceiling. Successful management replies indicate Host processing; `rgb-frame` messages have **no reply** and a successful UDP write does not verify rendering.

Examples of application IPC, both requiring an explicit lamp selector:

```json
{"type":"command","action":"status","lampId":"floor"}
```

```json
{"type":"rgb-frame","lampId":"floor","count":2,"colors":[255,0,0,0,255,255]}
```

Commands implemented by the Host: `status`, `lamp-status`, `resume`, `release`, `arm`, `disarm`, `set-power`, `set-brightness`, `set-color`, and `set-color-temperature`. Prefer Core's `BridgeIpcRequests` over hand-assembling these JSON messages.

## PowerShell module 1: `GoveeHost` (through the Host)

**Use this for normal bridge operation.** It sends commands or frames to `127.0.0.1:41005` and never addresses the physical lamp. Start and configure the Host in a separate terminal first. This module requires PowerShell **7.6+** and a built `GoveeBridge.Core.dll` targeting .NET 10.

```powershell
# In pwsh 7.6+, from the repository root:
dotnet build .\src\GoveeBridge.Core\GoveeBridge.Core.csproj
Import-Module .\src\GoveeBridge.PowerShell.Host\GoveeHost.psd1 -Force
Get-Command -Module GoveeHost

# All commands now require -LampId, even status and JSON previews.
Get-GoveeHostStatus -LampId 'floor'
Get-GoveeHostLampStatus -LampId 'floor'  # fresh physical query; unavailable when polling is suspended

# Safe inspect-only mode: no UDP datagram is sent.
Set-GoveeHostPower -LampId 'floor' -State On -PreviewJson
Send-GoveeHostFrame -LampId 'floor' -Colors @('Red','Blue') -PreviewJson
```

The module locates Core in the sibling project's Debug/Release build output or beside the module. To select a DLL explicitly **before importing**, set `$env:GOVEEBRIDGE_CORE_DLL` to its full path. Restart `pwsh` after rebuilding the assembly: .NET cannot replace an already loaded assembly in the same session.

### Direct commands and a manual frame

```powershell
# PC Direct: the Host will not take ownership without this step.
Resume-GoveeHostControl -LampId 'floor'
Set-GoveeHostPower -LampId 'floor' -State On
Set-GoveeHostBrightness -LampId 'floor' -BrightnessPercent 50
Set-GoveeHostColor -LampId 'floor' -Color '#FF0000'
Set-GoveeHostColorTemperature -LampId 'floor' -Kelvin 4000

# Streaming is a separate explicit state transition.
Arm-GoveeHostStreaming -LampId 'floor'
Send-GoveeHostSolidFrame -LampId 'floor' -Color '#00FFFF' -LogicalLedCount 8 -PassThru
Send-GoveeHostFrame -LampId 'floor' -Colors @('#FF0000','#00FFFF')

# A single frame will become stale. End streaming deliberately.
Disarm-GoveeHostStreaming -LampId 'floor'
Release-GoveeHostControl -LampId 'floor'
```

Use a color count consistent with the configured frame ceiling and the selected lamp's `RealtimeZoneCount`. The `-PassThru` result for frame commands reports **UDP submission only**, not success on the lamp. State-changing commands support `-WhatIf` and `-Verbose`; management requests return typed Host status/error responses.

### Animation and monitoring

```powershell
# Auto-resume and ARM once, alternate every five seconds, refresh every 500 ms,
# then release/disarm in the script's finally block.
Start-GoveeHostAnimation -LampId 'floor' `
  -FirstColor '#00FFFF' -SecondColor '#FF0000' `
  -IntervalSeconds 5 -DurationSeconds 15 -FrameRefreshMs 500 `
  -LogicalLedCount 8 -Resume -ReleaseOnExit

# Run in another pwsh window while the animation is active:
Watch-GoveeHostStatus -LampId 'floor' -Count 30 -IntervalSeconds 2
```

`-Resume` is significant: without it, the animation helper **only submits frames** and does not change Host ownership or ARM Razer mode; those frames may never be displayed. `-ReleaseOnExit` performs explicit release on normal completion or script cleanup. Without that flag, the Host may remain in PC Streaming until the frame-stale timeout releases it. An externally killed PowerShell process cannot guarantee script cleanup; the Host still has its own stale-stream policy.

The other exported commands include `Arm-GoveeHostStreaming`, `Disarm-GoveeHostStreaming`, `Get-GoveeHostLampStatus`, and `ConvertTo-GoveeHostJson`. Source and additional scripts are under `src/GoveeBridge.PowerShell.Host/`. Some comments and that module's older README still call implemented `set-*` commands “planned”; current Runtime dispatch is authoritative.

## PowerShell module 2: `GoveeLan` (direct physical diagnostics)

**Use this only when the Host is stopped and you want to test the Govee LAN protocol independently.** It does not perform Host ownership arbitration, does not use `lampId`, and opens the physical response port **UDP 4002** for status queries. Running a real-device Host and the direct diagnostic module together can cause socket bind failures or conflicting lamp state.

```powershell
# Windows PowerShell 5.1 or PowerShell 7+; from repo root.
Import-Module .\src\GoveeBridge.PowerShell\GoveeLan.psd1 -Force
$ip = '192.168.1.101'  # actual lamp IPv4 address

Get-GoveeStatus -LampIp $ip
Get-GoveeBrightness -LampIp $ip
Get-GoveeColor -LampIp $ip
Get-GoveeColorTemperature -LampIp $ip

Set-GoveePower -LampIp $ip -State On
Set-GoveeBrightness -LampIp $ip -Brightness 60
Set-GoveeColor -LampIp $ip -Color '#4080FF'
Set-GoveeColorTemperature -LampIp $ip -Kelvin 4500

# Experimental multizone streaming, with best-effort DISARM on exit.
Set-GoveePattern -LampIp $ip -Colors @('#FF0000','#00FFFF') `
  -LEDCount 2 -Seconds 10 -FrameIntervalMs 250 -Gradient
```

`Set-GoveePattern` sends experimental ARM (`B1`), complete RGB frames (`B0`), and best-effort DISARM (`B1`) in `finally`. The `-LEDCount` argument is the **number of colors in the submitted frame**, and if supplied **must equal** `-Colors.Count`; it does not resample a pattern or identify physical LEDs. Its valid range is 1–255. `-Seconds -1` streams until interrupted and also attempts cleanup. The direct module does **not** power the lamp on automatically before pattern streaming; turn the lamp on first.

Setters support `-WhatIf`, `-Confirm`, and `-Verbose`. `Get-*` status operations are polled and may not reflect a native scene or the instantaneous per-zone Razer output. No physical UDP command is authenticated or acknowledged.

## SignalRGB add-on

`GoveeBridge.js` is a **SignalRGB network add-on**, not a Lightscript. In the current code it exposes an eight-position virtual channel (`LED_LIMIT = 8`), sends frames at no more than roughly 10 per second, and targets `127.0.0.1:41005`.

Before trying it, edit the add-on's **`LAMP_ID`** constant to a configured `Bridge.Lamps[].Id` (for example, `"floor"`). The committed value is blank, and frame output is intentionally disabled until it is set. Keep the add-on's color count compatible with your Host configuration. Then use `Resume-GoveeHostControl` and `Arm-GoveeHostStreaming` separately: SignalRGB frame data never seizes Host ownership. The JavaScript mock test is in `tests/SignalRGB_Addon.mock-test.cjs`; actual SignalRGB integration still deserves device-level validation.

## Automated testing and real-lamp smoke test

From the repository root:

```powershell
# MSTest on Windows; fake clients and loopback IPC tests do not use Govee LAN ports.
dotnet test .\tests\GoveeBridge.Host.Tests\GoveeBridge.Host.Tests.csproj

# Run just the Worker localhost UDP integration tests.
dotnet test .\tests\GoveeBridge.Host.Tests\GoveeBridge.Host.Tests.csproj `
  --filter 'FullyQualifiedName~WorkerLoopbackContractTests'

# PowerShell DTO contract checks; no physical lamp or Host needed.
.\src\GoveeBridge.PowerShell.Host\Tests\Test-GoveeHost.Contracts.ps1
.\src\GoveeBridge.PowerShell\Test-GoveeLan.Offline.ps1
```

The C# tests exercise mode transitions, configuration, malformed input, packet contracts, monitoring and retry behavior, frame coalescing, multi-lamp isolation, a loopback UDP Worker, and a backend-switchable smoke workflow. The recording fake validates input using production packet factories, but it does **not** simulate Govee firmware. Diagnostic mode accepts and counts input without physical output. `TestCategory("Swappable")` selects the backend-aware smoke test; `DoNotParallelize` is applied to socket and hardware fixtures.

### Explicitly switch the smoke test from Fake to Real

**This has not yet been validated against a real lamp.** The fixture is `tests/GoveeBridge.Host.Tests/SwappableLampSmokeTests.cs`, method `CmdletWorkflowAgainstSelectedBackend`. By default, it uses a recording fake. Its `Real` backend builds the production Runtime and opens UDP 4002; it **does not launch BridgeWorker or invoke the PowerShell cmdlets themselves**. It checks the Runtime command workflow and requires you to observe physical results.

Before opting in, **stop all running GoveeBridge Host processes/services** and direct-LAN diagnostics, confirm the intended lamp IP and local LAN-Control setting, and expect the light to change power, brightness, color, and mode. In the **same PowerShell session** that launches `dotnet test`:

```powershell
# These exact environment variable names and consent value are required.
$env:GOVEE_TEST_BACKEND = 'Real'
$env:GOVEE_TEST_REAL = 'I_ACCEPT_LAMP_CHANGES'
$env:GOVEE_TEST_LAMP_ID = 'floor'
$env:GOVEE_TEST_LAMP_IP = '192.168.1.101'   # replace with your actual lamp IP
$env:GOVEE_TEST_ZONES = '8'                 # frame colors, not physical LED count; 1..255

# Run ONLY the opted-in smoke method, not the entire suite.
dotnet test .\tests\GoveeBridge.Host.Tests\GoveeBridge.Host.Tests.csproj `
  --filter 'FullyQualifiedName~SwappableLampSmokeTests.CmdletWorkflowAgainstSelectedBackend' `
  --logger 'console;verbosity=detailed'
```

Expected visual sequence: start Manual; resume PC control; power ON and set **50% brightness**; show **red for approximately two seconds**; switch to **4000 K white for approximately two seconds**; explicitly ARM real-time mode; then show **cyan → red → cyan**, approximately four seconds each. The test DISARMs/releases and attempts cleanup in `finally`. It does not restore your exact pre-test color/scene, and successful UDP submissions do not prove the lamp displayed the requested frame. Check the lamp visually and check the test output.

To run the same workflow against **input-only Diagnostic** (or return to Fake), without touching hardware:

```powershell
$env:GOVEE_TEST_BACKEND = 'Diagnostic'  # or 'Fake'
Remove-Item Env:GOVEE_TEST_REAL -ErrorAction SilentlyContinue

dotnet test .\tests\GoveeBridge.Host.Tests\GoveeBridge.Host.Tests.csproj `
  --filter 'FullyQualifiedName~SwappableLampSmokeTests.CmdletWorkflowAgainstSelectedBackend'
```

For a normal test session, you can also remove all `GOVEE_TEST_*` variables; absence of `GOVEE_TEST_BACKEND` defaults to `Fake`. A real-backend run is dangerous to run casually because it deliberately changes physical lamp settings.

### Known test infrastructure caveat: intermittent Worker UDP socket failures

The `WorkerLoopbackContractTests` currently call `worker.StartAsync()` and immediately send localhost datagrams. In **.NET 10**, `BackgroundService.ExecuteAsync()` is scheduled entirely on a background thread, so `StartAsync()` can complete before the Worker has bound the IPC listener. Their `ReserveCandidatePort()` also closes the temporary reservation before the Worker binds, creating an additional port-allocation race. On Windows, sending UDP to an unbound destination can result in a later `SocketException` (`ConnectionReset` / 10054) rather than a plain timeout. This is a plausible cause of intermittent `ValidCommandGetsReplyButMissingLampSelectorIsRejected` failures.

`FrameUdpIsUnacknowledgedAndMailboxIsPumpedAfterExplicitArm` also relies on a short 140 ms receive/cancellation probe and catches only `OperationCanceledException`. Record the actual `SocketErrorCode` and stack location before assigning its failures to the same cause. Prefer an explicit Worker **listener-ready signal** and a bound, deterministic test port/socket lifecycle rather than an arbitrary sleep; optionally suppress Windows UDP reset notifications on the **test client** as well. These are test/IPC-lifecycle concerns, not evidence that the physical Govee protocol is faulty.

## Limitations and future work

- **IPC security:** Loopback UDP is not authentication. Other local processes can request control. A named pipe with Windows ACLs is a candidate for reliable, authorized management commands; the existing fast frame path may remain UDP. Nothing pipe-based is implemented yet.
- **Delivery:** No management correlation IDs, version negotiation, guaranteed acknowledgments, or durable subscriptions exist today. Command queueing and best-effort replies are not a reliable RPC contract.
- **Physical status:** `devStatus` cannot report every native scene or experimental frame, does not identify who changed the lamp, and can be stale while streaming. Remote priority is heuristic.
- **Device coverage:** Govee Razer streaming is experimental. The maximum of 255 is **per-frame RGB positions**, not a maximum physical LED count. Whether firmware accepts more frame colors than physical zones is still open.
- **Status receiver:** Physical queries are matched by source IP; very late replies can be confused with later queries to the same lamp. Invalid device observations are discarded; rate-limited logging of rejects remains a TODO in `GoveeLanNetwork`.
- **Software integration:** SignalRGB's full device registration and lighting pipeline need continued hands-on testing. Windows Service hosting is registered, but deployment/service installation is not automated.
- **Documentation debt:** `docs/architecture.md` and the per-module READMEs contain some pre-multi-lamp or pre-`set-*` material. This root README describes the current implementation and takes precedence when those older documents disagree.

## Project layout

```text
GoveeBridge.js                      SignalRGB network add-on
GoveeBridge.qml                     SignalRGB companion resource
src/GoveeBridge.Core/               Frames, settings, IPC DTOs
src/GoveeBridge.Protocol/           Govee LAN network, packets, Razer encoder
src/GoveeBridge.Host/               Worker, Runtime, appsettings
src/GoveeBridge.PowerShell.Host/    Host IPC cmdlets and scripts
src/GoveeBridge.PowerShell/         Direct Govee LAN diagnostics
tests/GoveeBridge.Host.Tests/       MSTest fixtures and recording fake
tests/SignalRGB_Addon.mock-test.cjs JavaScript add-on mock test
docs/architecture.md                Earlier design notes (some out of date)
```

**Review baseline:** repository `main` at [`e4ed56db9b7149b5355adca687569667e6f1b845`](https://github.com/david-browning/GoveeBridge/commit/e4ed56db9b7149b5355adca687569667e6f1b845) (2026-11-08). This README is a proposed replacement; it does not imply that every feature above has been physically verified.
