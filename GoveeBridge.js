// GoveeBridge SignalRGB NETWORK ADD-ON (not a Lightscript).
// This file intentionally has NO physical lamp address or Govee packet encoder.
// SignalRGB -> sampled canvas colors -> loopback UDP -> .NET GoveeBridge service.
import udp from "@SignalRGB/udp";

export function Name() { return "GoveeBridge"; }
export function Version() { return "0.2.0"; }
export function Type() { return "network"; }
export function Publisher() { return "Community development"; }
export function Size() { return [1, 8]; }
export function SubdeviceController() { return true; }

const DEVICE_ID = "goveebridge-h6072-local-v1";
const CHANNEL = "Lamp RGB";
const LED_LIMIT = 8;  // Logical LED positions, not H6072 physical pixel count.
const BRIDGE_HOST = "127.0.0.1";
const BRIDGE_PORT = 41005;
const MIN_FRAME_GAP_MS = 100;  // 10 fps to the bridge; the service limits physical writes.
let socket;
let lastFrameAt = 0;
let loggedSocketFailure = false;

class BridgeController {
    constructor() {
        this.id = DEVICE_ID;
        this.name = "GoveeBridge (local)";
        this.sku = "H6072-BRIDGE";
        this.ip = BRIDGE_HOST; // Address of local bridge; real lamp IP belongs only to C#.
        this.paired = true;
        this.initialized = false;
    }
    update() {
        if (!this.initialized) {
            this.initialized = true;
            service.updateController(this);
            service.announceController(this);
        }
    }
}

export function DiscoveryService() {
    this.IconUrl = "https://assets.signalrgb.com/brands/govee/logo.png";
    this.registered = false;
    this.Initialize = function () { service.log("GoveeBridge: registering local virtual network device"); };
    this.Update = function () {
        // Synthetic network-controller registration still requires testing on your version.
        // Do not mark registered before the host accepts the controller.
        if (this.registered) return;
        try {
            let c = service.getController(DEVICE_ID);
            if (c === undefined) {
                service.addController(new BridgeController());
                c = service.getController(DEVICE_ID);
            }
            if (c === undefined) return; // retry on next Update()
            c.paired = true;
            service.announceController(c);
            this.registered = true;
            service.log("GoveeBridge: local controller announced");
        } catch (e) {
            service.log(`GoveeBridge: registration unsuccessful; retrying: ${e}`);
        }
    };
    this.Shutdown = function () {};
}

export function Initialize() {
    device.setName("Govee H6072 via GoveeBridge");
    device.addChannel(CHANNEL, LED_LIMIT);
    device.channel(CHANNEL).SetLedLimit(LED_LIMIT);
    device.SetLedLimit(LED_LIMIT);
    socket = udp.createSocket();
    if (socket) {
        socket.on("error", (code, message) => {
            if (!loggedSocketFailure) device.log(`GoveeBridge UDP error: ${code} ${message}`);
            loggedSocketFailure = true;
        });
    }
    device.log("GoveeBridge: channel Lamp RGB has 8 logical slots. Assign a generic component.");
}

export function Render() {
    if (!socket) return;
    const now = Date.now();
    if (now - lastFrameAt < MIN_FRAME_GAP_MS) return;
    const channel = device.channel(CHANNEL);
    if (channel.LedCount() <= 0) return;
    const colors = channel.getColors("Inline", "RGB");
    if (!colors || colors.length < 3 || colors.length % 3 !== 0) return;
    lastFrameAt = now;
    const packet = JSON.stringify({ type: "rgb-frame", count: colors.length / 3, colors });
    // SignalRGB's addon UDP socket .write is NOT a browser fetch or Node.js socket.
    socket.write(packet, BRIDGE_HOST, BRIDGE_PORT);
}

export function Shutdown() {
    if (socket) {
        socket.close();
        socket = undefined;
    }
}
