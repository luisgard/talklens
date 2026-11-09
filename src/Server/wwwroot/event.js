// RF-22, RF-34: agenda, speakers, times and rooms. A full room offers its stream instead.
(function () {
  const { el, link, strings, formatWhen, newHub, mountNav } = TL;
  const t = strings({
    en: { getTicket: 'Get a free ticket', defaultName: 'Agenda', empty: 'The organizer has not published the agenda yet.', full: 'Room full, follow it live here', follow: 'Watch live',
      liveNow: 'Live now', people: '{n} people here', capacity: '{n} of {c} places', talkPage: 'Summary',
      findTitle: 'Find the right talk.', findHint: 'Try: something on security and privacy', byMeaning: '✦ Matched by meaning', best: 'Best matches', noMatch: 'No talk matches that. Try saying it another way.' },
    es: { getTicket: 'Obtén un ticket gratis', defaultName: 'Agenda', empty: 'El organizador todavía no publica la agenda.', full: 'Sala llena, síguela en vivo aquí', follow: 'Ver en vivo',
      liveNow: 'En vivo ahora', people: '{n} personas aquí', capacity: '{n} de {c} lugares', talkPage: 'Resumen',
      findTitle: 'Encuentra la charla.', findHint: 'Prueba: algo de seguridad y privacidad', byMeaning: '✦ Encontrada por su significado', best: 'Lo que más se parece', noMatch: 'Ninguna charla coincide. Intenta decirlo de otra forma.' },
  });
  mountNav('agenda');

  let agenda = [];
  let rooms = [];
  let found = null;
  const SEARCH_DELAY_MS = 350;

  function card(item, byMeaning) {
    const room = rooms.find((r) => r.id === item.roomId);
    const box = el('article', 'room-card');
    if (byMeaning) box.append(el('span', 'find-why', t('byMeaning')));
    const top = el('div', 'room-card-top');
    top.append(el('span', '', `${formatWhen(item.startsAt)} · ${room ? room.name : ''}`));
    // The room being live says nothing about the talks scheduled there for later.
    const live = room && room.live && room.talk && item.startsAt <= Date.now() && Date.now() < item.endsAt;
    if (live) {
      const pill = el('span', 'pill', t('liveNow'));
      pill.dataset.state = 'live';
      top.append(pill);
    }
    box.append(top, el('h3', '', item.title));
    if (item.speaker) box.append(el('p', 'who', item.speaker));
    if (item.about) box.append(el('p', 'facts', item.about));
    if (room) {
      // A number only: it never says who is in the room.
      if (room.capacity > 0) box.append(el('p', 'facts', t('capacity', { n: room.occupancy, c: room.capacity })));
      else if (room.occupancy > 0) box.append(el('p', 'facts', t('people', { n: room.occupancy })));
      if (room.full) box.append(el('p', 'full-note', t('full')));
      if (live) box.append(link(room.full ? 'primary' : 'secondary', `/r/${room.id}`, t('follow')));
    }
    return box;
  }

  function renderFound() {
    const place = document.getElementById('find-results');
    if (!found || agenda.length === 0) { place.replaceChildren(); return; }
    const hits = found.map((hit) => [agenda.find((item) => item.id === hit.id), hit.byMeaning]).filter(([item]) => item);
    if (hits.length === 0) { place.replaceChildren(el('p', 'find-empty', t('noMatch'))); return; }
    const list = el('div', 'room-list');
    hits.forEach(([item, byMeaning]) => list.append(card(item, byMeaning)));
    place.replaceChildren(el('h2', 'section-title', t('best')), list);
  }

  function wireSearch() {
    const input = document.getElementById('find-input');
    input.placeholder = t('findHint');
    let timer = 0;
    let latest = 0;
    input.addEventListener('input', () => {
      clearTimeout(timer);
      const query = input.value.trim();
      const asked = ++latest;
      if (!query) { found = null; renderFound(); return; }
      timer = setTimeout(async () => {
        const response = await fetch(`/api/event/search?q=${encodeURIComponent(query)}`);
        // An answer to something they have already changed is no longer wanted.
        if (asked !== latest || !response.ok) return;
        found = await response.json();
        renderFound();
      }, SEARCH_DELAY_MS);
    });
  }

  function render() {
    document.getElementById('find').hidden = agenda.length === 0;
    renderFound();
    const place = document.getElementById('agenda');
    if (agenda.length === 0) {
      place.replaceChildren(el('p', 'home-empty glass', t('empty')));
      return;
    }
    const days = new Map();
    for (const item of agenda) {
      const day = new Date(item.startsAt).toDateString();
      if (!days.has(day)) days.set(day, []);
      days.get(day).push(item);
    }
    const nodes = [];
    const format = new Intl.DateTimeFormat(TL.language, { weekday: 'long', month: 'long', day: 'numeric' });
    for (const items of days.values()) {
      nodes.push(el('h2', 'section-title', format.format(new Date(items[0].startsAt))));
      const list = el('div', 'room-list');
      items.forEach((item) => list.append(card(item)));
      nodes.push(list);
    }
    place.replaceChildren(...nodes);
  }

  async function load() {
    const data = await (await fetch('/api/event')).json();
    document.getElementById('event-name').textContent = data.name || t('defaultName');
    document.getElementById('event-where').textContent = [data.venue, data.dates].filter(Boolean).join(' · ');
    document.getElementById('event-about').textContent = data.about;
    document.getElementById('register-cta').hidden = !!TL.ticket();
    if (data.name) document.title = data.name;
    agenda = data.agenda;
    rooms = data.summary.rooms;
    render();
  }

  wireSearch();
  const hub = newHub();
  hub.on('rooms', (summary) => { rooms = summary.rooms; render(); });
  hub.start().catch(() => {});
  load();
})();
