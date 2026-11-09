// Publishes the phone camera or a shared screen to MediaMTX over WHIP (RF-02, RF-04).
(function () {
  const $ = (id) => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  const path = query.get('path') === 'slides' ? 'slides' : 'cam';
  const room = Number(query.get('room')) || 1;
  const isSlides = path === 'slides';
  const CAMERA_BITRATE = { 720: 1800000, 480: 800000 };
  const SLIDE_BITRATE = 1200000;
  const RETRY_FIRST_MS = 1000;
  const RETRY_MAX_MS = 5000;
  const CONNECT_TIMEOUT_MS = 10000;
  const DISCONNECT_GRACE_MS = 3000;

  let stream = null;
  let connection = null;
  let wakeLock = null;
  let sending = false;
  let attempts = 0;
  let generation = 0;
  let retry = null;
  let watchdog = null;

  const kind = isSlides ? 'Slides source' : 'Camera';
  $('heading').textContent = kind;
  fetch('/api/rooms').then((r) => r.json()).then((summary) => {
    const found = summary.rooms.find((r) => r.id === room);
    if (found) $('heading').textContent = `${kind} · ${found.name}`;
  }).catch(() => {});
  if (isSlides && navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia) {
    $('source').append(new Option('Share a screen or window', 'screen'));
  }
  $('source').append(new Option('Rear camera', 'environment'));
  $('source').append(new Option('Front camera', 'user'));

  function setPill(state, text) {
    $('pill').dataset.state = state;
    $('pill').textContent = text;
  }

  async function captureSource() {
    const choice = $('source').value;
    if (choice === 'screen') {
      // Audio stays off: the speaker's voice comes from the audio source, not the laptop.
      return navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 5 }, audio: false });
    }
    const height = Number($('quality').value);
    return navigator.mediaDevices.getUserMedia({
      video: { facingMode: { ideal: choice }, height: { ideal: height }, frameRate: { ideal: isSlides ? 5 : 30 } },
      // Speech recognition works better on the raw microphone signal.
      audio: isSlides ? false : { echoCancellation: false, noiseSuppression: false, autoGainControl: true },
    });
  }

  async function keepAwake() {
    try {
      if ('wakeLock' in navigator) wakeLock = await navigator.wakeLock.request('screen');
    } catch { /* not fatal: the note on the page tells the operator */ }
  }

  function closeConnection() {
    clearTimeout(watchdog);
    if (!connection) return;
    connection.pc.onconnectionstatechange = null;
    connection.pc.close();
    if (connection.session) fetch(connection.session, { method: 'DELETE', keepalive: true }).catch(() => {});
    connection = null;
  }

  function stop() {
    sending = false;
    generation++;
    clearTimeout(retry);
    closeConnection();
    if (stream) stream.getTracks().forEach((track) => track.stop());
    stream = null;
    $('preview').srcObject = null;
    if (wakeLock) wakeLock.release().catch(() => {});
    wakeLock = null;
    $('start').hidden = false;
    $('stop').hidden = true;
    setPill('waiting', 'Not sending');
  }

  function scheduleReconnect() {
    if (!sending) return;
    clearTimeout(retry);
    closeConnection();
    setPill('bad', 'Connection lost, reconnecting');
    const delay = Math.min(RETRY_MAX_MS, RETRY_FIRST_MS * 2 ** attempts);
    attempts++;
    retry = setTimeout(reconnect, delay);
  }

  function watch(current) {
    const { pc } = current;
    const giveUpAfter = (ms) => {
      clearTimeout(watchdog);
      watchdog = setTimeout(scheduleReconnect, ms);
    };
    giveUpAfter(CONNECT_TIMEOUT_MS);
    pc.onconnectionstatechange = () => {
      const pcState = pc.connectionState;
      if (pcState === 'connected') {
        clearTimeout(watchdog);
        attempts = 0;
        $('error').textContent = '';
        setPill('ok', 'Sending');
      }
      // "disconnected" often clears by itself, so it gets a moment first.
      if (pcState === 'disconnected') {
        setPill('warn', 'Connection unstable');
        giveUpAfter(DISCONNECT_GRACE_MS);
      }
      if (pcState === 'failed') scheduleReconnect();
    };
  }

  function useStream(captured) {
    stream = captured;
    $('preview').srcObject = captured;
    captured.getVideoTracks().forEach((track) => {
      if (isSlides) track.contentHint = 'detail';
      track.addEventListener('ended', () => {
        if (stream !== captured) return;
        // Ending a screen share is the person's choice. A camera ends when the phone takes it away.
        if ($('source').value === 'screen') return stop();
        captured.getTracks().forEach((other) => other.stop());
        stream = null;
        scheduleReconnect();
      });
    });
  }

  function publish() {
    const maxBitrate = isSlides ? SLIDE_BITRATE : CAMERA_BITRATE[$('quality').value];
    return TalkLensRtc.publishWhip(`/media/live/r${room}/${path}/whip`, stream, { maxBitrate });
  }

  // Keeps the same camera when it is still live, so the picture only pauses for the new session.
  async function reconnect() {
    const mine = ++generation;
    setPill('warn', 'Reconnecting');
    try {
      if (!stream) {
        const captured = await captureSource();
        if (mine !== generation) return captured.getTracks().forEach((track) => track.stop());
        useStream(captured);
      }
      const next = await publish();
      if (mine !== generation) {
        next.pc.close();
        if (next.session) fetch(next.session, { method: 'DELETE', keepalive: true }).catch(() => {});
        return;
      }
      connection = next;
      watch(next);
    } catch (error) {
      if (mine !== generation) return;
      if (error.status === 401) {
        stop();
        $('error').textContent = 'Operator key required: scan the QR again.';
        return;
      }
      $('error').textContent = error.message;
      scheduleReconnect();
    }
  }

  async function start() {
    $('error').textContent = '';
    $('start').disabled = true;
    try {
      if (!navigator.mediaDevices) throw new Error('This browser blocks the camera on this page. Open it over HTTPS.');
      useStream(await captureSource());
      connection = await publish();
      sending = true;
      attempts = 0;
      watch(connection);
      setPill('warn', 'Connecting');
      await keepAwake();
      $('start').hidden = true;
      $('stop').hidden = false;
    } catch (error) {
      stop();
      $('error').textContent = error.status === 401 ? 'Operator key required: scan the QR again.' : error.message;
    } finally {
      $('start').disabled = false;
    }
  }

  // A phone coming back should not sit out the rest of a retry delay.
  function retryNow() {
    if (!sending || connection) return;
    clearTimeout(retry);
    attempts = 0;
    reconnect();
  }

  $('start').addEventListener('click', start);
  $('stop').addEventListener('click', stop);
  window.addEventListener('online', retryNow);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible' || !sending) return;
    keepAwake();
    retryNow();
  });
})();
