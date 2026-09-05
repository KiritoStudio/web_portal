'use strict';

/**
 * Home Portal front end.
 *
 * Ordering: most-opened first, ties going to the newer link. A new link has zero
 * clicks, so without that tiebreak it would sit at the bottom forever and never
 * get the chance to earn any.
 */

/** Group palette; a given group name always lands on the same hue. */
const HUES = [8, 32, 140, 190, 214, 258, 292, 320];

const state = { sites: [], groups: [], clicks: {} };

/**
 * Display order for this visit, frozen when the page loads.
 * Opening a link does not make it jump forward under the cursor — otherwise the
 * second thing you meant to click has already moved.
 */
let pinned = [];

let view = 'flat';
let query = '';
let editing = null;

const $ = (id) => document.getElementById(id);
const canvas = $('canvas');
const stat = $('stat');

// ---------------------------------------------------------------- API calls

/**
 * Calls the backend. On failure throws an Error carrying the server's own wording,
 * leaving it to the caller to decide how to tell the user.
 * @returns {Promise<any>} the parsed response body
 */
async function api(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = res.status === 204 ? '' : await res.text();

  // The body may be empty, or not JSON at all (an error page from something in front
  // of us). Calling res.json() directly lets a parse error bury the useful status code.
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      throw new Error(`Server returned something unexpected (${res.status})`);
    }
  }

  if (!res.ok) throw new Error(data && data.error ? data.error : `Request failed (${res.status})`);
  return data;
}

// ---------------------------------------------------------------- Rendering

function clicksOf(id) {
  return state.clicks[id] || 0;
}

function hueOf(group) {
  if (!group) return null;
  let n = 0;
  for (let i = 0; i < group.length; i++) n = (n * 31 + group.charCodeAt(i)) % 9973;
  return HUES[n % HUES.length];
}

/** Ungrouped cards drop saturation to zero and land on grey, no separate rule needed. */
function styleOf(group) {
  const h = hueOf(group);
  return h === null ? '--g-s:0%;--g-ts:0%;' : `--h:${h};`;
}

function shortAddr(url) {
  const u = new URL(url);
  return u.hostname + (u.port ? `:${u.port}` : '');
}

