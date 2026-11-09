// Two talks at the same time against a running server: checks that each room keeps its own captions, transcript
// and answers, and that stopping one leaves the other alone. Runs throwaway talks on the configured providers.
// Usage: node rooms.mjs <clip for room A> <clip for room B> [path to the running copy]
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as signalR from '@microsoft/signalr';

const here = dirname(fileURLToPath(import.meta.url));
const [clipA, clipB, root = join(here, '..', '..')] = process.argv.slice(2);
const base = process.env.TALKLENS_URL ?? 'http://localhost:8080';
const key = readFileSync(join(root, 'data', 'operator.key'), 'utf8').trim();
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
const talkBody = (title) => ({ title, speaker: 'Probe', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter: false, audioSource: 'camera', slideSource: 'none' });
const publish = (file, room) => spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-i', file, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', `rtsp://127.0.0.1:8554/live/r${room}/cam`], { stdio: 'inherit' });

async function attendee(room) {
  const hub = new signalR.HubConnectionBuilder().withUrl(`${base}/hub`).configureLogging(signalR.LogLevel.Warning).build();
  const seen = { captions: [], started: 0, ended: 0 };
  hub.on('caption', (c) => { if (c.final) seen.captions.push(c.id); });
  hub.on('talkStarted', () => { seen.started++; });
  hub.on('talkEnded', () => { seen.ended++; });
  for (const quiet of ['rooms', 'translation', 'slide', 'slideText', 'captionMode']) hub.on(quiet, () => {});
  await hub.start();
  await hub.invoke('JoinRoom', room, '');
  return { hub, seen };
}

check('anonymous callers cannot add rooms', (await fetch(`${base}/api/op/rooms`, { method: 'POST' })).status === 401);
check('anonymous WHIP publish is rejected', (await fetch(`${base}/media/live/r1/cam/whip`, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: 'v=0' })).status === 401);
check('a room slide stream is not public', (await fetch(`${base}/media/live/r1/slides/whep`, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: 'v=0' })).status === 401);

const a = await (await op('/rooms', 'POST', { name: 'Probe A' })).json();
const b = await (await op('/rooms', 'POST', { name: 'Probe B' })).json();
const watcherA = await attendee(a.id);
const watcherB = await attendee(b.id);

const startedA = await op(`/rooms/${a.id}/talks/start`, 'POST', talkBody('Talk A'));
const startedB = await op(`/rooms/${b.id}/talks/start`, 'POST', talkBody('Talk B'));
check('two talks start at the same time', startedA.ok && startedB.ok);
check('a second talk in the same room is refused', (await op(`/rooms/${a.id}/talks/start`, 'POST', talkBody('Again'))).status === 400);
check('a room that does not exist is refused', (await op('/rooms/999999/talks/start', 'POST', talkBody('Nope'))).status === 400);
await sleep(watcherA.seen.started && watcherB.seen.started ? 0 : 500);
check('each room hears only its own start', watcherA.seen.started === 1 && watcherB.seen.started === 1, `${watcherA.seen.started}, ${watcherB.seen.started}`);

const home = await (await fetch(`${base}/api/rooms`)).json();
const liveNames = home.rooms.filter((r) => r.live).map((r) => r.talk.title);
check('the home list shows both as live', liveNames.includes('Talk A') && liveNames.includes('Talk B'), liveNames.join(', '));
check('the home list counts people per room', home.rooms.find((r) => r.id === a.id).watching === 1 && home.rooms.find((r) => r.id === b.id).watching === 1);

const pubA = publish(clipA, a.id);
const pubB = publish(clipB, b.id);
await sleep(10000);
const live = await (await op('/status')).json();
const liveA = live.rooms.find((r) => r.id === a.id);
const liveB = live.rooms.find((r) => r.id === b.id);
console.log(`Laptop with two rooms: server CPU ${live.server.cpuPercent}%, ${live.server.memoryMb} MB`);
check('both cameras reach the relay', liveA.camera?.ready && liveB.camera?.ready, `${liveA.camera?.tracks} | ${liveB.camera?.tracks}`);
check('both rooms receive audio', liveA.talk?.audio.receiving && liveB.talk?.audio.receiving);

await sleep(30000);
const transcriptA = await (await fetch(`${base}/api/rooms/${a.id}/transcript`)).json();
const transcriptB = await (await fetch(`${base}/api/rooms/${b.id}/transcript`)).json();
const idsA = transcriptA.segments.map((s) => s.id);
const idsB = transcriptB.segments.map((s) => s.id);
console.log('Room A heard:', transcriptA.segments.map((s) => s.text).join(' ').slice(0, 200));
console.log('Room B heard:', transcriptB.segments.map((s) => s.text).join(' ').slice(0, 200));
check('both rooms produce captions', idsA.length > 0 && idsB.length > 0, `${idsA.length}, ${idsB.length}`);
check('transcripts do not mix', !idsA.some((id) => idsB.includes(id)));
check('attendees only get their room\'s captions',
  watcherA.seen.captions.every((id) => idsA.includes(id)) && watcherB.seen.captions.every((id) => idsB.includes(id)) && watcherA.seen.captions.length > 0,
  `${watcherA.seen.captions.length}, ${watcherB.seen.captions.length}`);

const catchUp = await fetch(`${base}/api/rooms/${b.id}/catchup`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ minutes: 2, language: 'es' }) });
const summary = await catchUp.json();
check('a talk can be caught up on from its room', catchUp.ok && summary.citations.every((c) => c.t >= 0), summary.mode);

pubA.kill();
await op(`/rooms/${a.id}/talks/stop`, 'POST');
await sleep(500);
const after = await (await fetch(`${base}/api/rooms`)).json();
check('stopping one room leaves the other live', !after.rooms.find((r) => r.id === a.id).live && after.rooms.find((r) => r.id === b.id).live);
check('only that room hears the end', watcherA.seen.ended === 1 && watcherB.seen.ended === 0);

const refused = await op(`/rooms/${b.id}`, 'DELETE');
check('a room with a live talk cannot be removed', refused.status === 400);

pubB.kill();
await op(`/rooms/${b.id}/talks/stop`, 'POST');
check('an empty room can be removed', (await op(`/rooms/${a.id}`, 'DELETE')).ok && (await op(`/rooms/${b.id}`, 'DELETE')).ok);
await watcherA.hub.stop();
await watcherB.hub.stop();

console.log(failures.length ? `\n${failures.length} check(s) failed: ${failures.join('; ')}` : '\nAll checks passed.');
process.exit(failures.length ? 1 : 0);
