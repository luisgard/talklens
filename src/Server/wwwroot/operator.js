// Operator panel: one tile per room, and the selected room's setup or live status (RF-09, RF-14).
(function () {
  const $ = (id) => document.getElementById(id);
  const LEVEL_FULL_SCALE = 0.25;

  let selected = Number(location.hash.replace('#room=', '')) || null;
  let player = null;
  let runningTalks = '';
  let lastStatus = null;
  let attendeeUrl = '';
  let questionCount = 0;

  function setPill(id, state, text) {
    const pill = $(id);
    pill.dataset.state = state;
    pill.textContent = text;
  }

  function formatTime(ms) {
    const total = Math.floor(ms / 1000);
    const h = Math.floor(total / 3600);
    const m = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
    const s = String(total % 60).padStart(2, '0');
    return h > 0 ? `${h}:${m}:${s}` : `${m}:${s}`;
  }

  async function api(path, options) {
    const response = await fetch('/api/op' + path, options);
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `Request failed (${response.status})`);
    }
    return response.status === 204 ? null : response.json().catch(() => null);
  }

  const send = (method, body) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  // ----- Rooms -----
  function renderStrip(rooms) {
    const strip = $('room-strip');
    strip.replaceChildren();
    for (const room of rooms) {
      const tile = document.createElement('button');
      tile.type = 'button';
      tile.className = 'room-tab';
      tile.setAttribute('aria-current', room.id === selected);

      const name = document.createElement('b');
      name.textContent = room.name;
      const pill = document.createElement('span');
      pill.className = 'pill';
      pill.dataset.state = room.talk ? 'live' : room.camera && room.camera.ready ? 'ok' : '';
      pill.textContent = room.talk ? 'Live' : room.camera && room.camera.ready ? 'Camera on' : 'Idle';
      name.append(pill);

      const line = document.createElement('span');
      line.className = 'line';
      line.textContent = room.talk ? room.talk.talk.title : 'No talk';
      const people = document.createElement('span');
      people.className = 'line';
      people.textContent = room.talk ? `${room.attendees} watching · ${formatTime(room.talk.elapsedMs)}${room.openQuestions ? ` · ${room.openQuestions} to answer` : ''}` : '';

      tile.append(name, line, people);
      tile.addEventListener('click', () => selectRoom(room.id));
      strip.append(tile);
    }
    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'room-tab add';
    add.textContent = '+ Add room';
    add.addEventListener('click', addRoom);
    strip.append(add);
  }

  async function addRoom() {
    const count = lastStatus ? lastStatus.rooms.length : 1;
    const room = await api('/rooms', send('POST', { name: `Room ${count + 1}` })).catch((e) => alert(e.message));
    if (!room) return;
    lastStatus.rooms.push({ ...room, attendees: 0, camera: null, slideStream: null, talk: null });
    selectRoom(room.id);
    $('room-name').value = room.name;
    $('room-name').select();
  }

  function selectRoom(id) {
    const previous = selected;
    selected = id;
    location.hash = `room=${id}`;
    $('feed').replaceChildren();
    $('talk-error').textContent = '';
    $('camera-qr').hidden = false;
    $('qr-camera').src = `/api/op/qr/camera.svg?room=${id}`;
    $('qr-camera-live').src = $('qr-camera').src;
    cameraAgain = false;
    $('qr-screen').src = `/api/op/qr/screen.svg?room=${id}`;
    $('qr-room').src = `/api/op/qr/attendee.svg?room=${id}`;
    $('qr-speaker').src = `/api/op/qr/speaker.svg?room=${id}`;
    $('speaker-link').href = `/speaker?room=${id}`;
    $('screen-link').href = `/screen/${id}`;
    loadQuestions();
    loadPolls();
    loadAgendaChoices();
    showRoomUrl();
    if (player) player.stop();
    player = new TalkLensRtc.WhepPlayer(`/media/live/r${id}/cam/whep`, $('preview'));
    player.start(false);
    if (hub.state === 'Connected') hub.invoke('WatchRoom', previous || id, id).catch(() => {});
    if (lastStatus) render(lastStatus);
  }

  $('room-name').addEventListener('change', async (e) => {
    await api(`/rooms/${selected}`, send('PUT', { name: e.target.value })).catch((error) => alert(error.message));
  });

  $('remove-room').addEventListener('click', async () => {
    const room = lastStatus && lastStatus.rooms.find((r) => r.id === selected);
    if (!room || !confirm(`Remove ${room.name}? Its past talks are kept.`)) return;
    await api(`/rooms/${selected}`, { method: 'DELETE' }).catch((e) => alert(e.message));
    selected = null;
  });

  // ----- Status -----
  function render(status) {
    lastStatus = status;
    const rooms = status.rooms;
    // First update, or the selected room was just removed.
    if (!player || !rooms.some((r) => r.id === selected)) return selectRoom(rooms.some((r) => r.id === selected) ? selected : rooms[0].id);
    renderStrip(rooms);

    const total = rooms.reduce((sum, r) => sum + r.attendees, 0);
    setPill('internet-pill', status.internet ? 'ok' : 'warn', status.internet ? 'Internet on' : 'No internet: offline captions');
    setPill('attendees-pill', '', `${total} watching`);
    $('server-detail').textContent = `Server CPU ${status.server.cpuPercent}%, ${status.server.memoryMb} MB. Video stays on this network; only AI work uses the internet.`;

    const room = rooms.find((r) => r.id === selected);
    if (document.activeElement !== $('room-name')) $('room-name').value = room.name;
    const talk = room.talk;
    $('remove-room').hidden = !!talk || rooms.length < 2;
    $('talk-form').hidden = !!talk;
    $('running').hidden = !talk;
    setPill('talk-pill', talk ? 'live' : '', talk ? 'Live' : 'Not started');
    $('clock').textContent = talk ? `${talk.talk.title} · ${formatTime(talk.elapsedMs)}` : '';

    const running = rooms.filter((r) => r.talk).map((r) => r.talk.talk.id).join();
    if (running !== runningTalks) {
      runningTalks = running;
      loadTalks();
    }

    renderCameraStep(room, status);
    if (talk) renderLights(room);
  }

  // A phone left streaming from an earlier talk counts as connected, so the QR stays one tap away for switching phones.
  let cameraAgain = false;
  $('camera-again').addEventListener('click', () => {
    cameraAgain = true;
    if (lastStatus) render(lastStatus);
  });

  function renderCameraStep(room, status) {
    const connected = room.camera && room.camera.ready;
    $('step-camera').dataset.done = !!connected;
    $('camera-qr').hidden = !!connected && !cameraAgain;
    $('camera-again').hidden = !connected || cameraAgain;
    $('camera-lost').hidden = !room.talk || !!connected;
    $('camera-hint').textContent = !status.mediaMtx.installed ? 'The video relay is missing. Run the start script again.'
      : connected ? 'Camera connected.'
      : 'Scan this with the phone that will film the speaker.';
  }

  function renderLights(room) {
    const camera = room.camera;
    const talk = room.talk;
    if (camera && camera.ready) {
      setPill('video-pill', 'ok', 'Live');
      $('video-detail').textContent = camera.tracks;
    } else {
      setPill('video-pill', 'warn', 'Waiting');
      $('video-detail').textContent = 'Waiting for the camera.';
    }

    const audio = talk.audio;
    if (audio.receiving) {
      setPill('audio-pill', 'ok', audio.level > 0.01 ? 'Speech' : 'Silence');
      $('audio-detail').textContent = '';
    } else {
      setPill('audio-pill', 'warn', 'Waiting');
      $('audio-detail').textContent = 'No audio yet.';
    }
    $('audio-meter').style.width = `${Math.min(100, (audio.level || 0) / LEVEL_FULL_SCALE * 100)}%`;

    const captions = talk.captions;
    const state = captions.mode === 'cloud' ? 'ok' : captions.mode === 'local' ? 'warn' : 'bad';
    setPill('captions-pill', state, captions.mode === 'cloud' ? 'Cloud' : captions.mode === 'local' ? 'Offline' : 'Off');
    $('captions-detail').textContent = captions.problem || captions.engine;

    const slides = talk.slides;
    if (slides.running || slides.count) {
      setPill('slides-pill', slides.receiving ? 'ok' : 'warn', slides.receiving ? 'Receiving' : 'Waiting');
      $('slides-detail').textContent = `${slides.count} so far`;
    } else {
      setPill('slides-pill', '', 'Off');
      $('slides-detail').textContent = '';
    }
  }

  // ----- Agenda and chat -----
  let agendaItems = [];
  async function loadAgendaChoices() {
    const event = await (await fetch('/api/event')).json().catch(() => null);
    if (!event) return;
    agendaItems = event.agenda.filter((item) => item.roomId === selected);
    $('agenda-pick').hidden = agendaItems.length === 0;
    const when = new Intl.DateTimeFormat('en', { hour: 'numeric', minute: '2-digit' });
    $('agenda-choice').replaceChildren(new Option('Choose a talk', ''), ...agendaItems.map((item) => new Option(`${when.format(new Date(item.startsAt))} · ${item.title}`, item.id)));
  }
  $('agenda-choice').addEventListener('change', () => {
    const item = agendaItems.find((a) => String(a.id) === $('agenda-choice').value);
    if (!item) return;
    $('title').value = item.title;
    $('speaker').value = item.speaker;
  });

  let chatRoomTalk = null;
  async function loadChat() {
    const room = lastStatus && lastStatus.rooms.find((r) => r.id === selected);
    const talk = room && room.talk ? room.talk.talk.id : null;
    if (talk !== chatRoomTalk && talk) hub.invoke('WatchChat', talk).catch(() => {});
    chatRoomTalk = talk;
    $('chat-card').hidden = !talk;
    showQuestionsCard();
    if (!talk) return;
    const messages = await (await fetch(`/api/chat/${talk}`)).json().catch(() => []);
    $('chat-moderation').replaceChildren(...messages.slice(-30).reverse().map((message) => {
      const row = document.createElement('div');
      row.className = 'talk-row';
      const text = document.createElement('p');
      text.textContent = `${message.author}: ${message.text}`;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'secondary';
      remove.textContent = 'Remove';
      remove.addEventListener('click', async () => {
        await api(`/chat/${message.id}?talk=${talk}`, { method: 'DELETE' }).catch((e) => alert(e.message));
        loadChat();
      });
      row.append(text, remove);
      return row;
    }));
  }

  // ----- Questions for the speaker (RF-18, RF-29) -----
  function questionButton(text, className, group, status) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = className;
    button.textContent = text;
    button.addEventListener('click', async () => {
      await api(`/rooms/${selected}/questions/${group.id}/status`, send('POST', { status })).catch((e) => alert(e.message));
    });
    return button;
  }

  function questionRow(group) {
    const row = document.createElement('article');
    row.className = 'question';
    row.dataset.status = group.status;

    const top = document.createElement('div');
    top.className = 'question-top';
    const count = document.createElement('span');
    count.className = 'question-count';
    count.textContent = group.count;
    count.setAttribute('aria-label', `Asked by ${group.count}`);
    const text = document.createElement('p');
    text.className = 'question-text';
    text.textContent = group.text;
    top.append(count, text);
    row.append(top);

    if (group.variants.length) {
      const list = document.createElement('ul');
      for (const variant of group.variants) {
        const item = document.createElement('li');
        item.textContent = variant;
        list.append(item);
      }
      row.append(list);
    }

    const actions = document.createElement('div');
    actions.className = 'actions';
    if (group.status === 'shown') actions.append(questionButton('Hide from speaker', 'secondary', group, 'open'));
    if (group.status === 'open') actions.append(questionButton('Show to speaker', 'primary', group, 'shown'));
    if (group.status === 'open' || group.status === 'shown') {
      actions.append(questionButton('Answered', 'secondary', group, 'answered'), questionButton('Dismiss', 'secondary', group, 'dismissed'));
    } else {
      actions.append(questionButton('Back to the queue', 'secondary', group, 'open'));
    }
    row.append(actions);
    return row;
  }

  async function loadQuestions() {
    const room = selected;
    const groups = await api(`/rooms/${room}/questions`).catch(() => null);
    if (!groups || room !== selected) return;
    const waiting = groups.filter((g) => g.status === 'open' || g.status === 'shown');
    const done = groups.filter((g) => g.status === 'answered' || g.status === 'dismissed');
    if (waiting.length) {
      $('questions').replaceChildren(...waiting.map(questionRow));
    } else {
      const empty = document.createElement('p');
      empty.className = 'muted';
      empty.textContent = 'None yet. Attendees send them from the Ask tab, and repeated ones are grouped here.';
      $('questions').replaceChildren(empty);
    }
    questionCount = waiting.length + done.length;
    showQuestionsCard();
    $('questions-done-box').hidden = done.length === 0;
    $('questions-done-title').textContent = `Answered and dismissed (${done.length})`;
    $('questions-done').replaceChildren(...done.map(questionRow));
  }

  // Nothing to moderate before a talk starts, so the first thing on screen stays the three steps.
  function showQuestionsCard() {
    const room = lastStatus && lastStatus.rooms.find((r) => r.id === selected);
    $('questions-card').hidden = !(room && room.talk) && questionCount === 0;
    $('polls-card').hidden = !(room && room.talk) && pollCount === 0;
  }

  // ----- Polls -----
  let pollCount = 0;

  async function loadPolls() {
    const room = selected;
    const polls = await api(`/rooms/${room}/polls`).catch(() => null);
    if (!polls || room !== selected) return;
    pollCount = polls.length;
    showQuestionsCard();
    $('polls').replaceChildren(...polls.map((poll) => {
      const box = document.createElement('article');
      box.className = 'poll-result';
      const title = document.createElement('p');
      title.className = 'question-text';
      title.textContent = poll.question;
      box.append(title);
      const total = poll.counts.reduce((a, b) => a + b, 0);
      poll.options.forEach((option, i) => box.append(pollBar(option, poll.counts[i], total)));
      const foot = document.createElement('div');
      foot.className = 'actions';
      const note = document.createElement('span');
      note.className = 'muted';
      note.textContent = `${total} ${total === 1 ? 'vote' : 'votes'}${poll.open ? ' · open' : ' · closed'}`;
      foot.append(note);
      if (poll.open) {
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'secondary';
        close.textContent = 'Close poll';
        close.addEventListener('click', () => api(`/polls/${poll.id}/close`, { method: 'POST' }).catch((e) => alert(e.message)));
        foot.append(close);
      }
      box.append(foot);
      return box;
    }));
  }

  function pollBar(label, count, total) {
    const row = document.createElement('div');
    row.className = 'poll-bar';
    const fill = document.createElement('span');
    fill.className = 'fill';
    fill.style.width = `${total ? Math.round((count / total) * 100) : 0}%`;
    const text = document.createElement('span');
    text.className = 'label';
    text.textContent = label;
    const value = document.createElement('b');
    value.textContent = total ? `${count} · ${Math.round((count / total) * 100)}%` : '0';
    row.append(fill, text, value);
    return row;
  }

  $('poll-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    $('poll-error').textContent = '';
    try {
      await api(`/rooms/${selected}/polls`, send('POST', { question: $('poll-question').value, options: $('poll-options').value.split('\n') }));
      $('poll-question').value = '';
      $('poll-options').value = '';
      loadPolls();
    } catch (err) {
      $('poll-error').textContent = err.message;
    }
  });

  // ----- Photo wall: for the whole event, not one room -----
  function photoCard(photo) {
    const figure = document.createElement('figure');
    figure.className = 'op-photo';
    const img = document.createElement('img');
    img.loading = 'lazy';
    img.src = photo.url;
    img.alt = photo.alt;
    const caption = document.createElement('figcaption');
    const about = document.createElement('span');
    about.className = 'muted';
    about.textContent = [photo.talk || 'Between talks', photo.likes ? `♥ ${photo.likes}` : ''].filter(Boolean).join(' · ');
    caption.append(about);
    const why = photo.status === 'reported' ? 'Someone in it asked to take it down.' : photo.flag ? `AI held it back: ${photo.flag}` : '';
    if (why) {
      const flag = document.createElement('span');
      flag.className = 'flag';
      flag.textContent = why;
      caption.append(flag);
    }
    if (photo.alt) {
      const alt = document.createElement('span');
      alt.textContent = photo.alt;
      caption.append(alt);
    }
    const row = document.createElement('div');
    row.className = 'row';
    const action = (label, kind, run) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = kind;
      button.textContent = label;
      button.addEventListener('click', () => run().catch((e) => alert(e.message)));
      row.append(button);
    };
    const status = (value) => () => api(`/photos/${photo.id}/status`, send('POST', { status: value }));
    if (photo.status !== 'approved') action(photo.status === 'reported' ? 'Put it back' : 'Approve', 'primary', status('approved'));
    action(photo.status === 'approved' ? 'Hide' : 'Reject', 'secondary', status('rejected'));
    action('Delete', 'danger', () => (confirm('Delete this photo for good?') ? api(`/photos/${photo.id}`, { method: 'DELETE' }) : Promise.resolve()));
    caption.append(row);
    figure.append(img, caption);
    return figure;
  }

  async function loadPhotos() {
    const photos = await api('/photos').catch(() => null);
    if (!photos) return;
    const waiting = photos.filter((p) => p.status !== 'approved');
    const approved = photos.filter((p) => p.status === 'approved');
    $('photos-pending').replaceChildren(...waiting.map(photoCard));
    $('photos-none').hidden = waiting.length > 0;
    $('photos-waiting').hidden = waiting.length === 0;
    $('photos-waiting').textContent = waiting.length;
    $('photos-approved-title').textContent = `Published (${approved.length})`;
    $('photos-approved').replaceChildren(...approved.map(photoCard));
  }

  // ----- Past talks -----
  async function loadTalks() {
    const talks = await api('/talks');
    const box = $('talks');
    box.replaceChildren();
    if (!talks.length) {
      const empty = document.createElement('p');
      empty.className = 'muted';
      empty.textContent = 'No talks yet.';
      box.appendChild(empty);
    }
    for (const talk of talks) {
      const row = document.createElement('div');
      row.className = 'talk-row';
      const info = document.createElement('div');
      const when = new Date(talk.startedAtUnixMs).toLocaleString();
      info.textContent = `${talk.title} · ${when}`;
      const remove = document.createElement('button');
      remove.className = 'secondary';
      remove.type = 'button';
      remove.textContent = 'Delete';
      remove.setAttribute('aria-label', `Delete ${talk.title}`);
      remove.addEventListener('click', async () => {
        if (!confirm(`Delete "${talk.title}" with its transcript and slides?`)) return;
        await api(`/talks/${talk.id}`, { method: 'DELETE' }).catch((e) => alert(e.message));
        loadTalks();
      });
      row.append(info, remove);
      box.appendChild(row);
    }
  }

  async function loadDevices() {
    const devices = await api('/devices').catch(() => ({ audio: [], video: [] }));
    for (const name of devices.audio) $('audio-source').append(new Option(`Device: ${name}`, `device:${name}`));
    for (const name of devices.video) $('slide-source').append(new Option(`Capture: ${name}`, `device:${name}`));
  }

  function showRoomUrl() {
    if (!attendeeUrl || !selected) return;
    $('room-url').textContent = `${attendeeUrl}r/${selected}`;
    $('room-url').href = `${attendeeUrl}r/${selected}`;
  }

  async function loadNetwork() {
    const network = await api('/network');
    attendeeUrl = network.attendeeUrl;
    showRoomUrl();
    $('attendee-url').textContent = network.attendeeUrl;
    $('attendee-url').href = network.attendeeUrl;
    const select = $('host');
    select.replaceChildren();
    for (const address of network.addresses) select.append(new Option(address, address));
    select.value = network.host;
    if (network.wifiSsid) {
      $('qr-wifi').hidden = false;
      $('qr-wifi-img').src = '/api/op/qr/wifi.svg';
      $('wifi-name').textContent = network.wifiSsid;
    }
  }

  $('host').addEventListener('change', async (e) => {
    await api('/network/host', send('POST', { host: e.target.value }));
    document.querySelectorAll('.qr img[src]').forEach((img) => {
      const url = new URL(img.src);
      url.searchParams.set('v', Date.now());
      img.src = url;
    });
    loadNetwork();
  });

  $('slide-source').addEventListener('change', (e) => { $('screen-qr').hidden = e.target.value !== 'stream'; });
  // A talk that is not kept leaves no video either.
  $('keep').addEventListener('change', (e) => {
    $('record').disabled = !e.target.checked;
    $('record').checked = e.target.checked;
  });

  $('talk-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    $('talk-error').textContent = '';
    try {
      await api(`/rooms/${selected}/talks/start`, send('POST', {
        title: $('title').value,
        speaker: $('speaker').value,
        language: $('language').value,
        organizerAuthorized: $('auth-organizer').checked,
        speakerAuthorized: $('auth-speaker').checked,
        keepAfter: $('keep').checked,
        recordVideo: $('record').checked,
        audioSource: $('audio-source').value,
        slideSource: $('slide-source').value,
      }));
      $('feed').replaceChildren();
      for (const id of ['title', 'speaker']) $(id).value = '';
      for (const id of ['auth-organizer', 'auth-speaker']) $(id).checked = false;
    } catch (error) {
      $('talk-error').textContent = error.message;
    }
  });

  $('stop').addEventListener('click', async () => {
    if (!confirm('End the talk and the stream for everyone in this room? Their phones will say it has ended.')) return;
    await api(`/rooms/${selected}/talks/stop`, { method: 'POST' }).catch((e) => alert(e.message));
  });

  const hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
  hub.on('status', render);
  hub.on('questions', (room) => { if (room === selected) loadQuestions(); });
  hub.on('polls', (room) => { if (room === selected) loadPolls(); });
  hub.on('chat', loadChat);
  hub.on('photos', loadPhotos);
  setInterval(loadChat, 5000);
  hub.on('caption', (c) => {
    if (!c.final) return;
    const line = document.createElement('p');
    line.textContent = `${formatTime(c.t)}  ${c.text}`;
    $('feed').prepend(line);
    while ($('feed').children.length > 30) $('feed').lastChild.remove();
  });
  const watch = () => {
    if (!selected) return;
    hub.invoke('WatchRoom', selected, selected).catch(() => {});
    loadQuestions();
    loadPolls();
  };
  hub.onreconnected(watch);
  hub.start().then(watch);

  loadTalks();
  loadPhotos();
  loadDevices();
  loadNetwork();
  api('/status').then((status) => { if (status) render(status); }).catch(() => {});
})();
