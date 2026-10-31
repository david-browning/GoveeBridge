# Architecture decisions & next milestones

## Why keep the local loopback connection?

- The SignalRGB add-on is an in-process **adapter**, not a driver for the physical device.
- `127.0.0.1:41005` is **inter-process communication**. The physical lamp's address belongs in Host's private configuration, not in JS, QML, the Lightscript, or Git.
- One Windows worker can own the fixed inbound UDP 4002 Govee response port; competing iCUE/SignalRGB Govee integrations have already caused binding failures.
- Simulated/Diagnostic *mode* remains useful for development; the localhost IPC link remains necessary in *production mode* for our proposed architecture.

## Message contract (v0)

From SignalRGB add-on to Host:

```json
{"type":"rgb-frame","count":2,"colors":[255,0,0,0,0,255]}
```

From CLI to Host (also UDP localhost:41005, same receiver):

```json
{"type":"command","action":"release"}
```

Other actions: `resume`, `status`, `lamp-status`. Host replies to CLI's ephemeral UDP endpoint with a JSON response. This IPC is *unreliable UDP*, not an authenticated public API. Only loopback is accepted. For a broader multi-user service, a named pipe with ACLs or loopback HTTP with authentication would be preferable. This project does **not** give untrusted programs a remote-control interface.

## More expressive LEDs

The eight virtual LED positions do NOT prove H6072 LAN support for eight segments. Standard Govee commands `turn`, `colorwc`, `devStatus` affect a whole device. `GoveeStatic` currently averages frame colors into one RGB color. Only after a verified Razer/DreamView streaming packet works should we implement `GoveeRealtimeTransport` and make the segment count configurable from its capabilities.

## Next engineering tasks

1. Run VS debugger with Host in Diagnostic mode; get virtual SignalRGB add-on registration working on installed SignalRGB version.
2. Add an integration test: JS emits test colors -> Host sees 8 logical entries; verify layout orientation.
3. Verify status/control using GoveeStatic with manual release/resume. Test power-off remote arbitration; record races and packet timings.
4. Isolate and probe firmware-compatible Razer/DreamView frames, with stock add-ons OFF. Document actual number and order of addressable segments.
5. Replace average-color transport with realtime packet protocol, with rate limiting/backpressure and a safe handoff from PC to remote.
6. Add UI in QML for bridge status and release/resume once a documented add-on-to-bridge response path is confirmed.
7. Add proper automated tests with xUnit/NUnit when package restore is available. Current smoke tests avoid extra NuGet packages.

## Notes

The root `GoveeBridge.qml` is an **optional add-on configuration view** (Qt Quick), not an LED shape definition. Shape/geometry is selected as a SignalRGB component and placed in Layout; the Lightscript outputs one image. The .NET service should not know Lightscript canvas coordinates.

Host starts released and does not turn the lamp on until `resume`. `AutoReleaseWhenRemoteTurnsOff` is best-effort, not complete remote override. A remote's built-in scene may not be reflected in standard `devStatus` at all. Do not claim magic automatic arbitration.
