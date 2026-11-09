// RF-25: anyone on the team finds a person and taps once. A QR ticket is optional. It only talks to the laptop, so it works with no internet.
(function () {
  const $ = (id) => document.getElementById(id);
  const REPEAT_MS = 4000;
  const REFRESH_MS = 8000;
  const MAX_MATCHES = 8;
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };
  const send = (body) => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  // So "jose" finds José, whatever keyboard the person at the door has.
  const plain = (text) => text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
  const clock = (ms) => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

  let people = [];
  let lastCode = '';
  let lastAt = 0;
  let scanning = false;

  function show(state, text) {
    $('result').hidden = false;
    $('result').dataset.state = state;
    $('result').textContent = text;
    if (navigator.vibrate) navigator.vibrate(state === 'ok' ? 80 : [80, 60, 80]);
  }

  function ready() {
    $('search').value = '';
    renderMatches();
    $('search').focus();
  }

  async function checkInCode(code) {
    const room = Number($('where').value) || null;
    const response = await fetch('/api/op/checkin', send({ code, room })).catch(() => null);
    if (!response || !response.ok) {
      show('bad', 'Could not reach the laptop.');
      return;
    }
    const result = await response.json();
    if (result.status === 'unknown') show('bad', 'This ticket is not on the list. Try searching by name.');
    else if (result.status === 'already') show('warn', `${result.name} already came in.`);
    else show('ok', `Welcome, ${result.name}.`);
    loadPeople();
  }

  async function checkInPerson(person) {
    const room = Number($('where').value) || null;
    const response = await fetch(`/api/op/attendees/${person.id}/checkin`, send({ room })).catch(() => null);
    if (!response || !response.ok) {
      show('bad', 'Could not reach the laptop.');
      return;
    }
    const result = await response.json();
    // Another phone at the door may have got there first.
    if (result.status === 'already') show('warn', `${person.name} already came in.`);
    else show('ok', `Welcome, ${person.name}.`);
    await loadPeople();
    ready();
  }

  function scan() {
    const video = $('camera');
    const canvas = document.createElement('canvas');
    const read = () => {
      if (!$('scan-box').open) {
        setTimeout(read, 400);
        return;
      }
      if (video.readyState >= 2) {
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        const context = canvas.getContext('2d', { willReadFrequently: true });
        context.drawImage(video, 0, 0);
        const found = jsQR(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
        const now = Date.now();
        // A ticket held in front of the camera is read many times a second.
        if (found && found.data && (found.data !== lastCode || now - lastAt > REPEAT_MS)) {
          lastCode = found.data;
          lastAt = now;
          checkInCode(found.data);
        }
      }
      setTimeout(read, 150);
    };
    read();
  }

  // Only when asked: most doors never scan, and nobody should meet a camera prompt to search for a name.
  async function startCamera() {
    if (scanning) return;
    if (!navigator.mediaDevices) {
      $('camera-note').textContent = 'The camera only works through the address in "Use another phone at the door". Searching by name works here.';
      return;
    }
    try {
      $('camera').srcObject = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
      await $('camera').play();
      scanning = true;
      scan();
    } catch {
      $('camera-note').textContent = 'The camera is not available. Searching by name still works.';
    }
  }

  function matches() {
    const words = plain($('search').value.trim()).split(/\s+/).filter(Boolean);
    if (words.join('').length < 2) return null;
    return people.filter((p) => {
      const text = plain(`${p.name} ${p.email}`);
      return words.every((word) => text.includes(word));
    });
  }

  function renderMatches() {
    const found = matches();
    const typed = $('search').value.trim();
    $('walk-in').hidden = !found || found.length > 0;
    if (found && found.length === 0) {
      $('walk-name').value = typed.includes('@') ? '' : typed;
      $('walk-email').value = typed.includes('@') ? typed : '';
    }
    if (!found) {
      $('matches').replaceChildren();
      return;
    }
    const nodes = found.slice(0, MAX_MATCHES).map((person) => {
      const row = el('div', 'talk-row door-row');
      const info = el('div');
      info.append(el('b', '', person.name), el('p', 'muted small', person.email));
      if (person.checkedInAt) info.append(el('p', 'door-arrived', `Arrived at ${clock(person.checkedInAt)}`));
      const action = el('button', person.checkedInAt ? 'secondary' : 'primary', person.checkedInAt ? 'Undo' : 'Check in');
      action.type = 'button';
      action.addEventListener('click', async () => {
        if (!person.checkedInAt) {
          await checkInPerson(person);
          return;
        }
        await fetch(`/api/op/attendees/${person.id}/checkin`, { method: 'DELETE' });
        await loadPeople();
      });
      row.append(info, action);
      return row;
    });
    if (found.length > MAX_MATCHES) nodes.push(el('p', 'muted small', `And ${found.length - MAX_MATCHES} more. Keep typing to narrow it down.`));
    if (found.length === 0) nodes.push(el('p', 'muted door-none', 'Nobody with that name or email. Check the spelling, or try their email.'));
    $('matches').replaceChildren(...nodes);
  }

  async function loadPeople() {
    const response = await fetch('/api/op/attendees').catch(() => null);
    if (!response || !response.ok) return;
    people = await response.json();
    const arrived = people.filter((p) => p.checkedInAt).length;
    $('count').textContent = people.length ? `${arrived} of ${people.length} people have arrived.` : 'Your guest list is empty. You can add it under Set up.';
    renderMatches();
  }

  async function loadRooms() {
    const summary = await (await fetch('/api/rooms')).json();
    $('where').replaceChildren(new Option('Event entrance', ''), ...summary.rooms.map((r) => new Option(`Door of ${r.name}`, r.id)));
  }

  $('search').addEventListener('input', () => {
    $('result').hidden = true;
    renderMatches();
  });
  // With one person left on screen, Enter is as good as tapping.
  $('search').addEventListener('keydown', (e) => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    const found = matches();
    if (found && found.length === 1 && !found[0].checkedInAt) checkInPerson(found[0]);
  });
  $('scan-box').addEventListener('toggle', () => {
    if ($('scan-box').open) startCamera();
  });
  $('walk-in').addEventListener('submit', async (e) => {
    e.preventDefault();
    $('walk-error').textContent = '';
    const response = await fetch('/api/op/attendees', send({ name: $('walk-name').value, email: $('walk-email').value })).catch(() => null);
    if (!response || !response.ok) {
      const body = response ? await response.json().catch(() => ({})) : {};
      $('walk-error').textContent = body.error || 'Could not reach the laptop.';
      return;
    }
    const added = await response.json();
    await checkInPerson({ id: added.id, name: $('walk-name').value });
  });

  loadRooms();
  loadPeople();
  setInterval(loadPeople, REFRESH_MS);
  $('search').focus();
})();
