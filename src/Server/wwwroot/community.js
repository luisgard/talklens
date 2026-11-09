// RF-32: the event-wide chat and people with shared interests. Only when the organizer turned it on, and only for those who opt in.
(function () {
  const { el, link, strings, api, mountNav, newHub, chat } = TL;
  const t = strings({
    en: {
      title: 'Community', intro: 'A place for the whole event, beyond any one talk.', closed: 'The organizer has not turned this on.', peopleTitle: 'People with your interests',
      peopleNote: 'Only people who opted in, and only their first name. Say hello in person or in the chat.', optIn: 'Turn this on in your ticket page.', openTicket: 'Open my ticket', none: 'Nobody with shared interests yet.', shared: 'You share: {list}', chatTitle: 'Event chat', needTicket: 'You need a free ticket to see this.',
    },
    es: {
      title: 'Comunidad', intro: 'Un lugar para todo el evento, más allá de una charla.', closed: 'El organizador no ha activado esto.', peopleTitle: 'Personas con tus intereses',
      peopleNote: 'Solo quienes lo activaron, y solo su nombre de pila. Salúdense en persona o en el chat.', optIn: 'Actívalo en la página de tu ticket.', openTicket: 'Abrir mi ticket', none: 'Todavía nadie con intereses en común.', shared: 'Comparten: {list}', chatTitle: 'Chat del evento', needTicket: 'Necesitas un ticket gratis para ver esto.',
    },
  });
  mountNav('community');
  const $ = (id) => document.getElementById(id);

  async function loadPeople() {
    const response = await api('/api/community/people').catch(() => null);
    if (!response) return;
    if (response.status === 404) return;
    $('people-card').hidden = false;
    if (response.status === 401) {
      $('people').replaceChildren(el('p', 'muted', t('needTicket')), link('secondary', '/me', t('openTicket')));
      return;
    }
    const data = await response.json();
    if (!data.optedIn) {
      $('people').replaceChildren(el('p', 'muted', t('optIn')), link('secondary', '/me', t('openTicket')));
      return;
    }
    $('people').replaceChildren(...(data.people.length ? data.people.map((person) => {
      const row = el('div', 'talk-row');
      const info = el('div');
      info.append(el('b', '', person.name), el('p', 'muted small', t('shared', { list: person.shared.join(', ') })));
      row.append(info);
      return row;
    }) : [el('p', 'muted', t('none'))]));
  }

  (async function boot() {
    const summary = await (await fetch('/api/rooms')).json();
    if (!summary.community) {
      $('closed').hidden = false;
      return;
    }
    $('chat-card').hidden = false;
    const hub = newHub();
    const box = chat($('chat'), 0, hub, { eventWide: true });
    hub.start().then(() => box.watch()).catch(() => {});
    loadPeople();
  })();
})();
