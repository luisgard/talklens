// The room's big screen: how to join, captions, the poll while it runs, approved photos, and reactions floating up.
// It follows the room without counting as an attendee.
(function () {
  const t = TL.strings({
    en: {
      join: 'Scan to follow on your phone', idle: 'Photos people share will appear here.', newPhoto: 'New',
      hintReact: 'React from your phone', hintPhoto: 'Share photos in the Photos tab', hintAsk: 'Ask questions, privately',
      live: 'Live', waiting: 'Not started', ended: 'Ended', voteNow: 'Vote on your phone', votes: '{n} votes', resultsAt: 'Results show once {n} people have voted.',
      pollClosed: 'Poll closed',
      roundHead: 'Now', roundTitle: 'Round of introductions', roundText: 'Turn to someone you have not met and tell them what you are looking for. Or open Connect on your phone.',
      roundLeft: '{time} left',
    },
    es: {
      join: 'Escanea para seguir en tu celular', idle: 'Aquí aparecerán las fotos que comparta la gente.', newPhoto: 'Nueva',
      hintReact: 'Reacciona desde tu celular', hintPhoto: 'Comparte fotos en la pestaña Fotos', hintAsk: 'Haz preguntas, en privado',
      live: 'En vivo', waiting: 'Sin iniciar', ended: 'Terminó', voteNow: 'Vota en tu celular', votes: '{n} votos', resultsAt: 'Los resultados aparecen cuando voten {n} personas.',
      pollClosed: 'Encuesta cerrada',
      roundHead: 'Ahora', roundTitle: 'Ronda de presentaciones', roundText: 'Voltea con alguien que no conozcas y cuéntale qué buscas. O abre Conectar en tu celular.',
      roundLeft: 'Quedan {time}',
    },
  });
  const $ = (id) => document.getElementById(id);
  const ROOM = Number(location.pathname.split('/').pop()) || 1;
  const PHOTO_MS = 8000;
  const CLOSED_POLL_MS = 60000;
  const CAPTION_CLEAR_MS = 9000;

  const state = { talk: null, live: false, poll: null, pollClosedAt: 0, photos: [], photoIndex: 0, fresh: null, roundUntil: 0 };

  $('qr').src = `/api/rooms/${ROOM}/qr.svg`;
  $('qr').alt = t('join');
  fetch(`/api/rooms/${ROOM}/join`).then((r) => r.json()).then((d) => { $('join-url').textContent = d.url.replace(/^https?:\/\//, ''); }).catch(() => {});

  function setHeader(roomName) {
    if (roomName !== undefined) $('room').textContent = roomName;
    $('title').textContent = state.talk ? state.talk.title : '';
    $('speaker').textContent = state.talk ? state.talk.speaker : '';
    const pill = state.live ? 'live' : state.talk ? 'ended' : 'waiting';
    $('live').dataset.state = pill;
    $('live').textContent = t(pill);
  }

  // ----- Captions: the last two lines, large -----
  const lines = new Map();
  let clearTimer;
  function onCaption(caption) {
    lines.delete(caption.key);
    lines.set(caption.key, caption.text);
    while (lines.size > 2) lines.delete(lines.keys().next().value);
    $('captions').replaceChildren(...[...lines.values()].map((text) => TL.el('p', '', text)));
    clearTimeout(clearTimer);
    clearTimer = setTimeout(() => { lines.clear(); $('captions').replaceChildren(); }, CAPTION_CLEAR_MS);
  }

  // ----- The spotlight: the poll while it matters, otherwise photos -----
  function pollShowing() {
    return state.poll && (state.poll.open || Date.now() - state.pollClosedAt < CLOSED_POLL_MS);
  }

  function renderPoll() {
    const poll = state.poll;
    const box = $('poll-view');
    const parts = [TL.el('p', 'screen-poll-head', poll.open ? t('voteNow') : t('pollClosed')), TL.el('h2', '', poll.question)];
    const total = poll.counts ? poll.counts.reduce((a, b) => a + b, 0) : 0;
    poll.options.forEach((option, i) => {
      const share = poll.counts && total ? Math.round((poll.counts[i] / total) * 100) : 0;
      const row = TL.el('div', 'poll-bar');
      const fill = TL.el('span', 'fill');
      fill.style.width = `${share}%`;
      row.append(fill, TL.el('span', 'label', option), TL.el('b', '', poll.counts ? `${share}%` : ''));
      parts.push(row);
    });
    parts.push(TL.el('p', 'muted', poll.counts ? t('votes', { n: total }) : t('resultsAt', { n: poll.minimum })));
    box.replaceChildren(...parts);
  }

  function showPhoto(item, isNew) {
    $('photo-img').src = item.url;
    $('photo-img').alt = item.photo.alt;
    $('photo-alt').textContent = item.photo.alt;
    $('photo-new').hidden = !isNew;
  }

  // ----- The organizer's round of introductions: above everything else while it lasts -----
  function renderRound() {
    const left = state.roundUntil - Date.now();
    $('round-view').hidden = left <= 0;
    if (left <= 0) { state.roundUntil = 0; return false; }
    const seconds = Math.ceil(left / 1000);
    $('round-left').textContent = t('roundLeft', { time: `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` });
    return true;
  }
  setInterval(() => { if (state.roundUntil) render(); }, 1000);
  function onAnnouncement(announcement) {
    state.roundUntil = announcement && announcement.kind === 'intro-round' ? announcement.until : 0;
    render();
  }

  function render() {
    if (renderRound()) {
      $('poll-view').hidden = $('photo-view').hidden = $('idle').hidden = true;
      return;
    }
    const poll = pollShowing();
    $('poll-view').hidden = !poll;
    if (poll) renderPoll();
    const photo = !poll && state.photos.length > 0;
    $('photo-view').hidden = !photo;
    $('idle').hidden = poll || photo;
  }

  function nextPhoto() {
    if (state.fresh) {
      showPhoto(state.fresh, true);
      state.fresh = null;
    } else if (state.photos.length) {
      state.photoIndex = (state.photoIndex + 1) % state.photos.length;
      showPhoto(state.photos[state.photoIndex], false);
    }
    render();
  }
  setInterval(nextPhoto, PHOTO_MS);

  async function loadPhotos() {
    const data = await (await fetch('/api/photos')).json();
    const approved = data.photos.filter((p) => p.photo.status === 'approved');
    // This room's photos first; the rest of the event fills in until it has some.
    const here = approved.filter((p) => p.photo.roomId === ROOM);
    state.photos = here.length ? here : approved;
    if (state.photoIndex >= state.photos.length) state.photoIndex = 0;
    if (state.photos.length && $('photo-view').hidden) showPhoto(state.photos[state.photoIndex], false);
    render();
  }

  async function loadState() {
    const data = await (await fetch(`/api/rooms/${ROOM}/state`)).json();
    state.talk = data.talk;
    state.live = data.live;
    setHeader(data.room.name);
    onAnnouncement((await (await fetch('/api/announcement')).json()).announcement);
    const poll = await (await fetch(`/api/rooms/${ROOM}/poll`)).json();
    state.poll = poll.poll;
    // A poll that closed before this screen opened is old news.
    state.pollClosedAt = 0;
    await loadPhotos();
  }

  // ----- Live -----
  const hub = TL.newHub();
  hub.on('caption', onCaption);
  hub.on('reaction', (r) => TL.floatReaction($('float-layer'), r.kind));
  hub.on('poll', (poll) => {
    if (state.poll && state.poll.open && !poll.open) state.pollClosedAt = Date.now();
    state.poll = poll;
    render();
  });
  hub.on('photo', async (photo) => {
    await loadPhotos();
    const item = state.photos.find((p) => p.photo.id === photo.id);
    if (item && !pollShowing()) {
      showPhoto(item, true);
      render();
    } else if (item) {
      state.fresh = item;
    }
  });
  hub.on('photoRemoved', (id) => {
    if (state.fresh && state.fresh.photo.id === id) state.fresh = null;
    state.photos = state.photos.filter((p) => p.photo.id !== id);
    if (!$('photo-view').hidden && $('photo-img').src.includes(`/photos/${id}.jpg`)) nextPhoto();
    render();
  });
  hub.on('announcement', onAnnouncement);
  hub.on('talkStarted', (talk) => { state.talk = talk; state.live = true; state.poll = null; setHeader(); render(); });
  hub.on('talkEnded', () => { state.live = false; setHeader(); });
  hub.onreconnected(async () => {
    await hub.invoke('WatchRoom', 0, ROOM).catch(() => {});
    await loadState().catch(() => {});
  });

  (async function boot() {
    try {
      await hub.start();
      await hub.invoke('WatchRoom', 0, ROOM);
      await loadState();
    } catch {
      setTimeout(boot, 2000);
    }
  })();
})();
