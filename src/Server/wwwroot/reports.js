// RF-15, RF-16, RF-17, RF-20: aggregated reports and clips. Small groups are hidden by the server, not here.
(function () {
  const $ = (id) => document.getElementById(id);
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  };
  const time = (ms) => {
    const total = Math.floor(ms / 1000);
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
  };
  const api = async (path) => {
    const response = await fetch('/api/op' + path);
    if (!response.ok) throw new Error(String(response.status));
    return response.json();
  };
  const hidden = (value, minimum) => (value === null || value === undefined ? `under ${minimum}` : value);

  let talks = [];
  let overview = null;
  let sortBy = 'peak';

  // ----- Every talk side by side -----
  const COLUMNS = [
    ['title', 'Talk'], ['peak', 'Most at once'], ['watchMinutes', 'Person-minutes'], ['rating', 'Rating'], ['ratings', 'Ratings'],
    ['reactions', 'Reactions'], ['questions', 'Questions'], ['chat', 'Chat'], ['pollVotes', 'Poll votes'], ['catchUps', 'Catch-ups'],
  ];
  const REACTION_LOOK = {
    clap: ['👏', 'Applause', '#ffc35c'], fire: ['🔥', 'Fire', '#ff7a45'], wow: ['🤯', 'Mind blown', '#b48cff'], lost: ['❓', 'Lost', '#5fb8ff'],
  };

  async function loadOverview() {
    overview = await api('/reports/overview');
    const t = overview.totals;
    $('tiles').replaceChildren(...[
      [t.talks, 'talks'], [t.watchMinutes, 'person-minutes watched'], [t.questions, 'questions to speakers'],
      [t.chat, 'chat messages'], [t.pollVotes, 'poll votes'], [t.ratings, 'ratings'], [t.reactions, 'reactions'], [t.photos, 'photos on the wall'],
    ].map(([value, label]) => {
      const tile = el('div', 'tile');
      tile.append(el('b', '', value.toLocaleString()), el('span', 'muted', label));
      return tile;
    }));
    const best = [['Most attended', overview.best.attended], ['Best rated', overview.best.rated], ['Most questions', overview.best.questions]]
      .filter(([, title]) => title);
    $('best').replaceChildren(...best.map(([label, title]) => {
      const line = el('p', '');
      line.append(el('span', 'muted', label + ': '), el('b', '', title));
      return line;
    }));
    renderOverview();
  }

  function renderOverview() {
    const rows = [...overview.talks].sort((a, b) => sortBy === 'title'
      ? a.title.localeCompare(b.title)
      : (b[sortBy] ?? -1) - (a[sortBy] ?? -1));
    const top = Math.max(1, ...rows.map((r) => (typeof r[sortBy] === 'number' ? r[sortBy] : 0)));
    const head = el('tr');
    for (const [key, label] of COLUMNS) {
      const th = el('th');
      const button = el('button', key === sortBy ? 'sort on' : 'sort', label);
      button.type = 'button';
      button.addEventListener('click', () => { sortBy = key; renderOverview(); });
      th.append(button);
      head.append(th);
    }
    const body = rows.map((row) => {
      const tr = el('tr');
      for (const [key] of COLUMNS) {
        const value = row[key];
        const td = el('td', key === 'title' ? '' : 'num');
        if (key === 'title') {
          td.append(el('b', '', row.title), el('div', 'muted small', [row.speaker, row.room, row.minutes != null ? `${row.minutes} min` : ''].filter(Boolean).join(' · ')));
        } else {
          td.textContent = value == null ? '–' : key === 'rating' ? `${value} ★` : value.toLocaleString();
          // A bar in the sorted column shows the gaps at a glance.
          if (key === sortBy && typeof value === 'number') {
            const bar = el('span', 'bar');
            bar.style.width = `${Math.round((value / (sortBy === 'rating' ? 5 : top)) * 100)}%`;
            td.append(bar);
          }
        }
        tr.append(td);
      }
      return tr;
    });
    $('overview').replaceChildren(head, ...body);
    if (!body.length) $('overview').append(el('caption', 'muted', 'No talks yet.'));
  }

  async function audience(model) {
    $('topics-note').textContent = model ? 'Summarizing…' : '';
    const data = await api(`/reports/audience?model=${model}`);
    $('minimum').textContent = `Anything counted from fewer than ${data.minimum} people or messages is hidden. Private questions to the AI are never stored, so they are not in these reports.`;
    $('topics-note').textContent = data.topics.length
      ? (data.fromModel ? 'Topics summarized by AI from questions to speakers and public chat.' : 'Most repeated words in questions to speakers and public chat.')
      : 'Not enough questions or chat messages yet.';
    $('topics').replaceChildren(...data.topics.map((topic) => el('span', 'pill-glass', `${topic.name} · ${topic.count}`)));
    $('by-talk').replaceChildren(...data.perTalk.filter((row) => row.peak !== null).map((row) => {
      const line = el('div', 'talk-row');
      line.append(el('b', '', row.talk), el('span', 'muted', `Most at once: ${row.peak} · ${row.watchMinutes} person-minutes · ${hidden(row.registered, data.minimum)} with a ticket`));
      return line;
    }));
  }

  async function feedback() {
    if (!$('talk').value) {
      $('feedback').replaceChildren(el('p', 'muted', 'No talks yet.'));
      return;
    }
    const data = await api(`/reports/speaker/${$('talk').value}`);
    const nodes = [el('p', 'muted', `${hidden(data.questions, data.minimum)} questions · ${hidden(data.catchUps, data.minimum)} catch-up requests`)];
    if (data.moments.length) {
      nodes.push(el('h3', '', 'Moments that drew the most reaction'));
      data.moments.forEach((m) => nodes.push(el('p', '', `${time(m.t)}: ${m.questions} questions, ${m.catchUps} catch-ups, ${m.chat} chat messages`)));
    }
    if (data.repeated.length) {
      nodes.push(el('h3', '', 'Most repeated questions'));
      data.repeated.forEach((q) => nodes.push(el('p', '', `${q.count} × ${q.text}`)));
    }
    nodes.push(el('h3', '', 'Ratings'));
    if (data.rating) {
      nodes.push(el('p', '', `${data.rating.average} ★ from ${data.rating.count} people`));
      const chart = el('div', 'stars-chart');
      const most = Math.max(1, ...data.rating.stars);
      [...data.rating.stars].reverse().forEach((count, i) => {
        const line = el('div', 'stars-row');
        const track = el('span', 'track');
        const bar = el('span', 'bar');
        bar.style.width = `${Math.round((count / most) * 100)}%`;
        track.append(bar);
        line.append(el('span', '', `${5 - i} ★`), track, el('span', 'muted', String(count)));
        chart.append(line);
      });
      nodes.push(chart);
      data.rating.comments.slice(0, 20).forEach((c) => nodes.push(el('blockquote', 'comment', c)));
    } else {
      nodes.push(el('p', 'muted', `Ratings appear once at least ${data.minimum} people rated the talk (${hidden(data.ratingCount, data.minimum)} so far).`));
    }
    nodes.push(el('h3', '', 'How the room reacted'));
    if (data.reactions) nodes.push(...reactionNodes(data.reactions, data.reactionKinds));
    else nodes.push(el('p', 'muted', `Reactions appear once at least ${data.minimum} people reacted.`));
    if (data.polls.length) {
      nodes.push(el('h3', '', 'Polls'));
      for (const poll of data.polls) {
        nodes.push(el('p', '', poll.question));
        const total = poll.counts ? poll.counts.reduce((a, b) => a + b, 0) : 0;
        poll.options.forEach((option, i) => nodes.push(el('p', 'muted small', poll.counts
          ? `${option}: ${poll.counts[i]} (${Math.round((poll.counts[i] / Math.max(1, total)) * 100)}%)`
          : option)));
        if (!poll.counts) nodes.push(el('p', 'muted small', `Results hidden: fewer than ${data.minimum} votes.`));
      }
    }
    if (nodes.length === 1) nodes.push(el('p', 'muted', 'Not enough activity to show moments without exposing individuals.'));
    $('feedback').replaceChildren(...nodes);
  }

  // Stacked bars every 30 seconds, then the peaks with what was being said, so the speaker sees what landed and where people got lost.
  function reactionNodes(summary, kinds) {
    const nodes = [];
    const legend = el('div', 'react-legend');
    kinds.forEach((kind, i) => {
      const item = el('span', '');
      const swatch = el('i');
      swatch.style.background = REACTION_LOOK[kind][2];
      item.append(swatch, document.createTextNode(`${REACTION_LOOK[kind][0]} ${REACTION_LOOK[kind][1]} · ${summary.totals[i]}`));
      legend.append(item);
    });
    nodes.push(legend);
    const chart = el('div', 'react-chart');
    chart.setAttribute('role', 'img');
    chart.setAttribute('aria-label', `${summary.total} reactions over the talk`);
    const last = summary.timeline.length ? summary.timeline[summary.timeline.length - 1].t : 0;
    const most = Math.max(1, ...summary.timeline.map((b) => b.counts.reduce((a, c) => a + c, 0)));
    const byTime = new Map(summary.timeline.map((b) => [b.t, b]));
    for (let t = 0; t <= last; t += 30000) {
      const column = el('div', 'col');
      const bucket = byTime.get(t);
      column.title = time(t);
      if (bucket) {
        bucket.counts.forEach((count, i) => {
          if (!count) return;
          const part = el('span');
          part.style.height = `${(count / most) * 90}px`;
          part.style.background = REACTION_LOOK[kinds[i]][2];
          column.append(part);
        });
        column.title = `${time(t)}: ${bucket.counts.reduce((a, c) => a + c, 0)} reactions`;
      }
      chart.append(column);
    }
    nodes.push(chart, el('p', 'muted small', `0:00 to ${time(last + 30000)}`));
    if (summary.peaks.length) {
      const list = el('ul', 'peaks');
      for (const peak of summary.peaks) {
        const item = el('li');
        const text = el('span');
        text.append(el('b', '', `${time(peak.t)} · ${peak.count} ${REACTION_LOOK[peak.kind][1].toLowerCase()}`), document.createElement('br'),
          el('span', 'muted', peak.said ? `"${peak.said}"` : ''));
        item.append(el('span', 'emoji', REACTION_LOOK[peak.kind][0]), text);
        list.append(item);
      }
      nodes.push(list);
    }
    return nodes;
  }

  async function sponsor() {
    if (!$('sponsor').value) {
      $('sponsor-report').replaceChildren(el('p', 'muted', 'Add sponsors in Event setup.'));
      return;
    }
    const data = await api(`/reports/sponsor/${$('sponsor').value}`);
    const nodes = [el('p', 'muted', `${data.mentions.length} mentions by speakers and on slides · ${hidden(data.questionMentions, data.minimum)} in questions · ${hidden(data.chatMentions, data.minimum)} in chat · ${data.leads ?? 0} contacts`)];
    data.mentions.forEach((m) => {
      const row = el('p', 'search-hit');
      const a = el('a', 'cite', time(m.t));
      a.href = `/talk/${m.talkId}#t=${m.t}`;
      a.target = '_blank';
      row.append(a, ` ${m.talkTitle} (${m.kind === 'slide' ? 'slide' : 'said'}): ${m.excerpt}`);
      nodes.push(row);
    });
    $('sponsor-report').replaceChildren(...nodes);
  }

  async function clips() {
    const list = await api('/clips');
    const titles = new Map(talks.map((t) => [t.id, t.title]));
    $('clips').replaceChildren(...(list.length ? list.map((clip) => {
      const row = el('div', 'talk-row');
      const info = el('div');
      info.append(el('b', '', titles.get(clip.talkId) || 'Talk'), el('p', 'muted small', `${time(clip.t)} · ${clip.reason} · ${clip.status}`));
      const actions = el('div', 'actions');
      if (clip.status === 'ready') {
        const download = el('a', 'primary', 'Download');
        download.href = `/api/op/clips/${clip.id}.mp4`;
        actions.append(download);
      }
      const remove = el('button', 'secondary', 'Delete');
      remove.type = 'button';
      remove.addEventListener('click', async () => {
        await fetch(`/api/op/clips/${clip.id}`, { method: 'DELETE' });
        clips();
      });
      actions.append(remove);
      row.append(info, actions);
      return row;
    }) : [el('p', 'muted', 'No clips yet. Tick "Record video for clips" when you start a talk.')]));
  }

  (async function boot() {
    talks = await api('/talks');
    $('talk').replaceChildren(...talks.map((t) => new Option(t.title, t.id)));
    const sponsors = await api('/sponsors');
    $('sponsor').replaceChildren(...sponsors.map((s) => new Option(s.name, s.id)));
    $('talk').addEventListener('change', feedback);
    $('sponsor').addEventListener('change', sponsor);
    $('topics-model').addEventListener('click', () => audience(true));
    loadOverview();
    audience(false);
    feedback();
    sponsor();
    clips();
    setInterval(clips, 8000);
  })();
})();
