// The event around the talks, against a running server: tickets, door, contacts, sponsors, reports, library, chat, outside access,
// going back 2 minutes and clips. Removes what it creates. Needs ffmpeg on the PATH and a video to publish. Set CHROME if needed.
// Usage: node event.mjs <path to the running copy> <video to publish> [folder for screenshots]
import { spawn, spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [root, video, shots] = process.argv.slice(2);
const base = process.env.TALKLENS_URL ?? 'http://localhost:8080';
const key = readFileSync(join(root, 'data', 'operator.key'), 'utf8').trim();
const chrome = process.env.CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const failures = [];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function check(name, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`);
  if (!ok) failures.push(name);
}

const json = { 'Content-Type': 'application/json' };
const op = (path, method = 'GET', body) => fetch(`${base}/api/op${path}`, {
  method,
  headers: { Cookie: `talklens_operator=${key}`, ...json },
  body: body === undefined ? undefined : JSON.stringify(body),
});
const opJson = async (...args) => (await op(...args)).json();
const post = (path, body, headers = {}) => fetch(base + path, { method: 'POST', headers: { ...json, ...headers }, body: JSON.stringify(body) });
const asTicket = (token) => ({ 'X-Ticket': token });
const outside = { 'X-Forwarded-For': '203.0.113.9' };

const before = await opJson('/event');
const profile = mkdtempSync(join(tmpdir(), 'tl-edge-'));
const port = 9336;
// A Spanish browser, to prove the pages still start in English.
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--lang=es-MX', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });

async function openTab(url, width = 390, height = 844, mobile = true) {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })).json(); } catch { await sleep(250); }
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => { ws.onopen = r; });
  let id = 0;
  const pending = new Map();
  const errors = [];
  ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    if (pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
    if (d.method === 'Runtime.exceptionThrown') errors.push(d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text);
  };
  const call = (method, params = {}) => new Promise((r) => { const mine = ++id; pending.set(mine, r); ws.send(JSON.stringify({ id: mine, method, params })); });
  await call('Runtime.enable');
  await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: mobile ? 2 : 1, mobile });
  await call('Page.navigate', { url });
  const ev = async (expression) => (await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })).result?.result?.value;
  const shot = async (name) => {
    if (!shots) return;
    mkdirSync(shots, { recursive: true });
    writeFileSync(join(shots, name + '.png'), Buffer.from((await call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true })).result.data, 'base64'));
  };
  const until = async (expression, ms = 8000) => {
    for (const end = Date.now() + ms; Date.now() < end; await sleep(150)) if (await ev(expression)) return true;
    return false;
  };
  return { ev, shot, until, errors, close: () => ws.close() };
}

const attendees = [];
const person = async (name) => {
  const response = await post('/api/register', { name, email: `${name.toLowerCase().replace(/\W/g, '')}.${Date.now()}@example.com` });
  const body = await response.json();
  const list = await opJson('/attendees');
  const row = list.find((p) => p.token === body.token || p.name === name);
  attendees.push(row.id);
  return { token: body.token, id: row.id, email: row.email, name };
};

const room = (await opJson('/rooms', 'POST', { name: 'Event probe' })).id;
let publisher;
let talk;
let sponsor;
let agendaId;

try {
  // ----- Registration (RF-23) -----
  const ada = await person('Ada Lovelace');
  const alan = await person('Alan Turing');
  check('registering gives a ticket', /^[0-9a-f]{32}$/.test(ada.token));
  const again = await post('/api/register', { name: 'Impostor', email: ada.email });
  const againBody = await again.json();
  check('a registered email never returns its ticket', again.ok && againBody.existing === true && !('token' in againBody));
  check('a wrong email is refused', (await post('/api/register', { name: 'X', email: 'nope' })).status === 400);
  check('my data needs my ticket', (await fetch(`${base}/api/me`)).status === 401);
  const me = await (await fetch(`${base}/api/me`, { headers: asTicket(ada.token) })).json();
  check('my ticket shows only me', me.me.name === 'Ada Lovelace' && me.ticketQr.startsWith('<svg') || me.ticketQr.includes('<svg'));
  check('attendee list needs the operator', (await fetch(`${base}/api/op/attendees`)).status === 401);

  // ----- CSV (RF-26) -----
  const csv = await fetch(`${base}/api/op/import/csv`, { method: 'POST', headers: { Cookie: `talklens_operator=${key}`, 'Content-Type': 'text/csv' }, body: `name,email\n"=HYPERLINK(""http://evil"")",formula.${Date.now()}@example.com\nGrace Hopper,grace.${Date.now()}@example.com\n` });
  check('CSV imports people', (await csv.json()).created === 2);
  const everyone = await opJson('/attendees');
  everyone.filter((p) => p.name.startsWith('=') || p.name === 'Grace Hopper').forEach((p) => attendees.push(p.id));
  const exported = await (await op('/export/attendees.csv')).text();
  check('an exported name cannot run as a formula', exported.includes("'=HYPERLINK") && !/(^|,)"?=HYPERLINK/m.test(exported));

  // ----- A Luma guest list: only who is coming, in any spreadsheet's format, with no QR -----
  const uploadCsv = async (body) => (await fetch(`${base}/api/op/import/csv`, { method: 'POST', headers: { Cookie: `talklens_operator=${key}`, 'Content-Type': 'text/csv' }, body })).json();
  const lumaFile = `api_id,name,first_name,last_name,email,approval_status,checked_in_at\n`
    + `gst-a,José Pérez,,,Jose.${Date.now()}@EXAMPLE.com,approved,\ngst-b,Nope,,,nope.${Date.now()}@example.com,declined,\ngst-c,Later,,,later.${Date.now()}@example.com,waitlist,\ngst-d,No Mail,,,,approved,\n`;
  const imported = await uploadCsv(lumaFile);
  check('a Luma list adds only who is coming', imported.created === 1 && imported.notComing === 2 && imported.noEmail === 1, JSON.stringify(imported));
  check('uploading it again adds nobody', (await uploadCsv(lumaFile)).created === 0);
  const latin = await uploadCsv(Buffer.from(`nombre;correo\nMaría Ñáñez;maria.${Date.now()}@example.com\n`, 'latin1'));
  check('a Spanish Excel file with semicolons works', latin.created === 1, JSON.stringify(latin));
  check('a file with no email column says what to do', /email column/.test((await uploadCsv('name\nAda\n')).error ?? ''));
  check('the template downloads', (await (await op('/import/template.csv')).text()).startsWith('name,email'));
  const listed = await opJson('/attendees');
  const jose = listed.find((p) => p.name === 'José Pérez');
  const maria = listed.find((p) => p.name === 'María Ñáñez');
  check('accents survive and emails are tidied', !!maria && jose?.email === jose?.email.toLowerCase());
  listed.filter((p) => p.name === 'José Pérez' || p.name === 'María Ñáñez').forEach((p) => attendees.push(p.id));

  const door = await openTab(`${base}/checkin?key=${key}`);
  await door.until(`document.getElementById('count').textContent.includes('arrived')`);
  check('the door does not ask for the camera', await door.ev(`!document.getElementById('camera').srcObject`));
  const typeAtDoor = (text) => door.ev(`(() => { const s = document.getElementById('search'); s.value = ${JSON.stringify(text)}; s.dispatchEvent(new Event('input')); return 1; })()`);
  await typeAtDoor('jose perez');
  check('searching without accents finds the person', await door.until(`document.getElementById('matches').innerText.includes('José Pérez')`));
  await door.shot('door-search');
  await door.ev(`document.querySelector('#matches button.primary').click(); 1`);
  check('one tap checks them in', await door.until(`document.getElementById('result').textContent.includes('Welcome, José Pérez')`));
  check('the box is ready for the next person', await door.ev(`document.getElementById('search').value === ''`));
  check('a second phone is told they already came', (await (await op(`/attendees/${jose.id}/checkin`, 'POST')).json()).status === 'already');
  await typeAtDoor(jose.email);
  check('searching by email finds them, already arrived', await door.until(`document.getElementById('matches').innerText.includes('Arrived at')`));
  await typeAtDoor('nobody on this list');
  check('someone not on the list can be added', await door.until(`!document.getElementById('walk-in').hidden && document.getElementById('walk-name').value === 'nobody on this list'`));
  check('the door has no script errors', door.errors.length === 0, door.errors[0] ?? '');
  door.close();

  // ----- Door (RF-25) -----
  const scan = async (code, roomId = null) => (await opJson('/checkin', 'POST', { code, room: roomId })).status;
  check('scanning a ticket link checks in', (await scan(`http://192.168.0.2:8080/ticket/${ada.token}`)) === 'ok');
  check('scanning twice is told apart', (await scan(ada.token)) === 'already');
  check('an unknown code is refused', (await scan('https://example.com/whatever')) === 'unknown');
  check('check-in needs the operator', (await post('/api/op/checkin', { code: ada.token })).status === 401);

  // ----- Agenda and capacity (RF-22, RF-34) -----
  const agenda = await opJson('/agenda', 'POST', { roomId: room, title: 'Probe keynote', speaker: 'Grace', startsAt: Date.now() + 60000, endsAt: Date.now() + 120000, about: 'About it' });
  agendaId = agenda.id;
  const clash = await op('/agenda', 'POST', { roomId: room, title: 'Bad', startsAt: 2000, endsAt: 1000 });
  check('an agenda entry cannot end before it starts', clash.status === 400);

  // Find the right talk. Up to one sync after the change when Elasticsearch is running.
  let foundTalk = [];
  for (let i = 0; i < 15 && !foundTalk.some((hit) => hit.id === agendaId); i++) {
    foundTalk = await (await fetch(`${base}/api/event/search?q=probe%20keynote`)).json();
    if (!foundTalk.some((hit) => hit.id === agendaId)) await new Promise((r) => setTimeout(r, 1500));
  }
  check('the schedule can be searched', foundTalk.some((hit) => hit.id === agendaId));
  check('an empty search finds nothing', (await (await fetch(`${base}/api/event/search?q=`)).json()).length === 0);
  const engine = await (await op('/search-engine')).json();
  check('the organizer can see which search engine is on', typeof engine.elastic === 'boolean' && typeof engine.moments === 'number');
  check('attendees cannot see the search engine status', (await fetch(`${base}/api/op/search-engine`)).status !== 200);
  check('the story page is public', (await fetch(`${base}/welcome`)).ok);
  await op(`/rooms/${room}/limits`, 'PUT', { capacity: 0, markedFull: true });
  const summary = await (await fetch(`${base}/api/rooms`)).json();
  check('a room marked full says so', summary.rooms.find((r) => r.id === room).full === true);
  await op(`/rooms/${room}/limits`, 'PUT', { capacity: 0, markedFull: false });

  // ----- Contacts (RF-24): nothing is shared until the other person accepts -----
  const code = await (await post('/api/me/contact-code', {}, asTicket(ada.token))).json();
  const preview = await (await fetch(`${base}/api/contacts/preview/${code.code}`, { headers: asTicket(alan.token) })).json();
  check('scanning shows a first name, and no email yet', preview.firstName === 'Ada' && !JSON.stringify(preview).includes('@'));
  check('nobody can connect with themselves', (await post('/api/contacts', { code: code.code }, asTicket(ada.token))).status === 400);
  check('before accepting, nobody has the contact', (await (await fetch(`${base}/api/me`, { headers: asTicket(alan.token) })).json()).contacts.length === 0);
  check('accepting connects', (await post('/api/contacts', { code: code.code, room }, asTicket(alan.token))).ok);
  const contacts = (await (await fetch(`${base}/api/me`, { headers: asTicket(ada.token) })).json()).contacts;
  check('both see each other', contacts.length === 1 && contacts[0].email === alan.email);
  check('a used code stops working', (await fetch(`${base}/api/contacts/preview/${code.code}`, { headers: asTicket(alan.token) })).status === 404);

  // ----- Sponsors (RF-37): only those who accepted, and they can withdraw -----
  const created = await opJson('/sponsors', 'POST', { name: 'Acme', keywords: 'acme' });
  const sponsors = await opJson('/sponsors');
  sponsor = sponsors.find((s) => s.id === created.id);
  const portalPath = new URL(sponsor.portalUrl).pathname;
  const standCode = sponsor.code ?? created.code;
  const report = async () => (await fetch(`${base}/api/sponsor/${portalPath.split('/').pop()}`)).json();
  check('a sponsor starts with no contacts', (await report()).leads.length === 0);
  check('sharing needs a ticket', (await post(`/api/stands/${standCode}/share`, {})).status === 401);
  await post(`/api/stands/${standCode}/share`, {}, asTicket(ada.token));
  const leads = (await report()).leads;
  check('the sponsor sees only who accepted', leads.length === 1 && leads[0].email === ada.email);
  await fetch(`${base}/api/stands/${standCode}/share`, { method: 'DELETE', headers: asTicket(ada.token) });
  check('withdrawing removes it', (await report()).leads.length === 0);
  check('a wrong sponsor link shows nothing', (await fetch(`${base}/api/sponsor/nope`)).status === 404);

  // ----- A live talk: recording, chat, HLS -----
  publisher = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-stream_loop', '-1', '-re', '-i', video, '-c', 'copy', '-f', 'rtsp', '-rtsp_transport', 'tcp', `rtsp://127.0.0.1:8554/live/r${room}/cam`], { stdio: 'ignore' });
  await sleep(1500);
  const started = await opJson(`/rooms/${room}/talks/start`, 'POST', { title: 'Event probe talk', speaker: 'Grace', language: 'es', organizerAuthorized: true, speakerAuthorized: true, keepAfter: true, recordVideo: true, audioSource: 'camera', slideSource: 'none' });
  talk = started.id ?? started.talk?.id;
  check('a talk starts with video recording', !!talk);

  check('a question in chat is published', (await post(`/api/chat/${talk}`, { name: 'Ada', text: 'Hello everyone from Acme' })).ok);
  const chatList = await (await fetch(`${base}/api/chat/${talk}`)).json();
  check('chat is readable by the room', chatList.length === 1 && chatList[0].author === 'Ada');
  const secondChat = await (await post(`/api/chat/${talk}`, { name: '', text: 'remove me' })).json();
  check('chat without a name is a guest', secondChat.author.length > 0 && secondChat.author !== '');
  check('moderating chat needs the operator', (await fetch(`${base}/api/op/chat/${secondChat.id}?talk=${talk}`, { method: 'DELETE' })).status === 401);
  await op(`/chat/${secondChat.id}?talk=${talk}`, 'DELETE');
  check('moderation removes a message', (await (await fetch(`${base}/api/chat/${talk}`)).json()).length === 1);
  check('an empty chat message is refused', (await post(`/api/chat/${talk}`, { name: '', text: '  ' })).status === 400);

  let hlsOk = false;
  for (let i = 0; i < 30 && !hlsOk; i++) { hlsOk = (await fetch(`${base}/hls/r${room}/index.m3u8`)).ok; if (!hlsOk) await sleep(1000); }
  check('the room has an HLS copy for going back', hlsOk);

  // ----- Outside the venue (RF-13, RF-21) -----
  await op('/event', 'PUT', { remoteEnabled: false, remoteVideo: false });
  check('outside is closed by default', (await fetch(`${base}/api/event`, { headers: outside })).status === 403);
  check('operator pages are closed from outside', (await fetch(`${base}/operator?key=${key}`, { headers: outside })).status === 403);
  check('the operator key does not work from outside', (await fetch(`${base}/api/op/attendees`, { headers: { ...outside, Cookie: `talklens_operator=${key}` } })).status === 403);
  await op('/event', 'PUT', { remoteEnabled: true, remoteVideo: false });
  check('opening it lets captions through', (await fetch(`${base}/api/event`, { headers: outside })).ok);
  check('video stays inside until it is allowed apart', (await fetch(`${base}/hls/r${room}/index.m3u8`, { headers: outside })).status === 403);
  check('the operator stays closed when it is open', (await fetch(`${base}/api/op/attendees`, { headers: { ...outside, Cookie: `talklens_operator=${key}` } })).status === 403);
  await op('/event', 'PUT', { remoteVideo: true });
  check('video goes out only once allowed', (await fetch(`${base}/hls/r${room}/index.m3u8`, { headers: outside })).ok);
  await op('/event', 'PUT', { remoteEnabled: false, remoteVideo: false });

  // ----- API for platforms (RF-21) -----
  const apiKey = (await opJson('/api-key', 'POST')).key;
  check('the API needs its key', (await fetch(`${base}/api/v1/talks`)).status === 401);
  check('a wrong API key is refused', (await fetch(`${base}/api/v1/talks`, { headers: { 'X-Api-Key': 'wrong' } })).status === 401);
  check('the API answers with its key', (await fetch(`${base}/api/v1/talks`, { headers: { 'X-Api-Key': apiKey } })).ok);
  check('the API works from outside even when the venue is closed', (await fetch(`${base}/api/v1/rooms`, { headers: { 'X-Api-Key': apiKey, ...outside } })).ok);
  await op('/api-key', 'DELETE');
  check('turning off the API cancels its key', (await fetch(`${base}/api/v1/talks`, { headers: { 'X-Api-Key': apiKey } })).status === 401);
  check('with no key set, an empty one does not get in', (await fetch(`${base}/api/v1/talks`, { headers: { 'X-Api-Key': '' } })).status === 401);

  // ----- Pages (all of them, in English on a Spanish browser, with no script errors) -----
  const pages = [
    ['event', `${base}/event`, 'Agenda'],
    ['me', `${base}/me`, 'Get your free ticket'],
    ['library', `${base}/library`, 'Library'],
    ['community', `${base}/community`, 'Community'],
    ['embed', `${base}/embed?room=${room}&accent=ff5500`, 'Ask'],
    ['talk', `${base}/talk/${talk}`, 'The talk in one minute'],
    ['sponsor', `${base}${portalPath}`, 'Acme'],
  ];
  for (const [name, url, text] of pages) {
    const tab = await openTab(url);
    const shown = await tab.until(`document.body.innerText.includes(${JSON.stringify(text)})`);
    await tab.shot(`page-${name}`);
    const wide = await tab.ev('document.documentElement.scrollWidth > innerWidth + 1');
    check(`${name} loads in English`, shown);
    check(`${name} has no script errors`, tab.errors.length === 0, tab.errors[0] ?? '');
    check(`${name} fits a phone`, !wide);
    tab.close();
  }
  const panels = [
    ['start', `${base}/start?key=${key}`, 'Add your guest list'],
    ['manage', `${base}/manage?key=${key}#talks`, 'Add your talks'],
    ['organizer', `${base}/organizer?key=${key}`, 'People in rooms'],
    ['checkin', `${base}/checkin?key=${key}`, 'Find the person'],
    ['reports', `${base}/reports?key=${key}`, 'Feedback for the speaker'],
    ['operator', `${base}/operator?key=${key}#room=${room}`, 'Name the talk'],
  ];
  for (const [name, url, text] of panels) {
    const bare = await fetch(url.replace(/\?key=[^#]*/, '').replace(/#.*/, ''), { redirect: 'manual' });
    check(`${name} needs the operator key`, !(await bare.text()).includes(`/${name}.js`));
    const tab = await openTab(url, 1280, 1000, false);
    const shown = await tab.until(`document.body.innerText.includes(${JSON.stringify(text)})`);
    await sleep(800);
    await tab.shot(`panel-${name}`);
    check(`${name} loads`, shown);
    check(`${name} has no script errors`, tab.errors.length === 0, tab.errors[0] ?? '');
    tab.close();
  }

  // ----- A ticket link lands on the phone and leaves the address -----
  const phone = await openTab(`${base}/ticket/${alan.token}`);
  await phone.until(`document.body.innerText.includes('Your ticket')`);
  check('a ticket link stores the ticket', (await phone.ev(`localStorage.getItem('talklens.ticket')`)) === JSON.stringify(alan.token));
  check('and cleans the address', !(await phone.ev('location.href')).includes(alan.token));
  await phone.shot('me-ticket');
  check('my page has no script errors', phone.errors.length === 0, phone.errors[0] ?? '');
  phone.close();

  // ----- Spanish, when the person chooses it -----
  const es = await openTab(`${base}/event`);
  await es.until(`document.body.innerText.includes('Agenda')`);
  await es.ev(`localStorage.setItem('talklens.ui', '"es"'); location.href = '/me'; 1`);
  check('the choice of Spanish carries over', await es.until(`document.body.innerText.includes('Mi ticket')`));
  await es.ev(`localStorage.removeItem('talklens.ui'); 1`);
  es.close();

  // ----- The room: chat tab and going back 2 minutes (RF-30, RF-33) -----
  const roomTab = await openTab(`${base}/r/${room}`);
  await roomTab.until(`document.readyState === 'complete' && location.pathname.startsWith('/r/') && document.getElementById('live-pill').dataset.state === 'live'`);
  check('the room has a chat tab', await roomTab.until(`!!document.getElementById('tab-chat')`));
  check('room labels are real words', !(await roomTab.ev(`document.body.innerText`)).includes('tabChat'));
  await roomTab.shot('room-before-join');
  await roomTab.ev(`document.getElementById('join').click(); 1`);
  check('live video plays', await roomTab.until(`document.getElementById('video').currentTime > 0`, 15000));
  await roomTab.ev(`document.getElementById('tab-chat').click(); 1`);
  check('old chat is there', await roomTab.until(`document.getElementById('chat-box').innerText.includes('Hello everyone')`));
  await roomTab.shot('room-chat');
  await post(`/api/chat/${talk}`, { name: 'Alan', text: 'Arrives live' });
  check('a new message arrives live', await roomTab.until(`document.getElementById('chat-box').innerText.includes('Arrives live')`));
  await roomTab.ev(`document.getElementById('tab-live').click(); 1`);
  check('the back button shows', await roomTab.until(`!document.getElementById('rewind').hidden`));
  await sleep(10000);
  await roomTab.ev(`document.getElementById('rewind').click(); 1`);
  check('going back plays the HLS copy', await roomTab.until(`document.getElementById('video').src.startsWith('blob:') && document.getElementById('video').currentTime > 0`, 20000));
  await sleep(2500);
  check('it says how far behind', await roomTab.until(`document.getElementById('stage-note').textContent.includes('behind')`), await roomTab.ev(`document.getElementById('stage-note').textContent`));
  check('the button offers the way back', (await roomTab.ev(`document.getElementById('rewind').textContent`)) === 'Back to live');
  await roomTab.shot('room-rewound');
  await roomTab.ev(`document.getElementById('rewind').click(); 1`);
  check('back to live returns to WebRTC', await roomTab.until(`!!document.getElementById('video').srcObject && document.getElementById('video').currentTime > 0`, 15000));
  check('the room has no script errors', roomTab.errors.length === 0, roomTab.errors[0] ?? '');
  roomTab.close();

  // ----- Keeping it, the hallway, clips (RF-17, RF-35) -----
  await sleep(8000);
  await op(`/rooms/${room}/talks/stop`, 'POST');
  await sleep(1500);
  const info = await (await fetch(`${base}/api/talks/${talk}`)).json();
  check('the hallway stays open after the talk', info.info.hallwayOpen === true);
  check('the chat stays open in the hallway', (await post(`/api/chat/${talk}`, { name: 'Ada', text: 'In the hallway' })).ok);
  await op('/event', 'PUT', { hallwayHours: 0 });
  check('the hallway can close', (await post(`/api/chat/${talk}`, { name: 'Ada', text: 'Too late' })).status === 409);
  await op('/event', 'PUT', { hallwayHours: before.hallwayHours });

  const clip = await opJson(`/talks/${talk}/clips`, 'POST', { t: 2000 });
  let ready;
  for (let i = 0; i < 90 && !ready; i++) {
    const list = await opJson('/clips');
    const mine = list.find((c) => c.id === clip.id);
    if (mine?.status === 'ready') ready = mine;
    if (mine?.status === 'failed') break;
    await sleep(1000);
  }
  check('a clip is made from the recording', !!ready);
  if (ready) {
    const file = join(profile, 'clip.mp4');
    writeFileSync(file, Buffer.from(await (await op(`/clips/${clip.id}.mp4`)).arrayBuffer()));
    const probe = spawnSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', file], { encoding: 'utf8' }).stdout.trim();
    const [w, h] = probe.split(',').map(Number);
    check('the clip is vertical', h > w, probe);
    check('a clip needs the operator', (await fetch(`${base}/api/op/clips/${clip.id}.mp4`)).status === 401);
  }

  // ----- Watching the whole talk afterwards (RF-19) -----
  let replay;
  for (let i = 0; i < 60 && !replay; i++) {
    const data = await (await fetch(`${base}/api/talks/${talk}`)).json();
    if (data.replay.parts.length > 0 && !data.replay.pending) replay = data.replay;
    else await sleep(1000);
  }
  check('the recording becomes a video to watch', !!replay);
  if (replay) {
    const url = base + replay.parts[0].url;
    const file = join(profile, 'replay.mp4');
    writeFileSync(file, Buffer.from(await (await fetch(url)).arrayBuffer()));
    const streams = spawnSync('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_name', '-of', 'csv=p=0', file], { encoding: 'utf8' }).stdout;
    check('it is in a format every phone plays', /h264/.test(streams) && !/opus/.test(streams), streams.replace(/\s+/g, ' '));
    check('it can be sought', (await fetch(url, { headers: { Range: 'bytes=0-99' } })).status === 206);
    check('a changed link does not open it', (await fetch(url.replace(/k=.{4}/, 'k=0000'))).status === 403);
    check('no link does not open it', (await fetch(url.split('?')[0])).status === 403);
    check('it does not leave the venue by default', (await fetch(url, { headers: outside })).status === 403);
    await op('/event', 'PUT', { remoteEnabled: true });
    const seenOutside = await (await fetch(`${base}/api/talks/${talk}`, { headers: outside })).json();
    check('outside, the page offers no video without its setting', seenOutside.replay.parts.length === 0);
    check('outside, the video stays closed without its setting', (await fetch(url, { headers: outside })).status === 403);
    await op('/event', 'PUT', { remoteEnabled: before.remoteEnabled });
    const library = await (await fetch(`${base}/api/library`)).json();
    check('the library shows which talks have video', library.talks.find((x) => x.id === talk)?.video === true);

    const watch = await openTab(`${base}/talk/${talk}#t=3000`);
    check('the talk page shows the video', await watch.until(`!!document.getElementById('replay').src && !document.getElementById('replay-card').hidden`));
    check('a link to a minute lands on that minute', await watch.until(`Math.abs(document.getElementById('replay').currentTime - 3) < 1`));
    await watch.ev(`document.getElementById('replay').play(); 1`);
    check('the video plays', await watch.until(`document.getElementById('replay').currentTime > 3.5`, 10000));
    await watch.shot('replay');
    check('the page has no script errors', watch.errors.length === 0, watch.errors[0] ?? '');
    watch.close();
  }

  // ----- Reports (RF-15, RF-16, RF-20): small groups stay hidden -----
  await op('/event', 'PUT', { minParticipants: 5 });
  const feedback = await opJson(`/reports/speaker/${talk}`);
  check('a report hides a count below the minimum', feedback.questions === null && feedback.moments.length === 0);
  const sponsorReport = await opJson(`/reports/sponsor/${sponsor.id}`);
  check('a sponsor report hides small counts', sponsorReport.chatMentions === null);
  await op('/event', 'PUT', { minParticipants: 1 });
  check('with a lower minimum it shows the count', (await opJson(`/reports/sponsor/${sponsor.id}`)).chatMentions >= 1);
  await op('/event', 'PUT', { minParticipants: before.minParticipants });

  // ----- Library (RF-12, RF-19) -----
  await op('/event', 'PUT', { libraryCode: 'open-sesame', hallwayHours: 0 });
  const lockedLibrary = await (await fetch(`${base}/api/library`)).json();
  check('a library code locks finished talks', lockedLibrary.locked === true && lockedLibrary.talks.length === 0);
  check('a finished talk is locked too', (await fetch(`${base}/api/talks/${talk}`)).status === 403);
  check('the code opens it', (await (await fetch(`${base}/api/library`, { headers: { 'X-Library': 'open-sesame' } })).json()).locked === false);
  check('a ticket opens it', (await (await fetch(`${base}/api/library`, { headers: asTicket(ada.token) })).json()).locked === false);
  check('a wrong code does not', (await (await fetch(`${base}/api/library`, { headers: { 'X-Library': 'nope' } })).json()).locked === true);
  await op('/event', 'PUT', { libraryCode: before.libraryCode, hallwayHours: before.hallwayHours });

  // ----- Deleting my data (RF-23) -----
  const leaving = await person('Temporary Person');
  check('I can delete my data', (await fetch(`${base}/api/me`, { method: 'DELETE', headers: asTicket(leaving.token) })).ok);
  check('my ticket stops working', (await fetch(`${base}/api/me`, { headers: asTicket(leaving.token) })).status === 401);
} catch (error) {
  console.error(error);
  failures.push('crashed: ' + error.message);
}

if (publisher) publisher.kill();
await op(`/rooms/${room}/talks/stop`, 'POST').catch(() => {});
await op('/event', 'PUT', { minParticipants: before.minParticipants, hallwayHours: before.hallwayHours, libraryCode: before.libraryCode, community: before.community, remoteEnabled: before.remoteEnabled, remoteVideo: before.remoteVideo, remoteUrl: before.remoteUrl }).catch(() => {});
for (const id of attendees) await op(`/attendees/${id}`, 'DELETE').catch(() => {});
if (sponsor) await op(`/sponsors/${sponsor.id}`, 'DELETE').catch(() => {});
if (agendaId) await op(`/agenda/${agendaId}`, 'DELETE').catch(() => {});
if (talk) {
  for (const clip of await opJson('/clips').catch(() => [])) if (clip.talkId === talk) await op(`/clips/${clip.id}`, 'DELETE').catch(() => {});
  await op(`/talks/${talk}`, 'DELETE').catch(() => {});
}
await op(`/rooms/${room}`, 'DELETE').catch(() => {});
browser.kill();
await sleep(500);
try { rmSync(profile, { recursive: true, force: true }); } catch { /* still closing */ }

console.log(failures.length ? `\n${failures.length} FAILED:\n- ${failures.join('\n- ')}` : '\nAll passed.');
process.exit(failures.length ? 1 : 0);
