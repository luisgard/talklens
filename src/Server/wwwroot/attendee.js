// Attendee app: video, live captions, slides, transcript and private questions to the talk.
(function () {
  const STRINGS = {
    en: {
      signAvatar: 'Signing avatar (experimental)', signNote: 'A research demo, not an interpreter: it knows about 830 signs of mixed or unconfirmed sign language, no one who signs has reviewed it, and it needs internet. The captions are still what you can rely on.',
      signSkipped: 'It skipped some signs to keep up with the talk.',
      signLoading: 'Loading the avatar…', signReady: 'Experimental.', signFailed: 'The avatar could not load. It needs internet.',
      tabChat: 'Chat', listenTranslated: 'Listen in my language', voiceNote: 'Reads the translated captions aloud. Choose a caption language first, and use headphones.',
      back2: 'Back 2 min', backLive: 'Back to live', behind: 'Watching {n} behind', remoteNoVideo: 'Outside the venue: captions, transcript and slides only.', fullHere: 'This room is full. You can follow it from here.', hallwayLink: 'Hallway and summary',
      appLanguage: 'App language', targetLabel: 'Who this question goes to', targetAi: 'Ask the AI', targetSpeaker: 'Ask the speaker',
      speakerNote: 'Goes to the moderator, without your name. If it is left unanswered, it stays anonymously in the hallway after the talk.', speakerPlaceholder: 'Your question for the speaker…', sendSpeaker: 'Send',
      toSpeaker: 'To the speaker', sendToSpeaker: 'Send to the speaker', sending: 'Sending…', sentToSpeaker: 'Sent to the moderator, who chooses what reaches the speaker.',
      sentShort: 'Sent to the moderator', speakerNotLive: 'The talk is not live, so this cannot reach the speaker.',
      allRooms: 'All rooms', settings: 'Settings', join: 'Tap to join with sound', tabSlide: 'Slide', tabTranscript: 'Transcript', tabAsk: 'Ask',
      noSlide: 'The current slide will appear here.', noTranscript: 'What the speaker says will appear here.',
      catchUp: 'Catch me up', last2: 'Last 2 min', last5: 'Last 5 min', wholeTalk: 'Whole talk',
      privateNote: 'Private: only you see your question and its answer.', questionLabel: 'Your question about the talk',
      questionPlaceholder: 'Ask about the talk…', send: 'Ask',
      captionLanguage: 'Caption language', original: 'Original', captionSize: 'Caption size', showCaptions: 'Show captions',
      highContrast: 'High contrast', audioOnly: 'Audio and captions only',
      audioOnlyNote: 'Uses far less Wi-Fi. Handy when the room screen is easy to see.',
      translationNote: 'Translation needs internet. Without it you see the original captions.', done: 'Done',
      live: 'Live', waiting: 'Not started', ended: 'Ended',
      inviteTitle: 'Meet people here', inviteText: 'Say what you are looking for. It takes 30 seconds.', inviteOpen: 'Start', inviteClose: 'Close',
      roundTitle: 'Round of introductions', roundText: 'Open Connect to see who you should meet here.', roundOpen: 'Open',
      endedTitle: 'This talk has ended', endedDetail: 'It ended at {time}. The transcript and chat stay below.',
      statusLive: 'Live now · started at {time}', statusEnded: 'This talk ended at {time}. The transcript stays here.',
      statusSoon: 'Not started yet. {title} is scheduled for {time}.', statusLate: 'Starting soon: {title} was scheduled for {time}. The video starts here as soon as it begins.',
      statusNext: 'Next here: {title} at {time}.', statusIdle: 'Nothing is live in this room right now. The video starts here as soon as a talk begins.',
      noVideo: 'Waiting for the camera…', connecting: 'Connecting…', audioMode: 'Audio only',
      localCaptions: 'Captions are running on the offline model and may be less accurate.',
      noCaptions: 'Captions are not available right now.', reconnecting: 'Reconnecting to the event network…',
      thinking: 'Reading the talk…', notFound: 'The talk has not covered that so far.',
      emptyTalk: 'Nothing has been said yet.', offlineAnswer: 'The AI is not available right now. These are the closest moments in the talk.',
      rateLimited: 'Too many questions in a minute. Try again shortly.', busy: 'Many people are asking right now. Try again in a moment.',
      failed: 'That did not work. Try again.', slide: 'Slide', said: 'Said', catchUpLabel: 'Catch up', slideAt: 'Slide at {t}',
      glossaryChip: 'Terms explained', aiUnavailable: 'The AI is not available right now. Try again later.',
      reactionsLabel: 'React', tabPhotos: 'Photos', photoShare: '📸 Share a photo', photoAlbum: 'See the event album',
      photoConsent: 'It may appear on the room screen and in the public album once approved. Only share photos the people in them agree to.',
      noPhotos: 'No photos from this room yet.', photoSending: 'Sending…', photoSent: 'Sent. It shows once the organizer approves it.',
      photoWaiting: 'Waiting for approval', photoTooMany: 'That is the limit for now. Try again in an hour.', photoFailed: 'That photo could not be sent. Try another one.',
    },
    es: {
      signAvatar: 'Avatar de señas (experimental)', signNote: 'Una demo de investigación, no un intérprete: conoce unas 830 señas de lengua de señas mezclada o sin confirmar, nadie que signe la ha revisado, y necesita internet. Los subtítulos siguen siendo lo que puedes leer con confianza.',
      signSkipped: 'Se saltó algunas señas para alcanzar la charla.',
      signLoading: 'Cargando el avatar…', signReady: 'Experimental.', signFailed: 'El avatar no pudo cargar. Necesita internet.',
      tabChat: 'Chat', listenTranslated: 'Escuchar en mi idioma', voiceNote: 'Lee en voz alta los subtítulos traducidos. Primero elige idioma de subtítulos, y usa audífonos.',
      back2: 'Regresar 2 min', backLive: 'Volver al vivo', behind: 'Viendo {n} atrás', remoteNoVideo: 'Fuera del recinto: solo subtítulos, transcripción y diapositivas.', fullHere: 'Esta sala está llena. Puedes seguirla desde aquí.', hallwayLink: 'Pasillo y resumen',
      appLanguage: 'Idioma de la app', targetLabel: 'A quién va esta pregunta', targetAi: 'Preguntar a la IA', targetSpeaker: 'Preguntar al ponente',
      speakerNote: 'Llega al moderador, sin tu nombre. Si queda sin responder, sigue anónima en el pasillo después de la charla.', speakerPlaceholder: 'Tu pregunta para el ponente…', sendSpeaker: 'Enviar',
      toSpeaker: 'Para el ponente', sendToSpeaker: 'Enviar al ponente', sending: 'Enviando…', sentToSpeaker: 'Enviada al moderador, que elige qué llega al ponente.',
      sentShort: 'Enviada al moderador', speakerNotLive: 'La charla no está en vivo, así que esto no puede llegar al ponente.',
      allRooms: 'Todas las salas', settings: 'Ajustes', join: 'Toca para entrar con sonido', tabSlide: 'Diapositiva', tabTranscript: 'Transcripción', tabAsk: 'Preguntar',
      noSlide: 'Aquí aparecerá la diapositiva actual.', noTranscript: 'Aquí aparecerá lo que diga el ponente.',
      catchUp: 'Ponme al día', last2: 'Últimos 2 min', last5: 'Últimos 5 min', wholeTalk: 'Toda la charla',
      privateNote: 'Privado: solo tú ves tu pregunta y su respuesta.', questionLabel: 'Tu pregunta sobre la charla',
      questionPlaceholder: 'Pregunta sobre la charla…', send: 'Preguntar',
      captionLanguage: 'Idioma de los subtítulos', original: 'Original', captionSize: 'Tamaño de los subtítulos', showCaptions: 'Mostrar subtítulos',
      highContrast: 'Alto contraste', audioOnly: 'Solo audio y subtítulos',
      audioOnlyNote: 'Consume mucho menos Wi-Fi. Útil si ves bien la pantalla de la sala.',
      translationNote: 'La traducción necesita internet. Sin ella verás los subtítulos originales.', done: 'Listo',
      live: 'En vivo', waiting: 'Sin iniciar', ended: 'Terminó',
      inviteTitle: 'Conoce gente aquí', inviteText: 'Cuenta qué buscas. Toma 30 segundos.', inviteOpen: 'Empezar', inviteClose: 'Cerrar',
      roundTitle: 'Ronda de presentaciones', roundText: 'Abre Conectar y mira a quién te conviene conocer aquí.', roundOpen: 'Abrir',
      endedTitle: 'Esta charla ya terminó', endedDetail: 'Terminó a las {time}. La transcripción y el chat siguen abajo.',
      statusLive: 'En vivo · empezó a las {time}', statusEnded: 'Esta charla terminó a las {time}. La transcripción se queda aquí.',
      statusSoon: 'Todavía no empieza. {title} está programada a las {time}.', statusLate: 'Está por empezar: {title} estaba programada a las {time}. El video aparece aquí en cuanto inicie.',
      statusNext: 'Lo siguiente aquí: {title} a las {time}.', statusIdle: 'No hay nada en vivo en esta sala ahora. El video aparece aquí en cuanto empiece una charla.',
      noVideo: 'Esperando la cámara…', connecting: 'Conectando…', audioMode: 'Solo audio',
      localCaptions: 'Los subtítulos usan el modelo sin conexión y pueden ser menos precisos.',
      noCaptions: 'Los subtítulos no están disponibles ahora.', reconnecting: 'Reconectando con la red del evento…',
      thinking: 'Leyendo la charla…', notFound: 'La charla no ha tratado eso hasta ahora.',
      emptyTalk: 'Todavía no se ha dicho nada.', offlineAnswer: 'La IA no está disponible ahora. Estos son los momentos más cercanos de la charla.',
      rateLimited: 'Demasiadas preguntas en un minuto. Intenta en un momento.', busy: 'Mucha gente está preguntando. Intenta en un momento.',
      failed: 'No funcionó. Intenta de nuevo.', slide: 'Diapositiva', said: 'Dicho', catchUpLabel: 'Ponerme al día', slideAt: 'Diapositiva en {t}',
      glossaryChip: 'Términos explicados', aiUnavailable: 'La IA no está disponible ahora. Intenta más tarde.',
      reactionsLabel: 'Reaccionar', tabPhotos: 'Fotos', photoShare: '📸 Compartir una foto', photoAlbum: 'Ver el álbum del evento',
      photoConsent: 'Puede salir en la pantalla de la sala y en el álbum público cuando se apruebe. Comparte solo fotos con las que estén de acuerdo quienes salen.',
      noPhotos: 'Todavía no hay fotos de esta sala.', photoSending: 'Enviando…', photoSent: 'Enviada. Aparece cuando el organizador la apruebe.',
      photoWaiting: 'Esperando aprobación', photoTooMany: 'Es el límite por ahora. Intenta en una hora.', photoFailed: 'No se pudo enviar esa foto. Prueba con otra.',
    },
  };

  const ROOM = Number(location.pathname.split('/').pop());
  const CAPTION_CLEAR_MS = 8000;
  const TRANSLATION_WAIT_MS = 5000;
  const VOICE_DUCK = 0.12;
  const VOICE_MAX_QUEUE = 2;
  const $ = (id) => document.getElementById(id);

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
  };

  // English unless the person chooses otherwise. The home page reads the same setting.
  const uiLanguage = STRINGS[store.get('ui', 'en')] ? store.get('ui', 'en') : 'en';
  const t = (key, values = {}) => STRINGS[uiLanguage][key].replace(/\{(\w+)\}/g, (all, name) => (name in values ? values[name] : all));

  const prefs = {
    size: store.get('size', 20),
    captions: store.get('captions', true),
    contrast: store.get('contrast', false),
    audioOnly: store.get('audioOnly', false),
    sign: store.get('sign', false),
    language: store.get('language', ''),
    voice: store.get('voice', false),
  };
  store.set('lastRoom', ROOM);

  const state = { room: null, talk: null, next: null, live: false, slides: [], answerCount: 0, elapsedMs: 0, elapsedAt: 0 };
  const captionLines = new Map();
  let clearTimer;

  function formatTime(ms) {
    const total = Math.max(0, Math.floor(ms / 1000));
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = String(total % 60).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
  }

  // ----- Localized chrome -----
  document.documentElement.lang = uiLanguage;
  document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });

  // Where a question goes is always visible, and only a deliberate choice changes it (spec: three conversations, three rules).
  let askTarget = 'ai';
  function showAskTarget() {
    const toSpeaker = askTarget === 'speaker';
    $('question').placeholder = t(toSpeaker ? 'speakerPlaceholder' : 'questionPlaceholder');
    $('composer-note').textContent = t(toSpeaker ? 'speakerNote' : 'privateNote');
    $('ask-send').textContent = t(toSpeaker ? 'sendSpeaker' : 'send');
    $('catchup').hidden = toSpeaker;
  }
  document.querySelectorAll('[name=ask-target]').forEach((radio) => {
    radio.addEventListener('change', () => { askTarget = radio.value; showAskTarget(); });
  });
  showAskTarget();

  // ----- Preferences -----
  function applyPrefs() {
    document.documentElement.style.setProperty('--caption-size', prefs.size + 'px');
    document.body.classList.toggle('high-contrast', prefs.contrast);
    $('captions').hidden = !prefs.captions;
    $('stage').classList.toggle('audio-only', prefs.audioOnly);
    $('translation-note').hidden = prefs.language === '';
    $('video').volume = prefs.voice && prefs.language !== '' ? VOICE_DUCK : 1;
  }

  $('set-size').value = prefs.size;
  $('set-captions').checked = prefs.captions;
  $('set-contrast').checked = prefs.contrast;
  $('set-audio-only').checked = prefs.audioOnly;
  $('set-voice').checked = prefs.voice;
  $('set-voice').addEventListener('change', (e) => {
    prefs.voice = e.target.checked;
    store.set('voice', prefs.voice);
    voice.reset();
    if (prefs.voice) voice.unlock();
    applyPrefs();
  });

  $('set-size').addEventListener('input', (e) => { prefs.size = Number(e.target.value); store.set('size', prefs.size); applyPrefs(); });
  $('set-captions').addEventListener('change', (e) => { prefs.captions = e.target.checked; store.set('captions', prefs.captions); applyPrefs(); });
  // ----- Experimental signing avatar. Off unless someone asks for it, because it loads from outside the venue -----
  const signStatus = (key) => { $('sign-status').textContent = t(key); };
  let signer = null;
  function applySign() {
    $('sign-panel').hidden = !prefs.sign;
    $('sign-pip').hidden = !prefs.sign;
    $('stage').classList.toggle('signing', Boolean(prefs.sign));
    if (!prefs.sign || signer) return;
    signStatus('signLoading');
    TLSign.load().then(() => {
      // One piece waiting at most: at speaking speed a longer queue would only leave it further behind.
      signer ??= TLSign.player({
        maxQueued: 1,
        onDone: () => signStatus('signReady'),
        onSkip: () => signStatus('signSkipped'),
        onSay: (item) => $('sign-said').replaceChildren(TLSign.tokens(item)),
      });
      signStatus('signReady');
      watchSigns();
    }).catch(() => signStatus('signFailed'));
  }
  // Declared further down, so only call this once the page has finished starting.
  function watchSigns() {
    if (hub.state === 'Connected') hub.invoke('WatchSigns', Boolean(prefs.sign && signer)).catch(() => {});
  }
  function onSign(item) {
    if (!signer || !prefs.sign || rewound) return;
    signer.say(item);
  }
  $('set-sign').checked = prefs.sign;
  $('set-sign').addEventListener('change', (e) => { prefs.sign = e.target.checked; store.set('sign', prefs.sign); applySign(); watchSigns(); });
  applySign();
  $('set-contrast').addEventListener('change', (e) => { prefs.contrast = e.target.checked; store.set('contrast', prefs.contrast); applyPrefs(); });
  $('set-audio-only').addEventListener('change', (e) => {
    prefs.audioOnly = e.target.checked;
    store.set('audioOnly', prefs.audioOnly);
    applyPrefs();
    if (!player.stopped) player.start(prefs.audioOnly);
  });
  $('set-language').addEventListener('change', async (e) => {
    prefs.language = e.target.value;
    store.set('language', prefs.language);
    applyPrefs();
    captionLines.clear();
    renderCaptions();
    await hub.invoke('JoinRoom', ROOM, prefs.language).catch(() => {});
  });
  $('open-settings').addEventListener('click', () => $('settings').showModal());
  $('set-ui').value = uiLanguage;
  $('set-ui').addEventListener('change', (e) => { store.set('ui', e.target.value); location.reload(); });

  // ----- Tabs -----
  const tabs = ['transcript', 'live', 'chat', 'ask', 'photos'];
  function selectTab(name) {
    for (const tab of tabs) {
      const selected = tab === name;
      $('tab-' + tab).setAttribute('aria-selected', selected);
      $('tab-' + tab).tabIndex = selected ? 0 : -1;
      $('panel-' + tab).hidden = !selected;
    }
    $('ask-form').hidden = name !== 'ask';
    if (name === 'transcript') $('panel-transcript').scrollTop = $('panel-transcript').scrollHeight;
    if (name === 'photos') loadPhotos();
  }
  tabs.forEach((tab, i) => {
    $('tab-' + tab).addEventListener('click', () => selectTab(tab));
    $('tab-' + tab).addEventListener('keydown', (e) => {
      const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
      if (!step) return;
      const next = tabs[(i + step + tabs.length) % tabs.length];
      selectTab(next);
      $('tab-' + next).focus();
    });
  });

  // ----- Video -----
  const player = new TalkLensRtc.WhepPlayer(`/media/live/r${ROOM}/cam/whep`, $('video'), {
    onState(playerState) {
      const note = playerState === 'playing' ? (prefs.audioOnly ? t('audioMode') : '')
        : playerState === 'no-stream' ? t('noVideo')
        : t('connecting');
      $('stage-note').textContent = note;
      $('stage-note').hidden = note === '';
    },
  });

  // Browsers only start sound after a tap, so joining is an explicit gesture.
  $('join').addEventListener('click', () => {
    $('join').hidden = true;
    setTimeout(invite, INVITE_AFTER_MS);
    if (prefs.voice) voice.unlock();
    if (state.remote) {
      if (state.remoteVideo) startHls(false);
      else showNote(t('remoteNoVideo'));
    } else {
      player.start(prefs.audioOnly);
      $('video').play().catch(() => {});
    }
    updateRewind();
  });

  function showNote(text) {
    $('stage-note').textContent = text;
    $('stage-note').hidden = text === '';
  }

  // ----- Going back 2 minutes (RF-33): WebRTC has no past, so this switches to the room's HLS copy -----
  const REWIND_SECONDS = 120;
  let hls = null;
  let rewound = false;
  let hlsCaptionTimer;

  function startHls(backwards) {
    stopHls();
    const url = `/hls/r${ROOM}/index.m3u8`;
    const video = $('video');
    video.srcObject = null;
    if (window.Hls && Hls.isSupported()) {
      hls = new Hls({ startPosition: backwards ? -REWIND_SECONDS : -1, backBufferLength: REWIND_SECONDS * 2, lowLatencyMode: false });
      hls.loadSource(url);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
    } else {
      // Safari plays HLS itself.
      video.src = url;
      video.addEventListener('loadedmetadata', () => {
        if (backwards && video.seekable.length) video.currentTime = Math.max(video.seekable.start(0), video.seekable.end(0) - REWIND_SECONDS);
        video.play().catch(() => {});
      }, { once: true });
    }
  }

  function stopHls() {
    if (hls) hls.destroy();
    hls = null;
    $('video').removeAttribute('src');
    clearInterval(hlsCaptionTimer);
  }

  function liveEdgeOffsetMs() {
    const video = $('video');
    if (hls && hls.liveSyncPosition) return Math.max(0, (hls.liveSyncPosition - video.currentTime) * 1000);
    if (video.seekable.length) return Math.max(0, (video.seekable.end(0) - video.currentTime) * 1000);
    return 0;
  }

  // Captions follow the moment on screen, from what is already in the transcript.
  function showRewoundCaption() {
    const target = currentTalkMs() - liveEdgeOffsetMs();
    const rows = [...$('transcript').children].filter((row) => !row.hidden && row.querySelector('span') && Number(row.dataset.t) <= target);
    const row = rows[rows.length - 1];
    $('captions').replaceChildren();
    if (row && target - Number(row.dataset.t) < CAPTION_CLEAR_MS) {
      const p = document.createElement('p');
      p.textContent = row.querySelector('span').textContent;
      $('captions').append(p);
    }
    showNote(t('behind').replace('{n}', formatTime(liveEdgeOffsetMs())));
  }

  function currentTalkMs() {
    return state.elapsedMs + (performance.now() - state.elapsedAt);
  }

  function updateRewind() {
    const button = $('rewind');
    // Outside the venue the video is already HLS, and there may be none at all.
    button.hidden = !state.live || $('join').hidden === false || (state.remote && !state.remoteVideo);
    button.textContent = t(rewound ? 'backLive' : 'back2');
  }

  $('rewind').addEventListener('click', () => {
    rewound = !rewound;
    if (rewound) {
      player.stop();
      startHls(true);
      hlsCaptionTimer = setInterval(showRewoundCaption, 1000);
    } else {
      stopHls();
      showNote('');
      captionLines.clear();
      renderCaptions();
      if (state.remote) startHls(false);
      else {
        player.start(prefs.audioOnly);
        $('video').play().catch(() => {});
      }
    }
    updateRewind();
  });

  // ----- Voice translation (RF-11): a cloud voice when the organizer set one up, or the phone's own voice, free and offline -----
  const voice = {
    queue: [],
    audio: new Audio(),
    busy: false,
    reset() {
      this.queue = [];
      this.busy = false;
      this.audio.pause();
      if (window.speechSynthesis) speechSynthesis.cancel();
    },
    // Browsers only allow speech that starts from a tap.
    unlock() {
      this.audio.play().catch(() => {});
      this.audio.pause();
      if (window.speechSynthesis) speechSynthesis.speak(new SpeechSynthesisUtterance(''));
    },
    say(id, text, language) {
      this.queue.push({ id, text, language });
      // Falling behind breaks the 5 second rule, so old sentences are dropped rather than read late.
      while (this.queue.length > VOICE_MAX_QUEUE) this.queue.shift();
      if (!this.busy) this.next();
    },
    next() {
      const item = this.queue.shift();
      this.busy = !!item;
      if (!item) return;
      if (state.voiceCloud) {
        this.audio.src = `/api/rooms/${ROOM}/voice/${item.id}/${item.language}.mp3`;
        this.audio.onended = () => this.next();
        this.audio.onerror = () => this.speakLocally(item);
        this.audio.play().catch(() => this.speakLocally(item));
      } else {
        this.speakLocally(item);
      }
    },
    speakLocally(item) {
      if (!window.speechSynthesis) { this.next(); return; }
      const utterance = new SpeechSynthesisUtterance(item.text);
      utterance.lang = item.language;
      utterance.onend = () => this.next();
      utterance.onerror = () => this.next();
      speechSynthesis.speak(utterance);
    },
  };

  // ----- Captions -----
  function renderCaptions() {
    const box = $('captions');
    box.replaceChildren();
    for (const line of [...captionLines.values()].slice(-2)) {
      const el = document.createElement('p');
      el.textContent = line.text;
      if (!line.final) el.className = 'partial';
      box.appendChild(el);
    }
    clearTimeout(clearTimer);
    clearTimer = setTimeout(() => { captionLines.clear(); box.replaceChildren(); }, CAPTION_CLEAR_MS);
  }

  function showLine(key, text, final) {
    captionLines.delete(key);
    captionLines.set(key, { text, final });
    while (captionLines.size > 3) captionLines.delete(captionLines.keys().next().value);
    renderCaptions();
    if (final) announce(text);
  }

  function announce(text) {
    const live = $('sr-captions');
    const el = document.createElement('p');
    el.textContent = text;
    live.appendChild(el);
    while (live.children.length > 5) live.removeChild(live.firstChild);
  }

  const translating = () => prefs.language !== '' && state.talk && prefs.language !== state.talk.language;
  const pendingTranslations = new Map();

  function onCaption(c) {
    if (c.final) addSegment({ id: c.id, t: c.t, text: c.text });
    if (rewound) return;
    if (!translating()) return showLine(c.key, c.text, c.final);
    if (!c.final) return;
    // Show the original if the translation does not arrive in time (RNF-04).
    pendingTranslations.set(c.id, setTimeout(() => {
      pendingTranslations.delete(c.id);
      showLine(c.key, c.text, true);
    }, TRANSLATION_WAIT_MS));
  }

  function onTranslation(tr) {
    if (tr.language !== prefs.language) return;
    if (!rewound) showLine('tr' + tr.id, tr.text, true);
    if (prefs.voice && !rewound) voice.say(tr.id, tr.text, tr.language);
    // Pieces translated together become one transcript line.
    tr.ids.forEach((id, i) => {
      clearTimeout(pendingTranslations.get(id));
      pendingTranslations.delete(id);
      const row = $('seg-' + id);
      if (!row) return;
      if (i === 0) row.querySelector('span').textContent = tr.text;
      else row.hidden = true;
    });
  }

  // ----- Transcript -----
  function insertByTime(el, time) {
    el.dataset.t = time;
    const list = $('transcript');
    let before = null;
    for (let i = list.children.length - 1; i >= 0; i--) {
      if (Number(list.children[i].dataset.t) <= time) break;
      before = list.children[i];
    }
    const panel = $('panel-transcript');
    const pinned = panel.scrollHeight - panel.scrollTop - panel.clientHeight < 80;
    list.insertBefore(el, before);
    $('no-transcript').hidden = true;
    if (pinned) panel.scrollTop = panel.scrollHeight;
  }

  function addSegment(segment) {
    if ($('seg-' + segment.id)) return;
    const li = document.createElement('li');
    li.id = 'seg-' + segment.id;
    const time = document.createElement('time');
    time.textContent = formatTime(segment.t);
    const text = document.createElement('span');
    text.textContent = segment.text;
    li.append(time, text);
    insertByTime(li, segment.t);
  }

  function slideUrl(slide) {
    return `/slides/${slide.talkId}/${slide.id}.jpg`;
  }

  function addSlide(slide) {
    if ($('slide-' + slide.id)) return;
    state.slides.push(slide);
    const li = document.createElement('li');
    li.id = 'slide-' + slide.id;
    li.className = 'transcript-slide';
    const time = document.createElement('time');
    time.textContent = formatTime(slide.t);
    const img = document.createElement('img');
    img.src = slideUrl(slide);
    img.alt = slide.text || t('slideAt').replace('{t}', formatTime(slide.t));
    img.loading = 'lazy';
    li.append(time, img);
    insertByTime(li, slide.t);
    showCurrentSlide(slide);
  }

  function showCurrentSlide(slide) {
    const latest = state.slides.reduce((a, b) => (b.t > a.t ? b : a), slide);
    $('current-slide-img').src = slideUrl(latest);
    $('current-slide-img').alt = latest.text || t('slideAt').replace('{t}', formatTime(latest.t));
    $('current-slide-time').textContent = t('slideAt').replace('{t}', formatTime(latest.t));
    $('current-slide').hidden = false;
    $('no-slide').hidden = true;
  }

  function goToMoment(time) {
    selectTab('transcript');
    const items = [...$('transcript').children];
    const target = items.filter((el) => !el.hidden && Number(el.dataset.t) <= time).pop() || items[0];
    if (!target) return;
    target.scrollIntoView({ block: 'center' });
    target.classList.remove('flash');
    void target.offsetWidth;
    target.classList.add('flash');
    history.replaceState(null, '', '#t=' + time);
  }

  // ----- Questions (RF-08) -----
  function addAnswerCard(prompt) {
    const card = document.createElement('article');
    card.className = 'answer';
    const q = document.createElement('p');
    q.className = 'answer-question';
    q.textContent = prompt;
    const body = document.createElement('div');
    body.className = 'answer-body muted';
    body.textContent = t('thinking');
    card.append(q, body);
    $('answers').prepend(card);
    return body;
  }

  function renderAnswer(body, answer) {
    body.replaceChildren();
    body.classList.remove('muted');
    if (answer.mode === 'not_found' || answer.mode === 'empty') {
      body.classList.add('muted');
      body.textContent = t(answer.mode === 'empty' ? 'emptyTalk' : 'notFound');
      return;
    }
    if (answer.mode === 'offline') {
      const note = document.createElement('p');
      note.className = 'muted';
      note.textContent = t('offlineAnswer');
      body.appendChild(note);
    }
    const byNumber = new Map(answer.citations.map((c) => [c.n, c]));
    for (const line of answer.text.split('\n')) {
      const p = document.createElement('p');
      for (const part of line.split(/(\[\d+\])/)) {
        const match = part.match(/^\[(\d+)\]$/);
        const citation = match && byNumber.get(Number(match[1]));
        if (!citation) { if (!match) p.append(part); continue; }
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'cite';
        link.textContent = formatTime(citation.t);
        const kind = t(citation.kind === 'slide' ? 'slide' : 'said');
        link.setAttribute('aria-label', `${kind} ${formatTime(citation.t)}`);
        link.addEventListener('click', () => goToMoment(citation.t));
        p.append(link);
      }
      body.appendChild(p);
    }
  }

  // Sends a question on only when the person asks, and tells them plainly how it went.
  async function sendToSpeaker(question) {
    const response = await fetch(`/api/rooms/${ROOM}/questions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question }),
    });
    if (response.ok) return null;
    return t(response.status === 409 ? 'speakerNotLive' : response.status === 429 ? 'rateLimited' : 'failed');
  }

  async function askSpeaker(question) {
    const body = addAnswerCard(`${t('toSpeaker')}: ${question}`);
    body.textContent = t('sending');
    const problem = await sendToSpeaker(question).catch(() => t('failed'));
    body.textContent = problem || t('sentToSpeaker');
  }

  function offerSendToSpeaker(body, question) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'secondary answer-forward';
    button.textContent = t('sendToSpeaker');
    button.addEventListener('click', async () => {
      button.disabled = true;
      button.textContent = t('sending');
      const problem = await sendToSpeaker(question).catch(() => t('failed'));
      button.textContent = problem || t('sentShort');
      button.disabled = !problem;
    });
    body.after(button);
  }

  async function requestAnswer(url, payload, prompt) {
    const body = addAnswerCard(prompt);
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (response.status === 429 || response.status === 503) {
        body.textContent = t(response.status === 429 ? 'rateLimited' : 'busy');
        return;
      }
      if (!response.ok) throw new Error(String(response.status));
      renderAnswer(body, await response.json());
      if (payload.question) offerSendToSpeaker(body, payload.question);
    } catch {
      body.textContent = t('failed');
    }
  }

  $('ask-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const question = $('question').value.trim();
    if (!question) return;
    $('question').value = '';
    if (askTarget === 'speaker') askSpeaker(question);
    else requestAnswer(`/api/rooms/${ROOM}/ask`, { question }, question);
  });
  $('question').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      $('ask-form').requestSubmit();
    }
  });
  document.querySelectorAll('[data-minutes]').forEach((chip) => {
    chip.addEventListener('click', () => {
      const minutes = chip.dataset.minutes ? Number(chip.dataset.minutes) : null;
      requestAnswer(`/api/rooms/${ROOM}/catchup`, { minutes, language: uiLanguage }, `${t('catchUpLabel')}: ${chip.textContent}`);
    });
  });

  // ----- Glossary: the jargon so far, explained in the app's language -----
  $('glossary-chip').addEventListener('click', async () => {
    const body = addAnswerCard($('glossary-chip').textContent);
    if (!state.talk) { body.textContent = t('emptyTalk'); return; }
    const response = await TL.api(`/api/talks/${state.talk.id}/glossary`, { method: 'POST', json: { language: uiLanguage } }).catch(() => null);
    if (!response || !response.ok) {
      body.textContent = t(!response ? 'failed' : response.status === 429 ? 'rateLimited' : response.status === 503 ? 'aiUnavailable' : 'failed');
      return;
    }
    TL.glossary(body, (await response.json()).items);
  });

  // ----- Polls and ratings -----
  const pollBox = TL.poll($('poll-card'));
  function showRating() {
    // Once the talk is over, while it is still on this page.
    const ended = state.talk && !state.live;
    $('rate-card').hidden = !ended;
    if (ended && $('rate-card').dataset.talk !== String(state.talk.id)) {
      $('rate-card').dataset.talk = state.talk.id;
      TL.rating($('rate-card'), state.talk.id);
    }
  }

  // ----- Reactions: anonymous taps that float up on every screen in the room -----
  const reactionBar = $('reactions');
  reactionBar.setAttribute('aria-label', t('reactionsLabel'));
  for (const [kind, emoji, label] of TL.REACTIONS) {
    const button = TL.el('button', 'reaction', emoji);
    button.type = 'button';
    button.setAttribute('aria-label', TL.common(label));
    button.addEventListener('click', () => {
      button.classList.remove('pop');
      void button.offsetWidth;
      button.classList.add('pop');
      if (navigator.vibrate) navigator.vibrate(10);
      TL.api(`/api/rooms/${ROOM}/react`, { method: 'POST', json: { voter: TL.device(), kind } }).catch(() => {});
    });
    reactionBar.append(button);
  }

  // ----- Photos: they reach the room screen and the album once the organizer approves them -----
  let photoReload;
  async function loadPhotos() {
    const response = await fetch(`/api/photos?voter=${TL.device()}`).catch(() => null);
    if (!response || !response.ok) return;
    const items = (await response.json()).photos.filter((p) => p.photo.roomId === ROOM).slice(0, 30);
    $('no-photos').hidden = items.length > 0;
    $('photo-grid').replaceChildren(...items.map((item) => {
      const tile = TL.el('figure', 'photo-tile');
      const img = TL.el('img');
      img.loading = 'lazy';
      img.src = item.url;
      img.alt = item.photo.alt;
      tile.append(img);
      if (item.photo.status !== 'approved') tile.append(TL.el('figcaption', 'photo-badge', t('photoWaiting')));
      return tile;
    }));
  }
  // Many phones at once: wait a moment so one approval is one reload, not several.
  function photosChanged() {
    if ($('panel-photos').hidden) return;
    clearTimeout(photoReload);
    photoReload = setTimeout(loadPhotos, 300 + Math.random() * 1200);
  }

  $('photo-file').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    e.target.value = '';
    if (!file) return;
    $('photo-note').textContent = t('photoSending');
    try {
      const blob = await TL.shrinkPhoto(file);
      const response = await fetch(`/api/rooms/${ROOM}/photos?voter=${TL.device()}`, { method: 'POST', headers: { 'Content-Type': 'image/jpeg' }, body: blob });
      $('photo-note').textContent = response.ok ? t('photoSent') : t(response.status === 429 ? 'photoTooMany' : 'photoFailed');
      if (response.ok) loadPhotos();
    } catch {
      $('photo-note').textContent = t('photoFailed');
    }
  });

  // ----- Status -----
  function setHeader() {
    const roomName = state.room ? state.room.name : '';
    $('talk-title').textContent = state.talk ? state.talk.title : roomName || 'TalkLens';
    $('talk-speaker').textContent = state.talk ? [roomName, state.talk.speaker].filter(Boolean).join(' · ') : '';
    const pill = $('live-pill');
    const pillState = state.live ? 'live' : state.talk ? 'ended' : 'waiting';
    pill.dataset.state = pillState;
    pill.textContent = t(pillState);
    reactionBar.hidden = !state.live;
    renderStatus();
    if (state.talk) document.title = state.talk.title;
    updateRewind();
    watchChat();
  }

  // Schedules slip: talks start late and end early, so say plainly whether this one is on, over or still to come.
  const clock = new Intl.DateTimeFormat(uiLanguage, { hour: 'numeric', minute: '2-digit' });
  const dayClock = new Intl.DateTimeFormat(uiLanguage, { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });
  function renderStatus() {
    const at = (ms) => (new Date(ms).toDateString() === new Date().toDateString() ? clock : dayClock).format(new Date(ms));
    const next = state.next;
    const ended = state.talk && !state.live;
    const ahead = next ? t(next.startsAt > Date.now() ? 'statusSoon' : 'statusLate', { title: next.title, time: at(next.startsAt) }) : '';
    const status = $('talk-status');
    status.dataset.state = state.live ? 'live' : ended ? 'ended' : next ? 'soon' : 'waiting';
    status.textContent = state.live ? t('statusLive', { time: at(state.talk.startedAtUnixMs) })
      : ended ? [t('statusEnded', { time: at(state.talk.endedAtUnixMs ?? Date.now()) }), next ? t('statusNext', { title: next.title, time: at(next.startsAt) }) : ''].filter(Boolean).join(' ')
      : ahead || t('statusIdle');
    // Over the video too, in big letters: a camera left running after the talk would otherwise look live.
    $('ended').hidden = !ended;
    $('ended-meet').hidden = !ended || !!profileDone();
    if (ended) {
      // The organizer's round of introductions is for exactly this moment, between talks.
      if ($('invite').dataset.mode !== 'round') $('invite').hidden = true;
      $('ended-detail').textContent = t('endedDetail', { time: at(state.talk.endedAtUnixMs ?? Date.now()) });
      $('video').pause();
    }
  }
  setInterval(renderStatus, 30000);

  // ----- Meeting people: asked once, a few minutes in or when the talk ends, never before the person has seen the talk -----
  const INVITE_AFTER_MS = 3 * 60 * 1000;
  const INVITE_SNOOZE_MS = 6 * 60 * 60 * 1000;
  let profile = null;
  const profileDone = () => profile && (profile.seeking || profile.offering);
  // During the talk: a small bar. After it, renderStatus puts the invitation where the video was, instead of squeezing the transcript.
  function invite() {
    if (!state.live || profileDone() || TL.store.get('inviteSnoozedUntil', 0) > Date.now()) return;
    $('invite').hidden = false;
  }
  function snoozeInvite() {
    TL.store.set('inviteSnoozedUntil', Date.now() + INVITE_SNOOZE_MS);
    $('invite').hidden = true;
  }
  async function openProfile() {
    const saved = await TL.editProfile(profile);
    if (!saved) return snoozeInvite();
    profile = saved;
    $('invite').hidden = true;
    $('ended-meet').hidden = true;
    // A new profile is a new ticket on this phone: the room counts it from now on.
    await hub.invoke('Identify', TL.ticket()).catch(() => {});
  }
  $('invite-close').setAttribute('aria-label', t('inviteClose'));
  $('invite-close').addEventListener('click', snoozeInvite);
  $('invite-open').addEventListener('click', () => {
    if ($('invite').dataset.mode === 'round') location.href = '/connect';
    else openProfile();
  });

  // ----- The organizer's announcement: a round of introductions takes the same bar, whatever was snoozed -----
  let roundTimer;
  function inviteMode(mode) {
    $('invite').dataset.mode = mode;
    $('invite-title').textContent = t(mode ? 'roundTitle' : 'inviteTitle');
    $('invite').querySelector('.muted').textContent = t(mode ? 'roundText' : 'inviteText');
    $('invite-open').textContent = t(mode ? 'roundOpen' : 'inviteOpen');
  }
  function onAnnouncement(announcement) {
    clearTimeout(roundTimer);
    if (announcement && announcement.kind === 'intro-round' && announcement.until > Date.now()) {
      inviteMode('round');
      $('invite').hidden = false;
      roundTimer = setTimeout(() => onAnnouncement(null), announcement.until - Date.now());
    } else if ($('invite').dataset.mode === 'round') {
      $('invite').hidden = true;
      inviteMode('');
    }
  }
  const loadAnnouncement = () => fetch('/api/announcement').then((r) => r.json()).then((d) => onAnnouncement(d.announcement)).catch(() => {});
  $('ended-meet').addEventListener('click', openProfile);
  TL.myProfile().then((me) => {
    profile = me;
    renderStatus();
  }).catch(() => {});

  // ----- Chat (RF-30), and the hallway it becomes after the talk (RF-35) -----
  let chatTalk = null;
  function watchChat() {
    if (!state.talk || state.talk.id === chatTalk || hub.state !== 'Connected') return;
    chatTalk = state.talk.id;
    TL.chat($('chat-box'), chatTalk, hub).watch();
  }

  function setCaptionBanner(mode) {
    const banner = $('banner');
    const message = !state.live ? '' : mode === 'local' ? t('localCaptions') : mode === 'none' ? t('noCaptions') : '';
    banner.textContent = message;
    banner.hidden = message === '';
  }

  function resetTalk() {
    state.slides = [];
    $('transcript').replaceChildren();
    $('no-transcript').hidden = false;
    $('current-slide').hidden = true;
    $('no-slide').hidden = false;
    $('answers').replaceChildren();
    captionLines.clear();
    renderCaptions();
  }

  // The back button shows how many other talks are live right now, because they run at the same time as this one.
  function onRooms(summary) {
    const here = summary.rooms.find((r) => r.id === ROOM);
    if (here) {
      setCaptionBanner(here.captionMode);
      state.elapsedMs = here.elapsedMs;
      state.elapsedAt = performance.now();
      if (here.full && $('banner').hidden) {
        $('banner').textContent = t('fullHere');
        $('banner').hidden = false;
      }
    }
    const others = summary.rooms.filter((r) => r.id !== ROOM && r.live).length;
    $('back').hidden = false;
    $('back').href = '/?home=1';
    $('others').hidden = others === 0;
    $('others').textContent = others;
  }

  async function loadState() {
    const response = await fetch(`/api/rooms/${ROOM}/state`);
    if (response.status === 404) {
      location.replace('/');
      return;
    }
    const data = await response.json();
    state.room = data.room;
    state.talk = data.talk;
    state.next = data.next;
    state.live = data.live;
    state.remote = data.remote;
    state.remoteVideo = data.remoteVideo;
    state.voiceCloud = data.voiceCloud;
    state.elapsedMs = data.elapsedMs;
    state.elapsedAt = performance.now();

    const select = $('set-language');
    select.replaceChildren();
    select.append(new Option(t('original'), ''));
    const names = new Intl.DisplayNames([uiLanguage], { type: 'language' });
    for (const code of data.languages) select.append(new Option(names.of(code), code));
    select.value = prefs.language;

    setHeader();
    setCaptionBanner(data.captionMode);

    onRooms(await (await fetch('/api/rooms')).json());

    const transcript = await (await fetch(`/api/rooms/${ROOM}/transcript`)).json();
    transcript.segments.forEach(addSegment);
    transcript.slides.forEach(addSlide);
    pollBox.load(ROOM);
    showRating();
  }

  // ----- Hub -----
  const hub = new signalR.HubConnectionBuilder()
    .withUrl('/hub')
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * (ctx.previousRetryCount + 1), 5000) })
    .build();

  hub.on('caption', onCaption);
  hub.on('sign', onSign);
  hub.on('translation', onTranslation);
  hub.on('slide', addSlide);
  hub.on('slideText', (s) => {
    const img = document.querySelector(`#slide-${s.id} img`);
    if (img) img.alt = s.text;
  });
  hub.on('captionMode', setCaptionBanner);
  hub.on('rooms', onRooms);
  hub.on('reaction', (r) => TL.floatReaction($('float-layer'), r.kind));
  hub.on('photo', (photo) => { if (photo.roomId === ROOM) photosChanged(); });
  hub.on('photoRemoved', photosChanged);
  hub.on('poll', (poll) => { if (state.talk && poll.talkId === state.talk.id) pollBox.show(poll); });
  hub.on('announcement', onAnnouncement);
  hub.on('talkStarted', (talk) => {
    resetTalk();
    state.talk = talk;
    state.live = true;
    state.elapsedMs = 0;
    state.elapsedAt = performance.now();
    setHeader();
    if ($('join').hidden) $('video').play().catch(() => {});
    pollBox.show(null);
    showRating();
  });
  hub.on('talkEnded', (ended) => {
    state.live = false;
    if (state.talk) state.talk.endedAtUnixMs = Date.now();
    if (!ended.kept) {
      state.talk = null;
      resetTalk();
    }
    setHeader();
    setCaptionBanner('none');
    showRating();
  });

  hub.onreconnecting(() => { $('banner').textContent = t('reconnecting'); $('banner').hidden = false; });
  hub.onreconnected(async () => {
    $('banner').hidden = true;
    await hub.invoke('JoinRoom', ROOM, prefs.language).catch(() => {});
    watchSigns();
    if (TL.ticket()) await hub.invoke('Identify', TL.ticket()).catch(() => {});
    await loadState().catch(() => {});
    loadAnnouncement();
  });

  applyPrefs();
  const openTab = new URLSearchParams(location.search).get('tab');
  selectTab(tabs.includes(openTab) ? openTab : 'transcript');
  setHeader();
  (async function boot() {
    try {
      await hub.start();
      await hub.invoke('JoinRoom', ROOM, prefs.language);
      watchSigns();
      if (TL.ticket()) await hub.invoke('Identify', TL.ticket()).catch(() => {});
      await loadState();
      loadAnnouncement();
      const moment = location.hash.match(/^#t=(\d+)$/);
      if (moment) goToMoment(Number(moment[1]));
    } catch {
      setTimeout(boot, 2000);
    }
  })();
})();
