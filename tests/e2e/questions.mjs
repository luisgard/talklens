// The queue of questions for the speaker, against a running server, in a throwaway room. Uses the configured chat model to
// group questions, and drives the real pages in headless Chrome. Set CHROME if it is not in the usual place.
// Usage: node questions.mjs [path to the running copy] [folder for screenshots]
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [root = join(here, '..', '..'), shots] = process.argv.slice(2);
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
const ask = (room, question) => fetch(`${base}/api/rooms/${room}/questions`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question }) });
const groups = async (room) => (await op(`/rooms/${room}/questions`)).json();
const talkBody = (keepAfter) => ({ title: 'Queue probe', speaker: 'Probe', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter, audioSource: 'camera', slideSource: 'none' });

const profile = mkdtempSync(join(tmpdir(), 'tl-edge-'));
const port = 9335;
// A Spanish browser, to prove the app still starts in English.
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--lang=es-MX', 'about:blank'], { stdio: 'ignore' });

async function openTab(url, width, height, mobile) {
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
  await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: mobile ? 2 : 1, mobile });
  await call('Page.navigate', { url });
  const ev = async (expression) => (await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })).result?.result?.value;
  const shot = async (name) => {
    if (!shots) return;
    mkdirSync(shots, { recursive: true });
    writeFileSync(join(shots, name + '.png'), Buffer.from((await call('Page.captureScreenshot', { format: 'png' })).result.data, 'base64'));
  };
  return { ev, shot };
}

