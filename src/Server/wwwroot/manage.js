// Set up: one step at a time, so a first-time organizer only ever sees what they are doing now.
(function () {
  const $ = (id) => document.getElementById(id);
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };
  const button = (className, text, onClick) => {
    const b = el('button', className, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
  };
  const svg = (markup, alt) => {
    const img = el('img');
    img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(markup);
    img.alt = alt;
    return img;
  };

  async function api(path, options) {
    const response = await fetch('/api/op' + path, options);
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `Request failed (${response.status})`);
    }
    return response.json().catch(() => null);
  }
  const send = (method, body) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const flash = (id, text) => { $(id).textContent = text; setTimeout(() => { $(id).textContent = ''; }, 2500); };

  let rooms = [];
  let people = [];

  function showStep() {
    const wanted = location.hash.slice(1);
    const step = document.querySelector(`[data-step="${CSS.escape(wanted)}"]`) ? wanted : 'event';
    document.querySelectorAll('[data-step]').forEach((box) => { box.hidden = box.dataset.step !== step; });
    document.querySelectorAll('.stepper a').forEach((link) => {
      if (link.dataset.to === step) link.setAttribute('aria-current', 'step');
      else link.removeAttribute('aria-current');
    });
    scrollTo(0, 0);
  }
  addEventListener('hashchange', showStep);
  showStep();

  async function loadEvent() {
    const data = await api('/event');
    $('name').value = data.name;
    $('venue').value = data.venue;
    $('dates').value = data.dates;
    $('about').value = data.about;
    $('min').value = data.minParticipants;
    $('hall').value = data.hallwayHours;
    $('library').value = data.libraryCode;
    $('community').checked = data.community;
    $('sign-always').checked = data.signAlways;
    $('photos-auto').checked = data.photosAuto;
    $('spots').value = data.meetingSpots;
    $('remote').checked = data.remoteEnabled;
    $('remote-video').checked = data.remoteVideo;
    $('remote-url').value = data.remoteUrl;
    $('api-key').textContent = data.apiKey || 'No key yet';
    $('luma-id').value = data.lumaEventId;
    $('luma-note').textContent = data.lumaKey
      ? 'Imports the event details and approved guests. Their Luma QR works at the door. Arrivals stay here: Luma has no way to mark them through its API, so download who arrived.'
      : 'Not needed if you upload the CSV file. This needs a Luma Plus plan and LUMA_API_KEY in the .env file.';
    $('mail-note').textContent = data.mailConfigured
      ? `Mail: ${data.mail.sent} sent, ${data.mail.pending} waiting.`
      : `Mail is not set up, so tickets stay on the phone that registered. ${data.mail.pending} waiting. See the README to set it up.`;
  }

  $('event-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      await api('/event', send('PUT', { name: $('name').value, venue: $('venue').value, dates: $('dates').value, about: $('about').value }));
      location.hash = 'talks';
    } catch (err) {
      flash('event-saved', err.message);
    }
  });

  $('access-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    await api('/event', send('PUT', {
      minParticipants: Number($('min').value), hallwayHours: Number($('hall').value), libraryCode: $('library').value, community: $('community').checked,
      remoteEnabled: $('remote').checked, remoteVideo: $('remote-video').checked, remoteUrl: $('remote-url').value, signAlways: $('sign-always').checked, photosAuto: $('photos-auto').checked,
      meetingSpots: $('spots').value,
    })).catch((err) => alert(err.message));
    flash('access-saved', 'Saved');
  });

  $('new-key').addEventListener('click', async () => {
    if ($('api-key').textContent !== 'No key yet' && !confirm('The old key will stop working. Create a new one?')) return;
    $('api-key').textContent = (await api('/api-key', { method: 'POST' })).key;
  });

  $('drop-key').addEventListener('click', async () => {
    await fetch('/api/op/api-key', { method: 'DELETE' });
    $('api-key').textContent = 'No key yet';
  });

  // ----- Agenda and capacity -----
  async function loadRooms() {
    rooms = (await (await fetch('/api/rooms')).json()).rooms;
    $('a-room').replaceChildren(...rooms.map((r) => new Option(r.name, r.id)));
    $('limits').replaceChildren(...rooms.map((room) => {
      const row = el('div', 'talk-row');
      const capacity = el('input');
      capacity.type = 'number';
      capacity.min = '0';
      capacity.value = room.capacity;
      capacity.className = 'small-number';
      capacity.setAttribute('aria-label', `Capacity of ${room.name}`);
      const label = el('label', 'check');
      const full = el('input');
      full.type = 'checkbox';
      full.checked = room.full && room.occupancy < (room.capacity || Infinity);
      label.append(full, ' Mark full now');
      const save = () => api(`/rooms/${room.id}/limits`, send('PUT', { capacity: Number(capacity.value) || 0, markedFull: full.checked })).catch((err) => alert(err.message));
      capacity.addEventListener('change', save);
      full.addEventListener('change', save);
      const fits = el('label', 'check room-fits');
      fits.append(capacity, ' people fit');
      row.append(el('b', '', room.name), fits, label);
      return row;
    }));
  }

  $('room-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = $('room-new').value.trim();
    if (!name) return;
    await api('/rooms', send('POST', { name })).catch((err) => alert(err.message));
    $('room-new').value = '';
    loadRooms();
  });

  async function loadAgenda() {
    const data = await (await fetch('/api/event')).json();
    const format = new Intl.DateTimeFormat('en', { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    $('agenda').replaceChildren(...data.agenda.map((item) => {
      const row = el('div', 'talk-row');
      const room = rooms.find((r) => r.id === item.roomId);
      const info = el('div');
      info.append(el('b', '', item.title), el('p', 'muted small', `${format.format(new Date(item.startsAt))} · ${room ? room.name : ''}${item.speaker ? ' · ' + item.speaker : ''}`));
      row.append(info, button('secondary', 'Remove', async () => { await api(`/agenda/${item.id}`, { method: 'DELETE' }); loadAgenda(); }));
      return row;
    }));
  }

  $('agenda-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    $('agenda-error').textContent = '';
    try {
      await api('/agenda', send('POST', {
        roomId: Number($('a-room').value), title: $('a-title').value, speaker: $('a-speaker').value,
        startsAt: new Date($('a-start').value).getTime(), endsAt: new Date($('a-end').value).getTime(), about: $('a-about').value,
      }));
      $('a-title').value = '';
      $('a-speaker').value = '';
      $('a-about').value = '';
      loadAgenda();
    } catch (err) {
      $('agenda-error').textContent = err.message;
    }
  });

  // ----- Attendees -----
  function renderPeople() {
    const filter = $('filter').value.trim().toLowerCase();
    const shown = people.filter((p) => !filter || p.name.toLowerCase().includes(filter) || p.email.toLowerCase().includes(filter)).slice(0, 50);
    $('attendee-count').textContent = people.length
      ? `${people.length} ${people.length === 1 ? 'person' : 'people'} on your list, ${people.filter((p) => p.checkedInAt).length} arrived so far.`
      : 'Your list is empty. Add it one of these two ways.';
    $('attendees').replaceChildren(...shown.map((person) => {
      const row = el('div', 'talk-row');
      const info = el('div');
      info.append(el('b', '', person.name), el('p', 'muted small', `${person.email}${person.checkedInAt ? ' · arrived' : ''}`));
      const actions = el('div', 'actions');
      actions.append(
        button('secondary', 'Show QR', async () => {
          const ticket = await api(`/attendees/${person.id}/ticket`);
          $('desk-ticket').replaceChildren(svg(ticket.svg, `Ticket of ${person.name}`), el('b', '', person.name));
          $('desk-ticket').hidden = false;
          $('desk-ticket').scrollIntoView({ block: 'center' });
        }),
        button('danger', 'Delete', async () => {
          if (!confirm(`Delete ${person.name} and their data?`)) return;
          await api(`/attendees/${person.id}`, { method: 'DELETE' });
          loadPeople();
        }),
      );
      row.append(info, actions);
      return row;
    }));
  }

  async function loadPeople() {
    people = await api('/attendees');
    renderPeople();
  }

  $('filter').addEventListener('input', renderPeople);
  $('add-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    await api('/attendees', send('POST', { name: $('p-name').value, email: $('p-email').value })).then(() => {
      $('p-name').value = '';
      $('p-email').value = '';
    }).catch((err) => alert(err.message));
    loadPeople();
  });
  function showImport(state, text) {
    $('import-result').hidden = false;
    $('import-result').dataset.state = state;
    $('import-result').textContent = text;
  }
  function importSummary({ total, created, noEmail, notComing }) {
    const parts = [`Done. ${created} ${created === 1 ? 'person' : 'people'} added.`];
    if (total > created) parts.push(`${total - created} already on your list.`);
    if (notComing) parts.push(`${notComing} left out because they are not coming: declined, waitlisted or not approved.`);
    if (noEmail) parts.push(`${noEmail} ${noEmail === 1 ? 'row' : 'rows'} skipped with no valid email.`);
    return parts.join(' ');
  }
  $('csv').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    try {
      // The file itself, not its text: only the server can tell which encoding a spreadsheet saved it in.
      const result = await api('/import/csv', { method: 'POST', headers: { 'Content-Type': 'text/csv' }, body: file });
      showImport('ok', importSummary(result));
    } catch (err) {
      showImport('bad', err.message);
    }
    e.target.value = '';
    loadPeople();
    loadEvent();
  });
  $('luma-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      const result = await api('/import/luma', send('POST', { eventId: $('luma-id').value }));
      showImport('ok', `From Luma: ${result.total} guests, ${result.created} of them new.`);
    } catch (err) {
      showImport('bad', err.message);
    }
    loadPeople();
    loadEvent();
  });
  $('send-contacts').addEventListener('click', async () => {
    if (!confirm('Send everyone their list of contacts? Do this when the event is over.')) return;
    const result = await api('/mail/contact-lists', { method: 'POST' });
    showImport('ok', `${result.sent} lists queued.${result.mailConfigured ? '' : ' Mail is not set up, so they wait until it is.'}`);
    loadEvent();
  });
  $('purge').addEventListener('click', async () => {
    if (!confirm('Delete every attendee, their attendance, contacts and what they shared with sponsors? This cannot be undone.')) return;
    await api('/attendees', { method: 'DELETE' });
    loadPeople();
  });

  // ----- Sponsors -----
  async function loadSponsors() {
    const sponsors = await api('/sponsors');
    $('sponsors').replaceChildren(...sponsors.map((sponsor) => {
      const row = el('article', 'question');
      const info = el('div', 'question-text');
      info.append(el('b', '', sponsor.name), el('p', 'muted small', `Looks for: ${sponsor.keywords || sponsor.name} · ${sponsor.leads} contacts`));
      const details = el('details', 'more');
      details.append(el('summary', '', 'Stand QR and sponsor link'));
      const qr = el('div', 'qr');
      qr.style.maxWidth = '180px';
      qr.append(svg(sponsor.standQr, `QR code for the stand of ${sponsor.name}`), el('b', '', 'Print at the stand'));
      const portal = el('a', 'secondary', 'Sponsor link (private to them)');
      portal.href = sponsor.portalUrl;
      portal.target = '_blank';
      portal.rel = 'noopener';
      details.append(qr, portal);
      row.append(info, details, button('danger', 'Remove', async () => {
        if (!confirm(`Remove ${sponsor.name} and the contacts shared with it?`)) return;
        await api(`/sponsors/${sponsor.id}`, { method: 'DELETE' });
        loadSponsors();
      }));
      return row;
    }));
  }
  $('sponsor-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    await api('/sponsors', send('POST', { name: $('s-name').value, keywords: $('s-keys').value })).then(() => {
      $('s-name').value = '';
      $('s-keys').value = '';
    }).catch((err) => alert(err.message));
    loadSponsors();
  });

  loadEvent();
  loadRooms().then(loadAgenda);
  loadPeople();
  loadSponsors();
})();
