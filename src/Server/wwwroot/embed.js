// RF-21: captions and questions inside another platform's player, in that platform's colors. See docs/API.md.
(function () {
  const $ = (id) => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  const room = Number(query.get('room')) || 1;
  const language = /^[a-z]{2}$/.test(query.get('lang') || '') ? query.get('lang') : '';
  const accent = query.get('accent') || '';
  if (/^[0-9a-fA-F]{6}$/.test(accent)) document.documentElement.style.setProperty('--accent', '#' + accent);
  if (query.get('ask') === '0') $('ask-form').hidden = true;
  if (query.get('background') === 'transparent') document.body.classList.add('transparent');

  const lines = new Map();
  let clear;
  function show(key, text) {
    lines.delete(key);
    lines.set(key, text);
    while (lines.size > 2) lines.delete(lines.keys().next().value);
    $('caption').textContent = [...lines.values()].join(' ');
    clearTimeout(clear);
    clear = setTimeout(() => { lines.clear(); $('caption').textContent = ''; }, 8000);
  }

  const hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
  hub.on('caption', (c) => { if (!language) show(c.key, c.text); });
  hub.on('translation', (tr) => { if (tr.language === language) show('tr' + tr.id, tr.text); });
  const join = () => hub.invoke('JoinRoom', room, language).catch(() => {});
  hub.onreconnected(join);
  hub.start().then(join).catch(() => {});

  $('ask-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const question = $('question').value.trim();
    if (!question) return;
    $('question').value = '';
    $('answer').textContent = '…';
    const response = await fetch(`/api/rooms/${room}/ask`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question }) }).catch(() => null);
    if (!response || !response.ok) {
      $('answer').textContent = 'Try again in a moment.';
      return;
    }
    const answer = await response.json();
    const stamps = new Map(answer.citations.map((c) => [c.n, c.t]));
    const clock = (ms) => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`;
    $('answer').textContent = answer.text
      ? answer.text.replace(/\[(\d+)\]/g, (_, n) => (stamps.has(Number(n)) ? `(${clock(stamps.get(Number(n)))})` : ''))
      : 'The talk has not covered that.';
  });
})();
