// One place to meet people and find what sponsors offer. Nothing about the person is shown until they turn it on.
(function () {
  const { el, strings, api, mountNav, myProfile, editProfile } = TL;
  const t = strings({
    en: {
      title: 'Connect', intro: 'Meet the people here who can help you, or whom you can help.', people: 'People', offers: 'Opportunities',
      startTitle: 'Tell people what you are after', startText: 'A nickname and one line is enough. You choose whether anyone sees it.', start: 'Create my profile',
      seeking: 'Looking for', offering: 'Can help with', visible: 'Visible to others', hidden: 'Only you can see this', edit: 'Edit',
      peopleHidden: 'Turn on "Show me to others" to see and be seen.', peopleNone: 'Nobody else has shared a profile yet. As people do, they will appear here, best match first.',
      offersEmpty: 'Sponsor offers will appear here: demos, betas and short sessions with their team.',
      incomingTitle: 'Want to meet you', togetherTitle: 'Your meetings', suggestionsTitle: 'People for you',
      offersWhatYouSeek: 'Offers what you are looking for', seeksWhatYouOffer: 'Looking for what you offer', sharedTalk: 'Also at “{talk}”', byMeaning: 'Matched by meaning',
      connect: 'Connect', sent: 'Request sent', connected: 'Connected', met: 'You met',
      accept: 'Accept', notNow: 'Not now', cancel: 'Cancel', send: 'Send',
      askTitle: 'Connect with {name}', askText: 'Say why in a few words. It helps them say yes.',
      reasonSeek: 'I am interested in what you offer', reasonOffer: 'I can help with what you are looking for', reasonTalk: 'We were at the same talk',
      reasonPlaceholder: 'Or write your own (optional)',
      whereTitle: 'Where do you meet?', whereText: '{name} will see this as soon as you accept.', otherSpot: 'Somewhere else…',
      spotDoor: 'At the entrance', spotCoffee: 'At the coffee area', spotRoom: 'Outside this room',
      meetAt: 'Meet at: {spot}', meetNoSpot: 'Agree on a place when you see each other.', weMet: 'We met',
      meetTitle: 'Show this to {name}', meetText: 'When they scan it with their phone camera, your meeting is confirmed. The code lasts 5 minutes.',
      metDone: 'Done. You are now in each other’s contacts.', useful: 'Was it useful?', usefulYes: 'Yes', usefulSome: 'A little', usefulNo: 'No', thanks: 'Thanks for telling us.',
      tooMany: 'You have many requests waiting. Give people time to answer.', failed: 'That did not work. Try again.',
      offersStart: 'Create your profile to ask for an offer. Sponsors only see what you choose.',
      kind_demo: 'Demo', kind_beta: 'Beta', kind_chat: 'Chat', kind_info: 'Info', fits: 'Fits what you are looking for',
      want: 'I want it', requested: 'Requested', requestedAt: 'Requested · {slot}', attended: 'Done', takeBack: 'Cancel request',
      wantTitle: '{sponsor}', pickSlot: 'Pick a time', slotTaken: 'taken', sharedTitle: '{sponsor} will see', sharedName: 'Name',
      sharedSeeking: 'Looking for', shareEmail: 'Also share my email', notShared: 'Nothing else: not the talks you went to, not your contacts.',
      notePlaceholder: 'A note for them (optional)', sendRequest: 'Send request', slotGone: 'Someone just took that time. Pick another one.',
    },
    es: {
      title: 'Conectar', intro: 'Conoce a la gente de aquí que te puede ayudar, o a quien tú puedes ayudar.', people: 'Personas', offers: 'Oportunidades',
      startTitle: 'Cuenta qué buscas', startText: 'Con un apodo y una línea basta. Tú decides si alguien lo ve.', start: 'Crear mi perfil',
      seeking: 'Busca', offering: 'Puede aportar', visible: 'Visible para otros', hidden: 'Solo tú lo ves', edit: 'Editar',
      peopleHidden: 'Activa "Mostrarme a otros" para ver y que te vean.', peopleNone: 'Todavía nadie más ha compartido su perfil. Cuando lo hagan, aparecerán aquí, primero quien mejor encaje.',
      offersEmpty: 'Aquí aparecerán las ofertas de los patrocinadores: demos, betas y sesiones cortas con su equipo.',
      incomingTitle: 'Te quieren conocer', togetherTitle: 'Tus encuentros', suggestionsTitle: 'Personas para ti',
      offersWhatYouSeek: 'Aporta lo que buscas', seeksWhatYouOffer: 'Busca lo que tú aportas', sharedTalk: 'También en “{talk}”', byMeaning: 'Encontrado por significado',
      connect: 'Conectar', sent: 'Solicitud enviada', connected: 'Conectados', met: 'Se conocieron',
      accept: 'Aceptar', notNow: 'Ahora no', cancel: 'Cancelar', send: 'Enviar',
      askTitle: 'Conectar con {name}', askText: 'Di por qué en pocas palabras. Le ayuda a decir que sí.',
      reasonSeek: 'Me interesa lo que aportas', reasonOffer: 'Puedo ayudarte con lo que buscas', reasonTalk: 'Estuvimos en la misma charla',
      reasonPlaceholder: 'O escribe el tuyo (opcional)',
      whereTitle: '¿Dónde se ven?', whereText: '{name} lo verá en cuanto aceptes.', otherSpot: 'En otro lugar…',
      spotDoor: 'En la entrada', spotCoffee: 'En la zona de café', spotRoom: 'Afuera de esta sala',
      meetAt: 'Se ven en: {spot}', meetNoSpot: 'Pónganse de acuerdo cuando se vean.', weMet: 'Ya nos vimos',
      meetTitle: 'Muéstrale esto a {name}', meetText: 'Cuando lo escanee con la cámara de su teléfono, el encuentro queda confirmado. El código dura 5 minutos.',
      metDone: 'Listo. Ya están en los contactos del otro.', useful: '¿Te sirvió?', usefulYes: 'Sí', usefulSome: 'Un poco', usefulNo: 'No', thanks: 'Gracias por contarnos.',
      tooMany: 'Tienes muchas solicitudes esperando. Dale tiempo a la gente para responder.', failed: 'No funcionó. Intenta de nuevo.',
      offersStart: 'Crea tu perfil para pedir una oferta. Los patrocinadores solo ven lo que tú elijas.',
      kind_demo: 'Demo', kind_beta: 'Beta', kind_chat: 'Plática', kind_info: 'Info', fits: 'Encaja con lo que buscas',
      want: 'Lo quiero', requested: 'Solicitado', requestedAt: 'Solicitado · {slot}', attended: 'Atendido', takeBack: 'Cancelar solicitud',
      wantTitle: '{sponsor}', pickSlot: 'Elige un horario', slotTaken: 'ocupado', sharedTitle: '{sponsor} verá', sharedName: 'Nombre',
      sharedSeeking: 'Busca', shareEmail: 'Compartir también mi email', notShared: 'Nada más: ni las charlas a las que fuiste, ni tus contactos.',
      notePlaceholder: 'Una nota para ellos (opcional)', sendRequest: 'Enviar solicitud', slotGone: 'Alguien acaba de tomar ese horario. Elige otro.',
    },
  });
  mountNav('connect');
  const $ = (id) => document.getElementById(id);
  const REFRESH_MS = 20000;
  const MEET_CHECK_MS = 3000;
  let profile = null;
  let connections = { incoming: [], together: [], spots: [] };
  let people = [];

  function button(className, text, onClick) {
    const b = el('button', className, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
  }

  // ----- A glass sheet from the bottom, the same as the profile one -----
  function sheet(title, text) {
    const dialog = el('dialog', 'sheet');
    const body = el('div', 'sheet-body');
    const heading = el('h2', '', title);
    heading.id = `sheet-${Date.now()}`;
    dialog.setAttribute('aria-labelledby', heading.id);
    body.append(el('span', 'sheet-grip'), heading);
    if (text) body.append(el('p', 'muted', text));
    dialog.append(body);
    document.body.append(dialog);
    dialog.addEventListener('click', (event) => { if (event.target === dialog) dialog.close(); });
    dialog.addEventListener('close', () => dialog.remove());
    return { dialog, body, open: () => dialog.showModal(), close: () => dialog.close() };
  }

  /** Chips that pick one answer; the last one can open a text box. */
  function choices(options, onPick) {
    const row = el('div', 'chips choice-chips');
    row.setAttribute('role', 'radiogroup');
    for (const option of options) {
      const chip = button('chip', option, () => {
        row.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-checked', String(c === chip)));
        onPick(option);
      });
      chip.setAttribute('role', 'radio');
      chip.setAttribute('aria-checked', 'false');
      row.append(chip);
    }
    return row;
  }

  function lines(person) {
    const list = el('dl', 'profile-lines');
    for (const [key, value] of [['seeking', person.seeking], ['offering', person.offering]]) {
      if (value) list.append(el('dt', '', t(key)), el('dd', '', value));
    }
    return list;
  }

  function section(title, items) {
    if (!items.length) return [];
    return [el('h2', 'section-title', title), ...items];
  }

  // ----- Asking -----
  function ask(person) {
    const s = sheet(t('askTitle', { name: person.name }), t('askText'));
    const suggestions = [];
    if (person.offersWhatYouSeek) suggestions.push(t('reasonSeek'));
    if (person.seeksWhatYouOffer) suggestions.push(t('reasonOffer'));
    if (person.sharedTalks.length) suggestions.push(t('reasonTalk'));
    let reason = '';
    const own = el('input');
    own.type = 'text';
    own.maxLength = 160;
    own.placeholder = t('reasonPlaceholder');
    own.setAttribute('aria-label', t('reasonPlaceholder'));
    const note = el('p', 'sheet-note error');
    note.setAttribute('role', 'status');
    const send = button('primary', t('send'), async () => {
      send.disabled = true;
      const response = await api('/api/connections', { method: 'POST', json: { to: person.id, reason: own.value.trim() || reason } }).catch(() => null);
      send.disabled = false;
      if (!response || (!response.ok && response.status !== 409)) {
        note.textContent = t(response && response.status === 429 ? 'tooMany' : 'failed');
        return;
      }
      s.close();
      refresh();
    });
    const actions = el('div', 'sheet-actions');
    actions.append(button('secondary', t('cancel'), s.close), send);
    if (suggestions.length) s.body.append(choices(suggestions, (picked) => { reason = picked; }));
    s.body.append(own, note, actions);
    s.open();
  }

  // ----- Answering -----
  function acceptRequest(request) {
    const s = sheet(t('whereTitle'), t('whereText', { name: request.name }));
    const spots = connections.spots.length ? connections.spots : [t('spotDoor'), t('spotCoffee'), t('spotRoom')];
    let spot = '';
    const other = el('input');
    other.type = 'text';
    other.maxLength = 80;
    other.placeholder = t('otherSpot');
    other.setAttribute('aria-label', t('otherSpot'));
    const send = button('primary', t('accept'), async () => {
      send.disabled = true;
      const response = await api(`/api/connections/${request.id}/answer`, { method: 'POST', json: { accept: true, spot: other.value.trim() || spot } }).catch(() => null);
      send.disabled = false;
      if (response && response.ok) { s.close(); refresh(); }
    });
    const actions = el('div', 'sheet-actions');
    actions.append(button('secondary', t('cancel'), s.close), send);
    s.body.append(choices(spots, (picked) => { spot = picked; other.value = ''; }), other, actions);
    s.open();
  }

  async function decline(request) {
    await api(`/api/connections/${request.id}/answer`, { method: 'POST', json: { accept: false } }).catch(() => null);
    refresh();
  }

  // ----- Confirming the meeting: one shows a code, the other scans it -----
  async function meet(link) {
    const response = await api('/api/me/contact-code', { method: 'POST' }).catch(() => null);
    if (!response || !response.ok) return;
    const data = await response.json();
    const s = sheet(t('meetTitle', { name: link.name }), t('meetText'));
    const qr = el('div', 'qr meet-qr');
    const img = el('img');
    img.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(data.svg)}`;
    img.alt = t('meetTitle', { name: link.name });
    qr.append(img);
    const actions = el('div', 'sheet-actions');
    actions.append(button('secondary', t('cancel'), s.close));
    s.body.append(qr, actions);
    s.open();
    const timer = setInterval(async () => {
      await loadConnections();
      const now = connections.together.find((c) => c.id === link.id);
      if (now && now.status === 'met') {
        clearInterval(timer);
        s.body.replaceChildren(el('span', 'sheet-grip'), el('h2', '', t('metDone')), usefulRow(now, s.close));
        render();
      }
    }, MEET_CHECK_MS);
    s.dialog.addEventListener('close', () => clearInterval(timer));
  }

  function usefulRow(link, after) {
    const box = el('div', 'useful');
    box.append(el('p', '', t('useful')));
    const row = el('div', 'chips');
    for (const [answer, key] of [[2, 'usefulYes'], [1, 'usefulSome'], [0, 'usefulNo']]) {
      const chip = button('chip', t(key), async () => {
        await api(`/api/connections/${link.id}/useful`, { method: 'POST', json: { answer } }).catch(() => null);
        link.useful = answer;
        box.replaceChildren(el('p', 'muted', t('thanks')));
        if (after) setTimeout(() => { after(); render(); }, 900);
      });
      chip.setAttribute('aria-pressed', String(link.useful === answer));
      row.append(chip);
    }
    box.append(row);
    return box;
  }

  // ----- Cards -----
  function incomingCard(request) {
    const card = el('article', 'card person-card highlight');
    card.append(el('h3', '', request.name));
    if (request.reason) card.append(el('blockquote', 'reason', request.reason));
    card.append(lines(request));
    const actions = el('div', 'actions');
    actions.append(button('primary', t('accept'), () => acceptRequest(request)), button('secondary', t('notNow'), () => decline(request)));
    card.append(actions);
    return card;
  }

  function togetherCard(link) {
    const card = el('article', 'card person-card');
    const head = el('div', 'person-head');
    head.append(el('h3', '', link.name));
    if (link.status === 'met') {
      const done = el('span', 'pill', t('met'));
      done.dataset.state = 'ok';
      head.append(done);
    }
    card.append(head);
    if (link.status === 'met') {
      card.append(link.useful === null ? usefulRow(link) : el('p', 'muted small', t('thanks')));
      return card;
    }
    card.append(el('p', 'meet-spot', link.spot ? t('meetAt', { spot: link.spot }) : t('meetNoSpot')));
    const actions = el('div', 'actions');
    actions.append(button('primary', t('weMet'), () => meet(link)));
    card.append(actions);
    return card;
  }

  function personCard(person) {
    const card = el('article', 'card person-card');
    const head = el('div', 'person-head');
    head.append(el('h3', '', person.name));
    const why = el('div', 'why');
    if (person.offersWhatYouSeek) why.append(el('span', 'why-chip strong', t('offersWhatYouSeek')));
    if (person.seeksWhatYouOffer) why.append(el('span', 'why-chip strong', t('seeksWhatYouOffer')));
    for (const talk of person.sharedTalks) why.append(el('span', 'why-chip', t('sharedTalk', { talk })));
    if (person.byMeaning) why.append(el('span', 'why-chip meaning', t('byMeaning')));
    const link = person.link;
    const action = !link ? button('secondary', t('connect'), () => ask(person))
      : el('span', 'pill', t(link.status === 'met' ? 'met' : link.status === 'accepted' ? 'connected' : 'sent'));
    if (link && link.status !== 'requested') action.dataset.state = 'ok';
    head.append(action);
    card.append(head);
    if (why.childElementCount) card.append(why);
    card.append(lines(person));
    return card;
  }

  // ----- Sponsor offers -----
  /** Shows exactly what the sponsor will receive before anything is sent. */
  function want(offer) {
    const s = sheet(t('wantTitle', { sponsor: offer.sponsor }), offer.title);
    let slot = '';
    const free = offer.slots.filter((x) => !x.taken);
    if (offer.slots.length) {
      s.body.append(el('p', 'sheet-label', t('pickSlot')));
      const row = choices(offer.slots.map((x) => x.label), (picked) => { slot = picked; });
      row.querySelectorAll('.chip').forEach((chip, i) => {
        if (!offer.slots[i].taken) return;
        chip.disabled = true;
        chip.append(el('span', 'chip-note', ` · ${t('slotTaken')}`));
      });
      s.body.append(row);
    }
    const shared = el('div', 'share-preview');
    shared.append(el('p', 'sheet-label', t('sharedTitle', { sponsor: offer.sponsor })));
    const list = el('dl', 'profile-lines');
    list.append(el('dt', '', t('sharedName')), el('dd', '', share.name));
    if (share.seeking) list.append(el('dt', '', t('sharedSeeking')), el('dd', '', share.seeking));
    const email = [el('dt', '', 'Email'), el('dd', '', share.email)];
    email.forEach((e) => { e.hidden = true; });
    list.append(...email);
    shared.append(list);
    let emailInput = null;
    if (share.email) {
      const box = el('label', 'switch');
      emailInput = el('input');
      emailInput.type = 'checkbox';
      emailInput.setAttribute('role', 'switch');
      emailInput.setAttribute('aria-label', t('shareEmail'));
      emailInput.addEventListener('change', () => email.forEach((e) => { e.hidden = !emailInput.checked; }));
      box.append(el('span', '', t('shareEmail')), emailInput);
      shared.append(box);
    }
    shared.append(el('p', 'muted small', t('notShared')));
    const noteInput = el('input');
    noteInput.type = 'text';
    noteInput.maxLength = 160;
    noteInput.placeholder = t('notePlaceholder');
    noteInput.setAttribute('aria-label', t('notePlaceholder'));
    const note = el('p', 'sheet-note error');
    note.setAttribute('role', 'status');
    const send = button('primary', t('sendRequest'), async () => {
      if (offer.slots.length && !slot) { note.textContent = t('pickSlot'); return; }
      send.disabled = true;
      const response = await api(`/api/offers/${offer.sponsorId}/request`, {
        method: 'POST', json: { slot, shareEmail: !!(emailInput && emailInput.checked), note: noteInput.value.trim() },
      }).catch(() => null);
      send.disabled = false;
      if (!response || !response.ok) {
        note.textContent = t(response && response.status === 409 && free.length ? 'slotGone' : response && response.status === 429 ? 'tooMany' : 'failed');
        if (response && response.status === 409) loadOffers();
        return;
      }
      s.close();
      await loadOffers();
    });
    const actions = el('div', 'sheet-actions');
    actions.append(button('secondary', t('cancel'), s.close), send);
    s.body.append(shared, noteInput, note, actions);
    s.open();
  }

  async function takeBack(offer) {
    await api(`/api/offers/requests/${offer.request.id}`, { method: 'DELETE' }).catch(() => null);
    await loadOffers();
  }

  function offerCard(offer) {
    const card = el('article', `card person-card offer-card${offer.fits ? ' highlight' : ''}`);
    const head = el('div', 'person-head');
    const who = el('div', 'offer-who');
    who.append(el('span', 'offer-sponsor', offer.sponsor), el('span', 'pill quiet', t(`kind_${offer.kind}`)));
    head.append(who);
    card.append(head, el('h3', '', offer.title));
    if (offer.details) card.append(el('p', 'offer-details', offer.details));
    if (offer.fits) {
      const why = el('div', 'why');
      why.append(el('span', 'why-chip strong', t('fits')));
      card.append(why);
    }
    const actions = el('div', 'actions');
    const request = offer.request;
    if (!request) {
      const all = offer.slots.length > 0 && offer.slots.every((x) => x.taken);
      const b = button('primary', t('want'), () => want(offer));
      b.disabled = all;
      actions.append(b);
    } else {
      const pill = el('span', 'pill', request.attended ? t('attended') : request.slot ? t('requestedAt', { slot: request.slot }) : t('requested'));
      pill.dataset.state = 'ok';
      actions.append(pill);
      if (!request.attended) actions.append(button('link-button', t('takeBack'), () => takeBack(offer)));
    }
    card.append(actions);
    return card;
  }

  let offers = [];
  let share = { name: '', seeking: '', email: '' };

  function renderOffers() {
    if (!profile) return $('offers').replaceChildren(el('p', 'empty', t('offersStart')), button('primary', t('start'), edit));
    $('offers').replaceChildren(...(offers.length ? offers.map(offerCard) : [el('p', 'empty', t('offersEmpty'))]));
  }

  async function loadOffers() {
    if (profile) {
      const response = await api('/api/offers').catch(() => null);
      if (response && response.ok) ({ offers, share } = await response.json());
    }
    renderOffers();
  }

  // ----- The page -----
  function renderMine() {
    const card = $('my-card');
    if (!profile) {
      card.replaceChildren(el('h2', '', t('startTitle')), el('p', 'muted', t('startText')), button('primary', t('start'), edit));
      return;
    }
    const head = el('div', 'profile-head');
    const who = el('div');
    const badge = el('span', `pill${profile.networking ? '' : ' quiet'}`, t(profile.networking ? 'visible' : 'hidden'));
    if (profile.networking) badge.dataset.state = 'ok';
    who.append(el('h2', '', profile.name), badge);
    head.append(who, button('secondary', t('edit'), edit));
    card.replaceChildren(head, lines(profile));
  }

  function render() {
    renderMine();
    if (!profile) return $('people').replaceChildren();
    if (!profile.networking) return $('people').replaceChildren(el('p', 'empty', t('peopleHidden')));
    // Requests to me and accepted ones have their own sections above.
    const suggestions = people.filter((p) => !p.link || (p.link.status === 'requested' && p.link.mine));
    $('people').replaceChildren(
      ...section(t('incomingTitle'), connections.incoming.map(incomingCard)),
      ...section(t('togetherTitle'), connections.together.map(togetherCard)),
      ...(suggestions.length ? section(t('suggestionsTitle'), suggestions.map(personCard))
        : connections.together.length || connections.incoming.length ? [] : [el('p', 'empty', t('peopleNone'))]),
    );
  }

  async function loadConnections() {
    const response = await api('/api/connections').catch(() => null);
    if (response && response.ok) connections = await response.json();
  }

  async function refresh() {
    profile = await myProfile();
    if (profile && profile.networking) {
      const [found] = await Promise.all([api('/api/people').catch(() => null), loadConnections()]);
      if (found && found.ok) people = (await found.json()).people;
    }
    render();
    await loadOffers();
  }

  async function edit() {
    const saved = await editProfile(profile);
    if (saved) await refresh();
  }

  // ----- The two halves -----
  const tabs = ['people', 'offers'];
  function select(name) {
    for (const tab of tabs) {
      const b = $(`tab-${tab}`);
      b.setAttribute('aria-selected', String(tab === name));
      b.tabIndex = tab === name ? 0 : -1;
      $(`panel-${tab}`).hidden = tab !== name;
    }
  }
  tabs.forEach((tab, i) => {
    const b = $(`tab-${tab}`);
    b.addEventListener('click', () => select(tab));
    b.addEventListener('keydown', (event) => {
      const step = { ArrowRight: 1, ArrowLeft: -1 }[event.key];
      if (!step) return;
      const next = tabs[(i + step + tabs.length) % tabs.length];
      select(next);
      $(`tab-${next}`).focus();
    });
  });
  select(location.hash === '#offers' ? 'offers' : 'people');

  refresh().then(() => {
    if (!profile && new URLSearchParams(location.search).has('start')) edit();
  });
  // Answers and new requests arrive while the page is open, but never while a sheet is in the way.
  setInterval(() => { if (!document.hidden && !document.querySelector('dialog[open]')) refresh(); }, REFRESH_MS);
})();
