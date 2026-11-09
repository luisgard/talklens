// Home: every room of the event, so people can choose between talks that overlap and move between them.
(function () {
  const STRINGS = {
    en: {
      heroTitle: 'Every talk.\nIn your pocket.', heroText: 'Watch any room live, in your language, and switch whenever you like. No app. No account.', howItWorks: 'See how it works',
      tileAskTitle: 'Ask the talk.', tileAskText: 'Answers from what was really said.',
      tileCatchTitle: 'Late? Catch up.', tileCatchText: 'What you missed, in a minute.',
      tileFindTitle: 'Find the right talk.', tileFindText: 'Search by what you mean.',
      liveNow: 'Live now', ended: 'Ended recently', quiet: 'Nothing on right now', live: 'Live', endedPill: 'Ended',
      enter: 'Watch', summary: 'Catch me up', read: 'Read and catch up', noTalk: 'No talk right now',
      waitingCamera: 'Waiting for the camera', watching: '{n} watching', minutes: '{n} min in',
      empty: 'No talk has started yet. This page updates by itself.',
      full: 'Room full, follow it live here', hallways: 'Keep talking', hallway: 'Hallway', oneMinute: 'Summary in a minute',
    },
    es: {
      heroTitle: 'Todas las charlas.\nEn tu bolsillo.', heroText: 'Mira cualquier sala en vivo, en tu idioma, y cambia cuando quieras. Sin app. Sin cuenta.', howItWorks: 'Mira cómo funciona',
      tileAskTitle: 'Pregúntale a la charla.', tileAskText: 'Respuestas de lo que de verdad se dijo.',
      tileCatchTitle: '¿Llegaste tarde? Ponte al día.', tileCatchText: 'Lo que te perdiste, en un minuto.',
      tileFindTitle: 'Encuentra la charla.', tileFindText: 'Busca lo que quieres decir.',
      liveNow: 'En vivo ahora', ended: 'Terminaron hace poco', quiet: 'Sin charla ahora', live: 'En vivo', endedPill: 'Terminó',
      enter: 'Ver', summary: 'Ponme al día', read: 'Leer y ponerme al día', noTalk: 'Sin charla por ahora',
      waitingCamera: 'Esperando la cámara', watching: '{n} viendo', minutes: 'Lleva {n} min',
      empty: 'Todavía no empieza ninguna charla. Esta página se actualiza sola.',
      full: 'Sala llena, síguela en vivo aquí', hallways: 'Sigan platicando', hallway: 'Pasillo', oneMinute: 'Resumen en un minuto',
    },
  };
  // English unless the person chooses otherwise. The room page reads the same setting.
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem('talklens.ui')); } catch { /* private window */ }
  const uiLanguage = STRINGS[saved] ? saved : 'en';
  const t = (key) => STRINGS[uiLanguage][key];

  document.documentElement.lang = uiLanguage;
  document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
  const picker = document.getElementById('ui-language');
  picker.value = uiLanguage;
  picker.addEventListener('change', () => {
    try { localStorage.setItem('talklens.ui', JSON.stringify(picker.value)); } catch { /* private window */ }
    location.reload();
  });

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function link(className, href, text) {
    const a = el('a', className, text);
    a.href = href;
    return a;
  }

  // Each room keeps its colour, so people can tell them apart at a glance.
  const HUES = [215, 330, 25, 165, 265, 190];

  function card(room) {
    if (!room.talk) {
      const quiet = el('article', 'room-card quiet');
      const top = el('div', 'room-card-top');
      top.append(el('span', '', room.name));
      quiet.append(top, el('p', 'who', t('noTalk')));
      return quiet;
    }
    const poster = el('article', 'room-card poster');
    const art = el('div', 'poster-art');
    art.innerHTML = Story.stage(HUES[room.id % HUES.length]);
    const pill = el('span', 'pill', room.live ? t('live') : t('endedPill'));
    if (room.live) pill.dataset.state = 'live';
    art.append(pill);
    const box = el('div', 'poster-body');
    poster.append(art, box);
    box.append(el('p', 'poster-room', room.name), el('h3', '', room.talk.title));
    if (room.talk.speaker) box.append(el('p', 'who', room.talk.speaker));

    if (room.live) {
      const facts = [t('minutes').replace('{n}', Math.floor(room.elapsedMs / 60000))];
      if (room.watching > 0) facts.push(t('watching').replace('{n}', room.watching));
      if (!room.videoReady) facts.push(t('waitingCamera'));
      box.append(el('p', 'facts', facts.join(' · ')));
      if (room.full) box.append(el('p', 'full-note', t('full')));
    }

    const actions = el('div', 'actions');
    if (room.live) {
      actions.append(link('primary', `/r/${room.id}`, t('enter')), link('secondary', `/talk/${room.talk.id}`, t('oneMinute')));
    } else {
      actions.append(link('secondary', `/talk/${room.talk.id}`, t('read')));
    }
    box.append(actions);
    return poster;
  }

  function section(title, rooms) {
    if (!rooms.length) return [];
    const list = el('div', 'room-list');
    rooms.forEach((room) => list.append(card(room)));
    return [el('h2', 'section-title', title), list];
  }

  function render(summary) {
    const rooms = summary.rooms;
    document.body.classList.toggle('has-live', rooms.some((r) => r.live));
    // With one room there is nothing to choose, so skip straight to it.
    if (rooms.length === 1 && rooms[0].talk && !new URLSearchParams(location.search).has('home')) {
      location.replace(`/r/${rooms[0].id}`);
      return;
    }
    const nodes = rooms.some((r) => r.talk) ? [
      ...section(t('liveNow'), rooms.filter((r) => r.live)),
      ...section(t('ended'), rooms.filter((r) => !r.live && r.talk)),
      ...section(t('quiet'), rooms.filter((r) => !r.talk)),
    ] : [el('p', 'home-empty glass', t('empty'))];
    if (summary.hallways && summary.hallways.length) {
      const list = el('div', 'room-list');
      for (const talk of summary.hallways) {
        const box = el('article', 'room-card');
        const top = el('div', 'room-card-top');
        top.append(el('span', '', t('hallway')));
        box.append(top, el('h3', '', talk.title));
        const actions = el('div', 'actions');
        actions.append(link('secondary', `/talk/${talk.id}`, t('hallway')));
        box.append(actions);
        list.append(box);
      }
      nodes.push(el('h2', 'section-title', t('hallways')), list);
    }
    document.getElementById('rooms').replaceChildren(...nodes);
  }

  async function load() {
    try {
      render(await (await fetch('/api/rooms')).json());
    } catch {
      setTimeout(load, 3000);
    }
  }

  Story.mount();
  TL.mountNav('home');
  const hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
  hub.on('rooms', render);
  hub.onreconnected(load);
  hub.start().catch(() => {});
  load();
})();
