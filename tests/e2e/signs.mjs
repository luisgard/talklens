// The experimental signing avatar, against a running server, in headless Chrome. Needs internet: CWASA loads from its home at UEA.
// Proves it signs on /signs, and in a real talk that a final caption reaches the avatar. It cannot prove the signs are right.
// Usage: node signs.mjs <video with spoken English> [path to the running copy] [folder for screenshots]
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

const profile = mkdtempSync(join(tmpdir(), 'tl-edge-'));
const port = 9336;
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--enable-unsafe-swiftshader', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });

async function openTab(url) {
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
  await call('Emulation.setDeviceMetricsOverride', { width: 420, height: 900, deviceScaleFactor: 1, mobile: true });
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

const WATCH = `window.__signs = []; for (const h of ['animactive', 'animidle']) CWASA.addHook(h, () => __signs.push(h), 0); true`;
let room;
let talkId;
let publisher;
try {
  // ----- The page to try it -----
  const page = await openTab(`${base}/signs`);
  // People do not wait for the avatar, and a press must not be lost.
  await page.until(`document.getElementById('sign-status')?.textContent.startsWith('Loading')`, 5000);
  await page.ev(`document.getElementById('sign-text').value = 'Hello and welcome'; document.getElementById('sign-form').requestSubmit(); true`);
  check('a sentence sent while it loads is not lost', await page.until(`document.querySelectorAll('#sign-result .sign-chip').length > 0`, 45000));
  check('the avatar loads', await page.until(`document.getElementById('sign-status')?.textContent === 'Ready.'`, 45000));
  check('the page keeps its own dark background', (await page.ev(`getComputedStyle(document.body).backgroundColor`)) !== 'rgb(238, 238, 238)');
  check('it says what it is', (await page.ev(`document.body.innerText`)).includes('not an interpreter'));
  await page.ev(WATCH);
  await page.ev(`document.getElementById('sign-text').value = 'Hello and welcome xylophone'; document.getElementById('sign-form').requestSubmit(); true`);
  check('it starts signing', await page.until(`__signs.includes('animactive')`, 15000));
  check('the AI chose the signs', await page.until(`document.querySelector('.sign-how')?.textContent.includes('AI') && !document.querySelector('.sign-how').textContent.includes('without')`, 5000));
  await sleep(1500);
  await page.shot('signs-page');
  check('it finishes signing', await page.until(`__signs.includes('animidle')`, 25000));
  check('it goes back to ready', await page.until(`document.getElementById('sign-status').textContent === 'Ready.'`, 5000));
  check('a word without a sign is shown as left out', await page.ev(`[...document.querySelectorAll('#sign-result .missing')].some((c) => /xylophone/i.test(c.textContent))`));

  await page.ev(`document.getElementById('sign-text').value = 'xylophone'; document.getElementById('sign-form').requestSubmit(); true`);
  check('it says so when it has no sign at all', await page.until(`document.getElementById('sign-status').textContent.includes('no sign')`, 15000));

  // ----- The same avatar following a real talk -----
  room = (await (await op('/rooms', 'POST', { name: 'Signing probe' })).json()).id;
  // Kept and recorded, so the recording can be checked for the same signs.
  talkId = (await (await op(`/rooms/${room}/talks/start`, 'POST', { title: 'Signing probe', speaker: 'Probe', language: 'en', organizerAuthorized: true, speakerAuthorized: true, keepAfter: true, recordVideo: true, audioSource: 'camera', slideSource: 'none' })).json()).id;
  publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-stream_loop', '-1', '-i', video, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', `rtsp://127.0.0.1:8554/live/r${room}/cam`], { stdio: 'inherit' });

  const talk = await openTab(`${base}/r/${room}`);
  check('the talk page has the option', await talk.until(`!!document.querySelector('[data-i18n=signAvatar]')?.textContent`, 10000));
  check('the avatar is off by default', await talk.ev(`document.getElementById('sign-panel').hidden && typeof CWASA === 'undefined'`));
  await talk.ev(`document.getElementById('set-sign').click(); true`);
  check('turning it on loads the avatar', await talk.until(`typeof CWASA === 'object' && document.getElementById('sign-status').textContent.startsWith('Experimental')`, 45000));
  await talk.ev(WATCH);
  await talk.ev(`document.getElementById('join').click(); true`);
  check('a caption reaches the page', await talk.until(`document.getElementById('captions').innerText.trim().length > 0`, 45000));
  check('captions keep showing with the avatar on', !(await talk.ev(`document.getElementById('captions').hidden`)));
  check('a spoken sentence reaches the avatar', await talk.until(`document.querySelectorAll('#sign-said .sign-chip').length > 0`, 45000));
  check('the avatar signs along with the talk', await talk.until(`__signs.includes('animactive')`, 45000));
  console.log('      signed:', (await talk.ev(`document.getElementById('sign-said').innerText`))?.replace(/\s+/g, ' '));
  await sleep(1500);
  const pip = await talk.ev(`document.getElementById('sign-pip').clientHeight`);
  check('the avatar is big enough to read', pip >= 240, pip + 'px');

  // The server signs once and every phone with the avatar on gets it.
  const second = await openTab(`${base}/r/${room}`);
  check('a second phone remembers the avatar is on', await second.until(`document.getElementById('set-sign')?.checked === true`, 10000));
  check('a second phone signs along too', await second.until(`document.querySelectorAll('#sign-said .sign-chip').length > 0`, 60000));
  await talk.ev(`document.getElementById('set-sign').click(); true`);
  await sleep(4000);
  const before = await talk.ev(`document.getElementById('sign-said').innerText`);
  await sleep(10000);
  check('a phone that turns it off stops getting signs', before === await talk.ev(`document.getElementById('sign-said').innerText`));
  const seen = await second.ev(`document.getElementById('sign-said').innerText`);
  check('the other phone keeps signing', await second.until(`document.getElementById('sign-said').innerText !== ${JSON.stringify(seen)}`, 20000));
  await talk.ev(`document.getElementById('set-sign').click(); true`);
  await talk.shot('signs-talk');

  // ----- The recording signs what was signed live, at the moments it was said -----
  publisher.kill();
  await op(`/rooms/${room}/talks/stop`, 'POST');
  const saved = await (await fetch(`${base}/api/talks/${talkId}/signs`)).json();
  check('what it signed live is kept with the talk', saved.length > 0, `${saved.length} pieces`);
  check('each piece keeps when it was said', saved.every((s, i) => i === 0 || s.t >= saved[i - 1].t) && saved[0].t >= 0);
  const replay = await openTab(`${base}/talk/${talkId}`);
  check('the recording is ready', await replay.until(`!document.getElementById('replay').hidden && !!document.getElementById('replay').currentSrc`, 60000));
  check('the recording offers the avatar, already on', await replay.until(`!document.getElementById('sign-toggle').hidden && document.getElementById('set-sign').checked`, 10000));
  check('the avatar sits on the video', await replay.until(`typeof CWASA === 'object' && !document.getElementById('sign-pip').hidden`, 45000));
  const replayPip = await replay.ev(`document.getElementById('sign-pip').clientHeight`);
  check('the avatar on the recording is big enough to read', replayPip >= 180, replayPip + 'px');
  await replay.until(`document.getElementById('sign-status').textContent.startsWith('Experimental')`, 45000);
  await replay.ev(WATCH);
  await replay.ev(`const v = document.getElementById('replay'); v.muted = true; v.currentTime = Math.max(0, (${saved[0].t} - 1500) / 1000); v.play(); true`);
  check('the recording signs along', await replay.until(`__signs.includes('animactive')`, 30000));
  await sleep(1500);
  await replay.shot('signs-replay');
} finally {
  publisher?.kill();
  if (room) { await op(`/rooms/${room}/talks/stop`, 'POST'); await sleep(500); await op(`/rooms/${room}`, 'DELETE'); }
  if (talkId) await op(`/talks/${talkId}`, 'DELETE');
  browser.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still hold it */ }
}
console.log(failures.length ? `\n${failures.length} failed` : '\nAll passed');
process.exit(failures.length ? 1 : 0);
