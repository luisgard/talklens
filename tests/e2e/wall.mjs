// Reactions, the photo wall, the album and the room's big screen, against a running server, in headless Chrome.
// Needs the vision model online for the photo check. Usage: node wall.mjs <video with speech> [path to the running copy] [folder for screenshots]
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [video, root = join(here, '..', '..'), shots] = process.argv.slice(2);
const base = process.env.TALKLENS_URL ?? 'http://localhost:8080';
const key = readFileSync(join(root, 'data', 'operator.key'), 'utf8').trim();
const chrome = process.env.CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const failures = [];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function check(name, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`);
  if (!ok) failures.push(name);
}

const op = (path, method = 'GET', body) => fetch(`${base}/api/op${path}`, {
  method,
  headers: { Cookie: `talklens_operator=${key}`, 'Content-Type': 'application/json' },
  body: body === undefined ? undefined : JSON.stringify(body),
});
const post = (path, body) => fetch(`${base}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

const profile = mkdtempSync(join(tmpdir(), 'tl-edge-'));
const port = 9338;
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });

async function openTab(url, width = 420, height = 900, mobile = true) {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })).json(); } catch { await sleep(250); }
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => { ws.onopen = r; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (m) => { const d = JSON.parse(m.data); if (pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); } };
  const call = (method, params = {}) => new Promise((r) => { const mine = ++id; pending.set(mine, r); ws.send(JSON.stringify({ id: mine, method, params })); });
  await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile });
  await call('Page.navigate', { url });
  const ev = async (expression) => (await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })).result?.result?.value;
  const shot = async (name) => {
    if (!shots) return;
    mkdirSync(shots, { recursive: true });
    writeFileSync(join(shots, name + '.png'), Buffer.from((await call('Page.captureScreenshot', { format: 'png' })).result.data, 'base64'));
  };
  const until = async (expression, ms) => {
    for (const end = Date.now() + ms; Date.now() < end; await sleep(300)) if (await ev(expression)) return true;
    return false;
  };
  return { ev, shot, until };
}

const MAKE_PHOTO = `(async () => {
  const c = document.createElement('canvas'); c.width = 2400; c.height = 1600;
  const g = c.getContext('2d'); const grad = g.createLinearGradient(0, 0, 2400, 1600);
  grad.addColorStop(0, '#203a5f'); grad.addColorStop(1, '#e8a33d');
  g.fillStyle = grad; g.fillRect(0, 0, 2400, 1600); g.fillStyle = '#fff'; g.font = 'bold 160px sans-serif'; g.fillText('Hello from the meetup!', 200, 820);
  const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.9));
  const data = new DataTransfer(); data.items.add(new File([blob], 'meetup.jpg', { type: 'image/jpeg' }));
  const input = document.getElementById('photo-file'); input.files = data.files; input.dispatchEvent(new Event('change'));
  return true; })()`;

