// RF-12, RF-19: every talk of the event in one place, searchable and open to questions.
(function () {
  const { el, link, strings, api, store, formatTime, formatWhen, mountNav, answerBody } = TL;
  const t = strings({
    en: {
      title: 'Library', intro: 'Search everything said and shown across the event, or ask a question that spans several talks.',
      lockedText: 'Finished talks need the library code from the organizer, or your event ticket.', unlock: 'Unlock', searchPlaceholder: 'Which talks mentioned eBPF?', ask: 'Ask',
      allTalks: 'All talks', noTalks: 'No talks have been kept yet.', live: 'Live', hallway: 'Hallway open', thinking: 'Looking through the talks…', notFound: 'None of the talks covered that.',
      offline: 'Without internet: these are matching moments, not a written answer.', busy: 'Too many requests. Try in a moment.', foundIn: 'Found in {n} talks', from: 'From: {title}', matches: '{n} matches', wrongCode: 'That code did not work.', video: 'Video', related: 'Related',
    },
    es: {
      title: 'Biblioteca', intro: 'Busca todo lo dicho y mostrado en el evento, o haz una pregunta que abarque varias charlas.',
      lockedText: 'Las charlas terminadas piden el código de biblioteca del organizador, o tu ticket del evento.', unlock: 'Abrir', searchPlaceholder: '¿En qué charlas se habló de eBPF?', ask: 'Preguntar',
      allTalks: 'Todas las charlas', noTalks: 'Todavía no se ha guardado ninguna charla.', live: 'En vivo', hallway: 'Pasillo abierto', thinking: 'Buscando en las charlas…', notFound: 'Ninguna charla tocó eso.',
      offline: 'Sin internet: son momentos que coinciden, no una respuesta escrita.', busy: 'Demasiadas solicitudes. Intenta en un momento.', foundIn: 'Encontrado en {n} charlas', from: 'De: {title}', matches: '{n} coincidencias', wrongCode: 'Ese código no funcionó.', video: 'Video', related: 'Relacionado',
    },
  });
  mountNav('library');
  const $ = (id) => document.getElementById(id);

  function renderTalks(talks) {
    if (talks.length === 0) {
      $('talks').replaceChildren(el('p', 'muted', t('noTalks')));
      return;
    }
    $('talks').replaceChildren(...talks.map((talk) => {
      const box = el('article', 'room-card');
      const top = el('div', 'room-card-top');
      top.append(el('span', '', [talk.room, formatWhen(talk.startedAtUnixMs)].filter(Boolean).join(' · ')));
      if (talk.live) {
        const pill = el('span', 'pill', t('live'));
        pill.dataset.state = 'live';
        top.append(pill);
      } else if (talk.hallway) {
        top.append(el('span', 'pill', t('hallway')));
      } else if (talk.video) {
        top.append(el('span', 'pill', t('video')));
      }
      const heading = el('h3');
      heading.append(link('', `/talk/${talk.id}`, talk.title));
      box.append(top, heading);
      if (talk.speaker) box.append(el('p', 'who', talk.speaker));
      return box;
    }));
  }

  async function loadTalks() {
    const data = await (await api('/api/library')).json();
    $('locked').hidden = !data.locked;
    $('search-form').hidden = data.locked;
    renderTalks(data.talks);
    return data;
  }

  function renderResults(results) {
    const nodes = results.map((talk) => {
      const box = el('section', 'card');
      const heading = el('h3');
      heading.append(link('', `/talk/${talk.talkId}`, talk.title));
      box.append(heading, el('p', 'muted small', [talk.speaker, t('matches', { n: talk.count })].filter(Boolean).join(' · ')));
      for (const moment of talk.moments) {
        const row = el('p', 'search-hit');
        row.append(link('cite', `/talk/${talk.talkId}#t=${moment.t}`, formatTime(moment.t)), ' ' + moment.text);
        if (moment.byMeaning) row.append(el('span', 'by-meaning', t('related')));
        box.append(row);
      }
      return box;
    });
    if (results.length) nodes.unshift(el('h2', 'section-title', t('foundIn', { n: results.length })));
    $('results').replaceChildren(...nodes);
  }

  $('search-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const question = $('query').value.trim();
    if (!question) return;
    const card = el('article', 'answer');
    const body = el('div', 'answer-body muted', t('thinking'));
    card.append(el('p', 'answer-question', question), body);
    $('answer').replaceChildren(card);

    const search = await api(`/api/library/search?q=${encodeURIComponent(question)}`).catch(() => null);
    renderResults(search && search.ok ? await search.json() : []);

    const response = await api('/api/library/ask', { method: 'POST', json: { question } }).catch(() => null);
    if (!response || !response.ok) {
      body.textContent = t('busy');
      return;
    }
    const answer = await response.json();
    const owners = new Map((answer.talks || []).map((c) => [c.n, c]));
    answerBody(body, answer, { notFound: t('notFound'), offline: t('offline') }, (c) => {
      const owner = owners.get(c.n);
      if (owner) location.href = `/talk/${owner.talkId}#t=${c.t}`;
    });
  });

  $('code-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    store.set('libraryCode', $('code').value.trim());
    const data = await loadTalks();
    if (data.locked) alert(t('wrongCode'));
  });

  loadTalks();
})();
