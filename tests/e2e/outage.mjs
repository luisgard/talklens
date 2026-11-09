// RNF-06: with the fake provider, cuts "internet" mid-talk and checks captions never stop and come back to the cloud.
// Needs the server running with Ai__* pointing at fake-ai.mjs (see README).
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as signalR from '@microsoft/signalr';

const here = dirname(fileURLToPath(import.meta.url));
const base = 'http://localhost:8080';
const fake = 'http://127.0.0.1:9100';
const key = readFileSync(join(here, '..', '..', 'data', 'operator.key'), 'utf8').trim();
const headers = { Cookie: `talklens_operator=${key}`, 'Content-Type': 'application/json' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`);
  if (!ok) failures.push(name);
};

const hub = new signalR.HubConnectionBuilder().withUrl(`${base}/hub`).configureLogging(signalR.LogLevel.Error).build();
const captions = [];
const modes = [];
const translations = [];
const slideTexts = [];
let t0 = Date.now();
hub.on('caption', (c) => { if (c.final) captions.push({ at: Date.now() - t0, source: c.source, text: c.text }); });
hub.on('captionMode', (m) => { modes.push({ at: Date.now() - t0, mode: m }); });
hub.on('translation', (t) => { translations.push(t); });
hub.on('slideText', (s) => { slideTexts.push(s); });
for (const name of ['slide', 'talkStarted', 'talkEnded', 'publicStatus']) hub.on(name, () => {});
await hub.start();
await hub.invoke('JoinRoom', 1, 'en');

await fetch(`${base}/api/op/rooms/1/talks/start`, {
  method: 'POST',
  headers,
  body: JSON.stringify({ title: 'Outage test', speaker: 'Synthetic', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter: false, audioSource: 'camera', slideSource: 'camera' }),
});
const publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-stream_loop', '2', '-i', join(here, 'out', 'talk.mp4'), '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', 'rtsp://127.0.0.1:8554/live/r1/cam'], { stdio: 'inherit' });
t0 = Date.now();

await sleep(20000);
const answer = await (await fetch(`${base}/api/rooms/1/ask`, { method: 'POST', headers, body: JSON.stringify({ question: '¿qué se dijo?' }) })).json();
check('online answers come from the model', answer.mode === 'ai', answer.text);

const downAt = Date.now() - t0;
await fetch(`${fake}/control/down?seconds=25`, { method: 'POST' });
await sleep(14000);
const offline = await (await fetch(`${base}/api/rooms/1/ask`, { method: 'POST', headers, body: JSON.stringify({ question: 'hablemos de privacidad y costos' }) })).json();
check('offline answers fall back to excerpts', offline.mode === 'offline' || offline.mode === 'not_found', offline.mode);
await sleep(28000);

publisher.kill();
const stats = await (await fetch(`${fake}/stats`)).json();
await fetch(`${base}/api/op/rooms/1/talks/stop`, { method: 'POST', headers });
await hub.stop();

console.log('Modes:', modes.map((m) => `${m.mode}@${(m.at / 1000).toFixed(1)}s`).join(' '), `(outage at ${(downAt / 1000).toFixed(1)}s)`);
const toLocal = modes.find((m) => m.mode === 'local' && m.at > downAt);
const backToCloud = toLocal && modes.find((m) => m.mode === 'cloud' && m.at > toLocal.at);
check('falls back to the local model', !!toLocal, toLocal ? `${((toLocal.at - downAt) / 1000).toFixed(1)} s after the outage` : '');
check('returns to the cloud after reconnecting', !!backToCloud, backToCloud ? `${((backToCloud.at - downAt) / 1000).toFixed(1)} s after the outage` : '');

const during = captions.filter((c) => toLocal && c.at > toLocal.at && (!backToCloud || c.at < backToCloud.at));
check('captions keep flowing during the outage', during.length > 0 && during.every((c) => c.source === 'local'), `${during.length} local captions`);
if (backToCloud) check('cloud captions resume', captions.some((c) => c.at > backToCloud.at && c.source === 'cloud'));

const quietest = captions.reduce((worst, c, i) => (i && c.at - captions[i - 1].at > worst ? c.at - captions[i - 1].at : worst), 0);
console.log(`Longest gap between captions: ${(quietest / 1000).toFixed(1)} s`);
check('translations arrive while online', translations.length > 0, translations[0]?.text);
check('slide text comes from the vision model', slideTexts.length > 0 && stats.vision > 0, `${stats.vision} vision calls`);

console.log(failures.length ? `\n${failures.length} check(s) failed: ${failures.join('; ')}` : '\nAll checks passed.');
process.exit(failures.length ? 1 : 0);
