// RF-21: one script tag for a streaming platform. See docs/API.md.
// <div data-talklens-room="1" data-accent="ff5500" data-lang="en"></div>
// <script src="https://your-event-address/widget.js"></script>
(function () {
  const origin = new URL(document.currentScript.src).origin;
  document.querySelectorAll('[data-talklens-room]').forEach((host) => {
    const query = new URLSearchParams({ room: host.dataset.talklensRoom });
    for (const name of ['accent', 'lang', 'ask', 'background']) if (host.dataset[name]) query.set(name, host.dataset[name]);
    const frame = document.createElement('iframe');
    frame.src = `${origin}/embed?${query}`;
    frame.title = 'Live captions and questions';
    frame.style.cssText = `width:100%;height:${host.dataset.height || 260}px;border:0;background:transparent`;
    frame.allow = 'autoplay';
    host.replaceChildren(frame);
  });
})();
