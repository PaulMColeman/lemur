// A note page opened at a moment (#t=, as lemur://note/ links open it) in headless Edge, under its own CSP and the
// browser's usual autoplay rule (clicks are real mouse events). Checks that the line said then is lit and in view, that
// "Play from" plays it, that a changed #t= moves to another line, that a summary item's time plays from then, and that
// nothing is logged (no CSP violation, no error). Exits 1 if anything is off.
//   node note-moment.mjs <page.html> [seconds] [other seconds]
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const [, , page, first = "60", second = "120"] = process.argv;
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const port = 9337;
const proc = spawn(edge, ["--headless=new", "--mute-audio", "--hide-scrollbars", `--remote-debugging-port=${port}`,
  `--user-data-dir=${mkdtempSync(join(tmpdir(), "edge-cdp-"))}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.some(t => t.type === "page")) break; } catch {} await sleep(200); }
const ws = new WebSocket(targets.find(t => t.type === "page").webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r));
let id = 0; const pending = new Map(); const logged = [];
ws.addEventListener("message", m => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === "Runtime.consoleAPICalled") logged.push(`console.${msg.params.type}`);
  if (msg.method === "Runtime.exceptionThrown") logged.push(`exception: ${msg.params.exceptionDetails.text}`);
  if (msg.method === "Log.entryAdded") logged.push(`${msg.params.entry.level} (${msg.params.entry.source}): ${msg.params.entry.text}`);
});
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => (await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
const click = async selector => {
  const box = await ev(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e || e.hidden) return null;
    const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  if (!box) return false;
  for (const type of ["mousePressed", "mouseReleased"]) await send("Input.dispatchMouseEvent", { type, x: box.x, y: box.y, button: "left", clickCount: 1 });
  return true;
};
// What the page shows, and the line it should light for moment t: the last to start within or before that second.
const look = async t => (await ev(`(() => {
  const spans = [...document.querySelectorAll(".transcript span[data-t]")].sort((a, b) => a.dataset.t - b.dataset.t);
  const want = spans.filter(s => Math.floor(+s.dataset.t) <= ${t}).pop() ?? spans[0];
  const at = [...document.querySelectorAll(".transcript span.at")];
  const from = document.querySelector("#player .from");
  const r = at[0]?.getBoundingClientRect();
  const a = document.getElementById("audio");
  return { want: want?.dataset.t, at: at.map(s => s.dataset.t), inView: !!r && r.top >= 0 && r.bottom <= innerHeight,
           button: from && !from.hidden ? from.textContent : null, paused: a.paused, time: Math.round(a.currentTime * 10) / 10,
           now: [...document.querySelectorAll(".transcript span.now")].map(s => s.dataset.t) };
})()`)) ?? { gone: "not the note page any more", at: [], now: [], paused: true };
let failed = false;
const check = (what, ok, seen) => { console.log(`${ok ? "ok  " : "FAIL"} ${what}`, JSON.stringify(seen)); failed ||= !ok; };

await send("Page.enable"); await send("Runtime.enable"); await send("Log.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1100, height: 900, deviceScaleFactor: 1, mobile: false });
await send("Page.navigate", { url: `${pathToFileURL(page).href}#t=${first}` });
await sleep(2000);
let s = await look(first);
check(`opened at #t=${first}: its line lit and in view, Play offered, nothing playing`,
  s.at.length === 1 && s.at[0] === s.want && s.inView && !!s.button && s.paused, s);

await ev(`location.hash = "#t=${second}"`);
await sleep(800);
s = await look(second);
check(`#t=${second}: moved to its line`, s.at.length === 1 && s.at[0] === s.want && s.inView && !!s.button, s);

const clicked = await click("#player .from");
await sleep(1500); // it starts half a second or more before the line
s = await look(second);
check("Play from: playing that line", clicked && !s.paused && s.now.includes(s.want) && s.at.length === 0, s);
await ev(`document.getElementById("audio").pause()`);

if (await ev(`!!document.querySelector(".summary a.moment")`)) {
  const t = await ev(`+document.querySelector(".summary a.moment").dataset.t`);
  await ev("scrollTo(0, 0)");
  await click(".summary a.moment");
  await sleep(1500);
  s = await look(t);
  check(`the summary's first time (${t} s): playing from its line`, !s.paused && s.now.includes(s.want), s);
  await ev(`document.getElementById("audio").pause()`);
} else console.log("--   no summary item with a time on this page");

check("nothing logged (no CSP violation, no error)", logged.length === 0, logged);
ws.close(); proc.kill();
process.exit(failed ? 1 : 0);
