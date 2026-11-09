// Polls, ratings, the glossary and the speaker's pace, against a running server, in headless Chrome.
// Usage: node engagement.mjs <video with speech> [path to the running copy] [folder for screenshots]
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
const port = 9337;
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });

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

let room;
let talkId;
let publisher;
try {
  room = (await (await op('/rooms', 'POST', { name: 'Engagement probe' })).json()).id;
  talkId = (await (await op(`/rooms/${room}/talks/start`, 'POST', { title: 'Engagement probe', speaker: 'Probe', language: 'en', organizerAuthorized: true, speakerAuthorized: true, keepAfter: true, audioSource: 'camera', slideSource: 'none' })).json()).id;
  publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-stream_loop', '-1', '-i', video, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', `rtsp://127.0.0.1:8554/live/r${room}/cam`], { stdio: 'inherit' });

  const phone = await openTab(`${base}/r/${room}`);
  await phone.until(`!!localStorage.getItem('talklens.device')`, 10000);
  const device = await phone.ev(`JSON.parse(localStorage.getItem('talklens.device'))`);

  // ----- A poll -----
  const minimum = (await (await op('/event')).json()).minParticipants ?? 5;
  const opened = await op(`/rooms/${room}/polls`, 'POST', { question: 'What do you deploy with?', options: ['Kubernetes', 'A plain server'] });
  check('the operator opens a poll', opened.ok);
  const poll = await opened.json();
  check('it reaches the phone at once', await phone.until(`document.querySelectorAll('#poll-card .poll-option').length === 2`, 8000));
  await phone.ev(`document.querySelectorAll('#poll-card .poll-option')[1].click(); true`);
  check('the phone votes', await phone.until(`document.querySelector('#poll-card .poll-bar.chosen') !== null`, 5000));
  check('with few votes the numbers stay hidden', await phone.ev(`!/%/.test(document.getElementById('poll-card').innerText)`));
  for (let i = 1; i < minimum; i++) await post(`/api/polls/${poll.id}/vote`, { voter: `probe-voter-${i}`, choice: i % 2 });
  check('enough votes show the results live', await phone.until(`/%/.test(document.getElementById('poll-card').innerText)`, 8000));
  const polls = await (await op(`/rooms/${room}/polls`)).json();
  const total = polls[0]?.counts.reduce((a, b) => a + b, 0);
  check('the operator sees every vote', total === minimum, `${total}`);
  await phone.shot('poll-phone');
  await op(`/polls/${poll.id}/close`, 'POST');
  check('closing it tells the phone', await phone.until(`document.querySelector('#poll-card .poll-head')?.textContent !== '' && !document.querySelector('#poll-card [role=button]')`, 8000));
  check('a closed poll takes no votes', (await post(`/api/polls/${poll.id}/vote`, { voter: device, choice: 0 })).status === 409);

  // ----- The speaker's pace -----
  await phone.ev(`document.getElementById('join')?.click(); true`);
  check('captions arrive', await phone.until(`document.getElementById('captions').innerText.trim().length > 0`, 45000));
  await sleep(22000);
  const pace = await (await op(`/rooms/${room}/pace`)).json();
  check('the speaker screen gets the pace', pace.live && pace.wordsPerMinute > 0, `${pace.wordsPerMinute} words a minute, ${pace.behindMs} ms behind`);

  // ----- Terms explained, in the room -----
  await phone.ev(`document.querySelector('[data-tab=ask], #tab-ask')?.click(); document.getElementById('glossary-chip').click(); true`);
  check('the room explains its terms', await phone.until(`[...document.querySelectorAll('.glossary dt, .glossary b, .glossary strong')].length > 0 || /not available/.test(document.getElementById('answers')?.innerText ?? '')`, 60000));
  check('with the AI on, they are real terms', await phone.ev(`!/not available/.test(document.getElementById('answers').innerText)`));

  // ----- Rating, once the talk is over -----
  await op(`/rooms/${room}/talks/stop`, 'POST');
  check('the end of the talk asks for a rating', await phone.until(`!document.getElementById('rate-card').hidden && document.querySelectorAll('#rate-card .star').length === 5`, 15000));
  await phone.ev(`document.querySelectorAll('#rate-card .star')[3].click(); document.querySelector('#rate-card textarea').value = 'Clear and useful'; true`);
  await phone.ev(`[...document.querySelectorAll('#rate-card button.primary')].at(-1).click(); true`);
  await phone.shot('rating-phone');
  const mine = async () => (await (await fetch(`${base}/api/talks/${talkId}/rating?voter=${device}`)).json()).mine;
  let rated;
  for (let i = 0; i < 20 && !rated; i++, await sleep(300)) rated = await mine();
  check('the rating is kept', (rated?.stars ?? rated) === 4, JSON.stringify(rated));
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
