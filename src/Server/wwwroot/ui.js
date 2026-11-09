// Shared by the attendee pages: language, the ticket on this phone, small DOM helpers, the page menu and the chat.
window.TL = (function () {
  const store = {
    get(key, fallback) {
      try {
        const value = localStorage.getItem('talklens.' + key);
        return value === null ? fallback : JSON.parse(value);
      } catch { return fallback; }
    },
    set(key, value) {
      try { localStorage.setItem('talklens.' + key, JSON.stringify(value)); } catch { /* private window */ }
    },
    remove(key) {
      try { localStorage.removeItem('talklens.' + key); } catch { /* private window */ }
    },
  };

  const COMMON = {
    en: {
      navHome: 'Live', navAgenda: 'Agenda', navLibrary: 'Library', navMe: 'My ticket', navCommunity: 'Community', navConnect: 'Connect',
      profileTitle: 'Meet people here', profileIntro: 'Say what you are after and what you can offer. Nothing is shown until you turn it on.',
      profileName: 'Name or nickname', profileSeeking: 'I am looking for…', profileSeekingHint: 'e.g. help moving to Kubernetes, a backend job',
      profileOffering: 'I can help with…', profileOfferingHint: 'e.g. 5 years running Postgres, hiring Go developers',
      profileVisible: 'Show me to others at this event', profileEmail: 'Email (optional)', profileSummary: 'Send me the talk summaries',
      profileEmailNote: 'Only for the summaries and your ticket. Never shown to other people or sponsors unless you choose to.',
      profileSave: 'Save', profileLater: 'Not now', profileEmailTaken: 'That email already has a ticket. Open it from your email. Everything else was saved.',
      profileNeedName: 'Add a name or nickname.', profileBadEmail: 'That email does not look right.',
      chatNote: 'Public: everyone at this talk can read this.', chatEventNote: 'Public: everyone at the event can read this.',
      chatPlaceholder: 'Write to everyone…', chatPublish: 'Publish', chatName: 'Your name (optional)', chatEmpty: 'No messages yet. Say hello.',
      chatClosed: 'This conversation has closed.', chatGuest: 'Guest', rateLimited: 'Too many messages. Wait a moment.', failed: 'That did not work. Try again.',
      pollLabel: 'Poll', pollClosed: 'Poll closed', pollVoted: 'Thanks for voting. You can change your vote while it is open.',
      pollHidden: 'Results show once {n} people have voted.', pollVotes: '{n} votes',
      rateTitle: 'How was this talk?', rateComment: 'Anything for the speaker? (optional, anonymous)', rateSend: 'Send rating', rateThanks: 'Thanks! You can change it any time.',
      rateStars: '{n} of 5 stars', glossaryNote: 'Explained by AI from general knowledge, not by the speaker.', glossaryEmpty: 'No technical terms so far.',
      aiUnavailable: 'The AI is not available right now. Try again later.',
      navPhotos: 'Photos', reactClap: 'Applause', reactFire: 'Fire', reactWow: 'Mind blown', reactLost: 'I am lost',
    },
    es: {
      navHome: 'En vivo', navAgenda: 'Agenda', navLibrary: 'Biblioteca', navMe: 'Mi ticket', navCommunity: 'Comunidad', navConnect: 'Conectar',
      profileTitle: 'Conoce gente aquí', profileIntro: 'Cuenta qué buscas y qué puedes aportar. No se muestra nada hasta que lo actives.',
      profileName: 'Nombre o apodo', profileSeeking: 'Busco…', profileSeekingHint: 'p. ej. ayuda para migrar a Kubernetes, trabajo de backend',
      profileOffering: 'Puedo aportar…', profileOfferingHint: 'p. ej. 5 años con Postgres, contratamos gente de Go',
      profileVisible: 'Mostrarme a otros en este evento', profileEmail: 'Correo (opcional)', profileSummary: 'Mándame los resúmenes de las charlas',
      profileEmailNote: 'Solo para los resúmenes y tu ticket. Nunca se muestra a otras personas ni a patrocinadores, salvo que tú lo decidas.',
      profileSave: 'Guardar', profileLater: 'Ahora no', profileEmailTaken: 'Ese correo ya tiene un ticket. Ábrelo desde tu correo. Todo lo demás se guardó.',
      profileNeedName: 'Agrega un nombre o apodo.', profileBadEmail: 'Ese correo no parece correcto.',
      chatNote: 'Público: todos en esta charla pueden leerlo.', chatEventNote: 'Público: todos en el evento pueden leerlo.',
      chatPlaceholder: 'Escribe para todos…', chatPublish: 'Publicar', chatName: 'Tu nombre (opcional)', chatEmpty: 'Todavía no hay mensajes. Saluda.',
      chatClosed: 'Esta conversación ya cerró.', chatGuest: 'Invitado', rateLimited: 'Demasiados mensajes. Espera un momento.', failed: 'No funcionó. Intenta de nuevo.',
      pollLabel: 'Encuesta', pollClosed: 'Encuesta cerrada', pollVoted: 'Gracias por votar. Puedes cambiar tu voto mientras siga abierta.',
      pollHidden: 'Los resultados aparecen cuando voten {n} personas.', pollVotes: '{n} votos',
      rateTitle: '¿Qué te pareció esta charla?', rateComment: '¿Algo para el ponente? (opcional, anónimo)', rateSend: 'Enviar calificación', rateThanks: '¡Gracias! Puedes cambiarla cuando quieras.',
      rateStars: '{n} de 5 estrellas', glossaryNote: 'Explicado por IA con conocimiento general, no por el ponente.', glossaryEmpty: 'Todavía no hay términos técnicos.',
      aiUnavailable: 'La IA no está disponible ahora. Intenta más tarde.',
      navPhotos: 'Fotos', reactClap: 'Aplausos', reactFire: 'Fuego', reactWow: 'Me voló la cabeza', reactLost: 'Me perdí',
    },
  };

  // English unless the person chooses otherwise.
  const saved = store.get('ui', 'en');
  const language = COMMON[saved] ? saved : 'en';
  document.documentElement.lang = language;

  function translator(own) {
    const table = { ...COMMON[language], ...(own[language] || own.en) };
    return (key, values) => {
      let text = table[key] ?? key;
      for (const [name, value] of Object.entries(values || {})) text = text.replace(`{${name}}`, value);
      return text;
    };
  }

  function strings(own) {
    const t = translator(own);
    document.querySelectorAll('[data-i18n]').forEach((node) => { node.textContent = t(node.dataset.i18n); });
    document.querySelectorAll('[data-i18n-placeholder]').forEach((node) => { node.placeholder = t(node.dataset.i18nPlaceholder); });
    return t;
  }

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function link(className, href, text) {
    const a = el('a', className, text);
    a.href = href;
    return a;
  }

  function formatTime(ms) {
    const total = Math.max(0, Math.floor(ms / 1000));
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = String(total % 60).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
  }

  const clock = new Intl.DateTimeFormat(language, { weekday: 'short', hour: 'numeric', minute: '2-digit' });
  const formatWhen = (ms) => clock.format(new Date(ms));

  const ticket = () => store.get('ticket', '');
  const libraryCode = () => store.get('libraryCode', '');

  async function api(path, options = {}) {
    const headers = { ...(options.headers || {}) };
    if (ticket()) headers['X-Ticket'] = ticket();
    if (libraryCode()) headers['X-Library'] = libraryCode();
    if (options.json !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(path, { ...options, headers, body: options.json !== undefined ? JSON.stringify(options.json) : options.body });
  }

  function nav(active, community) {
    const items = [['/?home=1', 'navHome', 'home'], ['/event', 'navAgenda', 'agenda'], ['/connect', 'navConnect', 'connect'], ['/library', 'navLibrary', 'library'], ['/album', 'navPhotos', 'photos'], ['/me', 'navMe', 'me']];
    if (community) items.push(['/community', 'navCommunity', 'community']);
    const t = (key) => COMMON[language][key];
    const bar = el('nav', 'page-nav');
    bar.setAttribute('aria-label', 'Pages');
    for (const [href, key, name] of items) {
      const a = link('nav-link', href, t(key));
      if (name === active) a.setAttribute('aria-current', 'page');
      bar.append(a);
    }
    return bar;
  }

  function mountNav(active) {
    const place = document.getElementById('page-nav');
    if (!place) return;
    place.replaceWith(nav(active, false));
    const badge = () => {
      if (!ticket()) return;
      api('/api/connections/waiting').then((r) => (r.ok ? r.json() : null)).then((data) => {
        const link = document.querySelector('.page-nav a[href="/connect"]');
        if (!data || !data.count || !link) return;
        const dot = el('span', 'badge', String(data.count));
        link.append(dot);
      }).catch(() => {});
    };
    fetch('/api/rooms').then((r) => r.json()).then((summary) => {
      if (summary.community) document.querySelector('.page-nav').replaceWith(nav(active, true));
      badge();
    }).catch(badge);
  }

  function newHub() {
    return new signalR.HubConnectionBuilder().withUrl('/hub')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * (ctx.previousRetryCount + 1), 5000) })
      .build();
  }

  // ----- Chat: the third conversation. Always labelled public, and sent only by pressing publish. -----
  function chat(box, talkId, hub, { eventWide = false, onMessage } = {}) {
    const t = translator({});
    const list = el('ol', 'chat-list');
    const empty = el('p', 'empty', t('chatEmpty'));
    const form = el('form', 'chat-form');
    const note = el('p', 'composer-note', t(eventWide ? 'chatEventNote' : 'chatNote'));
    const name = el('input');
    name.maxLength = 80;
    name.placeholder = t('chatName');
    name.setAttribute('aria-label', t('chatName'));
    name.autocomplete = 'off';
    name.value = store.get('chatName', '');
    name.addEventListener('change', () => store.set('chatName', name.value.trim()));
    const row = el('div', 'composer-row');
    const text = el('textarea');
    text.rows = 1;
    text.maxLength = 500;
    text.placeholder = t('chatPlaceholder');
    text.setAttribute('aria-label', t('chatPlaceholder'));
    const publish = el('button', 'primary', t('chatPublish'));
    row.append(text, publish);
    const problem = el('p', 'error');
    problem.setAttribute('role', 'alert');
    form.append(note, name, row, problem);
    box.replaceChildren(empty, list, form);

    function add(message) {
      if (message.talkId !== talkId || document.getElementById('chat-' + message.id)) return;
      const li = el('li', 'chat-message');
      li.id = 'chat-' + message.id;
      li.append(el('b', '', message.author), el('p', '', message.text));
      const pinned = list.scrollHeight - list.scrollTop - list.clientHeight < 80;
      list.append(li);
      empty.hidden = true;
      if (pinned) list.scrollTop = list.scrollHeight;
      if (onMessage) onMessage(message);
    }

    async function load() {
      const response = await api(`/api/chat/${talkId}`).catch(() => null);
      if (response && response.ok) (await response.json()).forEach(add);
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const message = text.value.trim();
      if (!message) return;
      publish.disabled = true;
      problem.textContent = '';
      store.set('chatName', name.value.trim());
      const response = await api(`/api/chat/${talkId}`, { method: 'POST', json: { name: name.value.trim(), text: message } }).catch(() => null);
      publish.disabled = false;
      if (response && response.ok) {
        text.value = '';
        add(await response.json());
      } else {
        problem.textContent = t(!response ? 'failed' : response.status === 429 ? 'rateLimited' : response.status === 409 ? 'chatClosed' : 'failed');
      }
    });
    text.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        form.requestSubmit();
      }
    });

    hub.on('chat', add);
    hub.on('chatRemoved', (id) => { const row2 = document.getElementById('chat-' + id); if (row2) row2.remove(); });
    const watch = () => hub.invoke('WatchChat', talkId).catch(() => {});
    hub.onreconnected(() => { watch(); load(); });
    load();
    return { watch };
  }

  // Answers quote their sources, and the same drawing works for one talk or the whole event.
  function answerBody(body, answer, words, onCite) {
    body.replaceChildren();
    body.classList.remove('muted');
    if (answer.mode === 'not_found' || answer.mode === 'empty') {
      body.classList.add('muted');
      body.textContent = words.notFound;
      return;
    }
    if (answer.mode === 'offline') body.append(el('p', 'muted', words.offline));
    const byNumber = new Map(answer.citations.map((c) => [c.n, c]));
    for (const line of answer.text.split('\n')) {
      const p = el('p');
      for (const part of line.split(/(\[\d+\])/)) {
        const match = part.match(/^\[(\d+)\]$/);
        const citation = match && byNumber.get(Number(match[1]));
        if (!citation) { if (!match) p.append(part); continue; }
        const cite = el('button', 'cite', formatTime(citation.t));
        cite.type = 'button';
        cite.addEventListener('click', () => onCite(citation));
        p.append(cite);
      }
      body.append(p);
    }
  }

  // ----- One id per phone, so a vote or a rating counts once without an account. Works without HTTPS, unlike randomUUID. -----
  function device() {
    let id = store.get('device', '');
    if (!id) {
      id = [...crypto.getRandomValues(new Uint8Array(16))].map((b) => b.toString(16).padStart(2, '0')).join('');
      store.set('device', id);
    }
    return id;
  }

  // ----- Live poll: the question, a button per answer, and the results once enough people voted -----
  function poll(box) {
    const t = translator({});
    let current = null;
    let mine = null;

    function bar(label, count, total, chosen) {
      const row = el('div', chosen ? 'poll-bar chosen' : 'poll-bar');
      const fill = el('span', 'fill');
      fill.style.width = `${total ? Math.round((count / total) * 100) : 0}%`;
      row.append(fill, el('span', 'label', label), el('b', '', total ? `${Math.round((count / total) * 100)}%` : ''));
      return row;
    }

    function render() {
      box.hidden = !current;
      if (!current) return;
      const head = el('p', 'poll-head', current.open ? t('pollLabel') : t('pollClosed'));
      const question = el('p', 'poll-question', current.question);
      const parts = [head, question];
      if (current.open && mine === null) {
        current.options.forEach((option, i) => {
          const button = el('button', 'secondary poll-option', option);
          button.type = 'button';
          button.addEventListener('click', () => vote(i));
          parts.push(button);
        });
      } else if (current.counts) {
        const total = current.counts.reduce((a, b) => a + b, 0);
        current.options.forEach((option, i) => {
          const row = bar(option, current.counts[i], total, i === mine);
          if (current.open) {
            row.tabIndex = 0;
            row.setAttribute('role', 'button');
            row.addEventListener('click', () => vote(i));
          }
          parts.push(row);
        });
        parts.push(el('p', 'muted small', t('pollVotes', { n: total })));
      } else {
        current.options.forEach((option, i) => parts.push(bar(option, 0, 0, i === mine)));
        parts.push(el('p', 'muted small', t('pollHidden', { n: current.minimum })));
      }
      if (current.open && mine !== null) parts.push(el('p', 'muted small', t('pollVoted')));
      box.replaceChildren(...parts);
    }

    async function vote(choice) {
      mine = choice;
      render();
      const response = await api(`/api/polls/${current.id}/vote`, { method: 'POST', json: { voter: device(), choice } }).catch(() => null);
      if (response && response.ok) current = await response.json();
      render();
    }

    return {
      show(next, myVote) {
        if (!next) { current = null; render(); return; }
        if (!current || current.id !== next.id) mine = myVote ?? null;
        else if (myVote !== undefined && myVote !== null) mine = myVote;
        current = next;
        render();
      },
      async load(room) {
        const response = await api(`/api/rooms/${room}/poll?voter=${device()}`).catch(() => null);
        if (response && response.ok) {
          const data = await response.json();
          this.show(data.poll, data.mine);
        }
      },
    };
  }

  // ----- Rating: five stars and an optional anonymous comment, changeable later -----
  function rating(box, talkId) {
    const t = translator({});
    let stars = 0;
    const title = el('p', 'poll-question', t('rateTitle'));
    const row = el('div', 'stars');
    row.setAttribute('role', 'radiogroup');
    row.setAttribute('aria-label', t('rateTitle'));
    const buttons = [1, 2, 3, 4, 5].map((n) => {
      const star = el('button', 'star', '★');
      star.type = 'button';
      star.setAttribute('role', 'radio');
      star.setAttribute('aria-label', t('rateStars', { n }));
      star.addEventListener('click', () => { stars = n; paint(); comment.hidden = false; send.hidden = false; });
      row.append(star);
      return star;
    });
    const comment = el('textarea');
    comment.rows = 2;
    comment.maxLength = 500;
    comment.placeholder = t('rateComment');
    comment.setAttribute('aria-label', t('rateComment'));
    comment.hidden = true;
    const send = el('button', 'primary', t('rateSend'));
    send.type = 'button';
    send.hidden = true;
    const note = el('p', 'muted small');
    note.setAttribute('role', 'status');

    function paint() {
      buttons.forEach((b, i) => {
        b.classList.toggle('on', i < stars);
        b.setAttribute('aria-checked', String(i + 1 === stars));
      });
    }

    send.addEventListener('click', async () => {
      send.disabled = true;
      const response = await api(`/api/talks/${talkId}/rating`, { method: 'POST', json: { voter: device(), stars, comment: comment.value.trim() } }).catch(() => null);
      send.disabled = false;
      note.textContent = response && response.ok ? t('rateThanks') : t(response && response.status === 429 ? 'rateLimited' : 'failed');
    });

    box.replaceChildren(title, row, comment, send, note);
    box.hidden = false;
    api(`/api/talks/${talkId}/rating?voter=${device()}`).then((r) => (r.ok ? r.json() : null)).then((data) => {
      if (!data || !data.mine) return;
      stars = data.mine.stars;
      comment.value = data.mine.comment;
      comment.hidden = false;
      send.hidden = false;
      note.textContent = t('rateThanks');
      paint();
    }).catch(() => {});
  }

  // ----- Reactions: the same four everywhere, floating up from the bottom of a layer -----
  const REACTIONS = [['clap', '👏', 'reactClap'], ['fire', '🔥', 'reactFire'], ['wow', '🤯', 'reactWow'], ['lost', '❓', 'reactLost']];
  const MAX_FLOATING = 24;
  const calm = matchMedia('(prefers-reduced-motion: reduce)').matches;

  function floatReaction(layer, kind) {
    const found = REACTIONS.find((r) => r[0] === kind);
    if (!found || calm || layer.childElementCount >= MAX_FLOATING) return;
    const bubble = el('span', 'float-reaction', found[1]);
    bubble.setAttribute('aria-hidden', 'true');
    bubble.style.left = `${10 + Math.random() * 80}%`;
    bubble.style.setProperty('--drift', `${Math.round(Math.random() * 60 - 30)}px`);
    bubble.addEventListener('animationend', () => bubble.remove());
    layer.append(bubble);
  }

  // ----- Photos: resized on the phone, which also drops the location and camera details -----
  const PHOTO_MAX_SIDE = 1600;

  async function shrinkPhoto(file) {
    const image = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const scale = Math.min(1, PHOTO_MAX_SIDE / Math.max(image.width, image.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(image.width * scale);
    canvas.height = Math.round(image.height * scale);
    canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
    image.close();
    return new Promise((resolve, reject) => canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('jpeg'))), 'image/jpeg', 0.85));
  }

  // ----- Glossary: terms the talk used, explained by the AI -----
  function glossary(body, items) {
    const t = translator({});
    body.classList.remove('muted');
    if (!items.length) {
      body.classList.add('muted');
      body.textContent = t('glossaryEmpty');
      return;
    }
    const list = el('dl', 'glossary');
    for (const item of items) list.append(el('dt', '', item.term), el('dd', '', item.explanation));
    body.replaceChildren(list, el('p', 'muted small', t('glossaryNote')));
  }

  // ----- Profile: one sheet, from the talk or from Connect. The first save makes it, so nobody fills a form to get in. -----
  async function myProfile() {
    if (!ticket()) return null;
    const response = await api('/api/me').catch(() => null);
    return response && response.ok ? (await response.json()).me : null;
  }

  function field(label, input) {
    const box = el('label', 'sheet-field');
    input.setAttribute('aria-label', label);
    box.append(el('span', '', label), input);
    return box;
  }

  function toggle(label, checked) {
    const box = el('label', 'switch');
    const input = el('input');
    input.type = 'checkbox';
    input.setAttribute('role', 'switch');
    input.checked = checked;
    input.setAttribute('aria-label', label);
    box.append(el('span', '', label), input);
    return { box, input };
  }

  /** Resolves with the saved profile, or null if the person closed it. */
  function editProfile(current) {
    const t = translator({});
    const text = (type, value, max, hint) => {
      const input = el('input');
      input.type = type;
      if (max) input.maxLength = max;
      input.value = value || '';
      if (hint) input.placeholder = t(hint);
      return input;
    };
    const name = text('text', current && current.name, 80);
    name.autocomplete = 'nickname';
    const seeking = text('text', current && current.seeking, 200, 'profileSeekingHint');
    const offering = text('text', current && current.offering, 200, 'profileOfferingHint');
    const email = text('email', current && current.email);
    email.autocomplete = 'email';
    const visible = toggle(t('profileVisible'), current ? current.networking : true);
    const summary = toggle(t('profileSummary'), current ? current.wantsSummary : false);

    const sheet = el('dialog', 'sheet');
    sheet.setAttribute('aria-labelledby', 'profile-title');
    const form = el('form', 'sheet-body');
    const title = el('h2', '', t('profileTitle'));
    title.id = 'profile-title';
    const more = el('details', 'sheet-more');
    more.open = !!email.value;
    more.append(el('summary', '', t('profileEmail')), field(t('profileEmail'), email), summary.box, el('p', 'sheet-note', t('profileEmailNote')));
    const note = el('p', 'sheet-note error');
    note.setAttribute('role', 'status');
    const later = el('button', 'secondary', t('profileLater'));
    later.type = 'button';
    const save = el('button', 'primary', t('profileSave'));
    save.type = 'submit';
    const buttons = el('div', 'sheet-actions');
    buttons.append(later, save);
    form.append(el('span', 'sheet-grip'), title, el('p', 'muted', t('profileIntro')),
      field(t('profileName'), name), field(t('profileSeeking'), seeking), field(t('profileOffering'), offering), visible.box, more, note, buttons);
    sheet.append(form);
    document.body.append(sheet);

    return new Promise((resolve) => {
      let result = null;
      const close = () => sheet.close();
      later.addEventListener('click', close);
      // A tap on the dimmed page closes it, like any sheet.
      sheet.addEventListener('click', (event) => { if (event.target === sheet) close(); });
      sheet.addEventListener('close', () => { sheet.remove(); resolve(result); });
      form.addEventListener('submit', async (event) => {
        event.preventDefault();
        if (!name.value.trim()) {
          note.textContent = t('profileNeedName');
          name.focus();
          return;
        }
        save.disabled = true;
        const response = await api('/api/me/profile', { method: 'PUT', json: {
          name: name.value, seeking: seeking.value, offering: offering.value, networking: visible.input.checked, email: email.value, wantsSummary: summary.input.checked,
        } }).catch(() => null);
        save.disabled = false;
        if (!response || !response.ok) {
          const error = response && response.status === 400 ? (await response.json().catch(() => ({}))).error : '';
          note.textContent = t(error === 'email' ? 'profileBadEmail' : error === 'name' ? 'profileNeedName' : response && response.status === 429 ? 'rateLimited' : 'failed');
          return;
        }
        const data = await response.json();
        store.set('ticket', data.token);
        result = data.me;
        if (!data.emailTaken) return close();
        email.value = data.me.email || '';
        note.textContent = t('profileEmailTaken');
      });
      sheet.showModal();
    });
  }


  return { myProfile, editProfile, store, language, strings, el, link, formatTime, formatWhen, ticket, api, nav, mountNav, newHub, chat, answerBody, device, poll, rating, glossary, REACTIONS, floatReaction, shrinkPhoto, common: (key) => COMMON[language][key], aiUnavailable: () => translator({})('aiUnavailable') };
})();
