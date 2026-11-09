// The organizer's way around: the same few places on every page, in the order an event happens.
(function () {
  const PLACES = [
    ['/start', 'Home'],
    ['/manage', 'Set up'],
    ['/checkin', 'Check-in'],
    ['/operator', 'Go live'],
    ['/organizer', 'Live view'],
    ['/reports', 'Results'],
  ];
  const nav = document.createElement('nav');
  nav.className = 'page-nav console-nav';
  nav.setAttribute('aria-label', 'Organizer pages');
  for (const [href, label] of PLACES) {
    const link = document.createElement('a');
    link.className = 'nav-link';
    link.href = href;
    link.textContent = label;
    if (location.pathname === href) link.setAttribute('aria-current', 'page');
    nav.append(link);
  }
  const attendee = document.createElement('a');
  attendee.className = 'nav-link nav-aside';
  attendee.href = '/';
  attendee.target = '_blank';
  attendee.rel = 'noopener';
  attendee.textContent = 'What attendees see';
  nav.append(attendee);
  document.body.prepend(nav);
})();
