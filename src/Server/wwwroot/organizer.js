// RF-38: the whole event at a glance. First what needs the organizer now, then attendance per room, where the interest is, and technical state.
(function () {
  const $ = (id) => document.getElementById(id);
  const REFRESH_MS = 3000;
  const NOW_REFRESH_MS = 10000;
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };

  const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;

  function stat(value, label) {
    const box = el('div', 'stat glass');
    box.append(el('b', '', value), el('span', 'muted', label));
    return box;
  }

  function light(state, label) {
    const pill = el('span', 'pill', label);
    pill.dataset.state = state;
    return pill;
  }

  function roomTile(room) {
    const tile = el('article', 'room-card');
    const top = el('div', 'room-card-top');
    top.append(el('span', '', room.name));
    if (room.full) top.append(light('warn', 'Full'));
    tile.append(top, el('h3', '', room.talk || 'No talk right now'));
    if (room.speaker) tile.append(el('p', 'who', room.speaker));
    tile.append(el('p', 'facts', `${count(room.occupancy, 'person', 'people')}${room.capacity ? ` of ${room.capacity} places` : ''} · ${count(room.pendingQuestions, 'question', 'questions')} waiting · ${count(room.catchUps, 'catch-up', 'catch-ups')}`));
    if (room.talk) {
      const lights = el('div', 'lights');
      const details = room.tech.details;
      lights.append(
        light(room.tech.camera ? 'ok' : 'bad', room.tech.camera ? 'Video' : 'No video'),
        light(details && details.audio.receiving ? 'ok' : 'bad', details && details.audio.receiving ? 'Audio' : 'No audio'),
        light(room.tech.captionMode === 'cloud' ? 'ok' : room.tech.captionMode === 'local' ? 'warn' : 'bad', `Captions: ${room.tech.captionMode}`),
      );
      tile.append(lights);
    }
    const open = el('a', 'secondary', 'Open room');
    open.href = `/operator#room=${room.id}`;
    tile.append(open);
    return tile;
  }

  // ----- Now: only what needs the organizer, each card saying what to do about it -----
  function nowCard(id, title, count, body) {
    const card = $(id);
    const head = el('div', 'now-head');
    const badge = el('span', 'pill', String(count));
    badge.dataset.state = count ? 'warn' : 'ok';
    head.append(el('h3', '', title), badge);
    card.dataset.state = count ? 'warn' : 'ok';
    card.replaceChildren(head, ...body);
  }

  const row = (main, side, href, linkText) => {
    const line = el('div', 'now-row');
    const text = el('div');
    text.append(el('b', '', main));
    if (side) text.append(el('span', 'muted', side));
    line.append(text);
    if (href) {
      const a = el('a', 'secondary', linkText);
      a.href = href;
      if (href.startsWith('http')) { a.target = '_blank'; a.rel = 'noopener'; }
      line.append(a);
    }
    return line;
  };

  const clock = (ms) => {
    const seconds = Math.max(0, Math.ceil(ms / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  };

  async function announce(on) {
    await fetch('/api/op/announce', on
      ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ kind: 'intro-round' }) }
      : { method: 'DELETE' }).catch(() => null);
    loadNow();
  }

  let now = null;
  let shownLive = null;
  function renderAlone() {
    if (!now) return;
    const { alone, visible, announcement } = now;
    const live = !!announcement && announcement.until > Date.now();
    const timer = $('now-alone').querySelector('.round-left');
    // Ticking the clock alone, so the button under the pointer is not rebuilt every second.
    if (live && shownLive === true && timer) { timer.textContent = `On every screen · ${clock(announcement.until - Date.now())} left`; return; }
    if (!live && shownLive === false) return;
    shownLive = live;
    const body = [];
    body.push(el('p', 'now-lead', visible === 0 ? 'Nobody has a visible profile yet.'
      : alone.count === 0 ? `All ${visible} people with a profile have connected with someone.`
        : `${alone.count} of ${visible} people with a profile have not connected with anyone yet.`));
    if (alone.names.length) {
      const names = el('div', 'why');
      alone.names.forEach((name) => names.append(el('span', 'why-chip', name)));
      if (alone.count > alone.names.length) names.append(el('span', 'why-chip', `+${alone.count - alone.names.length}`));
      body.push(names);
    }
    const actions = el('div', 'actions');
    if (live) {
      const pill = el('span', 'pill round-left', `On every screen · ${clock(announcement.until - Date.now())} left`);
      pill.dataset.state = 'live';
      const stop = el('button', 'secondary', 'Stop');
      stop.type = 'button';
      stop.addEventListener('click', () => announce(false));
      actions.append(pill, stop);
    } else {
      const go = el('button', 'primary', 'Announce a round of introductions');
      go.type = 'button';
      go.addEventListener('click', () => announce(true));
      actions.append(go);
      body.push(el('p', 'muted small', 'Shows on every room screen and phone for 5 minutes.'));
    }
    body.push(actions);
    nowCard('now-alone', 'Not connected yet', alone.count, body);
  }

  function renderNow() {
    shownLive = null;
    renderAlone();
    const waiting = now.sponsorsWaiting;
    nowCard('now-sponsors', `Sponsor requests waiting over ${now.waitMinutes} min`, waiting.length, waiting.length
      ? waiting.map((w) => row(`${w.person} → ${w.sponsor}`, `${w.slot ? `${w.slot} · ` : ''}waiting ${w.minutes} min`, w.link, 'Sponsor page'))
      : [el('p', 'muted', 'Every request is being attended.')]);
    const meetings = now.notConfirmed;
    nowCard('now-meetings', 'Agreed to meet, not confirmed', meetings.length, meetings.length
      ? meetings.map((m) => row(`${m.from} & ${m.to}`, `${m.spot || 'No place chosen'} · ${m.minutes} min ago`))
      : [el('p', 'muted', 'No meeting is waiting to be confirmed.')]);
  }

  async function loadNow() {
    const response = await fetch('/api/op/now').catch(() => null);
    if (!response || !response.ok) return;
    now = await response.json();
    renderNow();
  }

  // Questions come from the room data; incidents are what a live room is missing.
  function renderRooms(data) {
    const rows = [];
    for (const room of data.rooms) {
      if (room.pendingQuestions) rows.push(row(`${room.name}: ${count(room.pendingQuestions, 'question', 'questions')} waiting`, room.talk, `/operator#room=${room.id}`, 'Open room'));
      if (!room.talk) continue;
      const missing = [];
      const details = room.tech.details;
      if (!room.tech.camera) missing.push('no video');
      if (!(details && details.audio.receiving)) missing.push('no audio');
      if (room.tech.captionMode !== 'cloud') missing.push(`captions ${room.tech.captionMode}`);
      if (room.full) missing.push('full');
      if (missing.length) rows.push(row(`${room.name}: ${missing.join(', ')}`, room.talk, `/operator#room=${room.id}`, 'Open room'));
    }
    if (data.rooms[0] && data.rooms[0].tech.internet === false) rows.unshift(row('No internet', 'Captions and translation run on this laptop until it returns.'));
    nowCard('now-rooms', 'Questions and incidents', rows.length, rows.length ? rows : [el('p', 'muted', 'All quiet.')]);
  }

  async function load() {
    const response = await fetch('/api/op/organizer').catch(() => null);
    if (!response || !response.ok) return;
    const data = await response.json();
    renderRooms(data);
    $('totals').replaceChildren(
      stat(data.rooms.reduce((sum, r) => sum + r.occupancy, 0), 'People in rooms'),
      stat(data.rooms.filter((r) => r.talk).length, 'Talks live'),
      stat(data.pendingQuestions, 'Questions waiting'),
      stat(`${data.checkedIn} / ${data.registered}`, 'Checked in'),
    );
    $('interest').replaceChildren(...(data.mostInterest.length
      ? data.mostInterest.map((room, i) => el('p', '', `${i + 1}. ${room.talk} (${room.name})`))
      : [el('p', 'muted', 'Nothing is live.')]));
    $('rooms').replaceChildren(...data.rooms.map(roomTile));
  }

  load();
  loadNow();
  setInterval(load, REFRESH_MS);
  setInterval(loadNow, NOW_REFRESH_MS);
  setInterval(renderAlone, 1000);
})();
