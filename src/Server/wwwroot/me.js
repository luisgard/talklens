// RF-23, RF-24, RF-36, RF-37: optional ticket, contacts and sponsors. Nothing is shared until the person accepts.
(function () {
  const { el, link, store, strings, api, ticket, mountNav } = TL;
  const t = strings({
    en: {
      registerTitle: 'Get your free ticket', registerNote: 'Only if you want one. You can follow every talk without it.', yourName: 'Your name', yourEmail: 'Your email',
      register: 'Get ticket', registerPrivacy: 'Your name and email are used for your ticket and event notices. They are never given to sponsors unless you scan their stand and accept.',
      badName: 'Enter your name.', badEmail: 'Enter a valid email.', tooMany: 'Too many tries. Wait a minute.', failed: 'That did not work. Try again.',
      existingMailed: 'That email is already registered. We sent the ticket to it again.', existingDesk: 'That email is already registered. Ask at the registration desk for your ticket.',
      ticketTitle: 'Your ticket', ticketNote: 'Show this at the door and at each talk.', mailed: 'A copy is on its way to {email}.',
      connectTitle: 'Meet someone', connectNote: 'Show this code to the other person. It lasts 5 minutes.', showCode: 'Show my contact code', scan: 'Scan their code', stopScan: 'Stop',
      noCamera: 'This page has no camera access on plain HTTP. Use your phone camera app to scan their code.',
      contactsTitle: 'My contacts', noContacts: 'People you connect with will appear here.', metAt: 'You met during "{talk}"', remove: 'Remove',
      connectAsk: 'Connect with {name}?', connectExplain: 'You will both see each other’s name and email, and the talk where you met. No one else will.', accept: 'Accept', decline: 'Not now',
      connectNeedsTicket: 'To connect, you need a free ticket first.', codeExpired: 'This code expired. Ask for a new one.', connected: 'Connected.',
      standAsk: 'Share your contact with {name}?', standExplain: '{name} will see your name, your email and the related talks you attended. Nothing else, and only if you accept.',
      standNeedsTicket: 'To share your contact, you need a free ticket first.', standUnknown: 'This stand code is not valid.', shared: 'Shared with {name}.',
      sharedTitle: 'Sponsors with your contact', stopSharing: 'Stop sharing',
      talksTitle: 'Talks you attended', minutes: '{n} min', networkingTitle: 'Networking',
      networkingOn: 'Show me to others with shared interests', interests: 'Your interests, separated by commas', save: 'Save', saved: 'Saved.',
      deleteTitle: 'Your data', deleteNote: 'This removes your ticket, attendance, contacts and what you shared with sponsors from this event.', deleteAll: 'Delete my data', deleteConfirm: 'Delete your ticket and all your data from this event?',
    },
    es: {
      registerTitle: 'Obtén tu ticket gratis', registerNote: 'Solo si quieres uno. Puedes seguir todas las charlas sin él.', yourName: 'Tu nombre', yourEmail: 'Tu correo',
      register: 'Obtener ticket', registerPrivacy: 'Tu nombre y correo se usan para tu ticket y los avisos del evento. Nunca se dan a patrocinadores a menos que escanees su stand y aceptes.',
      badName: 'Escribe tu nombre.', badEmail: 'Escribe un correo válido.', tooMany: 'Demasiados intentos. Espera un minuto.', failed: 'No funcionó. Intenta de nuevo.',
      existingMailed: 'Ese correo ya está registrado. Le enviamos el ticket otra vez.', existingDesk: 'Ese correo ya está registrado. Pide tu ticket en el registro.',
      ticketTitle: 'Tu ticket', ticketNote: 'Muéstralo en la puerta y en cada charla.', mailed: 'Va una copia en camino a {email}.',
      connectTitle: 'Conoce a alguien', connectNote: 'Muestra este código a la otra persona. Dura 5 minutos.', showCode: 'Mostrar mi código de contacto', scan: 'Escanear su código', stopScan: 'Detener',
      noCamera: 'Esta página no tiene acceso a la cámara por HTTP. Usa la cámara de tu celular para escanear su código.',
      contactsTitle: 'Mis contactos', noContacts: 'Aquí aparecerán las personas con las que conectes.', metAt: 'Se conocieron en "{talk}"', remove: 'Quitar',
      connectAsk: '¿Conectar con {name}?', connectExplain: 'Los dos verán el nombre y correo del otro, y la charla donde se conocieron. Nadie más.', accept: 'Aceptar', decline: 'Ahora no',
      connectNeedsTicket: 'Para conectar, primero necesitas un ticket gratis.', codeExpired: 'Este código expiró. Pide uno nuevo.', connected: 'Conectados.',
      standAsk: '¿Compartir tu contacto con {name}?', standExplain: '{name} verá tu nombre, tu correo y las charlas relacionadas a las que asististe. Nada más, y solo si aceptas.',
      standNeedsTicket: 'Para compartir tu contacto, primero necesitas un ticket gratis.', standUnknown: 'Este código de stand no es válido.', shared: 'Compartido con {name}.',
      sharedTitle: 'Patrocinadores con tu contacto', stopSharing: 'Dejar de compartir',
      talksTitle: 'Charlas a las que fuiste', minutes: '{n} min', networkingTitle: 'Networking',
      networkingOn: 'Mostrarme a otros con intereses en común', interests: 'Tus intereses, separados por comas', save: 'Guardar', saved: 'Guardado.',
      deleteTitle: 'Tus datos', deleteNote: 'Esto borra tu ticket, asistencia, contactos y lo que compartiste con patrocinadores de este evento.', deleteAll: 'Borrar mis datos', deleteConfirm: '¿Borrar tu ticket y todos tus datos de este evento?',
    },
  });
  mountNav('me');

  const main = document.getElementById('me');
  const query = new URLSearchParams(location.search);

  // A ticket link from the mail or a scan: keep it on this phone, then clean the address so it is not left in history.
  if (query.get('ticket')) {
    store.set('ticket', query.get('ticket'));
    query.delete('ticket');
    history.replaceState(null, '', location.pathname + (query.toString() ? '?' + query : ''));
  }
  let connectCode = query.get('connect');
  let standCode = query.get('stand');

  function card(title) {
    const box = el('section', 'card');
    if (title) box.append(el('h2', '', title));
    return box;
  }

  function button(className, text, onClick) {
    const b = el('button', className, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
  }

  function clearCodes() {
    connectCode = null;
    standCode = null;
    history.replaceState(null, '', location.pathname);
  }

  function svgImage(svg, alt) {
    const img = el('img');
    img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
    img.alt = alt;
    return img;
  }

  // ----- Registration -----
  function registerCard(reason) {
    const box = card(t('registerTitle'));
    if (reason) box.append(el('p', 'notice', reason));
    box.append(el('p', 'muted', t('registerNote')));
    const form = el('form');
    const name = el('input'); name.id = 'reg-name'; name.autocomplete = 'name'; name.maxLength = 80;
    const email = el('input'); email.id = 'reg-email'; email.type = 'email'; email.autocomplete = 'email';
    const nameLabel = el('label', '', t('yourName')); nameLabel.htmlFor = 'reg-name';
    const emailLabel = el('label', '', t('yourEmail')); emailLabel.htmlFor = 'reg-email';
    const problem = el('p', 'error'); problem.setAttribute('role', 'alert');
    const submit = el('button', 'primary wide', t('register'));
    form.append(nameLabel, name, emailLabel, email, problem, submit, el('p', 'muted small', t('registerPrivacy')));
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      problem.textContent = '';
      submit.disabled = true;
      const response = await api('/api/register', { method: 'POST', json: { name: name.value, email: email.value } }).catch(() => null);
      submit.disabled = false;
      if (!response || !response.ok) {
        const code = response ? (await response.json().catch(() => ({}))).error : '';
        problem.textContent = t(code === 'name' ? 'badName' : code === 'email' ? 'badEmail' : code === 'rate_limited' ? 'tooMany' : 'failed');
        return;
      }
      const data = await response.json();
      if (data.existing) {
        problem.textContent = t(data.mailed ? 'existingMailed' : 'existingDesk');
        return;
      }
      store.set('ticket', data.token);
      render();
    });
    box.append(form);
    return box;
  }

  // ----- A scanned contact code or stand: always asks first -----
  async function connectPrompt() {
    const response = await api(`/api/contacts/preview/${encodeURIComponent(connectCode)}`).catch(() => null);
    if (!response || !response.ok) {
      const box = card();
      box.append(el('p', 'notice', t('codeExpired')));
      clearCodes();
      return box;
    }
    const { firstName } = await response.json();
    const box = card(t('connectAsk', { name: firstName }));
    box.append(el('p', 'muted', t('connectExplain')));
    const actions = el('div', 'actions');
    actions.append(
      button('primary', t('accept'), async () => {
        const result = await api('/api/contacts', { method: 'POST', json: { code: connectCode, room: store.get('lastRoom', null) } }).catch(() => null);
        clearCodes();
        // A meeting arranged in Connect: back there, where it now shows as done.
        if (result && result.ok && (await result.clone().json().catch(() => ({}))).met) { location.href = '/connect'; return; }
        await render(result && result.ok ? t('connected') : t('codeExpired'));
      }),
      button('secondary', t('decline'), () => { clearCodes(); render(); }),
    );
    box.append(actions);
    return box;
  }

  async function standPrompt() {
    const response = await fetch(`/api/stands/${encodeURIComponent(standCode)}`).catch(() => null);
    if (!response || !response.ok) {
      const box = card();
      box.append(el('p', 'notice', t('standUnknown')));
      clearCodes();
      return box;
    }
    const { name } = await response.json();
    const box = card(t('standAsk', { name }));
    box.append(el('p', 'muted', t('standExplain', { name })));
    const actions = el('div', 'actions');
    actions.append(
      button('primary', t('accept'), async () => {
        const result = await api(`/api/stands/${encodeURIComponent(standCode)}/share`, { method: 'POST' }).catch(() => null);
        clearCodes();
        await render(result && result.ok ? t('shared', { name }) : t('failed'));
      }),
      button('secondary', t('decline'), () => { clearCodes(); render(); }),
    );
    box.append(actions);
    return box;
  }

  // ----- Contact exchange -----
  function connectCard() {
    const box = card(t('connectTitle'));
    const area = el('div');
    const video = el('video', 'scanner-video');
    video.playsInline = true;
    video.muted = true;
    video.hidden = true;
    let stream = null;
    const stopCamera = () => {
      if (stream) stream.getTracks().forEach((track) => track.stop());
      stream = null;
      video.hidden = true;
    };
    const actions = el('div', 'actions');
    actions.append(
      button('primary', t('showCode'), async () => {
        stopCamera();
        const response = await api('/api/me/contact-code', { method: 'POST' }).catch(() => null);
        if (!response || !response.ok) { area.replaceChildren(el('p', 'error', t('failed'))); return; }
        const data = await response.json();
        const qr = el('div', 'qr');
        qr.append(svgImage(data.svg, t('showCode')));
        area.replaceChildren(el('p', 'muted', t('connectNote')), qr);
      }),
      button('secondary', t('scan'), async () => {
        if (!navigator.mediaDevices) { area.replaceChildren(el('p', 'muted', t('noCamera'))); return; }
        try {
          stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
        } catch {
          area.replaceChildren(el('p', 'muted', t('noCamera')));
          return;
        }
        area.replaceChildren();
        video.srcObject = stream;
        video.hidden = false;
        video.play().catch(() => {});
        const canvas = document.createElement('canvas');
        const read = () => {
          if (!stream) return;
          if (video.readyState >= 2) {
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            const context = canvas.getContext('2d', { willReadFrequently: true });
            context.drawImage(video, 0, 0);
            const found = jsQR(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
            const match = found && found.data.match(/\/(connect|stand)\/([\w-]+)/);
            if (match) {
              stopCamera();
              if (match[1] === 'connect') connectCode = match[2]; else standCode = match[2];
              render();
              return;
            }
          }
          requestAnimationFrame(read);
        };
        read();
      }),
    );
    box.append(actions, video, area);
    return box;
  }

  async function render(notice) {
    const nodes = [];
    if (notice) {
      const banner = el('p', 'notice glass', notice);
      banner.setAttribute('role', 'status');
      nodes.push(banner);
    }

    if (!ticket()) {
      const need = connectCode ? t('connectNeedsTicket') : standCode ? t('standNeedsTicket') : '';
      nodes.push(registerCard(need));
      main.replaceChildren(...nodes);
      return;
    }

    const response = await api('/api/me').catch(() => null);
    if (response && response.status === 401) {
      store.remove('ticket');
      return render(notice);
    }
    if (!response || !response.ok) {
      main.replaceChildren(el('p', 'home-empty glass', t('failed')));
      return;
    }
    const data = await response.json();

    if (connectCode) nodes.push(await connectPrompt());
    if (standCode) nodes.push(await standPrompt());

    const ticketBox = card(t('ticketTitle'));
    ticketBox.append(el('p', '', data.me.name), el('p', 'muted', t('ticketNote')));
    const qr = el('div', 'qr ticket-qr');
    qr.append(svgImage(data.ticketQr, t('ticketTitle')));
    ticketBox.append(qr);
    nodes.push(ticketBox, connectCard());

    const contacts = card(t('contactsTitle'));
    if (data.contacts.length === 0) contacts.append(el('p', 'muted', t('noContacts')));
    for (const contact of data.contacts) {
      const row = el('div', 'talk-row');
      const info = el('div');
      info.append(el('b', '', contact.name), el('p', '', contact.email));
      if (contact.context) info.append(el('p', 'muted small', t('metAt', { talk: contact.context })));
      row.append(info, button('secondary', t('remove'), async () => {
        await api(`/api/contacts/${contact.id}`, { method: 'DELETE' }).catch(() => null);
        render();
      }));
      contacts.append(row);
    }
    nodes.push(contacts);

    if (data.sponsors.length) {
      const shared = card(t('sharedTitle'));
      for (const sponsor of data.sponsors) {
        const row = el('div', 'talk-row');
        row.append(el('span', '', sponsor.name), button('secondary', t('stopSharing'), async () => {
          await api(`/api/stands/${encodeURIComponent(sponsor.code)}/share`, { method: 'DELETE' }).catch(() => null);
          render();
        }));
        shared.append(row);
      }
      nodes.push(shared);
    }

    if (data.talks.length) {
      const talks = card(t('talksTitle'));
      for (const talk of data.talks) {
        const row = el('div', 'talk-row');
        row.append(link('', `/talk/${talk.talkId}`, talk.title), el('span', 'muted', t('minutes', { n: talk.minutes })));
        talks.append(row);
      }
      nodes.push(talks);
    }

    const summary = await (await fetch('/api/rooms')).json().catch(() => ({}));
    if (summary.community) {
      const net = card(t('networkingTitle'));
      const label = el('label', 'check');
      const on = el('input'); on.type = 'checkbox'; on.checked = data.me.networking;
      label.append(on, ' ' + t('networkingOn'));
      const interests = el('input'); interests.value = data.me.interests; interests.maxLength = 200;
      interests.setAttribute('aria-label', t('interests'));
      interests.placeholder = t('interests');
      const note = el('span', 'muted');
      const actions = el('div', 'actions');
      actions.append(button('secondary', t('save'), async () => {
        const saved = await api('/api/me/networking', { method: 'PUT', json: { networking: on.checked, interests: interests.value } }).catch(() => null);
        note.textContent = saved && saved.ok ? t('saved') : t('failed');
      }), note);
      net.append(label, interests, actions);
      nodes.push(net);
    }

    const leave = card(t('deleteTitle'));
    leave.append(el('p', 'muted', t('deleteNote')), button('danger', t('deleteAll'), async () => {
      if (!confirm(t('deleteConfirm'))) return;
      await api('/api/me', { method: 'DELETE' }).catch(() => null);
      store.remove('ticket');
      render();
    }));
    nodes.push(leave);

    main.replaceChildren(...nodes);
  }

  render();
})();
