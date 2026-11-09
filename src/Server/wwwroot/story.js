// The illustrations: a stage, a phone showing the product, and the laptop-router-phones picture. No images, so nothing to download on venue Wi-Fi.
window.Story = (function () {
  const language = window.TL ? TL.language : 'en';
  const CAPTION_MS = 3400;
  const calm = matchMedia('(prefers-reduced-motion: reduce)').matches;

  const COPY = {
    en: {
      live: 'Live', room: 'Main stage', talk: 'Seeing inside the kernel', captions: 'Captions',
      ask: 'What delay did they measure?', answer: 'Under five seconds, from the stage to the phone.', jump: 'Jump to 12:04',
      missed: 'You missed 12 minutes', missedLines: [['02:10', 'Why the old tools stopped working'], ['06:45', 'A live demo, tracing one request'], ['11:30', 'What it costs in production']],
      searchQuery: 'something on eBPF and security', searchTitle: 'Seeing inside the kernel', searchWhen: 'Main stage · 2:30 PM', searchWhy: 'Matched by meaning',
      rooms: ['Main stage', 'Workshop B', 'Hallway track'], roomTalks: ['Seeing inside the kernel', 'Your first cluster', 'Open questions'],
      laptop: 'Your laptop', router: 'Your router', phones: 'Every phone', cloud: 'Cloud AI, when there is internet',
    },
    es: {
      live: 'En vivo', room: 'Escenario', talk: 'Viendo dentro del kernel', captions: 'Subtítulos',
      ask: '¿Qué retraso midieron?', answer: 'Menos de cinco segundos, del escenario al celular.', jump: 'Ir a 12:04',
      missed: 'Te perdiste 12 minutos', missedLines: [['02:10', 'Por qué dejaron de servir las herramientas viejas'], ['06:45', 'Una demo en vivo, siguiendo una petición'], ['11:30', 'Cuánto cuesta en producción']],
      searchQuery: 'algo de eBPF y seguridad', searchTitle: 'Viendo dentro del kernel', searchWhen: 'Escenario · 2:30 PM', searchWhy: 'Encontrada por su significado',
      rooms: ['Escenario', 'Taller B', 'Pasillo'], roomTalks: ['Viendo dentro del kernel', 'Tu primer clúster', 'Preguntas abiertas'],
      laptop: 'Tu laptop', router: 'Tu router', phones: 'Cada celular', cloud: 'IA en la nube, cuando hay internet',
    },
  };
  const c = COPY[language] || COPY.en;

  // The same sentence the speaker just said, in whichever language the person picked.
  const CAPTIONS = [
    ['EN', 'So every packet that crosses the kernel can be traced, without changing a single line of your app.'],
    ['ES', 'Así, cada paquete que cruza el kernel se puede rastrear, sin cambiar una sola línea de tu aplicación.'],
    ['FR', 'Chaque paquet qui traverse le noyau peut être suivi, sans changer une seule ligne de votre application.'],
  ];

  let ids = 0;

  function stage(hue = 215) {
    const id = `stage${++ids}`;
    const heads = [18, 58, 100, 140, 182, 222, 262, 302].map((x, i) => `<circle cx="${x}" cy="${172 - (i % 2) * 5}" r="${15 - (i % 3)}"/>`).join('');
    return `<svg class="stage-art" viewBox="0 0 320 180" role="img" aria-hidden="true" preserveAspectRatio="xMidYMid slice">
      <defs>
        <linearGradient id="${id}a" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${hue} 55% 15%)"/><stop offset="1" stop-color="hsl(${hue} 50% 5%)"/></linearGradient>
        <linearGradient id="${id}b" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${hue} 95% 78%)"/><stop offset="1" stop-color="hsl(${hue + 45} 90% 58%)"/></linearGradient>
        <linearGradient id="${id}c" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".2"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>
      </defs>
      <rect width="320" height="180" fill="url(#${id}a)"/>
      <path d="M60 0 L20 180 H120 L84 0Z M250 0 L200 180 H300 L270 0Z" fill="url(#${id}c)"/>
      <rect x="62" y="24" width="156" height="88" rx="5" fill="url(#${id}b)"/>
      <rect x="74" y="36" width="70" height="7" rx="3.5" fill="#fff" opacity=".95"/>
      <rect x="74" y="48" width="44" height="4" rx="2" fill="#fff" opacity=".6"/>
      <g fill="#fff" opacity=".85"><rect x="76" y="86" width="12" height="16" rx="2"/><rect x="94" y="76" width="12" height="26" rx="2"/><rect x="112" y="66" width="12" height="36" rx="2"/><rect x="130" y="82" width="12" height="20" rx="2"/></g>
      <circle cx="184" cy="78" r="17" fill="none" stroke="#fff" stroke-width="7" opacity=".85" stroke-dasharray="70 40"/>
      <g fill="#05070b"><circle cx="252" cy="82" r="10"/><path d="M234 142 q2 -46 18 -48 q16 2 18 48z"/><path d="M228 142 h30 l3 -22 h-36z" opacity=".9"/><rect x="0" y="142" width="320" height="38"/></g>
      <g fill="#020305">${heads}</g>
    </svg>`;
  }

  const shell = (inner) => `<div class="device"><div class="device-screen">${inner}</div></div>`;
  const liveTag = `<span class="mini-live">${c.live}</span>`;
  const mast = (hue) => `<div class="mini-video">${stage(hue)}${liveTag}</div><div class="mini-title"><small>${c.room}</small><b>${c.talk}</b></div>`;

  const SCENES = {
    captions: () => shell(`${mast(215)}
      <div class="mini-captions"><span class="mini-chip" data-caption-language>${CAPTIONS[0][0]}</span><p data-caption>${CAPTIONS[0][1]}</p></div>`),
    ask: () => shell(`${mast(265)}
      <div class="mini-chat"><p class="mine">${c.ask}</p><p class="theirs">${c.answer}<span class="mini-jump">▶ ${c.jump}</span></p></div>`),
    catchup: () => shell(`${mast(25)}
      <div class="mini-catch"><b>${c.missed}</b>${c.missedLines.map(([t, line]) => `<div><time>${t}</time><span>${line}</span></div>`).join('')}</div>`),
    search: () => shell(`<div class="mini-search"><div class="mini-field">${c.searchQuery}</div>
      <div class="mini-result">${stage(160)}<div><small class="mini-why">✦ ${c.searchWhy}</small><b>${c.searchTitle}</b><small>${c.searchWhen}</small></div></div></div>`),
    rooms: () => shell(`<div class="mini-rooms">${c.rooms.map((room, i) => `<div class="mini-poster">${stage([215, 330, 165][i])}${i < 2 ? liveTag : ''}<div><small>${room}</small><b>${c.roomTalks[i]}</b></div></div>`).join('')}</div>`),
  };

  function network() {
    const phones = [430, 500, 570].map((x, i) => `<g transform="translate(${x} ${118 + (i % 2) * 22})"><rect width="44" height="86" rx="9" class="net-body"/><rect x="4" y="5" width="36" height="76" rx="6" fill="url(#net-glow)"/></g>`).join('');
    return `<svg class="network" viewBox="0 0 640 270" role="img" aria-label="${c.laptop}, ${c.router}, ${c.phones}">
      <defs><linearGradient id="net-glow" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7cc4ff"/><stop offset="1" stop-color="#c58bff"/></linearGradient></defs>
      <path class="net-link" d="M196 158 H272"/>
      <path class="net-link" d="M336 158 C 380 158 390 150 430 150"/><path class="net-link" d="M336 158 C 400 158 440 200 500 190"/><path class="net-link" d="M336 158 C 400 158 500 120 570 150"/>
      <path class="net-link optional" d="M304 120 V62"/>
      <g transform="translate(40 88)"><rect x="14" width="128" height="84" rx="8" class="net-body"/><rect x="21" y="7" width="114" height="70" rx="3" fill="url(#net-glow)"/><path d="M0 90 h156 l-8 8 h-140z" class="net-body"/></g>
      <g transform="translate(272 132)"><rect width="64" height="42" rx="10" class="net-body"/><circle cx="16" cy="21" r="3.5" fill="#6ee7a0"/><circle cx="30" cy="21" r="3.5" fill="#6ee7a0"/><path d="M14 0 L8 -16 M50 0 L56 -16" stroke="currentColor" stroke-width="4" stroke-linecap="round"/></g>
      ${phones}
      <g transform="translate(304 36)"><path class="net-cloud" d="M-34 16 a14 14 0 0 1 2 -27 a20 20 0 0 1 38 -5 a16 16 0 0 1 26 12 a11 11 0 0 1 -4 20z"/></g>
      <text x="118" y="222" text-anchor="middle">${c.laptop}</text><text x="304" y="222" text-anchor="middle">${c.router}</text><text x="500" y="256" text-anchor="middle">${c.phones}</text>
      <text x="344" y="42" class="net-note">${c.cloud}</text>
    </svg>`;
  }

  function cycleCaptions(root) {
    const line = root.querySelector('[data-caption]');
    if (!line || calm) return;
    const chip = root.querySelector('[data-caption-language]');
    let at = 0;
    setInterval(() => {
      at = (at + 1) % CAPTIONS.length;
      line.classList.add('swap');
      setTimeout(() => {
        chip.textContent = CAPTIONS[at][0];
        line.textContent = CAPTIONS[at][1];
        line.classList.remove('swap');
      }, 260);
    }, CAPTION_MS);
  }

  // Only hides what it will reveal, so a page without scripts or motion still shows everything.
  function reveal() {
    const parts = document.querySelectorAll('.reveal');
    if (calm || !('IntersectionObserver' in window)) return;
    const watcher = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        entry.target.classList.remove('before');
        watcher.unobserve(entry.target);
      }
    }, { threshold: 0.15 });
    parts.forEach((part) => { part.classList.add('before'); watcher.observe(part); });
  }

  function mount() {
    document.querySelectorAll('[data-scene]').forEach((place) => {
      place.innerHTML = SCENES[place.dataset.scene]();
      if (place.dataset.scene === 'captions') cycleCaptions(place);
    });
    document.querySelectorAll('[data-network]').forEach((place) => { place.innerHTML = network(); });
    reveal();
  }

  return { stage, mount };
})();
