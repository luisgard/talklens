// The experimental signing avatar. CWASA runs unmodified from its home at the University of East Anglia, as its terms ask,
// so it is only loaded when someone turns this on, and it needs internet.
window.TLSign = (function () {
  const HOST = 'https://vhg.cmp.uea.ac.uk/tech/jas/vhg2025a/cwa';
  const LOAD_TIMEOUT_MS = 30000;
  const IDLE_TIMEOUT_MS = 20000;
  let loading = null;

  function load() {
    if (loading) return loading;
    loading = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('timeout')), LOAD_TIMEOUT_MS);
      const css = document.createElement('link');
      css.rel = 'stylesheet';
      css.href = `${HOST}/cwasa.css`;
      // First, so that its page colours do not win over ours.
      document.head.prepend(css);
      const script = document.createElement('script');
      script.src = `${HOST}/allcsa.js`;
      script.onerror = () => { clearTimeout(timer); reject(new Error('script')); };
      script.onload = () => {
        CWASA.addHook('avatarready', () => { clearTimeout(timer); resolve(); }, 0);
        CWASA.init({ ambIdle: false, useClientConfig: false, avSettings: { avList: 'avs', initAv: 'anna', initSpeed: 0 } });
      };
      document.head.append(script);
    });
    loading.catch(() => { loading = null; });
    return loading;
  }

  // Plays one sentence after another, and lets go of old ones so the avatar never falls far behind the talk.
  function player({ maxQueued = 3, onSay, onDone, onSkip } = {}) {
    const queue = [];
    let busy = false;
    let release = null;

    function next() {
      if (busy) return;
      if (queue.length === 0) { onDone?.(); return; }
      busy = true;
      const item = queue.shift();
      onSay?.(item);
      const signs = item.tokens.filter((token) => token.hasSign).length;
      if (signs === 0) { busy = false; next(); return; }
      const done = () => { clearTimeout(timer); release = null; busy = false; next(); };
      const timer = setTimeout(done, IDLE_TIMEOUT_MS);
      release = done;
      CWASA.playSiGMLText(item.sigml, 0);
    }

    CWASA.addHook('animidle', () => { if (release) release(); }, 0);

    return {
      say(item) {
        queue.push(item);
        while (queue.length > maxQueued) { queue.shift(); onSkip?.(); }
        next();
      },
    };
  }

  async function translate(text) {
    const response = await fetch('/api/signs/translate', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text }),
    });
    if (!response.ok) throw new Error(String(response.status));
    return response.json();
  }

  function tokens(item) {
    const line = document.createElement('p');
    line.className = 'sign-tokens';
    for (const token of item.tokens) {
      const chip = document.createElement('span');
      chip.className = token.hasSign ? 'sign-chip' : 'sign-chip missing';
      chip.textContent = token.word.replaceAll('_', ' ');
      line.append(chip);
    }
    return line;
  }

  return { load, player, translate, tokens };
})();
