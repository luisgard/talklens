// RF-16, RF-37: a sponsor's own link, made for a phone at the stand. Their one offer, the people who asked for it
// (marked attended and relevant as they go), the people who shared their contact, and mention evidence.
(function () {
  const { el, strings } = TL;
  const t = strings({
    en: {
      intro: 'Your offer, who asked for it, and your results from this event.', invalid: 'This link is not valid.',
      offerTitle: 'Your offer', offerHint: 'One concrete thing people can ask for. It appears in everyone’s Opportunities.',
      kind: 'Type', kind_demo: 'Demo', kind_beta: 'Beta', kind_chat: 'Chat', kind_info: 'Info',
      title: 'What you offer', titlePlaceholder: 'Bring a problem and we review it for 10 min', details: 'Details (optional)',
      slots: 'Times (optional, one per line)', slotsPlaceholder: '11:00\n11:15\n11:30', save: 'Save', cancel: 'Cancel', edit: 'Edit', remove: 'Take down',
      noOffer: 'You have no offer yet. People can only ask for something you publish.', tooLong: 'Something is too long. Shorten it and try again.',
      requestsTitle: 'Requests', asked: 'Asked', attendedCount: 'Attended', relevantCount: 'Relevant',
      noRequests: 'Nobody has asked yet. When someone does, they appear here.', attended: 'Attended', markAttended: 'Mark attended', undo: 'Undo',
      relevant: 'Relevant?', yes: 'Yes', no: 'No', at: 'At {slot}', seeking: 'Looking for', note: 'Note', email: 'Email', noEmail: 'Did not share an email',
      leadsTitle: 'People who chose to share their contact', download: 'Download CSV', noLeads: 'Nobody has shared their contact yet.', relatedTalks: 'Related talks: {talks}',
      mentionsTitle: 'Mentions in talks', mentionsSpoken: 'Mentions by speakers and on slides', mentionsQuestions: 'Questions to speakers that mention you',
      mentionsChat: 'Public chat messages that mention you', under: 'Under {n}', noMentions: 'No mentions yet of: {terms}', slide: 'slide', said: 'said',
    },
    es: {
      intro: 'Tu oferta, quién la pidió y tus resultados del evento.', invalid: 'Este enlace no es válido.',
      offerTitle: 'Tu oferta', offerHint: 'Una cosa concreta que la gente pueda pedir. Aparece en Oportunidades de todos.',
      kind: 'Tipo', kind_demo: 'Demo', kind_beta: 'Beta', kind_chat: 'Plática', kind_info: 'Info',
      title: 'Qué ofreces', titlePlaceholder: 'Trae un problema y lo revisamos 10 min', details: 'Detalles (opcional)',
      slots: 'Horarios (opcional, uno por línea)', slotsPlaceholder: '11:00\n11:15\n11:30', save: 'Guardar', cancel: 'Cancelar', edit: 'Editar', remove: 'Quitar',
      noOffer: 'Todavía no tienes oferta. La gente solo puede pedir lo que publiques.', tooLong: 'Algo es demasiado largo. Acórtalo e intenta de nuevo.',
      requestsTitle: 'Solicitudes', asked: 'Pedidas', attendedCount: 'Atendidas', relevantCount: 'Relevantes',
      noRequests: 'Nadie ha pedido todavía. Cuando alguien lo haga, aparecerá aquí.', attended: 'Atendido', markAttended: 'Marcar atendido', undo: 'Deshacer',
      relevant: '¿Relevante?', yes: 'Sí', no: 'No', at: 'A las {slot}', seeking: 'Busca', note: 'Nota', email: 'Email', noEmail: 'No compartió email',
      leadsTitle: 'Personas que eligieron compartir su contacto', download: 'Descargar CSV', noLeads: 'Nadie ha compartido su contacto todavía.', relatedTalks: 'Charlas relacionadas: {talks}',
      mentionsTitle: 'Menciones en charlas', mentionsSpoken: 'Menciones de ponentes y en diapositivas', mentionsQuestions: 'Preguntas a ponentes que te mencionan',
      mentionsChat: 'Mensajes del chat público que te mencionan', under: 'Menos de {n}', noMentions: 'Todavía no hay menciones de: {terms}', slide: 'diapositiva', said: 'dicho',
    },
  });
  const $ = (id) => document.getElementById(id);
  const key = encodeURIComponent(location.pathname.split('/').pop());
  const KINDS = ['demo', 'beta', 'chat', 'info'];
  const REFRESH_MS = 15000;
  let offer = null;
  let editing = false;
  let dirty = false;  // typed into the editor and not saved yet

  const time = (ms) => {
    const total = Math.floor(ms / 1000);
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
  };
  const clock = (ms) => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  // Names are chosen by attendees, and a spreadsheet would run a leading = as a formula.
  const cell = (value) => `"${(/^[=+\-@]/.test(value) ? "'" + value : value).replaceAll('"', '""')}"`;
  const send = (path, method, json) => fetch(`/api/sponsor/${key}${path}`, {
    method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(json),
  }).catch(() => null);

  function button(className, text, onClick) {
    const b = el('button', className, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
  }

  function stat(label, value, minimum) {
    const box = el('div', 'stat');
    box.append(el('b', '', value === null || value === undefined ? t('under', { n: minimum }) : value), el('span', 'muted', label));
    return box;
  }

  // ----- The offer -----
  function renderOffer() {
    const card = $('offer');
    if (offer && !editing) {
      card.oninput = null;
      const head = el('div', 'person-head');
      const who = el('div', 'offer-who');
      who.append(el('span', 'offer-sponsor', t('offerTitle')), el('span', 'pill quiet', t(`kind_${offer.kind}`)));
      head.append(who, button('secondary', t('edit'), () => { editing = true; renderOffer(); }));
      card.replaceChildren(head, el('h3', '', offer.title));
      if (offer.details) card.append(el('p', 'offer-details', offer.details));
      if (offer.slots) {
        const row = el('div', 'why');
        offer.slots.split('\n').forEach((s) => row.append(el('span', 'why-chip', s)));
        card.append(row);
      }
      return;
    }
    let kind = offer ? offer.kind : 'chat';
    const kinds = el('div', 'segmented glass');
    kinds.setAttribute('role', 'radiogroup');
    kinds.setAttribute('aria-label', t('kind'));
    for (const k of KINDS) {
      const b = button('', t(`kind_${k}`), () => {
        kind = k;
        kinds.querySelectorAll('button').forEach((x) => x.setAttribute('aria-checked', String(x === b)));
      });
      b.setAttribute('role', 'radio');
      b.setAttribute('aria-checked', String(k === kind));
      kinds.append(b);
    }
    const field = (label, input) => {
      const box = el('label', 'sheet-field');
      input.setAttribute('aria-label', label);
      box.append(el('span', '', label), input);
      return box;
    };
    const title = el('input');
    title.maxLength = 90;
    title.placeholder = t('titlePlaceholder');
    title.value = offer ? offer.title : '';
    const details = el('textarea');
    details.maxLength = 300;
    details.rows = 2;
    details.value = offer ? offer.details : '';
    const slots = el('textarea');
    slots.rows = 3;
    slots.placeholder = t('slotsPlaceholder');
    slots.value = offer ? offer.slots : '';
    const note = el('p', 'sheet-note error');
    note.setAttribute('role', 'status');
    const save = async (clear) => {
      const response = await send('/offer', 'PUT', clear ? { title: '' } : { kind, title: title.value, details: details.value, slots: slots.value });
      if (!response || !response.ok) { note.textContent = t('tooLong'); return; }
      editing = false;
      dirty = false;
      await load();
      renderOffer();
    };
    const actions = el('div', 'actions');
    actions.append(button('primary', t('save'), () => save(false)));
    if (offer) {
      actions.append(button('secondary', t('cancel'), () => { editing = false; dirty = false; renderOffer(); }));
      actions.append(button('link-button', t('remove'), () => save(true)));
    }
    card.oninput = () => { dirty = true; };
    card.replaceChildren(el('h2', '', t('offerTitle')), el('p', 'muted small', offer ? t('offerHint') : t('noOffer')),
      kinds, field(t('title'), title), field(t('details'), details), field(t('slots'), slots), note, actions);
  }

  // ----- Requests -----
  async function mark(request, change) {
    const response = await send(`/requests/${request.id}`, 'POST', change);
    if (response && response.ok) await load();
  }

  function requestCard(request) {
    const card = el('article', `card person-card${request.attended ? '' : ' highlight'}`);
    const head = el('div', 'person-head');
    head.append(el('h3', '', request.name), el('span', 'muted small', request.slot ? t('at', { slot: request.slot }) : clock(request.at)));
    card.append(head);
    const list = el('dl', 'profile-lines');
    if (request.seeking) list.append(el('dt', '', t('seeking')), el('dd', '', request.seeking));
    if (request.note) list.append(el('dt', '', t('note')), el('dd', '', request.note));
    list.append(el('dt', '', t('email')), el('dd', request.email ? '' : 'muted', request.email || t('noEmail')));
    card.append(list);
    const actions = el('div', 'actions request-actions');
    if (!request.attended) {
      actions.append(button('primary', t('markAttended'), () => mark(request, { attended: true })));
    } else {
      const done = el('span', 'pill', t('attended'));
      done.dataset.state = 'ok';
      const relevant = el('div', 'relevant');
      relevant.append(el('span', 'small', t('relevant')));
      for (const [value, label] of [[true, 'yes'], [false, 'no']]) {
        const chip = button('chip', t(label), () => mark(request, request.relevant === value ? { clearRelevant: true } : { relevant: value }));
        chip.setAttribute('aria-pressed', String(request.relevant === value));
        relevant.append(chip);
      }
      actions.append(done, relevant, button('link-button', t('undo'), () => mark(request, { attended: false })));
    }
    card.append(actions);
    return card;
  }

  function renderRequests(requests) {
    $('request-totals').replaceChildren(
      stat(t('asked'), requests.length),
      stat(t('attendedCount'), requests.filter((r) => r.attended).length),
      stat(t('relevantCount'), requests.filter((r) => r.relevant === true).length),
    );
    // Waiting first, in the order they asked; attended ones after.
    const ordered = [...requests.filter((r) => !r.attended), ...requests.filter((r) => r.attended).reverse()];
    $('requests').replaceChildren(...(ordered.length ? ordered.map(requestCard) : [el('p', 'empty', t('noRequests'))]));
  }

  // ----- Contacts and mentions -----
  function renderReport(report, leads) {
    $('leads').replaceChildren(...(leads.length ? leads.map((lead) => {
      const row = el('div', 'talk-row');
      const info = el('div');
      info.append(el('b', '', lead.name), el('p', '', lead.email));
      if (lead.talks.length) info.append(el('p', 'muted small', t('relatedTalks', { talks: lead.talks.join(', ') })));
      row.append(info);
      return row;
    }) : [el('p', 'muted', t('noLeads'))]));
    const csv = ['name,email,related_talks', ...leads.map((l) => [l.name, l.email, l.talks.join('; ')].map(cell).join(','))].join('\n');
    $('download').href = 'data:text/csv;charset=utf-8,' + encodeURIComponent(csv);
    $('download').hidden = leads.length === 0;

    $('totals').replaceChildren(
      stat(t('mentionsSpoken'), report.mentions.length, 1),
      stat(t('mentionsQuestions'), report.questionMentions, report.minimum),
      stat(t('mentionsChat'), report.chatMentions, report.minimum),
    );
    $('mentions').replaceChildren(...(report.mentions.length ? report.mentions.map((mention) => {
      const row = el('p', 'search-hit');
      const a = el('a', 'cite', time(mention.t));
      a.href = `/talk/${mention.talkId}#t=${mention.t}`;
      row.append(a, ` ${mention.talkTitle} (${t(mention.kind === 'slide' ? 'slide' : 'said')}): ${mention.excerpt}`);
      return row;
    }) : [el('p', 'muted', t('noMentions', { terms: report.terms.join(', ') }))]));
  }

  async function load() {
    const [mine, results] = await Promise.all([fetch(`/api/sponsor/${key}/offer`).catch(() => null), fetch(`/api/sponsor/${key}`).catch(() => null)]);
    if (!mine || !mine.ok) {
      $('name').textContent = t('invalid');
      return;
    }
    const data = await mine.json();
    $('name').textContent = data.name;
    document.title = data.name;
    offer = data.offer;
    if (!dirty && !editing && !$('offer').contains(document.activeElement)) renderOffer();
    renderRequests(data.requests);
    if (results && results.ok) {
      const { report, leads } = await results.json();
      renderReport(report, leads);
    }
  }

  load();
  // New requests arrive while the sponsor stands there. What they are typing into their offer is left alone.
  setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
})();