function esc(s) {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

function byHeat(a, b) {
  return clicksOf(b.id) - clicksOf(a.id) || Date.parse(b.addedAt) - Date.parse(a.addedAt);
}

function lockOrder() {
  pinned = state.sites.slice().sort(byHeat).map((s) => s.id);
}

function inPinnedOrder(list) {
  return list.slice().sort((a, b) => pinned.indexOf(a.id) - pinned.indexOf(b.id));
}

function cardHtml(site, index) {
  const tip = `${site.group ? site.group + ' · ' : ''}opened ${clicksOf(site.id)} times`;
  // noreferrer as well as noopener: several LAN services (qBittorrent's WebUI among them)
  // reject requests carrying a foreign Referer as cross-site, answering Unauthorized.
  // It also keeps the portal's own address from leaking to whatever is being opened.
  return `<a class="card" href="${esc(site.url)}" target="_blank" rel="noopener noreferrer"
    data-id="${site.id}" title="${esc(tip)}"
    style="${styleOf(site.group)}animation-delay:${Math.min(index * 18, 260)}ms">
    <span class="ico">${esc(site.name.slice(0, 1))}</span>
    <span class="txt"><span class="nm">${esc(site.name)}</span><span class="ad">${esc(shortAddr(site.url))}</span></span>
    <span class="tools">
      <button class="ibtn" data-act="edit" aria-label="Edit ${esc(site.name)}"><svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M11 2.5 13.5 5 5.5 13H3v-2.5z"/></svg></button>
      <button class="ibtn danger" data-act="del" aria-label="Delete ${esc(site.name)}"><svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 4.5h9M6.5 4.5V3h3v1.5M5 4.5l.6 8.5h4.8l.6-8.5"/></svg></button>
    </span>
  </a>`;
}

function groupHeadHtml(name, count) {
  if (name === null) {
    // Ungrouped takes no part in the ordering; it is always last
    return `<div class="ghead none"><span class="swatch"></span><span class="gname">Ungrouped</span>
      <span class="rule"></span><span class="cnt">${count} item${count === 1 ? '' : 's'}</span></div>`;
  }
  return `<div class="ghead" style="${styleOf(name)}" data-group="${esc(name)}" draggable="true">
    <span class="grip" aria-hidden="true"><svg width="10" height="14" viewBox="0 0 10 14" fill="currentColor"><circle cx="2.5" cy="2" r="1.3"/><circle cx="7.5" cy="2" r="1.3"/><circle cx="2.5" cy="7" r="1.3"/><circle cx="7.5" cy="7" r="1.3"/><circle cx="2.5" cy="12" r="1.3"/><circle cx="7.5" cy="12" r="1.3"/></svg></span>
    <span class="swatch"></span><span class="gname">${esc(name)}</span>
    <button class="ibtn rename" data-act="rename" aria-label="Rename group ${esc(name)}"><svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M11 2.5 13.5 5 5.5 13H3v-2.5z"/></svg></button>
    <span class="rule"></span><span class="cnt">${count} item${count === 1 ? '' : 's'}</span></div>`;
}

function render() {
  const q = query.toLowerCase();
  const hits = inPinnedOrder(
    state.sites.filter(
      (s) =>
        !q ||
        s.name.toLowerCase().includes(q) ||
        s.url.toLowerCase().includes(q) ||
        (s.group && s.group.toLowerCase().includes(q))
    )
  );

  const used = new Set(state.sites.map((s) => s.group).filter(Boolean));
  stat.textContent = `${state.sites.length} services · ${used.size} groups`;

  if (!state.sites.length) {
    canvas.innerHTML = `<div class="empty"><b>No links yet</b>Hit Add in the top right and start collecting — router, NAS, cameras, whatever you keep typing IPs for.</div>`;
    return;
  }
  if (!hits.length) {
    canvas.innerHTML = `<div class="empty"><b>Nothing matches</b>Try another word — names, addresses and group names are all searchable.</div>`;
    return;
  }

  if (view === 'flat') {
    canvas.innerHTML = `<div class="grid">${hits.map(cardHtml).join('')}</div>`;
    return;
  }

  const bag = new Map();
  const loose = [];
  for (const site of hits) {
    if (!site.group) loose.push(site);
    else if (bag.has(site.group)) bag.get(site.group).push(site);
    else bag.set(site.group, [site]);
  }
  // Group order is dragged and stored in groups; cards inside a group still go by clicks
  const order = state.groups.filter((g) => bag.has(g));

  let i = 0;
  let html = order
    .map((name) => {
      const members = bag.get(name);
      return `<section class="group">${groupHeadHtml(name, members.length)}
        <div class="grid">${members.map((s) => cardHtml(s, i++)).join('')}</div></section>`;
    })
    .join('');
  if (loose.length) {
    html += `<section class="group">${groupHeadHtml(null, loose.length)}
      <div class="grid">${loose.map((s) => cardHtml(s, i++)).join('')}</div></section>`;
  }
  canvas.innerHTML = html;
}

// ---------------------------------------------------------------- Toast

const toast = $('toast');
const tmsg = $('tmsg');
const taction = $('taction');
let toastTimer = null;
let toastFn = null;

function showToast(message, { label = null, onAction = null, bad = false, ms = 5000 } = {}) {
  tmsg.textContent = message;
  toastFn = onAction;
  taction.textContent = label || '';
  taction.hidden = !label;
  toast.classList.toggle('bad', bad);
  toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(hideToast, ms);
}

function hideToast() {
  toast.hidden = true;
  toastFn = null;
}

taction.addEventListener('click', () => {
  const fn = toastFn;
  hideToast();
  if (fn) fn();
});

// ---------------------------------------------------------------- Card interaction

canvas.addEventListener('click', async (event) => {
  const card = event.target.closest('.card');
  const head = event.target.closest('.ghead');

  if (head && event.target.closest('[data-act="rename"]')) {
    return startRename(head);
  }
  if (!card) return;

  const site = state.sites.find((s) => s.id === card.dataset.id);
  const action = event.target.closest('[data-act]');

  if (action) {
    event.preventDefault();
    if (action.dataset.act === 'edit') openSheet(site);
    else await removeSite(site);
    return;
  }

  // The link opens in a new tab as usual; this just records the open
  countClick(site);
});

function countClick(site) {
  state.clicks[site.id] = clicksOf(site.id) + 1;
  api('POST', `/api/sites/${site.id}/click`).catch((err) => {
    // NOTE: the link is already open. Interrupting someone over one lost count is worse
    // than losing it, so this stays in the console.
    console.error('Click count was not recorded:', err.message);
  });
}

async function removeSite(site) {
  const at = state.sites.indexOf(site);
  let removed;
  try {
    removed = await api('DELETE', `/api/sites/${site.id}`);
  } catch (err) {
    return showToast(`Could not delete: ${err.message}`, { bad: true });
  }

  state.sites.splice(at, 1);
  delete state.clicks[site.id];
  render();

  showToast(`Deleted "${site.name}"`, {
    label: 'Undo',
    onAction: async () => {
      try {
        // Carries the original id, timestamp and count, so undo leaves no trace of the delete
        await api('POST', '/api/sites', { ...site, clicks: removed.clicks });
      } catch (err) {
        return showToast(`Undo failed: ${err.message}`, { bad: true });
      }
      state.sites.splice(at, 0, site);
      if (removed.clicks) state.clicks[site.id] = removed.clicks;
      render();
    }
  });
}

// ---------------------------------------------------------------- Dragging groups

let dragging = null;

canvas.addEventListener('dragstart', (event) => {
  const head = event.target.closest('.ghead[draggable="true"]');
  if (!head) return;
  dragging = head.dataset.group;
  event.dataTransfer.effectAllowed = 'move';
  // Firefox fires no further drag events unless data is set
  event.dataTransfer.setData('text/plain', dragging);
  // The header is what you grab, but the drag image is the whole group so it looks
  // like the block is moving
  event.dataTransfer.setDragImage(head.closest('.group'), 24, 16);
  head.closest('.group').classList.add('dragging');
});

canvas.addEventListener('dragover', (event) => {
  if (!dragging) return;
  const head = event.target.closest('.ghead[draggable="true"]');
  if (!head || head.dataset.group === dragging) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = 'move';
  clearDropMarks();
  head.classList.add(movingDown(dragging, head.dataset.group) ? 'drop-after' : 'drop-before');
});

canvas.addEventListener('dragleave', (event) => {
  const head = event.target.closest('.ghead');
  if (head) head.classList.remove('drop-before', 'drop-after');
});

canvas.addEventListener('drop', async (event) => {
  const head = event.target.closest('.ghead[draggable="true"]');
  if (!dragging || !head || head.dataset.group === dragging) return;
  event.preventDefault();

  const moved = dragging;
  const before = state.groups.slice();
  const next = reorder(state.groups, moved, head.dataset.group);

  state.groups = next;
  endDrag();
  render();

  try {
    const res = await api('PUT', '/api/groups', { groups: next });
    state.groups = res.groups;
  } catch (err) {
    state.groups = before;
    render();
    showToast(`Could not reorder: ${err.message}`, { bad: true });
  }
});

canvas.addEventListener('dragend', endDrag);

/** Dropping below the target when dragging down, above it when dragging up — the same
 *  test that decides where the marker line is drawn. */
function movingDown(moved, target) {
  return state.groups.indexOf(target) > state.groups.indexOf(moved);
}

function reorder(list, moved, target) {
  const down = movingDown(moved, target);
  const next = list.filter((g) => g !== moved);
  next.splice(next.indexOf(target) + (down ? 1 : 0), 0, moved);
  return next;
}

function endDrag() {
  dragging = null;
  clearDropMarks();
  const held = canvas.querySelector('.group.dragging');
  if (held) held.classList.remove('dragging');
}

function clearDropMarks() {
  for (const el of canvas.querySelectorAll('.drop-before, .drop-after')) {
    el.classList.remove('drop-before', 'drop-after');
  }
}

// ---------------------------------------------------------------- Renaming a group

function startRename(head) {
  const from = head.dataset.group;
  const label = head.querySelector('.gname');
  const input = document.createElement('input');
  input.value = from;
  input.maxLength = 64;
  input.setAttribute('aria-label', 'Group name');
  label.replaceWith(input);
  // The header is draggable; leave it on and the browser reads text selection as the
  // start of a drag, making the field impossible to edit
  head.draggable = false;
  input.focus();
  input.select();

  let done = false;
  const finish = async (commit) => {
    if (done) return;
    done = true;
    const to = input.value.trim();
    if (!commit || !to || to === from) return render();

    try {
      const res = await api('PUT', `/api/groups/${encodeURIComponent(from)}`, { name: to });
      state.groups = res.groups;
      for (const site of state.sites) {
        if (site.group === from) site.group = to;
      }
    } catch (err) {
      showToast(`Rename failed: ${err.message}`, { bad: true });
    }
    render();
  };

  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') finish(true);
    if (e.key === 'Escape') finish(false);
  });
  input.addEventListener('blur', () => finish(true));
}

