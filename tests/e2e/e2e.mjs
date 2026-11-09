// End-to-end run against a running server: publishes the demo talk, then checks captions, slides,
// questions and access control, and measures caption delay against the known speech timing.
// Usage: node e2e.mjs [--fail-at=SECONDS]   (needs make-demo-talk.ps1 output and `npm install` here)
import { spawn } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as signalR from '@microsoft/signalr';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const base = process.env.TALKLENS_URL ?? 'http://localhost:8080';
const key = readFileSync(join(root, 'data', 'operator.key'), 'utf8').trim();
const cookie = `talklens_operator=${key}`;
const timing = JSON.parse(readFileSync(join(here, 'out', 'timing.json'), 'utf8').replace(/^﻿/, ''));
const video = join(here, 'out', 'talk.mp4');
const duration = Number(process.env.E2E_DURATION ?? 35.4);
const failures = [];

function check(name, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`);
  if (!ok) failures.push(name);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const op = (path, options = {}) => fetch(`${base}/api/op${path}`, { ...options, headers: { Cookie: cookie, ...(options.headers ?? {}) } });
const post = (path, body, headers = {}) => fetch(base + path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', ...headers },
  body: JSON.stringify(body),
});
const percentile = (values, p) => [...values].sort((a, b) => a - b)[Math.min(values.length - 1, Math.floor(values.length * p))];
const normalize = (s) => s.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9 ]/g, '');

// ----- Access control -----
check('operator API rejects anonymous callers', (await fetch(`${base}/api/op/status`)).status === 401);
check('anonymous WHIP publish is rejected', (await fetch(`${base}/media/live/r1/cam/whip`, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: 'v=0' })).status === 401);
check('slide stream is not public', (await fetch(`${base}/media/live/r1/slides/whep`, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: 'v=0' })).status === 401);
check('unknown media paths are refused', (await fetch(`${base}/media/v3/config/global/get`)).status === 404);

const refused = await op('/rooms/1/talks/start', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ title: 'x', speaker: 'x', language: 'es', organizerAuthorized: true, speakerAuthorized: false, keepAfter: true, audioSource: 'camera', slideSource: 'none' }),
});
check('a talk cannot start without both authorizations', refused.status === 400);

// ----- Attendee connection -----
const hub = new signalR.HubConnectionBuilder().withUrl(`${base}/hub`).configureLogging(signalR.LogLevel.Warning).build();
const finals = [];
const partials = [];
const slides = [];
const translations = [];
const modes = [];
let t0 = 0;
hub.on('caption', (c) => { (c.final ? finals : partials).push({ ...c, at: Date.now() - t0 }); });
hub.on('slide', (s) => { slides.push({ ...s, at: Date.now() - t0 }); });
hub.on('translation', (t) => { translations.push(t); });
hub.on('talkStarted', () => {});
hub.on('talkEnded', () => {});
hub.on('rooms', () => {});
hub.on('questions', () => {});
hub.on('slideText', () => {});
hub.on('captionMode', (m) => { modes.push({ mode: m, at: Date.now() - t0 }); });
await hub.start();
await hub.invoke('JoinRoom', 1, process.env.E2E_TRANSLATE ?? '');

// ----- Talk -----
const started = await op('/rooms/1/talks/start', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ title: 'Demo talk', speaker: 'Synthetic speaker', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter: true, audioSource: 'camera', slideSource: 'camera' }),
});
check('talk starts', started.ok);
const talk = await started.json();

// Two passes: the first warms everything up, measurements use the second.
const publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-progress', 'pipe:1', '-stats_period', '0.2', '-re', '-stream_loop', '1', '-i', video, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', 'rtsp://127.0.0.1:8554/live/r1/cam'], { stdio: ['ignore', 'pipe', 'inherit'] });
t0 = Date.now();
// Anchor the clock on the first media ffmpeg reports sent, not on process start.
let anchored = false;
publisher.stdout.on('data', (chunk) => {
  const match = /out_time_us=(\d+)/.exec(String(chunk));
  if (!anchored && match && Number(match[1]) > 0) {
    anchored = true;
    t0 = Date.now() - Number(match[1]) / 1000;
  }
});
await sleep(duration * 1000);
const liveStatus = await (await op('/status')).json();
await sleep((duration + 7) * 1000);
publisher.kill();

const pass2 = duration * 1000;
const secondFinals = finals.filter((f) => f.at > pass2);

// RNF-03: delay from the end of each spoken sentence to its final caption reaching this client.
const lags = [];
const firstWordLags = [];
for (const sentence of timing) {
  const words = normalize(sentence.text).split(' ').filter((w) => w.length > 3);
  const spokenEnd = pass2 + sentence.end * 1000;
  const spokenStart = pass2 + sentence.start * 1000;
  const match = secondFinals.find((f) => f.at > spokenEnd - 1500 && words.filter((w) => normalize(f.text).includes(w)).length >= Math.max(1, words.length / 3));
  if (match) lags.push(match.at - spokenEnd);
  const firstPartial = partials.find((p) => p.at > spokenStart);
  if (firstPartial) firstWordLags.push(firstPartial.at - spokenStart);
}

console.log('\nCaptions heard in the second pass:');
secondFinals.forEach((f) => console.log(`  ${(f.at / 1000).toFixed(1)}s [${f.source}] ${f.text}`));
check('captions were produced', secondFinals.length >= timing.length / 2, `${secondFinals.length} final captions`);
check('most sentences were matched', lags.length >= timing.length * 0.6, `${lags.length}/${timing.length}`);
if (lags.length) {
  const p50 = percentile(lags, 0.5);
  const max = Math.max(...lags);
  console.log(`End of sentence to final caption: p50 ${p50} ms, max ${max} ms`);
  console.log(`Start of sentence to first partial: p50 ${percentile(firstWordLags, 0.5)} ms`);
  check('RNF-01: nothing later than 5 s', max <= 5000, `max ${max} ms`);
}

// RF-06: one slide per change, near its real time.
console.log(`\nSlides detected at: ${slides.map((s) => (s.at / 1000).toFixed(1) + 's').join(', ')}`);
const slide2At = timing[3].start;
const slide3At = timing[6].start;
const near = (seconds) => slides.some((s) => s.at / 1000 >= seconds && s.at / 1000 - seconds < 3.5);
check('slide change detected: diagram', near(slide2At) && near(duration + slide2At));
check('slide change detected: privacy', near(slide3At) && near(duration + slide3At));
check('no extra slides from video noise', slides.length <= 7, `${slides.length} slides`);

const image = slides.length ? await fetch(`${base}/slides/${talk.id}/${slides[0].id}.jpg`) : null;
check('slide image is served', image?.ok && image.headers.get('content-type') === 'image/jpeg');

// RF-08
const transcript = await (await fetch(`${base}/api/rooms/1/transcript`)).json();
check('transcript is available to late joiners', transcript.segments.length > 0, `${transcript.segments.length} segments`);

const started2 = Date.now();
const answer = await (await post('/api/rooms/1/ask', { question: process.env.E2E_QUESTION ?? '¿Qué dijo sobre el router y los celulares?' })).json();
console.log('\nAnswer:', JSON.stringify(answer, null, 1));
check('answer cites a real moment', answer.citations.length > 0 && answer.citations.every((c) => c.t >= 0), answer.mode);
check('RNF-05: answer under 10 s', Date.now() - started2 < 10000, `${Date.now() - started2} ms`);

const unknown = await (await post('/api/rooms/1/ask', { question: 'xylofono zanahoria' })).json();
check('unknown topics are not invented', unknown.mode === 'not_found', unknown.mode);

const catchUp = await (await post('/api/rooms/1/catchup', { minutes: 2, language: 'es' })).json();
check('catch up returns cited text', catchUp.citations.length > 0, catchUp.mode);
console.log('Catch up:', catchUp.text);

const tooLong = await post('/api/rooms/1/ask', { question: 'x'.repeat(501) });
check('oversized questions are rejected', tooLong.status === 400);

if (process.env.E2E_TRANSLATE) check('translations arrive', translations.length > 0, `${translations.length}, e.g. ${translations[0]?.text}`);
if (modes.length) console.log('Caption modes:', modes.map((m) => `${m.mode}@${(m.at / 1000).toFixed(1)}s`).join(' '));

const status = await (await op('/status')).json();
console.log('\nOperator status sample:', JSON.stringify(status.rooms[0].talk?.captions), JSON.stringify(status.rooms[0].talk?.slides), JSON.stringify(liveStatus.rooms[0].camera), JSON.stringify(liveStatus.server));
check('operator sees the talk', status.rooms[0].talk?.talk?.id === talk.id);
check('video reached MediaMTX with tracks', liveStatus.rooms[0].camera?.ready === true, liveStatus.rooms[0].camera?.tracks);

await op('/rooms/1/talks/stop', { method: 'POST' });
await hub.stop();

console.log(failures.length ? `\n${failures.length} check(s) failed: ${failures.join('; ')}` : '\nAll checks passed.');
process.exit(failures.length ? 1 : 0);
