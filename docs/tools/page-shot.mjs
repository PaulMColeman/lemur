// A help page in headless Edge: node page-shot.mjs <page.html> <out.png> [width] [height] [light|dark] [#anchor]
// Also reports any request that isn't file: (the pages must load nothing from the network) and console errors.
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const [, , page, out, width = "1280", height = "900", scheme = "light", anchor = ""] = process.argv;
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const port = 9338;
const proc = spawn(edge, ["--headless=new", "--hide-scrollbars", `--remote-debugging-port=${port}`,
  `--user-data-dir=${mkdtempSync(join(tmpdir(), "edge-cdp-"))}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.some(t => t.type === "page")) break; } catch {} await sleep(200); }
const ws = new WebSocket(targets.find(t => t.type === "page").webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r));
let id = 0; const pending = new Map(); const problems = [];
ws.addEventListener("message", m => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  if (msg.method === "Network.requestWillBeSent" && !msg.params.request.url.startsWith("file:") && !msg.params.request.url.startsWith("data:"))
    problems.push("network: " + msg.params.request.url);
  if (msg.method === "Runtime.consoleAPICalled" && msg.params.type === "error") problems.push("console: " + JSON.stringify(msg.params.args.map(a => a.value)));
  if (msg.method === "Log.entryAdded" && msg.params.entry.level === "error") problems.push("log: " + msg.params.entry.text + " " + (msg.params.entry.url || ""));
});
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => (await send("Runtime.evaluate", { expression: expr, returnByValue: true })).result?.result?.value;
await send("Page.enable"); await send("Network.enable"); await send("Runtime.enable"); await send("Log.enable");
await send("Emulation.setDeviceMetricsOverride", { width: +width, height: +height, deviceScaleFactor: 1, mobile: +width < 600 });
await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: scheme }] });
await send("Page.navigate", { url: pathToFileURL(page).href + anchor });
await sleep(1800);
const overflow = await ev("document.documentElement.scrollWidth - document.documentElement.clientWidth");
const r = await send("Page.captureScreenshot", { format: "png" });
writeFileSync(out, Buffer.from(r.result.data, "base64"));
console.log(`saved ${out}; horizontal overflow ${overflow}px; problems: ${problems.length ? "\n  " + problems.join("\n  ") : "none"}`);
ws.close(); proc.kill();
