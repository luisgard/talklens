// Organizer home: what to do next, for someone who has never used this. Everything is worked out from what is already set up.
(function () {
  const $ = (id) => document.getElementById(id);
  const REFRESH_MS = 10000;
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };
  const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
  const json = async (path) => {
    const response = await fetch(path);
    if (!response.ok) throw new Error(path);
    return response.json();
  };

  function groups({ event, agenda, people, talks, live }) {
    const arrived = people.filter((p) => p.checkedInAt).length;
    const kept = talks.filter((t) => t.keepAfter && t.endedAtUnixMs).length;
    return [
      ['Before the event', [
        { title: 'Name your event', text: 'What it is called, where and when. Attendees see this.', done: !!event.name, status: event.name, href: '/manage#event', action: 'Add the details' },
        { title: 'Add your talks', text: 'The schedule people see on their phones. If you only have one talk, you can skip this.', done: agenda.length > 0, status: plural(agenda.length, 'talk', 'talks'), href: '/manage#talks', action: 'Add talks' },
        { title: 'Add your guest list', text: 'Upload the CSV file from Luma, or fill in our template. You need it to check people in at the door.', done: people.length > 0, status: plural(people.length, 'person', 'people'), href: '/manage#people', action: 'Upload the list' },
        { title: 'Share the event', text: 'Print the QR code below or show it on a screen.', href: '#qr-card', action: 'See the QR code', always: true },
      ]],
      ['On the day', [
        { title: 'Welcome people at the door', text: 'Search for each person by name or email and tap once. A team phone works too.', status: people.length ? `${arrived} of ${people.length} arrived` : '', href: '/checkin', action: 'Open check-in', always: true },
        { title: 'Start a talk', text: 'Use a phone as the camera and go live. Attendees get video, captions and translation.', status: live.length ? `Live now: ${live.join(', ')}` : '', href: '/operator', action: 'Go live', always: true },
      ]],
      ['After the event', [
        { title: 'See how it went', text: 'Recordings, clips, and reports for speakers and sponsors.', status: kept ? plural(kept, 'talk saved', 'talks saved') : '', href: '/reports', action: 'See the results', always: true },
      ]],
    ];
  }

  function render(data) {
    if (data.event.name) $('greeting').textContent = data.event.name;
    let number = 0;
    // One clear next thing: the first step not done yet, or the door once setup is finished.
    let nextChosen = false;
    $('journey').replaceChildren(...groups(data).map(([title, steps]) => {
      const card = el('section', 'card');
      card.append(el('h2', '', title));
      for (const step of steps) {
        number++;
        const isNext = !nextChosen && !step.done && step.href !== '#qr-card';
        if (isNext) nextChosen = true;
        const row = el('div', 'step');
        row.dataset.done = !!step.done;
        row.append(el('div', 'step-number', step.done ? '✓' : number));
        const body = el('div', 'journey-step');
        const words = el('div');
        words.append(el('h3', '', step.title), el('p', '', step.text));
        if (step.status && (step.done || step.always)) words.append(el('p', 'journey-status', step.status));
        const link = el('a', isNext ? 'primary' : 'secondary', step.done ? 'Change' : step.action);
        link.href = step.href;
        body.append(words, link);
        row.append(body);
        card.append(row);
      }
      return card;
    }));
  }

  async function refresh() {
    const [event, publicEvent, people, talks, status] = await Promise.all([
      json('/api/op/event'), json('/api/event'), json('/api/op/attendees'), json('/api/op/talks'), json('/api/op/status'),
    ]);
    render({ event, agenda: publicEvent.agenda, people, talks, live: status.rooms.filter((r) => r.talk).map((r) => r.talk.talk.title) });
    showSearchEngine(await json('/api/op/search-engine'));
  }

  function showSearchEngine({ elastic, moments }) {
    const line = $('search-engine');
    line.hidden = false;
    line.dataset.on = elastic;
    line.querySelector('span').textContent = elastic
      ? `Smart search is on, powered by Elasticsearch. ${plural(moments, 'moment', 'moments')} searchable.`
      : 'Search is working. For smarter results, start Elasticsearch on this laptop.';
  }

  async function loadNetwork() {
    const network = await json('/api/op/network');
    $('attendee-url').textContent = network.attendeeUrl;
    $('attendee-url').href = network.attendeeUrl;
    if (network.wifiSsid) {
      $('qr-wifi').hidden = false;
      $('qr-wifi-img').src = '/api/op/qr/wifi.svg';
      $('wifi-name').textContent = network.wifiSsid;
    }
  }

  refresh();
  loadNetwork();
  setInterval(() => refresh().catch(() => {}), REFRESH_MS);
  Story.mount();
})();