// ---------------------------------------------------------------- Form

const scrim = $('scrim');
const form = $('form');
const fName = $('f-name');
const fUrl = $('f-url');
const fGroup = $('f-group');
const chips = $('chips');
const err = $('err');

function usedGroups() {
  const live = new Set(state.sites.map((s) => s.group).filter(Boolean));
  return state.groups.filter((g) => live.has(g));
}

function paintChips() {
  const current = fGroup.value.trim();
  const list = usedGroups();
  let html = list
    .map(
      (g) =>
        `<button type="button" class="chip" data-g="${esc(g)}" style="${styleOf(g)}"
          aria-pressed="${g === current}"><span class="sw"></span>${esc(g)}</button>`
    )
    .join('');
  html += `<button type="button" class="chip none" data-g="" aria-pressed="${!current}"><span class="sw"></span>Ungrouped</button>`;
  // A name that does not exist yet: say plainly that saving will create a group
  if (current && !list.includes(current)) {
    html += `<span class="chip fresh"><span class="sw"></span>New "${esc(current)}"</span>`;
  }
  chips.innerHTML = html;
}

chips.addEventListener('click', (event) => {
  const chip = event.target.closest('.chip[data-g]');
  if (!chip) return;
  fGroup.value = chip.dataset.g;
  paintChips();
});
fGroup.addEventListener('input', paintChips);

