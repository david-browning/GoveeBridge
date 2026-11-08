// GoveeBridge SignalRGB NETWORK ADD-ON (not a Lightscript).
// This add-on submits RGB frames to the Host. It intentionally does not
// claim ownership or arm streaming; use the Host IPC to do that explicitly.
import udp from "@SignalRGB/udp";

export function Name() { return "GoveeBridge"; }
export function Version() { return "0.3.0"; }
export function Type() { return "network"; }
export function Publisher() { return "Community development"; }
export function Size() { return [1, 8]; }
export function SubdeviceController() { return true; }

const DEVICE_ID = "goveebridge-h6072-local-v1";
const CHANNEL = "Lamp RGB";
const LED_LIMIT = 8;
const BRIDGE_HOST = "127.0.0.1";
const BRIDGE_PORT = 41005;
// REQUIRED: set this to a stable Bridge.Lamps[].Id, NOT an IP or MAC.
// An unset/blank ID disables frame output; there is no implicit default.
const LAMP_ID = "";
const MIN_FRAME_GAP_MS = 100;
let socket;
let lastFrameAt = 0;
let loggedSocketFailure = false;

class BridgeController {
    constructor() {
        this.id = DEVICE_ID;
        this.name = "GoveeBridge (local)";
        this.sku = "H6072-BRIDGE";
        this.ip = BRIDGE_HOST;
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
        if (this.registered) return;
        try {
            let c = service.getController(DEVICE_ID);
            if (c === undefined) {
                service.addController(new BridgeController());
                c = service.getController(DEVICE_ID);
            }
            if (c === undefined) return;
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
    if (typeof LAMP_ID !== "string" || !LAMP_ID.trim() ||
        LAMP_ID !== LAMP_ID.trim() || LAMP_ID.length > 64) {
        device.log("GoveeBridge: configure LAMP_ID to a stable Bridge.Lamps[].Id before sending frames.");
        return;
    }
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
    device.log("GoveeBridge: send resume, then arm, through Host IPC before streaming.");
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
    const message = { type: "rgb-frame", lampId: LAMP_ID, count: colors.length / 3, colors };
    socket.write(JSON.stringify(message), BRIDGE_HOST, BRIDGE_PORT);
}

export function Shutdown() {
    if (socket) {
        socket.close();
        socket = undefined;
    }
}