const room = (await (await op('/rooms', 'POST', { name: 'Queue probe' })).json()).id;
const other = (await (await op('/rooms', 'POST', { name: 'Other probe' })).json()).id;
try {
  check('the queue is private to the operator', (await fetch(`${base}/api/op/rooms/${room}/questions`)).status === 401);
  check('moderating needs the operator key', (await fetch(`${base}/api/op/rooms/${room}/questions/1/status`, { method: 'POST' })).status === 401);
  check('the speaker screen needs the operator key', !(await (await fetch(`${base}/speaker?room=${room}`)).text()).includes('speaker.js'));
  check('nothing can be sent when no talk is live', (await ask(room, 'Anyone there?')).status === 409);

  await op(`/rooms/${room}/talks/start`, 'POST', talkBody(false));
  await op(`/rooms/${other}/talks/start`, 'POST', talkBody(false));

  check('an empty question is refused', (await ask(room, '   ')).status === 400);
  check('an oversized question is refused', (await ask(room, 'x'.repeat(501))).status === 400);

  check('a question is accepted', (await ask(room, 'How much does the event router cost?')).ok);
  await sleep(300);
  check('it reaches the moderator at once', (await groups(room)).length === 1);
  check('it stays in its own room', (await groups(other)).length === 0);

  await ask(room, 'how much does the event router cost');
  await sleep(1500);
  await ask(room, '¿Cuál es el precio de ese router?');
  await sleep(6000);
  await ask(room, 'Will the slides be shared after the talk?');
  // The model takes its time, more so on a laptop that is also serving everything else.
  let grouped = await groups(room);
  for (let i = 0; i < 25 && !(grouped[0].count === 3 && grouped.length === 2); i++) {
    await sleep(1000);
    grouped = await groups(room);
  }
  console.log(grouped.map((g) => `  ${g.count}x ${g.text}${g.variants.length ? ' | ' + g.variants.join(' | ') : ''}`).join('\n'));
  check('repeats and translations group together', grouped[0].count === 3, `${grouped[0].count} in the top group`);
  check('a different question stays apart', grouped.length === 2);

  // Real pages
  const home = await openTab(`${base}/`, 390, 844, true);
  const heroTitle = async () => {
    for (let i = 0; i < 16; i++) {
      await sleep(500);
      const title = await home.ev(`document.querySelector('.hero h1')?.textContent ?? ''`).catch(() => '');
      if (title) return title.replace(/\s+/g, ' ');
    }
    return '';
  };
  const homeTitle = await heroTitle();
  check('home starts in English on a Spanish browser', homeTitle === 'Every talk. In your pocket.', homeTitle);

  const attendee = await openTab(`${base}/r/${room}?tab=ask`, 390, 844, true);
  await sleep(2000);
  check('the room starts in English too', (await attendee.ev(`document.getElementById('tab-ask').textContent.trim()`)) === 'Ask');
  check('the AI is the default destination', (await attendee.ev(`document.querySelector('[name=ask-target]:checked').value`)) === 'ai');
  check('the page says the AI question is private', /Private/.test(await attendee.ev(`document.getElementById('composer-note').textContent`)));
  await attendee.shot('ask-ai');
  await attendee.ev(`document.querySelector('[name=ask-target][value=speaker]').click()`);
  check('choosing the speaker says who will see it', /moderator/.test(await attendee.ev(`document.getElementById('composer-note').textContent`)));
  await attendee.ev(`document.getElementById('question').value = 'Which router model do you recommend for 100 people?'`);
  await attendee.ev(`document.getElementById('ask-form').requestSubmit()`);
  await sleep(1500);
  check('the attendee is told it was sent', /Sent to the moderator/.test(await attendee.ev(`document.querySelector('.answer').textContent`)));
  await attendee.shot('ask-speaker');
  const beforeAi = (await groups(room)).reduce((sum, g) => sum + g.count, 0);
  await attendee.ev(`document.querySelector('[name=ask-target][value=ai]').click()`);
  await attendee.ev(`document.getElementById('question').value = 'What was said about the router?'`);
  await attendee.ev(`document.getElementById('ask-form').requestSubmit()`);
  await sleep(6000);
  check('a question to the AI does not reach the queue', (await groups(room)).reduce((sum, g) => sum + g.count, 0) === beforeAi);
  check('it can be sent on with a button', (await attendee.ev(`document.querySelector('.answer-forward')?.textContent`)) === 'Send to the speaker');
  await attendee.ev(`document.querySelector('.answer-forward').click()`);
  await sleep(1000);
  check('only that deliberate action forwards it', (await groups(room)).reduce((sum, g) => sum + g.count, 0) === beforeAi + 1);

  await attendee.ev(`document.getElementById('set-ui').value = 'es'; document.getElementById('set-ui').dispatchEvent(new Event('change'))`);
  await sleep(2000);
  check('choosing Spanish changes the app', (await attendee.ev(`document.getElementById('tab-ask').textContent.trim()`)) === 'Preguntar');
  await home.ev(`location.reload()`);
  await sleep(500);
  check('the choice carries to the home page', /Todas las charlas/.test(await heroTitle()));

  const panel = await openTab(`${base}/operator?key=${key}#room=${room}`, 1280, 1000, false);
  await sleep(1000);
  await panel.ev(`location.hash = 'room=${room}'`);
  await sleep(3000);
  check('the panel lists the queue', (await panel.ev(`document.querySelectorAll('#questions .question').length`)) >= 2);
  await panel.ev(`document.getElementById('questions-card').scrollIntoView()`);
  await sleep(300);
  await panel.shot('panel-queue');

  const top = (await groups(room))[0];
  const speaker = await openTab(`${base}/speaker?room=${room}`, 1280, 720, false);
  await sleep(1500);
  check('the speaker screen starts empty', (await speaker.ev(`document.getElementById('question').textContent`)) === 'No question right now');
  await panel.ev(`[...document.querySelectorAll('#questions .question')].find((q) => q.textContent.includes(${JSON.stringify(top.text)})).querySelector('button.primary').click()`);
  await sleep(1500);
  check('the chosen question reaches the speaker screen', (await speaker.ev(`document.getElementById('question').textContent`)) === top.text);
  check('with how many asked it', /Asked by \d+ people/.test(await speaker.ev(`document.getElementById('asked').textContent`)));
  check('and no other question', !(await speaker.ev(`document.body.textContent`)).includes('slides'));
  await speaker.shot('speaker-screen');
  await panel.ev(`[...document.querySelectorAll('#questions .question')].find((q) => q.textContent.includes(${JSON.stringify(top.text)})).querySelectorAll('button')[1].click()`);
  await sleep(1500);
  check('answering takes it off the speaker screen', (await speaker.ev(`document.getElementById('question').textContent`)) === 'No question right now');
  check('and out of the queue', (await groups(room)).find((g) => g.id === top.id).status === 'answered');

  const status = await (await op('/status')).json();
  check('the room tile counts what is left', status.rooms.find((r) => r.id === room).openQuestions >= 1);

  await op(`/rooms/${room}/talks/stop`, 'POST');
  check('a talk not kept loses its questions too', (await groups(room)).length === 0);
} catch (e) {
  check('the run finished', false, String(e));
}
await op(`/rooms/${room}/talks/stop`, 'POST');
await op(`/rooms/${other}/talks/stop`, 'POST');
await op(`/rooms/${room}`, 'DELETE');
await op(`/rooms/${other}`, 'DELETE');
browser.kill();
await sleep(500);
try { rmSync(profile, { recursive: true, force: true }); } catch { /* still closing */ }
console.log(failures.length ? `\n${failures.length} check(s) failed: ${failures.join('; ')}` : '\nAll checks passed.');
process.exit(failures.length ? 1 : 0);