function openSheet(site) {
  editing = site || null;
  $('stitle').textContent = site ? 'Edit link' : 'Add a link';
  fName.value = site ? site.name : '';
  fUrl.value = site ? site.url : '';
  fGroup.value = site && site.group ? site.group : '';
  err.hidden = true;
  paintChips();
  scrim.hidden = false;
  fName.focus();
}

function closeSheet() {
  scrim.hidden = true;
  editing = null;
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  const payload = {
    name: fName.value.trim(),
    url: fUrl.value.trim(),
    group: fGroup.value.trim() || null
  };
  const isNew = !editing;

  let saved;
  try {
    saved = editing
      ? await api('PUT', `/api/sites/${editing.id}`, payload)
      : await api('POST', '/api/sites', payload);
  } catch (e) {
    err.textContent = e.message;
    err.hidden = false;
    return;
  }

  if (isNew) {
    state.sites.push(saved);
    // A new link starts at the front; with zero clicks it would otherwise be born at the bottom
    pinned.unshift(saved.id);
  } else {
    Object.assign(editing, saved);
  }
  if (saved.group && !state.groups.includes(saved.group)) state.groups.push(saved.group);

  closeSheet();
  render();
  showToast(isNew ? `Added "${saved.name}" — pinned to the front` : 'Saved');
});

$('add').addEventListener('click', () => openSheet(null));
$('cancel').addEventListener('click', closeSheet);
scrim.addEventListener('click', (event) => {
  if (event.target === scrim) closeSheet();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !scrim.hidden) closeSheet();
});

// ---------------------------------------------------------------- Top bar

$('v-flat').addEventListener('click', () => switchView('flat'));
$('v-group').addEventListener('click', () => switchView('group'));

function switchView(next) {
  view = next;
  $('v-flat').setAttribute('aria-pressed', String(next === 'flat'));
  $('v-group').setAttribute('aria-pressed', String(next === 'group'));
  render();
}

