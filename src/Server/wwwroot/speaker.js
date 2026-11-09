// Speaker screen: shows only the question the moderator chose (RF-29).
(function () {
  const $ = (id) => document.getElementById(id);
  const room = Number(new URLSearchParams(location.search).get('room')) || 1;
  const REFRESH_MS = 5000;

  async function load() {
    try {
      const response = await fetch(`/api/op/rooms/${room}/questions`);
      if (!response.ok) return;
      const shown = (await response.json()).find((group) => group.status === 'shown');
      $('question').textContent = shown ? shown.text : 'No question right now';
      $('question').classList.toggle('muted', !shown);
      $('asked').textContent = shown && shown.count > 1 ? `Asked by ${shown.count} people` : '';
    } catch { /* the next refresh tries again */ }
  }

  // After the talk, what time did not allow can still be answered (RF-35).
  async function loadLater() {
    const response = await fetch(`/api/op/rooms/${room}/last-talk`).catch(() => null);
    if (!response || !response.ok) return;
    const { talk, questions } = await response.json();
    const running = await (await fetch('/api/rooms')).json().catch(() => ({ rooms: [] }));
    const live = running.rooms.some((r) => r.id === room && r.live);
    const open = questions.filter((q) => !q.answer);
    $('later').hidden = live || !talk || open.length === 0;
    if ($('later').hidden || document.activeElement.tagName === 'TEXTAREA') return;
    $('later-list').replaceChildren(...open.map((q) => {
      const row = document.createElement('article');
      row.className = 'question';
      const text = document.createElement('p');
      text.className = 'question-text';
      text.textContent = q.text;
      const answer = document.createElement('textarea');
      answer.rows = 2;
      answer.maxLength = 2000;
      answer.setAttribute('aria-label', 'Your answer');
      const send = document.createElement('button');
      send.type = 'button';
      send.className = 'primary';
      send.textContent = 'Publish answer';
      send.addEventListener('click', async () => {
        await fetch(`/api/op/talks/${talk.id}/questions/${q.groupId}/answer`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ answer: answer.value }) });
        answer.blur();
        loadLater();
      });
      row.append(text, answer, send);
      return row;
    }));
  }

  fetch('/api/rooms').then((r) => r.json()).then((summary) => {
    const found = summary.rooms.find((r) => r.id === room);
    if (found) $('room').textContent = found.name;
  }).catch(() => {});

  const hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
  hub.on('questions', (changed) => { if (changed === room) load(); });
  hub.start().catch(() => {});

  // Captions, translations and the signing avatar all fall behind a speaker who rushes, so the speaker hears it first.
  async function loadPace() {
    const response = await fetch(`/api/op/rooms/${room}/pace`).catch(() => null);
    const pace = response && response.ok ? await response.json() : null;
    const slow = pace && pace.live && (pace.tooFast || pace.behind);
    $('pace').hidden = !slow;
    if (!slow) return;
    $('pace').textContent = pace.behind
      ? `Slow down a little: captions are ${Math.round(pace.behindMs / 1000)} s behind you.`
      : `Slow down a little: about ${pace.wordsPerMinute} words a minute. Under ${pace.fastAbove} is easier to follow and to translate.`;
  }

  setInterval(load, REFRESH_MS);
  setInterval(loadLater, REFRESH_MS);
  setInterval(loadPace, REFRESH_MS);
  load();
  loadLater();
  loadPace();
})();
