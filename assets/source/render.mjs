// Renders SVG/HTML files to PNG in headless Edge, at exact pixel sizes, with a transparent background.
// usage: node render.mjs jobs.json   — jobs: [{ "src": "C:/.../x.svg", "out": "C:/.../x.png", "w": 32, "h": 32 }]
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import { mkdtempSync, writeFileSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const jobs = JSON.parse(readFileSync(process.argv[2], "utf8"));
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const port = 9341;
const proc = spawn(edge, ["--headless=new", "--hide-scrollbars", "--force-device-scale-factor=1", `--remote-debugging-port=${port}`,
  `--user-data-dir=${mkdtempSync(join(tmpdir(), "edge-icon-"))}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 60; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.some(t => t.type === "page")) break; } catch {} await sleep(200); }
const ws = new WebSocket(targets.find(t => t.type === "page").webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r));
let id = 0; const pending = new Map();
ws.addEventListener("message", m => { const msg = JSON.parse(m.data); if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
await send("Page.enable");
for (const job of jobs) {
  await send("Emulation.setDeviceMetricsOverride", { width: job.w, height: job.h, deviceScaleFactor: 1, mobile: false });
  await send("Emulation.setDefaultBackgroundColorOverride", { color: { r: 0, g: 0, b: 0, a: 0 } });
  await send("Page.navigate", { url: pathToFileURL(job.src).href });
  await sleep(job.wait ?? 350);
  const r = await send("Page.captureScreenshot", { format: "png", clip: { x: 0, y: 0, width: job.w, height: job.h, scale: 1 }, captureBeyondViewport: false });
  if (!r.result) { console.error("failed", job.out, JSON.stringify(r.error)); continue; }
  writeFileSync(job.out, Buffer.from(r.result.data, "base64"));
  console.log("ok", job.w + "x" + job.h, job.out.split(/[\/]/).pop());
}
ws.close(); proc.kill();