const search = $('q');
search.addEventListener('input', () => {
  query = search.value.trim();
  render();
});
search.addEventListener('keydown', (event) => {
  if (event.key !== 'Enter') return;
  const first = canvas.querySelector('.card');
  if (first) first.click();
});

// The Google box submits as a plain form, so search works even if this script fails;
// this only drives the clear button.
const gq = $('gq');
const gclear = $('gclear');
gq.addEventListener('input', () => { gclear.hidden = !gq.value; });
gclear.addEventListener('click', () => {
  gq.value = '';
  gclear.hidden = true;
  gq.focus();
});

// The clear button is out of the tab order (tabindex="-1") so that Tab goes straight
// from here to the portal's own search. Escape is the keyboard equivalent of clicking it.
gq.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && gq.value) {
    gq.value = '';
    gclear.hidden = true;
  }
});

// The cursor starts in the Google box, the way a search page behaves. Not on touch
// devices though: autofocus there throws up the soft keyboard over half the screen
// before anyone asked for it, and on a phone this page is opened to tap a link.
if (window.matchMedia('(hover: hover) and (pointer: fine)').matches) {
  gq.focus();
}

const isMac = /mac/i.test(navigator.platform || navigator.userAgent);
gq.title = `Search Google (${isMac ? '⌘K' : 'Ctrl+K'})`;

/**
 * What a browser's address bar does with Ctrl+Enter: a bare word becomes www.<word>.com.
 * Anything that already reads as an address is opened as it stands rather than wrapped.
 */
function asAddress(text) {
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(text)) return text;
  if (text.includes('.') || text.includes('/')) return 'https://' + text;
  return 'https://www.' + text + '.com';
}

gq.addEventListener('keydown', (event) => {
  if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey)) return;
  const typed = gq.value.trim();
  // Several words are a search phrase, not a hostname; leave those to plain Enter.
  if (!typed || /\s/.test(typed)) return;
  event.preventDefault();   // otherwise the form submits a search on top of this
  window.open(asAddress(typed), '_blank', 'noopener,noreferrer');
});

// Cmd+K / Ctrl+K jumps back here from anywhere on the page.
document.addEventListener('keydown', (event) => {
  if (!(event.metaKey || event.ctrlKey) || event.key.toLowerCase() !== 'k') return;
  // While the edit sheet is open, focus belongs to it: the box is behind the scrim.
  if (!scrim.hidden) return;
  event.preventDefault();
  gq.focus();
  gq.select();   // whatever was there is ready to be typed over
});

const menu = $('menu');
$('more').addEventListener('click', (event) => {
  event.stopPropagation();
  menu.hidden = !menu.hidden;
  $('more').setAttribute('aria-expanded', String(!menu.hidden));
});
document.addEventListener('click', () => {
  menu.hidden = true;
  $('more').setAttribute('aria-expanded', 'false');
});

menu.addEventListener('click', (event) => {
  const item = event.target.closest('[data-menu]');
  if (!item) return;
  if (item.dataset.menu === 'export') window.location.href = '/api/export';
  else $('file').click();
});

$('file').addEventListener('change', async (event) => {
  const file = event.target.files[0];
  if (!file) return;
  event.target.value = '';

  let parsed;
  try {
    parsed = JSON.parse(await file.text());
  } catch {
    return showToast('That file is not valid JSON', { bad: true });
  }

  try {
    const fresh = await api('POST', '/api/import', parsed);
    Object.assign(state, fresh);
  } catch (e) {
    return showToast(`Import failed: ${e.message}`, { bad: true });
  }

  lockOrder();
  render();
  showToast(`Imported ${state.sites.length} links`);
});

// ---------------------------------------------------------------- Boot

async function boot() {
  let data;
  try {
    data = await api('GET', '/api/state');
  } catch (e) {
    canvas.innerHTML = `<div class="empty"><b>Cannot reach the backend</b>${esc(e.message)}<br>Check the service is running, then reload.</div>`;
    stat.textContent = 'Not connected';
    return;
  }
  Object.assign(state, data);
  lockOrder();
  // Someone who created groups wants to see them. With none, the two views render the same thing.
  switchView(state.groups.length ? 'group' : 'flat');
}

boot();
