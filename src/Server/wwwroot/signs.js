// Try the signing avatar: type a sentence and see which of it can be signed so far.
(function () {
  const t = TL.strings({
    en: {
      badge: 'Experimental', title: 'Signing avatar', sign: 'Sign it', prompt: 'Type a sentence',
      warning: 'A research demo, not an interpreter. It knows about 830 signs from a published set, of mixed or unconfirmed sign language, and no one who signs has reviewed the result.',
      knownList: 'See all the signs', knownTitle: 'What it can sign', knownText: 'Anything else is skipped and shown crossed out, so you always see what was left out.',
      loading: 'Loading the avatar. It comes from the internet, so this can take a moment.', ready: 'Ready.', failed: 'The avatar could not load. It needs internet.',
      noSigns: 'It has no sign for any of those words yet.', thinking: 'Translating…', signing: 'Signing…', error: 'That did not work. Try again.', byModel: 'Order chosen by AI', byWords: 'Word by word, without AI',
    },
    es: {
      badge: 'Experimental', title: 'Avatar de señas', sign: 'Señarlo', prompt: 'Escribe una frase',
      warning: 'Una demo de investigación, no un intérprete. Conoce unas 830 señas de un conjunto publicado, de lengua de señas mezclada o sin confirmar, y ninguna persona que signe ha revisado el resultado.',
      knownList: 'Ver todas las señas', knownTitle: 'Lo que puede señar', knownText: 'Lo demás se omite y aparece tachado, para que siempre veas qué quedó fuera.',
      loading: 'Cargando el avatar. Viene de internet, así que puede tardar.', ready: 'Listo.', failed: 'El avatar no pudo cargar. Necesita internet.',
      noSigns: 'Todavía no tiene seña para ninguna de esas palabras.', thinking: 'Traduciendo…', signing: 'Señando…', error: 'No funcionó. Inténtalo otra vez.', byModel: 'Orden elegido por IA', byWords: 'Palabra por palabra, sin IA',
    },
  });
  TL.mountNav('');

  const $ = (id) => document.getElementById(id);
  const status = (key) => { $('sign-status').textContent = t(key); };

  fetch('/api/signs').then((r) => r.json()).then((glosses) => {
    $('sign-known').replaceChildren(...glosses.map((gloss) => TL.el('span', 'sign-chip', gloss)));
  });

  status('loading');
  const avatar = TLSign.load().then(() => {
    status('ready');
    return TLSign.player({ onSay: () => status('signing'), onDone: () => status('ready') });
  });
  avatar.catch(() => status('failed'));

  $('sign-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const text = $('sign-text').value.trim();
    if (!text) return;
    try {
      // Pressing the button before the avatar has loaded still counts: it signs as soon as it can.
      const say = await avatar;
      status('thinking');
      const item = await TLSign.translate(text);
      const note = TL.el('p', 'muted sign-how', t(item.byModel ? 'byModel' : 'byWords'));
      $('sign-result').replaceChildren(TLSign.tokens(item), note);
      if (item.tokens.some((token) => token.hasSign)) say.say(item);
      else status('noSigns');
    } catch (e) {
      if (e.message !== 'timeout' && e.message !== 'script') status('error');
    }
  });
})();
