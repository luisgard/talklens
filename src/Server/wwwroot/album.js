// The event album: every approved photo, by talk, with likes, a way for anyone in a photo to take it down, and a download of all of them.
(function () {
  const t = TL.strings({
    en: {
      title: 'Photos', intro: 'Shared by people at the event and approved by the organizers. Share yours from the Photos tab in any room.',
      filter: 'Which photos', all: 'Every photo', event: 'Between talks', download: 'Download all', empty: 'No photos yet.',
      report: 'I am in this photo, take it down', reported: 'Taken down. The organizers will check it.', remove: 'Delete my photo', removed: 'Deleted.',
      close: 'Close', like: '♡ {n}', liked: '♥ {n}', waiting: 'Waiting for approval', failed: 'That did not work. Try again.',
    },
    es: {
      title: 'Fotos', intro: 'Compartidas por la gente del evento y aprobadas por los organizadores. Comparte las tuyas desde la pestaña Fotos de cualquier sala.',
      filter: 'Qué fotos', all: 'Todas las fotos', event: 'Entre charlas', download: 'Descargar todas', empty: 'Todavía no hay fotos.',
      report: 'Salgo en esta foto, quítenla', reported: 'Quitada. Los organizadores la van a revisar.', remove: 'Borrar mi foto', removed: 'Borrada.',
      close: 'Cerrar', like: '♡ {n}', liked: '♥ {n}', waiting: 'Esperando aprobación', failed: 'No funcionó. Intenta de nuevo.',
    },
  });
  TL.mountNav('photos');
  const $ = (id) => document.getElementById(id);
  const voter = TL.device();
  let items = [];
  let open = null;

  function visible() {
    const filter = $('talk-filter').value;
    return items.filter((item) => filter === '' || (filter === 'event' ? item.photo.talkId === null : String(item.photo.talkId) === filter));
  }

  function render() {
    const shown = visible();
    $('empty').hidden = shown.length > 0;
    $('grid').replaceChildren(...shown.map((item) => {
      const tile = TL.el('figure', 'photo-tile');
      const img = TL.el('img');
      img.loading = 'lazy';
      img.src = item.url;
      img.alt = item.photo.alt;
      const button = TL.el('button', 'photo-open');
      button.type = 'button';
      button.setAttribute('aria-label', item.photo.alt || t('title'));
      button.addEventListener('click', () => view(item));
      tile.append(img, button);
      if (item.photo.status === 'approved' && item.photo.likes > 0) tile.append(TL.el('span', 'photo-likes', `♥ ${item.photo.likes}`));
      if (item.photo.status !== 'approved') tile.append(TL.el('figcaption', 'photo-badge', t('waiting')));
      return tile;
    }));
  }

  function paintLike() {
    $('like').textContent = t(open.liked ? 'liked' : 'like', { n: open.photo.likes });
  }

  function view(item) {
    open = item;
    $('viewer-img').src = item.url;
    $('viewer-img').alt = item.photo.alt;
    $('viewer-alt').textContent = item.photo.alt;
    $('viewer-note').textContent = '';
    const approved = item.photo.status === 'approved';
    $('like').hidden = !approved;
    $('report').hidden = !approved || item.photo.mine;
    $('remove').hidden = !item.photo.mine;
    if (approved) paintLike();
    $('viewer').showModal();
  }

  $('close').addEventListener('click', () => $('viewer').close());
  $('like').addEventListener('click', async () => {
    const response = await TL.api(`/api/photos/${open.photo.id}/like`, { method: 'POST', json: { voter } }).catch(() => null);
    if (!response || !response.ok) { $('viewer-note').textContent = t('failed'); return; }
    const result = await response.json();
    open.liked = result.liked;
    open.photo.likes = result.likes;
    paintLike();
    render();
  });
  $('report').addEventListener('click', async () => {
    const response = await TL.api(`/api/photos/${open.photo.id}/report`, { method: 'POST', json: { voter } }).catch(() => null);
    $('viewer-note').textContent = response && response.ok ? t('reported') : t('failed');
    if (response && response.ok) { items = items.filter((i) => i !== open); render(); }
  });
  $('remove').addEventListener('click', async () => {
    const response = await TL.api(`/api/photos/${open.photo.id}?voter=${voter}`, { method: 'DELETE' }).catch(() => null);
    $('viewer-note').textContent = response && response.ok ? t('removed') : t('failed');
    if (response && response.ok) { items = items.filter((i) => i !== open); render(); }
  });
  $('talk-filter').addEventListener('change', render);

  async function load() {
    const data = await (await fetch(`/api/photos?voter=${voter}`)).json();
    items = data.photos;
    const select = $('talk-filter');
    const chosen = select.value;
    select.replaceChildren(new Option(t('all'), ''), ...data.talks.map((talk) => new Option(`${talk.title} · ${talk.speaker}`, talk.id)));
    if (items.some((i) => i.photo.status === 'approved' && i.photo.talkId === null)) select.append(new Option(t('event'), 'event'));
    select.value = [...select.options].some((o) => o.value === chosen) ? chosen : '';
    render();
  }

  const hub = TL.newHub();
  hub.on('photo', load);
  hub.on('photoRemoved', (id) => { items = items.filter((i) => i.photo.id !== id); render(); });
  hub.on('photoLikes', (change) => {
    const item = items.find((i) => i.photo.id === change.id);
    if (!item) return;
    item.photo.likes = change.likes;
    if (open === item && $('viewer').open) paintLike();
    render();
  });
  hub.onreconnected(load);
  load();
  hub.start().catch(() => {});
})();