let room;
let talkId;
let publisher;
let photoId;
try {
  room = (await (await op('/rooms', 'POST', { name: 'Wall probe' })).json()).id;
  talkId = (await (await op(`/rooms/${room}/talks/start`, 'POST', { title: 'Wall probe', speaker: 'Probe', language: 'en', organizerAuthorized: true, speakerAuthorized: true, keepAfter: true, audioSource: 'camera', slideSource: 'none' })).json()).id;
  publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-stream_loop', '-1', '-i', video, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', `rtsp://127.0.0.1:8554/live/r${room}/cam`], { stdio: 'inherit' });

  // ----- The big screen -----
  const screen = await openTab(`${base}/screen/${room}`, 1280, 720, false);
  check('the screen shows the talk', await screen.until(`document.getElementById('title').textContent === 'Wall probe'`, 10000));
  check('the screen shows a QR to join', await screen.until(`document.getElementById('qr').naturalWidth > 0 && document.getElementById('join-url').textContent.includes('/r/${room}')`, 10000));
  await sleep(4000);
  const watching = (await (await fetch(`${base}/api/rooms`)).json()).rooms.find((r) => r.id === room).watching;
  check('the screen does not count as an attendee', watching === 0, `${watching} watching`);
  check('captions reach the screen', await screen.until(`document.getElementById('captions').innerText.trim().length > 0`, 45000));

  // ----- Reactions -----
  const phone = await openTab(`${base}/r/${room}`);
  check('the phone shows reactions while live', await phone.until(`!document.getElementById('reactions').hidden && document.querySelectorAll('#reactions .reaction').length === 4`, 10000));
  await screen.ev(`window.__floats = 0; new MutationObserver((m) => m.forEach((r) => (window.__floats += r.addedNodes.length))).observe(document.getElementById('float-layer'), { childList: true }); true`);
  await phone.ev(`document.querySelectorAll('#reactions .reaction')[1].click(); true`);
  check('a tap floats up on the screen', await screen.until(`window.__floats > 0`, 5000));
  check('and on the phone', await phone.until(`document.querySelectorAll('#float-layer .float-reaction').length > 0`, 5000));
  const first = await post(`/api/rooms/${room}/react`, { voter: 'probe-fast', kind: 'clap' });
  const second = await post(`/api/rooms/${room}/react`, { voter: 'probe-fast', kind: 'clap' });
  check('one phone cannot flood it', first.ok && second.status === 429);
  for (let i = 0; i < 6; i++) await post(`/api/rooms/${room}/react`, { voter: `probe-r${i}`, kind: 'fire' });

  // ----- A photo, sent through the phone's own picker -----
  await phone.ev(`document.getElementById('tab-photos').click(); true`);
  await phone.ev(MAKE_PHOTO);
  check('the phone sends it and says it waits', await phone.until(`document.querySelector('#photo-grid .photo-badge') !== null`, 15000));
  await phone.shot('wall-phone');
  const mine = (await (await op('/photos')).json()).find((p) => p.talk === 'Wall probe');
  photoId = mine?.id;
  check('the operator gets it', !!mine);
  const size = await phone.ev(`new Promise((r) => { const i = new Image(); i.onload = () => r([i.naturalWidth, i.naturalHeight]); i.src = document.querySelector('#photo-grid img').src; })`);
  check('the phone shrank it first', size && Math.max(...size) <= 1600, JSON.stringify(size));
  check('nobody else can see it yet', (await fetch(`${base}/photos/${photoId}.jpg`)).status === 404);
  let reviewed;
  for (let i = 0; i < 60 && !reviewed; i++, await sleep(1000)) reviewed = (await (await op('/photos')).json()).find((p) => p.id === photoId && (p.alt || p.flag));
  check('the AI describes it', !!reviewed?.alt, reviewed?.alt || reviewed?.flag || 'no review');
  check('the AI lets a harmless photo through', !!reviewed && !reviewed.flag);
  await op(`/photos/${photoId}/status`, 'POST', { status: 'approved' });
  check('approved, it appears on the screen', await screen.until(`!document.getElementById('photo-view').hidden && document.getElementById('photo-img').src.includes('/photos/${photoId}.jpg')`, 10000));
  check('marked as new', await screen.ev(`!document.getElementById('photo-new').hidden`));
  await sleep(800);
  await screen.shot('wall-screen');

  // ----- The album -----
  const album = await openTab(`${base}/album`);
  check('the album shows it', await album.until(`[...document.querySelectorAll('#grid img')].some((i) => i.src.includes('/photos/${photoId}.jpg'))`, 10000));
  const like = await post(`/api/photos/${photoId}/like`, { voter: 'probe-liker' });
  check('likes count', like.ok && (await like.json()).likes === 1);
  check('the album updates the likes live', await album.until(`!!document.querySelector('#grid .photo-likes')`, 5000));
  const zip = Buffer.from(await (await fetch(`${base}/api/photos.zip`)).arrayBuffer());
  check('everything downloads as a zip', zip.length > 1000 && zip.subarray(0, 2).toString() === 'PK' && zip.includes(Buffer.from(`Wall probe/${photoId}.jpg`)));
  await album.shot('wall-album');
  check('anyone in it can take it down', (await post(`/api/photos/${photoId}/report`, { voter: 'probe-in-photo' })).ok);
  check('it leaves the screen at once', await screen.until(`document.getElementById('photo-view').hidden || !document.getElementById('photo-img').src.includes('/photos/${photoId}.jpg')`, 5000));
  check('and the album', await album.until(`![...document.querySelectorAll('#grid img')].some((i) => i.src.includes('/photos/${photoId}.jpg'))`, 5000));
  check('the operator sees why it waits', (await (await op('/photos')).json()).find((p) => p.id === photoId)?.status === 'reported');

  // ----- After the talk -----
  await op(`/rooms/${room}/talks/stop`, 'POST');
  await sleep(500);
  const report = await (await op(`/reports/speaker/${talkId}`)).json();
  check('the report shows how the room reacted', report.reactions?.total >= 7 && report.reactions.peaks.length > 0, JSON.stringify(report.reactions?.peaks?.[0] ?? null));
  const moments = await (await fetch(`${base}/api/talks/${talkId}/moments`)).json();
  check('the recording lists where the room reacted', moments.peaks?.length > 0);
} finally {
  publisher?.kill();
  if (photoId) await op(`/photos/${photoId}`, 'DELETE');
  if (room) { await op(`/rooms/${room}/talks/stop`, 'POST'); await sleep(500); await op(`/rooms/${room}`, 'DELETE'); }
  if (talkId) await op(`/talks/${talkId}`, 'DELETE');
  browser.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still hold it */ }
}
console.log(failures.length ? `\n${failures.length} failed` : '\nAll passed');
process.exit(failures.length ? 1 : 0);
