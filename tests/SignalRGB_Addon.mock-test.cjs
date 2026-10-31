// Minimal offline simulation of SignalRGB's network add-on APIs; NOT integration testing.
// Run: node tests/SignalRGB_Addon.mock-test.cjs
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync('GoveeBridge.js', 'utf8')
    .replace(/^import udp from "@SignalRGB\/udp";\s*/m, '')
    .replace(/^export /gm, '')
  + '\nglobalThis.api = { Name, Version, Type, DiscoveryService, Initialize, Render, Shutdown };';
let registered = null;
const frames = [];
const channel = {
    SetLedLimit(n) { assert.equal(n, 8); },
    LedCount() { return 8; },
    getColors(order, colors) {
        assert.equal(order, 'Inline'); assert.equal(colors, 'RGB');
        return [255,0,0, 0,255,0, 0,0,255, 1,2,3, 4,5,6, 7,8,9, 10,11,12, 13,14,15];
    }
};
const ctx = {
    console, Date,
    udp: { createSocket() {return {on(){}, write(packet,ip,port){frames.push({packet,ip,port});}, close(){}};} },
    service: {
        log() {}, getController(id){return registered?.id === id ? registered : undefined;},
        addController(c){registered = c;}, announceController(c){assert.equal(c, registered);},
        updateController(c){assert.equal(c, registered);}
    },
    device: {
        setName() {}, addChannel(name, count){assert.equal(name,'Lamp RGB'); assert.equal(count,8);},
        channel(){return channel;}, SetLedLimit(n){assert.equal(n,8);}, log(){}
    }
};
vm.runInNewContext(source, ctx, {filename:'GoveeBridge.js'});
const discovery = new ctx.api.DiscoveryService();
discovery.Initialize(); discovery.Update(); discovery.Update();
assert.ok(registered);
ctx.api.Initialize(); ctx.api.Render(); ctx.api.Shutdown();
assert.equal(frames.length,1);
const f = frames[0];
assert.equal(f.ip,'127.0.0.1'); assert.equal(f.port,41005);
assert.equal(JSON.parse(f.packet).count,8);
assert.equal(JSON.parse(f.packet).colors.length,24);
console.log('SignalRGB JS mock checks passed (not a real SignalRGB integration test).');
