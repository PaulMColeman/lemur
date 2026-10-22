// The demo note page in headless Edge, with one transcript line playing: for the manual.
//   node note-shot.mjs <page.html> <out.png> [width] [height] [light|dark] [line]
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const [, , page, out, width = "1100", height = "1400", scheme = "light", line = "6"] = process.argv;
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const port = 9336;
const proc = spawn(edge, ["--headless=new", "--mute-audio", "--autoplay-policy=no-user-gesture-required", "--hide-scrollbars",
  `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "edge-cdp-"))}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.some(t => t.type === "page")) break; } catch {} await sleep(200); }
const ws = new WebSocket(targets.find(t => t.type === "page").webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r));
let id = 0; const pending = new Map();
ws.addEventListener("message", m => { const msg = JSON.parse(m.data); if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => (await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", { width: +width, height: +height, deviceScaleFactor: 1, mobile: +width < 600 });
await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: scheme }] });
await send("Page.navigate", { url: pathToFileURL(page).href });
await sleep(2000);
console.log("lines:", await ev("document.querySelectorAll('.transcript span[data-t]').length"),
  "page height:", await ev("document.documentElement.scrollHeight"));
await ev(`document.querySelectorAll('.transcript span[data-t]')[${+line}].click()`);
await sleep(1800);
await ev("document.getElementById('audio').pause()");
await ev("window.scrollTo(0, 0)");
await sleep(400);
const r = await send("Page.captureScreenshot", { format: "png" });
writeFileSync(out, Buffer.from(r.result.data, "base64"));
console.log("saved", out);
ws.close(); proc.kill();
