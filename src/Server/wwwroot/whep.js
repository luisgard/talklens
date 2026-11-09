// Minimal WebRTC helpers for WHEP playback and WHIP publishing against MediaMTX.
(function () {
  const ICE_WAIT_MS = 800;
  const SIGNALING_TIMEOUT_MS = 8000;

  // On a LAN there is no STUN to wait for: host candidates arrive at once.
  function iceGathered(pc) {
    return new Promise((resolve) => {
      if (pc.iceGatheringState === 'complete') return resolve();
      const done = () => {
        pc.removeEventListener('icegatheringstatechange', check);
        resolve();
      };
      const check = () => { if (pc.iceGatheringState === 'complete') done(); };
      pc.addEventListener('icegatheringstatechange', check);
      setTimeout(done, ICE_WAIT_MS);
    });
  }

  async function negotiate(pc, url) {
    await pc.setLocalDescription(await pc.createOffer());
    await iceGathered(pc);
    // Without a limit, a request lost with the Wi-Fi can hang for minutes and hold up the retry.
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), SIGNALING_TIMEOUT_MS);
    let response;
    try {
      response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/sdp' },
        body: pc.localDescription.sdp,
        signal: abort.signal,
      });
    } finally {
      clearTimeout(timer);
    }
    if (!response.ok) {
      const error = new Error(`Signaling failed: ${response.status}`);
      error.status = response.status;
      throw error;
    }
    const sdp = await response.text();
    await pc.setRemoteDescription({ type: 'answer', sdp });
    return response.headers.get('Location');
  }

  // Keeps a WHEP session alive, retrying while the camera is not publishing yet.
  class WhepPlayer {
    constructor(url, video, { onState } = {}) {
      this.url = url;
      this.video = video;
      this.onState = onState || (() => {});
      this.audioOnly = false;
      this.stopped = true;
    }

    start(audioOnly) {
      this.audioOnly = audioOnly;
      this.stopped = false;
      this.connect();
    }

    stop() {
      this.stopped = true;
      clearTimeout(this.retry);
      this.close();
    }

    close() {
      if (this.pc) {
        this.pc.ontrack = null;
        this.pc.onconnectionstatechange = null;
        this.pc.close();
        this.pc = null;
      }
      if (this.session) {
        fetch(this.session, { method: 'DELETE', keepalive: true }).catch(() => {});
        this.session = null;
      }
    }

    scheduleRetry() {
      if (this.stopped) return;
      clearTimeout(this.retry);
      this.retry = setTimeout(() => this.connect(), 2000);
    }

    async connect() {
      this.close();
      if (this.stopped) return;
      this.onState('connecting');
      const pc = new RTCPeerConnection();
      this.pc = pc;
      pc.addTransceiver('audio', { direction: 'recvonly' });
      if (!this.audioOnly) pc.addTransceiver('video', { direction: 'recvonly' });

      const stream = new MediaStream();
      pc.ontrack = (event) => {
        stream.addTrack(event.track);
        this.video.srcObject = stream;
      };
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'connected') this.onState('playing');
        if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
          this.onState('connecting');
          this.scheduleRetry();
        }
      };

      try {
        this.session = await negotiate(pc, this.url);
      } catch (error) {
        this.onState(error.status === 404 ? 'no-stream' : 'connecting');
        this.scheduleRetry();
      }
    }
  }

  async function publishWhip(url, stream, { maxBitrate, preferH264 = true } = {}) {
    const pc = new RTCPeerConnection();
    for (const track of stream.getTracks()) {
      const transceiver = pc.addTransceiver(track, { direction: 'sendonly', streams: [stream] });
      if (track.kind === 'video') {
        // Hardware H.264 keeps the phone cool, and every phone can play it back.
        if (preferH264 && RTCRtpSender.getCapabilities && transceiver.setCodecPreferences) {
          const codecs = RTCRtpSender.getCapabilities('video').codecs;
          const h264 = codecs.filter((c) => c.mimeType === 'video/H264');
          const rest = codecs.filter((c) => c.mimeType !== 'video/H264');
          if (h264.length) transceiver.setCodecPreferences([...h264, ...rest]);
        }
        if (maxBitrate) {
          const parameters = transceiver.sender.getParameters();
          parameters.encodings = parameters.encodings && parameters.encodings.length ? parameters.encodings : [{}];
          parameters.encodings[0].maxBitrate = maxBitrate;
          transceiver.sender.setParameters(parameters).catch(() => {});
        }
      }
    }
    try {
      return { pc, session: await negotiate(pc, url) };
    } catch (error) {
      pc.close();
      throw error;
    }
  }

  window.TalkLensRtc = { WhepPlayer, publishWhip };
})();
