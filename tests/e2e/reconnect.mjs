// Drives the real camera page and an attendee page in headless Chrome against a running server, breaks the camera link,
// and checks that both recover. node reconnect.mjs [path to the running copy]. Runs a throwaway talk on the configured providers.
import { spawn } from 'node:child_process';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const repo = process.argv[2] ?? join(import.meta.dirname, '..', '..');
const key = readFileSync(join(repo, 'data', 'operator.key'), 'utf8').trim();
const base = 'http://localhost:8080';
const mtx = 'http://127.0.0.1:9997/v3';
const chrome = process.env.CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const op = (p, o = {}) => fetch(`${base}/api/op${p}`, { ...o, headers: { Cookie: `talklens_operator=${key}`, 'Content-Type': 'application/json' } });
const safe = async (f, fallback = null) => { try { return await f(); } catch { return fallback; } };

const profile = mkdtempSync(join(tmpdir(), 'tl-edge-'));
const port = 9333;
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run',
  '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });

async function openTab(url) {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    target = await safe(async () => (await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' })).json());
    if (!target) await sleep(250);
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => { ws.onopen = r; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (m) => { const d = JSON.parse(m.data); if (pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); } };
  return async (expression) => {
    const mine = ++id;
    const reply = new Promise((r) => pending.set(mine, r));
    ws.send(JSON.stringify({ id: mine, method: 'Runtime.evaluate', params: { expression, returnByValue: true, awaitPromise: true } }));
    const d = await reply;
    return d.result?.result?.value;
  };
}

const pill = (cam) => cam(`document.getElementById('pill').textContent`);
const camReady = () => safe(async () => (await (await fetch(`${mtx}/paths/get/live/r1/cam`)).json()).ready, false);

// Waits for the page to say Sending and MediaMTX to have the stream, printing each change of the status text.
async function waitSending(cam, label, t0, limitMs = 90000) {
  let last = '';
  while (Date.now() - t0 < limitMs) {
    const now = await safe(() => pill(cam), '');
    if (now !== last) { console.log(`  ${((Date.now() - t0) / 1000).toFixed(1)}s ${now}`); last = now; }
    if (now === 'Sending' && await camReady()) { console.log(`  ${label}: back after ${((Date.now() - t0) / 1000).toFixed(1)} s`); return true; }
    await sleep(150);
  }
  console.log(`  ${label}: NOT back after ${limitMs / 1000} s, page says "${last}"`);
  return false;
}

// True when the attendee's picture is really moving, not just attached.
async function attendeeMoving(att, limitMs = 30000) {
  const t0 = Date.now();
  while (Date.now() - t0 < limitMs) {
    const a = await att(`(async()=>{const v=document.getElementById('video');const q=()=>v.getVideoPlaybackQuality().totalVideoFrames;const f=q();await new Promise(r=>setTimeout(r,800));return {w:v.videoWidth,more:q()-f}})()`);
    if (a && a.w > 0 && a.more > 0) { console.log(`  attendee moving ${((Date.now() - t0) / 1000).toFixed(1)} s later (${a.w}px, ${a.more} new frames in 0.8 s)`); return true; }
    await sleep(300);
  }
  console.log('  attendee NOT moving');
  return false;
}

const results = {};
try {
  const started = await op('/rooms/1/talks/start', { method: 'POST', body: JSON.stringify({ title: 'reconnect probe', speaker: 'probe', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter: false, audioSource: 'camera', slideSource: 'none' }) });
  if (!started.ok) throw new Error('start ' + started.status + ' ' + await started.text());

  const cam = await openTab(`${base}/publish?key=${key}&room=1&path=cam`);
  await sleep(1500);
  await cam(`document.getElementById('start').click()`);
  console.log('first start');
  results.first = await waitSending(cam, 'first start', Date.now(), 20000);

  const att = await openTab(`${base}/r/1`);
  await sleep(1500);
  await att(`document.getElementById('join').click()`);
  results.attendeeFirst = await attendeeMoving(att);

  console.log('A: server drops the camera session');
  const list = await (await fetch(`${mtx}/webrtcsessions/list`)).json();
  const sourceId = (await (await fetch(`${mtx}/paths/get/live/r1/cam`)).json()).source.id;
  const publisher = list.items.find((s) => s.id === sourceId);
  console.log('  sessions', list.items.map((s) => `${s.path}:${s.state}`).join(', '));
  let t0 = Date.now();
  const kicked = await fetch(`${mtx}/webrtcsessions/kick/${publisher.id}`, { method: 'POST' });
  console.log('  kick', kicked.status);
  await sleep(200);
  results.kick = await waitSending(cam, 'A', t0);
  results.attendeeAfterKick = await attendeeMoving(att);

  console.log('B: phone takes the camera away (track ended)');
  t0 = Date.now();
  await cam(`(()=>{const s=document.getElementById('preview').srcObject;const t=s.getVideoTracks()[0];t.stop();t.dispatchEvent(new Event('ended'));return 1})()`);
  await sleep(200);
  results.track = await waitSending(cam, 'B', t0);
  const live = await cam(`document.getElementById('preview').srcObject?.getVideoTracks()[0]?.readyState`);
  console.log('  new track', live);
  results.trackLive = live === 'live';
  results.attendeeAfterTrack = await attendeeMoving(att);

  await sleep(3000);
  const status = await (await op('/status')).json();
  console.log('  talk audio', JSON.stringify(status.rooms[0].talk?.audio), 'captions', JSON.stringify(status.rooms[0].talk?.captions));

  console.log('C: Stop still stops, and does not come back');
  await cam(`document.getElementById('stop').click()`);
  await sleep(7000);
  const stopped = await pill(cam);
  console.log('  page says', stopped);
  results.stopStays = stopped === 'Not sending' && !(await camReady());
  await op('/rooms/1/talks/stop', { method: 'POST' });

} catch (e) {
  console.log('ERROR', e);
  await safe(() => op('/rooms/1/talks/stop', { method: 'POST' }));
}
console.log(JSON.stringify(results, null, 1));
process.exitCode = Object.values(results).every(Boolean) && Object.keys(results).length ? 0 : 1;
browser.kill();
await sleep(500);
safe(() => rmSync(profile, { recursive: true, force: true }));
process.exit();
