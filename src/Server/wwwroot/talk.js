// RF-19, RF-33, RF-35: one talk's page. A one-minute summary, its hallway, private questions and the full transcript.
(function () {
  const { el, strings, api, formatTime, store, mountNav, newHub, chat, answerBody } = TL;
  const t = strings({
    en: {
      watchLive: 'Watch live', lockedTitle: 'This talk is in the library', lockedText: 'Enter the library code from the organizer, or use your event ticket.', unlock: 'Unlock', useTicket: 'Open my ticket',
      summaryTitle: 'The talk in one minute', summarizing: 'Summarizing…', notFound: 'Nothing has been said yet to summarize.', offline: 'Without internet: these are key moments, not a summary.', busy: 'Too many requests. Try in a moment.',
      hallQuestions: 'Questions left for the speaker', hallQuestionsNote: 'Anonymous. The speaker can still answer them here.', answered: 'Answer from the speaker', waiting: 'Waiting for an answer', askedBy: '{n} asked this',
      chatLive: 'Chat with everyone at this talk', chatHall: 'Hallway: keep talking after the talk', hallClosed: 'The hallway for this talk has closed.',
      askTitle: 'Ask about this talk', askPrivate: 'Private: only you see this question and its answer.', askPlaceholder: 'Your question…', send: 'Ask', thinking: 'Looking through the talk…',
      transcriptTitle: 'Everything that was said and shown', slideAt: 'Slide at {t}', room: 'Room',
      preparing: 'The video is being prepared. It will appear here by itself.', playFrom: 'Play from {t}',
      signAvatar: 'Signing avatar (experimental)', signLoading: 'Loading the avatar…', signReady: 'Experimental: it signs what it signed live.', signFailed: 'The avatar could not load. It needs internet.',
      subtitles: 'Subtitles', subtitlesOff: 'Off', original: 'Original', downloadSubtitles: 'Download .vtt', translatingSubtitles: 'Translating the subtitles…',
      subtitlesFailed: 'The translation is not available right now. Try again later.',
      momentsTitle: 'Where the room reacted', chaptersTitle: 'Chapters', glossaryTitle: 'Terms explained', thinkingTerms: 'Finding the technical terms…',
    },
    es: {
      watchLive: 'Ver en vivo', lockedTitle: 'Esta charla está en la biblioteca', lockedText: 'Escribe el código de biblioteca del organizador, o usa tu ticket del evento.', unlock: 'Abrir', useTicket: 'Abrir mi ticket',
      summaryTitle: 'La charla en un minuto', summarizing: 'Resumiendo…', notFound: 'Todavía no se ha dicho nada que resumir.', offline: 'Sin internet: son momentos clave, no un resumen.', busy: 'Demasiadas solicitudes. Intenta en un momento.',
      hallQuestions: 'Preguntas pendientes para el ponente', hallQuestionsNote: 'Anónimas. El ponente todavía puede contestarlas aquí.', answered: 'Respuesta del ponente', waiting: 'Esperando respuesta', askedBy: '{n} preguntaron esto',
      chatLive: 'Chat con todos en esta charla', chatHall: 'Pasillo: sigan platicando después de la charla', hallClosed: 'El pasillo de esta charla ya cerró.',
      askTitle: 'Pregunta sobre esta charla', askPrivate: 'Privado: solo tú ves esta pregunta y su respuesta.', askPlaceholder: 'Tu pregunta…', send: 'Preguntar', thinking: 'Buscando en la charla…',
      transcriptTitle: 'Todo lo que se dijo y mostró', slideAt: 'Diapositiva en {t}', room: 'Sala',
      preparing: 'El video se está preparando. Aparecerá aquí solo.', playFrom: 'Reproducir desde {t}',
      signAvatar: 'Avatar de señas (experimental)', signLoading: 'Cargando el avatar…', signReady: 'Experimental: seña lo mismo que señó en vivo.', signFailed: 'El avatar no pudo cargar. Necesita internet.',
      subtitles: 'Subtítulos', subtitlesOff: 'Sin subtítulos', original: 'Original', downloadSubtitles: 'Descargar .vtt', translatingSubtitles: 'Traduciendo los subtítulos…',
      subtitlesFailed: 'La traducción no está disponible ahora. Intenta más tarde.',
      momentsTitle: 'Donde reaccionó la sala', chaptersTitle: 'Capítulos', glossaryTitle: 'Términos explicados', thinkingTerms: 'Buscando los términos técnicos…',
    },
  });
  mountNav('library');

  const $ = (id) => document.getElementById(id);
  const TALK = Number(location.pathname.split('/').pop());
  const words = { notFound: t('notFound'), offline: t('offline') };
  const REPLAY_CHECK_MS = 5000;
  let parts = [];
  let currentPart = -1;
  let transcriptData = null;

  // Times are from the start of the talk. If the camera dropped, the video comes in parts that each know when they began.
  function playPart(index, offset, play) {
    const video = $('replay');
    const go = () => {
      video.currentTime = offset;
      if (play) video.play().catch(() => {});
    };
    if (index === currentPart) {
      go();
      return;
    }
    currentPart = index;
    video.src = parts[index].url;
    video.addEventListener('loadedmetadata', go, { once: true });
    showSubtitles();
  }

  // ----- Subtitles: made here from the transcript, for the part of the video that is playing -----
  let subtitleLines = null;
  let subtitleUrl = null;

  const stamp = (ms) => {
    const total = Math.max(0, Math.round(ms));
    const h = String(Math.floor(total / 3600000)).padStart(2, '0');
    const m = String(Math.floor((total % 3600000) / 60000)).padStart(2, '0');
    const s = String(Math.floor((total % 60000) / 1000)).padStart(2, '0');
    return `${h}:${m}:${s}.${String(total % 1000).padStart(3, '0')}`;
  };

  /** @param {number} offset Talk time where this video part starts. */
  function toVtt(lines, offset, until = Infinity) {
    const cues = lines
      .map((line, i) => ({ start: line.t, end: Math.max(line.t + 1000, Math.min(line.end || line.t + 4000, lines[i + 1] ? lines[i + 1].t : Infinity)), text: line.text }))
      .filter((cue) => cue.end > offset && cue.start < until)
      .map((cue) => `${stamp(cue.start - offset)} --> ${stamp(cue.end - offset)}\n${cue.text.replaceAll('-->', '→')}`);
    return `WEBVTT\n\n${cues.join('\n\n')}\n`;
  }

  function showSubtitles() {
    const video = $('replay');
    for (const track of [...video.querySelectorAll('track')]) track.remove();
    if (subtitleUrl) URL.revokeObjectURL(subtitleUrl);
    subtitleUrl = null;
    $('sub-download').hidden = !subtitleLines;
    if (!subtitleLines || currentPart < 0) return;
    const start = parts[currentPart].start;
    const until = parts[currentPart + 1] ? parts[currentPart + 1].start : Infinity;
    subtitleUrl = URL.createObjectURL(new Blob([toVtt(subtitleLines, start, until)], { type: 'text/vtt' }));
    const track = el('track');
    track.kind = 'subtitles';
    track.srclang = $('sub-language').value;
    track.label = $('sub-language').selectedOptions[0].textContent;
    track.src = subtitleUrl;
    track.default = true;
    video.append(track);
    track.track.mode = 'showing';
  }

  async function chooseSubtitles() {
    const language = $('sub-language').value;
    store.set('subtitles', language);
    subtitleLines = null;
    $('sub-note').textContent = '';
    if (language) {
      $('sub-note').textContent = language === transcriptData.info.talk.language ? '' : t('translatingSubtitles');
      const response = await api(`/api/talks/${TALK}/subtitles`, { method: 'POST', json: { language } }).catch(() => null);
      if ($('sub-language').value !== language) return;
      if (!response || !response.ok) {
        $('sub-note').textContent = t('subtitlesFailed');
      } else {
        subtitleLines = (await response.json()).items;
        $('sub-note').textContent = '';
      }
    }
    showSubtitles();
  }

  async function setUpSubtitles(talk) {
    const state = await fetch(`/api/rooms/${talk.roomId}/state`).then((r) => (r.ok ? r.json() : null)).catch(() => null);
    const languages = [...new Set([talk.language, ...((state && state.languages) || ['en', 'es'])])];
    const names = new Intl.DisplayNames([TL.language], { type: 'language' });
    $('sub-language').replaceChildren(new Option(t('subtitlesOff'), ''),
      ...languages.map((code) => new Option(code === talk.language ? `${names.of(code)} · ${t('original')}` : names.of(code), code)));
    const saved = store.get('subtitles', '');
    $('sub-language').value = languages.includes(saved) ? saved : '';
    if ($('sub-language').value) chooseSubtitles();
  }

  $('sub-language').addEventListener('change', chooseSubtitles);
  $('sub-download').addEventListener('click', () => {
    if (!subtitleLines) return;
    const a = el('a');
    a.href = URL.createObjectURL(new Blob([toVtt(subtitleLines, 0)], { type: 'text/vtt' }));
    a.download = `${transcriptData.info.talk.title.replace(/[^\p{L}\p{N} _-]/gu, '').trim() || 'talk'}.${$('sub-language').value}.vtt`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  });

  // ----- Chapters and the glossary, made by the AI once per language -----
  async function loadChapters() {
    const response = await api(`/api/talks/${TALK}/chapters`, { method: 'POST', json: { language: TL.language } }).catch(() => null);
    const chapters = response && response.ok ? (await response.json()).items : [];
    $('chapters-card').hidden = chapters.length < 2;
    $('chapters').replaceChildren(...chapters.map((chapter) => {
      const li = el('li');
      const jump = el('button', 'time-jump', formatTime(chapter.t));
      jump.type = 'button';
      jump.setAttribute('aria-label', t('playFrom', { t: formatTime(chapter.t) }));
      jump.addEventListener('click', () => goTo(chapter.t, true));
      li.append(jump, el('span', '', chapter.title));
      return li;
    }));
  }

  // The peaks of the room's reactions, shown only once enough people reacted.
  async function loadMoments() {
    const response = await api(`/api/talks/${TALK}/moments`).catch(() => null);
    const peaks = response && response.ok ? (await response.json()).peaks : [];
    $('moments-card').hidden = peaks.length === 0;
    $('moments').replaceChildren(...peaks.map((peak) => {
      const found = TL.REACTIONS.find((r) => r[0] === peak.kind);
      const li = el('li');
      const jump = el('button', 'time-jump', formatTime(peak.t));
      jump.type = 'button';
      jump.setAttribute('aria-label', t('playFrom', { t: formatTime(peak.t) }));
      jump.addEventListener('click', () => goTo(peak.t, true));
      const text = el('span', '');
      text.append(el('b', '', `${peak.count} × ${TL.common(found[2])}`), document.createElement('br'), el('span', 'muted', peak.said));
      li.append(el('span', 'emoji', found[1]), jump, text);
      return li;
    }));
  }

  async function loadGlossary() {
    $('glossary-card').hidden = false;
    $('glossary').textContent = t('thinkingTerms');
    const response = await api(`/api/talks/${TALK}/glossary`, { method: 'POST', json: { language: TL.language } }).catch(() => null);
    if (!response || !response.ok) {
      $('glossary-card').hidden = true;
      return;
    }
    TL.glossary($('glossary'), (await response.json()).items);
  }

  function seekVideo(time, play) {
    if (parts.length === 0) return;
    let index = 0;
    parts.forEach((part, i) => { if (part.start <= time) index = i; });
    playPart(index, Math.max(0, time - parts[index].start) / 1000, play);
  }

  function showReplay(replay) {
    const first = parts.length === 0 && replay.parts.length > 0;
    parts = replay.parts;
    $('replay').hidden = parts.length === 0;
    $('replay-card').hidden = parts.length === 0 && !replay.pending;
    $('replay-note').textContent = parts.length === 0 && replay.pending ? t('preparing') : '';
    if (replay.pending) setTimeout(checkReplay, REPLAY_CHECK_MS);
    if (first) {
      if (transcriptData) showTranscript(transcriptData);
      const moment = location.hash.match(/^#t=(\d+)$/);
      seekVideo(moment ? Number(moment[1]) : 0, false);
    }
  }

  async function checkReplay() {
    const response = await api(`/api/talks/${TALK}`).catch(() => null);
    if (response && response.ok) showReplay((await response.json()).replay);
    else setTimeout(checkReplay, REPLAY_CHECK_MS);
  }

  /** @param {boolean} play Only after a tap: phones refuse to start a video on their own. */
  function goTo(time, play = false) {
    seekVideo(time, play);
    const items = [...$('transcript').children];
    const target = items.filter((row) => Number(row.dataset.t) <= time).pop() || items[0];
    if (!target) return;
    target.scrollIntoView({ block: 'center' });
    target.classList.remove('flash');
    void target.offsetWidth;
    target.classList.add('flash');
    history.replaceState(null, '', '#t=' + time);
  }

  function showTranscript(data) {
    transcriptData = data;
    const rows = [
      ...data.segments.map((s) => ({ t: s.t, text: s.text })),
      ...data.slides.map((s) => ({ t: s.t, slide: s })),
    ].sort((a, b) => a.t - b.t);
    $('transcript').replaceChildren(...rows.map((row) => {
      const li = el('li', row.slide ? 'transcript-slide' : '');
      li.dataset.t = row.t;
      if (parts.length > 0) {
        const jump = el('button', 'time-jump', formatTime(row.t));
        jump.type = 'button';
        jump.setAttribute('aria-label', t('playFrom', { t: formatTime(row.t) }));
        jump.addEventListener('click', () => goTo(row.t, true));
        li.append(jump);
      } else {
        li.append(el('time', '', formatTime(row.t)));
      }
      if (row.slide) {
        const img = el('img');
        img.src = `/slides/${TALK}/${row.slide.id}.jpg`;
        img.alt = row.slide.text || t('slideAt', { t: formatTime(row.t) });
        img.loading = 'lazy';
        li.append(img);
      } else {
        li.append(el('span', '', row.text));
      }
      return li;
    }));
  }

  async function summarize() {
    const box = $('summary');
    box.textContent = t('summarizing');
    const response = await api(`/api/talks/${TALK}/summary`, { method: 'POST', json: { language: TL.language } }).catch(() => null);
    if (!response || !response.ok) {
      box.textContent = t('busy');
      return;
    }
    const data = await response.json();
    answerBody(box, data.answer, words, (c) => goTo(c.t, true));
    if (data.slide) {
      $('best-slide-img').src = data.slide.url;
      $('best-slide-img').alt = data.slide.text || t('slideAt', { t: formatTime(data.slide.t) });
      $('best-slide-time').textContent = t('slideAt', { t: formatTime(data.slide.t) });
      $('best-slide').hidden = false;
    }
  }

  function showQuestions(questions) {
    $('hall-questions-card').hidden = questions.length === 0;
    $('hall-questions').replaceChildren(...questions.map((q) => {
      const row = el('article', 'question');
      row.append(el('p', 'question-text', q.text));
      if (q.count > 1) row.append(el('p', 'muted small', t('askedBy', { n: q.count })));
      if (q.answer) {
        row.append(el('p', 'muted small', t('answered')), el('p', '', q.answer));
      } else {
        row.append(el('p', 'muted small', t('waiting')));
      }
      return row;
    }));
  }

  async function load() {
    const response = await api(`/api/talks/${TALK}`);
    if (response.status === 404) {
      location.replace('/library');
      return;
    }
    if (response.status === 403) {
      $('locked').hidden = false;
      $('open').hidden = true;
      return;
    }
    const data = await response.json();
    const { talk, live, room, hallwayOpen } = data.info;
    $('locked').hidden = true;
    $('open').hidden = false;
    $('title').textContent = talk.title;
    document.title = talk.title;
    $('who').textContent = [room, talk.speaker].filter(Boolean).join(' · ');
    $('watch').hidden = !live;
    $('watch').href = `/r/${talk.roomId}`;

    parts = [];
    currentPart = -1;
    showReplay(data.replay);
    loadSigns();
    showTranscript(data);
    setUpSubtitles(talk);
    showQuestions(data.questions);
    summarize();
    loadChapters();
    loadMoments();
    loadGlossary();
    if (!live) TL.rating($('rate-card'), TALK);

    $('chat-title').textContent = t(live ? 'chatLive' : 'chatHall');
    if (hallwayOpen) {
      const hub = newHub();
      const box = chat($('chat'), TALK, hub);
      hub.start().then(() => box.watch()).catch(() => {});
    } else {
      $('chat').replaceChildren(el('p', 'muted', t('hallClosed')));
    }

    const moment = location.hash.match(/^#t=(\d+)$/);
    if (moment) goTo(Number(moment[1]));
  }

  $('code-form').addEventListener('submit', (e) => {
    e.preventDefault();
    store.set('libraryCode', $('code').value.trim());
    load();
  });

  // ----- Experimental signing avatar: what it signed live, at the moment it was said. Only for talks someone watched it in -----
  let signs = [];
  let signer = null;
  let lastMs = -1;
  const signStatus = (key) => { $('sign-status').hidden = false; $('sign-status').textContent = t(key); };

  async function loadSigns() {
    const response = await api(`/api/talks/${TALK}/signs`).catch(() => null);
    signs = response && response.ok ? await response.json() : [];
    $('sign-toggle').hidden = signs.length === 0;
    $('set-sign').checked = store.get('sign', false);
    applySign();
  }

  function applySign() {
    const on = $('set-sign').checked && signs.length > 0;
    $('sign-pip').hidden = !on;
    $('sign-pip').parentElement.classList.toggle('signing', on);
    $('sign-status').hidden = !on;
    if (!on || signer) return;
    signStatus('signLoading');
    TLSign.load().then(() => {
      // One piece waiting at most, as in the room: a longer queue would drift from the video.
      signer ??= TLSign.player({ maxQueued: 1 });
      signStatus('signReady');
    }).catch(() => signStatus('signFailed'));
  }

  $('set-sign').addEventListener('change', (e) => { store.set('sign', e.target.checked); applySign(); });

  // Signs what was said since the last check. A jump in the video starts over from there without signing what it skipped.
  $('replay').addEventListener('timeupdate', () => {
    if (currentPart < 0) return;
    const now = parts[currentPart].start + $('replay').currentTime * 1000;
    const moved = now - lastMs;
    const from = lastMs;
    lastMs = now;
    if (!signer || !$('set-sign').checked || $('replay').paused || moved < 0 || moved > 2000) return;
    for (const sign of signs) if (sign.t > from && sign.t <= now) signer.say(sign.item);
  });

  $('replay').addEventListener('ended', () => {
    if (currentPart + 1 < parts.length) playPart(currentPart + 1, 0, true);
  });

  $('ask-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const question = $('question').value.trim();
    if (!question) return;
    $('question').value = '';
    const card = el('article', 'answer');
    const body = el('div', 'answer-body muted', t('thinking'));
    card.append(el('p', 'answer-question', question), body);
    $('answers').prepend(card);
    const response = await api(`/api/talks/${TALK}/ask`, { method: 'POST', json: { question } }).catch(() => null);
    if (!response || !response.ok) {
      body.textContent = t('busy');
      return;
    }
    answerBody(body, await response.json(), words, (c) => goTo(c.t, true));
  });

  load();
})();
