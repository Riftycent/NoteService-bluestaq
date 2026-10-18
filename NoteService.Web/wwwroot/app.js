'use strict';

/* NoteService test UI: a dependency-free single-page app for exercising every API feature.
   Buttons are deliberately not hidden by role so you can see the server enforce permissions. */

// ---------------------------------------------------------------- helpers
const $ = (selector, root = document) => root.querySelector(selector);
const ROLES = ['Viewer', 'Editor', 'Admin', 'Owner'];
const fmt = value => (value ? new Date(value).toLocaleString() : '');

function el(tag, props, ...kids) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'value') node.value = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid === null || kid === undefined || kid === false) continue;
    node.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  }
  return node;
}

function select(options, value, props) {
  const node = el('select', props);
  for (const [optionValue, label] of options) {
    node.append(el('option', { value: optionValue, selected: optionValue === value }, label));
  }
  return node;
}

function field(label, input) {
  return el('label', { class: 'field' }, el('span', { text: label }), input);
}

function toast(message, kind = 'info') {
  const node = el('div', { class: `toast ${kind}`, role: 'status' }, message);
  $('#toasts').append(node);
  setTimeout(() => node.remove(), kind === 'error' ? 7000 : 3500);
}

function confirmAction(message) {
  return window.confirm(message);
}

// ---------------------------------------------------------------- state
const S = {
  apiBase: localStorage.getItem('apiBase') || 'http://localhost:5172',
  auth: JSON.parse(sessionStorage.getItem('auth') || 'null'),
  teams: [], team: null, folders: [], tags: [], members: [],
  tab: 'notes',
  // notes tab
  folderFilter: '', tagFilter: '', pinnedOnly: false, sort: 'updatedAt',
  notes: [], nextCursor: null, note: null, noteTab: 'edit', ifMatchMode: 'current',
  pendingInvite: sessionStorage.getItem('pendingInvite') || null
};

function saveAuth(auth) {
  S.auth = auth;
  if (auth) sessionStorage.setItem('auth', JSON.stringify(auth)); else sessionStorage.removeItem('auth');
}

// ---------------------------------------------------------------- API client with request log
class ApiError extends Error {
  constructor(status, message, problem) {
    super(message);
    this.status = status;
    this.problem = problem;
  }
}

const requestLog = [];
const SECRET_FIELDS = new Set(['password', 'newPassword', 'accessToken', 'refreshToken', 'token']);

function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, SECRET_FIELDS.has(k) ? '***' : redact(v)]));
  }
  return value;
}

function addLog(entry) {
  requestLog.unshift(entry);
  if (requestLog.length > 40) requestLog.pop();
  renderLog();
}

function renderLog() {
  $('#logCount').textContent = requestLog.length;
  const list = $('#logList');
  list.replaceChildren(...requestLog.map(e => el('details', { class: 'entry' },
    el('summary', {},
      el('span', { class: `s${String(e.status)[0]}`, text: e.status || 'ERR' }),
      el('span', { text: e.method }), el('span', { text: e.path }), el('span', { class: 'muted', text: `${e.ms} ms` })),
    e.request ? el('pre', { text: `request\n${e.request}` }) : null,
    el('pre', { text: `response\n${e.response}` }))));
}

function problemMessage(status, data) {
  if (data && typeof data === 'object') {
    if (data.errors) {
      return Object.entries(data.errors).map(([field, messages]) => `${field}: ${[].concat(messages).join(' ')}`).join(' | ');
    }
    if (data.detail) return data.detail;
    if (data.title) return data.title;
  }
  return `Request failed (${status})`;
}

let refreshing = null;

async function tryRefresh() {
  if (!S.auth?.refreshToken) return false;
  refreshing ??= (async () => {
    try {
      const { data } = await call('POST', '/auth/refresh', { body: { refreshToken: S.auth.refreshToken }, noRefresh: true, anonymous: true });
      saveAuth(data);
      return true;
    } catch {
      saveAuth(null);
      return false;
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

/** Calls the API. Resolves {data, res}; rejects with ApiError for non-2xx and network failures. */
async function call(method, path, opts = {}) {
  const headers = {};
  if (S.auth && !opts.anonymous) headers.Authorization = `Bearer ${S.auth.accessToken}`;
  if (opts.ifMatch) headers['If-Match'] = opts.ifMatch;
  let body;
  if (opts.form) {
    body = opts.form;
  } else if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(opts.body);
  }

  const started = performance.now();
  const url = `${S.apiBase.replace(/\/$/, '')}/api/v1${path}`;
  let res;
  try {
    res = await fetch(url, { method, headers, body });
  } catch {
    addLog({ method, path, status: 0, ms: Math.round(performance.now() - started), response: 'Network error: is the API running, and is this origin allowed by CORS?' });
    throw new ApiError(0, `Cannot reach the API at ${S.apiBase}. Is it running, and is http://localhost:3000 in Cors:AllowedOrigins?`);
  }

  if (res.status === 401 && !opts.noRefresh && S.auth && await tryRefresh()) {
    return call(method, path, { ...opts, noRefresh: true });
  }

  let data = null;
  if (opts.blob && res.ok) {
    data = await res.blob();
  } else if ((res.headers.get('content-type') || '').includes('json')) {
    data = await res.json().catch(() => null);
  }

  addLog({
    method, path, status: res.status, ms: Math.round(performance.now() - started),
    request: [opts.ifMatch ? `If-Match: ${opts.ifMatch}` : '', opts.body !== undefined ? JSON.stringify(redact(opts.body), null, 2) : '', opts.form ? '(multipart form)' : ''].filter(Boolean).join('\n'),
    response: `${res.headers.get('etag') ? `ETag: ${res.headers.get('etag')}\n` : ''}${opts.blob && res.ok ? `(${data.size} bytes)` : JSON.stringify(redact(data), null, 2) ?? '(no body)'}`
  });

  if (!res.ok) {
    if (res.status === 401 && !opts.anonymous && S.auth) {
      saveAuth(null);
      render();
    }
    throw new ApiError(res.status, problemMessage(res.status, data), data);
  }
  return { data, res };
}

/** Runs an async action, reporting failures as toasts. Returns undefined on failure. */
async function attempt(action, successMessage) {
  try {
    const result = await action();
    if (successMessage) toast(successMessage, 'ok');
    return result ?? true;
  } catch (e) {
    toast(`${e.status ? `${e.status}: ` : ''}${e.message}`, 'error');
    return undefined;
  }
}

// ---------------------------------------------------------------- boot / routing
async function checkHealth() {
  const pill = $('#health');
  try {
    const res = await fetch(`${S.apiBase.replace(/\/$/, '')}/health/ready`);
    pill.textContent = res.ok ? 'API ready' : `API ${res.status}`;
    pill.className = `pill ${res.ok ? 'ok' : 'warn'}`;
  } catch {
    pill.textContent = 'API unreachable';
    pill.className = 'pill bad';
  }
}

let screen = { kind: 'auth' };

function consumeEmailLinks() {
  const path = location.pathname;
  const params = new URLSearchParams(location.search);
  const invite = path.match(/^\/invites\/(.+)$/);
  if (invite) {
    S.pendingInvite = decodeURIComponent(invite[1]);
    sessionStorage.setItem('pendingInvite', S.pendingInvite);
  } else if (path === '/confirm-email') {
    screen = { kind: 'confirm', userId: params.get('userId'), token: params.get('token') };
  } else if (path === '/reset-password') {
    screen = { kind: 'reset', email: params.get('email') || '', token: params.get('token') || '' };
  } else {
    return;
  }
  history.replaceState(null, '', '/');
}

async function render() {
  const root = $('#root');
  $('#logoutBtn').hidden = !S.auth;
  $('#whoami').textContent = S.auth ? S.auth.user.email : '';
  if (!S.auth) {
    root.replaceChildren(renderAuth());
    return;
  }
  if (!S.teams.length && !S.teamsLoaded) await loadTeams();
  root.replaceChildren(renderShell());
  await renderTab();
}

// ---------------------------------------------------------------- auth screen
function renderAuth() {
  const mode = screen.kind === 'auth' ? (screen.mode || 'login') : screen.kind;
  const box = el('div', { class: 'card auth stack' });
  const setMode = m => { screen = { kind: 'auth', mode: m }; render(); };

  if (S.pendingInvite) {
    box.append(el('div', { class: 'banner' }, 'You opened an invite link. Sign in or register with the invited email address and the invite will be accepted automatically.'));
  }

  if (mode === 'confirm') {
    box.append(el('h2', { text: 'Confirm email' }));
    const status = el('p', { class: 'muted', text: 'Confirming…' });
    box.append(status, el('button', { class: 'secondary', onclick: () => setMode('login') }, 'Go to sign in'));
    call('POST', '/auth/confirm-email', { body: { userId: screen.userId, token: screen.token }, anonymous: true })
      .then(() => { status.textContent = 'Email confirmed. You can sign in now.'; })
      .catch(e => { status.textContent = `Could not confirm: ${e.message}`; });
    return box;
  }

  box.append(el('div', { class: 'tabs' },
    ['login', 'register', 'forgot', 'reset'].map(m => el('button', { class: m === mode ? 'active' : '', onclick: () => setMode(m) },
      { login: 'Sign in', register: 'Register', forgot: 'Forgot password', reset: 'Reset password' }[m]))));

  const email = el('input', { type: 'email', required: true, autocomplete: 'username', value: screen.email || '' });
  const password = el('input', { type: 'password', required: true, autocomplete: 'current-password', minlength: 10 });
  const form = el('form', { class: 'stack' });
  const submit = el('button', { type: 'submit' });
  form.append(field('Email', email));

  if (mode === 'login') {
    form.append(field('Password', password), submit);
    submit.textContent = 'Sign in';
    form.onsubmit = async ev => {
      ev.preventDefault();
      await signIn(email.value, password.value);
    };
  } else if (mode === 'register') {
    const name = el('input', { required: true, maxlength: 100 });
    password.autocomplete = 'new-password';
    form.append(field('Display name', name), field('Password (min 10 characters)', password), submit);
    submit.textContent = 'Create account';
    form.onsubmit = async ev => {
      ev.preventDefault();
      const ok = await attempt(() => call('POST', '/auth/register', { body: { email: email.value, password: password.value, displayName: name.value }, anonymous: true }), 'Account created');
      if (ok) await signIn(email.value, password.value, true);
    };
  } else if (mode === 'forgot') {
    form.append(submit);
    submit.textContent = 'Send reset link';
    form.onsubmit = async ev => {
      ev.preventDefault();
      await attempt(() => call('POST', '/auth/forgot-password', { body: { email: email.value }, anonymous: true }), 'If the account exists a reset link was queued (see the API log in Development)');
    };
  } else {
    const token = el('input', { required: true, value: screen.token || '' });
    password.autocomplete = 'new-password';
    form.append(field('Reset token', token), field('New password', password), submit);
    submit.textContent = 'Reset password';
    form.onsubmit = async ev => {
      ev.preventDefault();
      const ok = await attempt(() => call('POST', '/auth/reset-password', { body: { email: email.value, token: token.value, newPassword: password.value }, anonymous: true }), 'Password changed. Sign in with the new password.');
      if (ok) setMode('login');
    };
  }
  box.append(form);
  if (mode === 'login' || mode === 'register') {
    box.append(el('p', { class: 'muted', text: 'In Development, e-mail confirmation is off, so a new account can sign in immediately.' }));
  }
  return box;
}

async function signIn(email, password, quiet) {
  const result = await attempt(() => call('POST', '/auth/login', { body: { email, password }, anonymous: true }), quiet ? '' : 'Signed in');
  if (!result) return;
  saveAuth(result.data);
  S.teams = []; S.teamsLoaded = false; S.team = null;
  if (S.pendingInvite) {
    const token = S.pendingInvite;
    S.pendingInvite = null;
    sessionStorage.removeItem('pendingInvite');
    await attempt(() => call('POST', `/invites/${encodeURIComponent(token)}/accept`, { body: {} }), 'Invite accepted');
  }
  await render();
}

async function signOut() {
  if (S.auth) {
    await attempt(() => call('POST', '/auth/logout', { body: { refreshToken: S.auth.refreshToken }, anonymous: true, noRefresh: true }));
  }
  saveAuth(null);
  S.teams = []; S.teamsLoaded = false; S.team = null; S.note = null; S.notes = [];
  screen = { kind: 'auth' };
  render();
}

// ---------------------------------------------------------------- shell: teams, folders, navigation
async function loadTeams() {
  const result = await attempt(() => call('GET', '/teams'));
  S.teamsLoaded = true;
  S.teams = result ? result.data : [];
  const remembered = localStorage.getItem('teamId');
  S.team = S.teams.find(t => t.id === (S.team?.id || remembered)) || S.teams[0] || null;
  if (S.team) await loadTeamData();
}

async function loadTeamData() {
  const base = `/teams/${S.team.id}`;
  const [folders, tags, members] = await Promise.all([
    attempt(() => call('GET', `${base}/folders`)),
    attempt(() => call('GET', `${base}/tags`)),
    attempt(() => call('GET', `${base}/members`))
  ]);
  S.folders = folders ? folders.data : [];
  S.tags = tags ? tags.data : [];
  S.members = members ? members.data : [];
}

async function selectTeam(id) {
  S.team = S.teams.find(t => t.id === id) || null;
  if (S.team) localStorage.setItem('teamId', S.team.id);
  S.note = null; S.notes = []; S.folderFilter = ''; S.tagFilter = '';
  if (S.team) await loadTeamData();
  render();
}

function folderOptions(withAll, allLabel = 'All folders') {
  const byParent = new Map();
  for (const f of S.folders) {
    const key = f.parentId || '';
    byParent.set(key, [...(byParent.get(key) || []), f]);
  }
  const out = withAll ? [['', allLabel]] : [['', '(top level)']];
  const walk = (parent, depth) => {
    for (const f of byParent.get(parent) || []) {
      out.push([f.id, `${'— '.repeat(depth)}${f.name}`]);
      walk(f.id, depth + 1);
    }
  };
  walk('', 0);
  return out;
}

const TABS = [['notes', 'Notes'], ['search', 'Search'], ['trash', 'Trash'], ['members', 'Members & invites'], ['organize', 'Folders & tags'], ['team', 'Team']];

function renderShell() {
  const teamSelect = select(S.teams.map(t => [t.id, `${t.name} (${t.role})`]), S.team?.id, { 'aria-label': 'Team', onchange: ev => selectTeam(ev.target.value) });
  const newTeamName = el('input', { placeholder: 'New team name', maxlength: 100, 'aria-label': 'New team name' });
  const createTeam = async () => {
    const result = await attempt(() => call('POST', '/teams', { body: { name: newTeamName.value } }), 'Team created');
    if (!result) return;
    S.teams.push(result.data);
    localStorage.setItem('teamId', result.data.id);
    S.team = result.data;
    await loadTeamData();
    render();
  };

  const accept = el('input', { placeholder: 'Invite token or link', 'aria-label': 'Invite token' });
  const side = el('aside', { class: 'side' },
    el('div', { class: 'card stack' },
      el('h3', { text: 'Team' }),
      S.teams.length ? teamSelect : el('p', { class: 'muted', text: 'No teams yet. Create one below, or accept an invite.' }),
      el('div', { class: 'row' }, newTeamName, el('button', { class: 'small', onclick: createTeam }, 'Create'))),
    el('div', { class: 'card stack' },
      el('h3', { text: 'Accept invite' }),
      accept,
      el('button', {
        class: 'small secondary',
        onclick: async () => {
          const token = accept.value.trim().split('/invites/').pop();
          if (!token) return;
          if (await attempt(() => call('POST', `/invites/${encodeURIComponent(token)}/accept`, { body: {} }), 'Invite accepted')) {
            S.teamsLoaded = false; S.teams = [];
            render();
          }
        }
      }, 'Accept')));

  const content = el('section', { class: 'stack' });
  if (!S.team) {
    content.append(el('div', { class: 'card empty', text: 'Create or join a team to start.' }));
  } else {
    content.append(el('div', { class: 'tabs' }, TABS.map(([id, label]) =>
      el('button', { class: id === S.tab ? 'active' : '', onclick: () => { S.tab = id; render(); } }, label))),
    el('div', { id: 'tabBody' }));
  }
  return el('div', { class: 'shell' }, side, content);
}

async function renderTab() {
  const body = $('#tabBody');
  if (!body || !S.team) return;
  const views = { notes: notesTab, search: searchTab, trash: trashTab, members: membersTab, organize: organizeTab, team: teamTab };
  await views[S.tab](body);
}

// ---------------------------------------------------------------- notes tab
const NOTES = () => `/teams/${S.team.id}/notes`;

async function notesTab(body) {
  const list = el('ul', { class: 'list', 'aria-label': 'Notes' });
  const more = el('button', { class: 'secondary small', hidden: true }, 'Load more');
  const editor = el('div', { id: 'editor', class: 'card' });

  const reload = () => loadNotes(list, more, true);
  const filters = el('div', { class: 'stack' },
    el('div', { class: 'row' },
      el('button', { onclick: () => { S.note = { isNew: true }; S.noteTab = 'edit'; renderEditor(); highlight(list); } }, 'New note'),
      el('button', { class: 'secondary', onclick: reload }, 'Refresh')),
    el('div', { class: 'row' },
      select(folderOptions(true), S.folderFilter, { 'aria-label': 'Folder filter', onchange: ev => { S.folderFilter = ev.target.value; reload(); } }),
      select([['', 'All tags'], ...S.tags.map(t => [t.name, t.name])], S.tagFilter, { 'aria-label': 'Tag filter', onchange: ev => { S.tagFilter = ev.target.value; reload(); } })),
    el('div', { class: 'row' },
      select([['updatedAt', 'Newest first'], ['title', 'Title A–Z']], S.sort, { 'aria-label': 'Sort', onchange: ev => { S.sort = ev.target.value; reload(); } }),
      el('label', { class: 'check' }, el('input', { type: 'checkbox', checked: S.pinnedOnly, onchange: ev => { S.pinnedOnly = ev.target.checked; reload(); } }), 'Pinned only')));

  more.onclick = () => loadNotes(list, more, false);
  body.replaceChildren(el('div', { class: 'split' }, el('div', { class: 'stack' }, filters, list, more), editor));
  await reload();
  renderEditor();
}

function highlight(list) {
  for (const li of list.children) li.classList.toggle('active', li.dataset.id === S.note?.id);
}

async function loadNotes(list, more, reset) {
  const params = new URLSearchParams({ limit: '15', sort: S.sort });
  if (S.folderFilter) params.set('folderId', S.folderFilter);
  if (S.tagFilter) params.set('tag', S.tagFilter);
  if (S.pinnedOnly) params.set('pinned', 'true');
  if (!reset && S.nextCursor) params.set('cursor', S.nextCursor);
  const result = await attempt(() => call('GET', `${NOTES()}?${params}`));
  if (!result) return;
  S.notes = reset ? result.data.items : [...S.notes, ...result.data.items];
  S.nextCursor = result.data.nextCursor;
  more.hidden = !S.nextCursor;
  list.replaceChildren(...(S.notes.length ? S.notes.map(n => noteRow(n, list)) : [el('li', { class: 'empty', text: 'No notes match.' })]));
  highlight(list);
}

function noteRow(n, list) {
  return el('li', { 'data-id': n.id, onclick: () => openNote(n.id, list) },
    el('div', { class: 'title' }, n.isPinned ? '📌 ' : '', n.title),
    el('div', { class: 'excerpt', text: n.excerpt || '(empty)' }),
    el('div', { class: 'row' }, n.tags.map(t => el('span', { class: 'chip', text: t })), el('span', { class: 'muted', text: fmt(n.updatedAt) })));
}

async function openNote(id, list) {
  const result = await attempt(() => call('GET', `${NOTES()}/${id}`));
  if (!result) return;
  S.note = result.data;
  if (list) highlight(list);
  renderEditor();
}

function memberName(userId) {
  const m = S.members.find(x => x.userId === userId);
  return m ? m.displayName : userId.slice(0, 8);
}

function renderEditor() {
  const box = $('#editor');
  if (!box) return;
  const note = S.note;
  if (!note) {
    box.replaceChildren(el('div', { class: 'empty', text: 'Select a note or create a new one.' }));
    return;
  }
  if (note.isNew) {
    box.replaceChildren(el('h2', { text: 'New note' }), noteForm(note));
    return;
  }

  const tabs = [['edit', 'Edit'], ['history', 'History'], ['sharing', 'Sharing'], ['files', 'Attachments']];
  const inner = el('div', { class: 'stack' });
  box.replaceChildren(
    el('div', { class: 'row' }, el('h2', { class: 'grow', text: note.title }), el('span', { class: `pill ${note.access === 'Edit' ? 'ok' : 'warn'}`, text: `${note.access} access` })),
    el('div', { class: 'muted', text: `ETag ${note.eTag} · updated ${fmt(note.updatedAt)} by ${memberName(note.updatedBy)}` }),
    el('div', { class: 'tabs' }, tabs.map(([id, label]) => el('button', { class: id === S.noteTab ? 'active' : '', onclick: () => { S.noteTab = id; renderEditor(); } }, label))),
    inner);
  ({ edit: () => inner.append(noteForm(note), tagEditor(note)), history: () => historyPanel(inner, note), sharing: () => sharingPanel(inner, note), files: () => filesPanel(inner, note) })[S.noteTab]();
}

function ifMatchFor(note) {
  if (S.ifMatchMode === 'omit') return undefined;
  if (S.ifMatchMode === 'stale') return `"${crypto.randomUUID().replaceAll('-', '')}"`;
  return note.eTag;
}

function noteForm(note) {
  const title = el('input', { value: note.title || '', maxlength: 300, required: true });
  const content = el('textarea', { rows: 14, maxlength: 1000000 });
  content.value = note.content || '';
  const folder = select(folderOptions(false), note.folderId || '');
  const pinned = el('input', { type: 'checkbox', checked: !!note.isPinned });
  const banner = el('div', { hidden: true });

  const apply = async result => {
    S.note = result.data;
    const index = S.notes.findIndex(n => n.id === S.note.id);
    if (index >= 0) S.notes[index] = { ...S.notes[index], ...S.note, excerpt: S.note.content.slice(0, 200).replaceAll('\n', ' '), tags: S.note.tags.map(t => t.name) };
    await refreshNotesList();
    renderEditor();
  };

  const save = async () => {
    banner.hidden = true;
    try {
      if (note.isNew) {
        const created = await call('POST', NOTES(), { body: { title: title.value, content: content.value, folderId: folder.value || null, isPinned: pinned.checked } });
        toast('Note created', 'ok');
        S.notes.unshift({ ...created.data, excerpt: '', tags: [] });
        return apply(created);
      }
      const body = { title: title.value, content: content.value, isPinned: pinned.checked };
      if (!folder.value && note.folderId) body.moveToRoot = true;
      else if (folder.value && folder.value !== note.folderId) body.folderId = folder.value;
      const updated = await call('PATCH', `${NOTES()}/${note.id}`, { body, ifMatch: ifMatchFor(note) });
      toast('Saved', 'ok');
      return apply(updated);
    } catch (e) {
      if (e.status === 412 || e.status === 428) {
        const reload = el('button', { class: 'small', onclick: async () => { await openNote(note.id); } }, 'Load latest version');
        banner.className = 'banner';
        banner.replaceChildren(el('strong', { text: `${e.status} ${e.status === 412 ? 'Precondition failed' : 'Precondition required'}: ` }), e.message, ' ',
          e.problem?.currentETag ? el('span', { class: 'muted', text: `(server ETag ${e.problem.currentETag}) ` }) : '', reload);
        banner.hidden = false;
      } else {
        toast(`${e.status ? `${e.status}: ` : ''}${e.message}`, 'error');
      }
      return undefined;
    }
  };

  const remove = async () => {
    if (!confirmAction('Move this note to the trash?')) return;
    if (await attempt(() => call('DELETE', `${NOTES()}/${note.id}`, { ifMatch: ifMatchFor(note) }), 'Moved to trash')) {
      S.notes = S.notes.filter(n => n.id !== note.id);
      S.note = null;
      await refreshNotesList();
      renderEditor();
    }
  };

  const ifMatch = select([['current', 'If-Match: current ETag'], ['stale', 'If-Match: stale ETag (expect 412)'], ['omit', 'No If-Match (expect 428)']], S.ifMatchMode,
    { 'aria-label': 'If-Match behaviour', onchange: ev => { S.ifMatchMode = ev.target.value; } });

  return el('form', { class: 'stack', onsubmit: ev => { ev.preventDefault(); save(); } },
    banner,
    field('Title', title),
    field('Content (Markdown)', content),
    el('div', { class: 'row' }, el('label', { class: 'field grow' }, el('span', { text: 'Folder' }), folder), el('label', { class: 'check' }, pinned, 'Pinned')),
    el('div', { class: 'row' },
      el('button', { type: 'submit' }, note.isNew ? 'Create note' : 'Save changes'),
      note.isNew ? null : el('button', { type: 'button', class: 'danger', onclick: remove }, 'Delete'),
      note.isNew ? null : ifMatch));
}

async function refreshNotesList() {
  const list = $('ul.list[aria-label="Notes"]');
  if (!list) return;
  list.replaceChildren(...(S.notes.length ? S.notes.map(n => noteRow(n, list)) : [el('li', { class: 'empty', text: 'No notes match.' })]));
  highlight(list);
}

function tagEditor(note) {
  const available = S.tags.filter(t => !note.tags.some(x => x.id === t.id));
  const picker = select([['', 'Add tag…'], ...available.map(t => [t.id, t.name])], '');
  const change = async (method, tagId, message) => {
    const result = await attempt(() => call(method, `${NOTES()}/${note.id}/tags/${tagId}`, method === 'PUT' ? { body: {} } : {}), message);
    if (result) {
      S.note = { ...S.note, tags: result.data };
      const index = S.notes.findIndex(n => n.id === note.id);
      if (index >= 0) S.notes[index].tags = result.data.map(t => t.name);
      await refreshNotesList();
      renderEditor();
    }
  };
  picker.onchange = () => picker.value && change('PUT', picker.value, 'Tag added');
  return el('div', { class: 'stack' }, el('h4', { text: 'Tags' }),
    el('div', { class: 'chips' }, note.tags.length ? note.tags.map(t => el('span', { class: 'chip' }, t.name, el('button', { type: 'button', 'aria-label': `Remove ${t.name}`, onclick: () => change('DELETE', t.id, 'Tag removed') }, '×'))) : el('span', { class: 'muted', text: 'No tags' })),
    picker);
}

async function historyPanel(box, note) {
  const result = await attempt(() => call('GET', `${NOTES()}/${note.id}/versions?limit=50`));
  if (!result) return;
  const preview = el('pre', { hidden: true });
  const rows = result.data.map(v => el('tr', {},
    el('td', { text: `v${v.versionNo}` }), el('td', { text: v.title }), el('td', { text: memberName(v.editedBy) }), el('td', { text: fmt(v.createdAt) }),
    el('td', {},
      el('button', {
        class: 'small secondary',
        onclick: async () => {
          const full = await attempt(() => call('GET', `${NOTES()}/${note.id}/versions/${v.versionNo}`));
          if (full) { preview.hidden = false; preview.textContent = `${full.data.title}\n\n${full.data.content}`; }
        }
      }, 'View'), ' ',
      el('button', {
        class: 'small',
        onclick: async () => {
          const restored = await attempt(() => call('POST', `${NOTES()}/${note.id}/versions/${v.versionNo}/restore`, { body: {}, ifMatch: ifMatchFor(note) }), `Restored v${v.versionNo}`);
          if (restored) { S.note = restored.data; S.noteTab = 'edit'; renderEditor(); }
        }
      }, 'Restore'))));
  box.replaceChildren(
    el('p', { class: 'muted', text: 'A snapshot is taken on create, when someone else edits, or when the same editor saves again after the throttle interval (5 minutes by default). Restoring adds a new entry.' }),
    rows.length ? el('table', {}, el('thead', {}, el('tr', {}, ['Version', 'Title', 'Editor', 'When', ''].map(h => el('th', { text: h })))), el('tbody', {}, rows)) : el('div', { class: 'empty', text: 'No history yet.' }),
    preview);
}

async function sharingPanel(box, note) {
  const result = await attempt(() => call('GET', `${NOTES()}/${note.id}/shares`));
  const shares = result ? result.data : [];
  const people = S.members.filter(m => m.userId !== S.auth.user.id);
  const person = select(people.map(m => [m.userId, `${m.displayName} (${m.role})`]), people[0]?.userId);
  const permission = select([['Read', 'Read'], ['Edit', 'Edit']], 'Edit');
  const set = async (userId, perm) => {
    if (await attempt(() => call('PUT', `${NOTES()}/${note.id}/shares/${userId}`, { body: { permission: perm } }), 'Share saved')) sharingPanel(box, note);
  };
  box.replaceChildren(
    el('p', { class: 'muted', text: 'A share overrides a member’s team role for this note only: Edit lets a viewer edit, Read restricts an editor. Only the author or a team admin can manage shares.' }),
    shares.length ? el('table', {}, el('tbody', {}, shares.map(s => el('tr', {}, el('td', { text: s.displayName }), el('td', { text: s.email }),
      el('td', {}, select([['Read', 'Read'], ['Edit', 'Edit']], s.permission, { onchange: ev => set(s.userId, ev.target.value) })),
      el('td', {}, el('button', {
        class: 'small danger',
        onclick: async () => { if (await attempt(() => call('DELETE', `${NOTES()}/${note.id}/shares/${s.userId}`), 'Share removed')) sharingPanel(box, note); }
      }, 'Remove')))))) : el('div', { class: 'empty', text: 'Not shared with anyone individually.' }),
    el('div', { class: 'row' }, person, permission, el('button', { onclick: () => person.value && set(person.value, permission.value) }, 'Share')));
}

async function filesPanel(box, note) {
  const result = await attempt(() => call('GET', `${NOTES()}/${note.id}/attachments`));
  const files = result ? result.data : [];
  const input = el('input', { type: 'file' });
  const refresh = () => filesPanel(box, note);
  box.replaceChildren(
    el('p', { class: 'muted', text: 'Allowed: png, jpeg, gif, webp, pdf, plain text, markdown, csv — up to 10 MB.' }),
    files.length ? el('table', {}, el('thead', {}, el('tr', {}, ['File', 'Type', 'Size', 'Uploaded', ''].map(h => el('th', { text: h })))), el('tbody', {}, files.map(f => el('tr', {},
      el('td', { text: f.fileName }), el('td', { text: f.contentType }), el('td', { text: `${(f.sizeBytes / 1024).toFixed(1)} KB` }), el('td', { text: fmt(f.uploadedAt) }),
      el('td', {},
        el('button', {
          class: 'small secondary',
          onclick: async () => {
            const blob = await attempt(() => call('GET', `/teams/${S.team.id}/attachments/${f.id}`, { blob: true }));
            if (!blob) return;
            const link = el('a', { href: URL.createObjectURL(blob.data), download: f.fileName });
            link.click();
            setTimeout(() => URL.revokeObjectURL(link.href), 2000);
          }
        }, 'Download'), ' ',
        el('button', { class: 'small danger', onclick: async () => { if (await attempt(() => call('DELETE', `/teams/${S.team.id}/attachments/${f.id}`), 'Attachment deleted')) refresh(); } }, 'Delete')))))) : el('div', { class: 'empty', text: 'No attachments.' }),
    el('div', { class: 'row' }, input, el('button', {
      onclick: async () => {
        if (!input.files[0]) return toast('Choose a file first', 'error');
        const form = new FormData();
        form.append('file', input.files[0]);
        if (await attempt(() => call('POST', `${NOTES()}/${note.id}/attachments`, { form }), 'Uploaded')) refresh();
      }
    }, 'Upload')));
}

// ---------------------------------------------------------------- search, trash
function searchTab(body) {
  const q = el('input', { placeholder: 'Search titles and content (2+ characters)', class: 'grow', 'aria-label': 'Search' });
  const results = el('ul', { class: 'list' });
  const run = async () => {
    const result = await attempt(() => call('GET', `/teams/${S.team.id}/search?q=${encodeURIComponent(q.value)}&limit=25`));
    if (!result) return;
    results.replaceChildren(...(result.data.length ? result.data.map(r => el('li', {
      onclick: async () => { S.tab = 'notes'; await render(); await openNote(r.noteId, $('ul.list[aria-label="Notes"]')); }
    }, el('div', { class: 'title', text: r.title }), el('div', { class: 'excerpt', text: r.snippet }), el('div', { class: 'muted', text: fmt(r.updatedAt) })) ) : [el('li', { class: 'empty', text: 'No results.' })]));
  };
  q.addEventListener('keydown', ev => { if (ev.key === 'Enter') run(); });
  body.replaceChildren(el('div', { class: 'card stack' }, el('div', { class: 'row' }, q, el('button', { onclick: run }, 'Search')), results));
}

async function trashTab(body) {
  const result = await attempt(() => call('GET', `/teams/${S.team.id}/trash`));
  const items = result ? result.data : [];
  body.replaceChildren(el('div', { class: 'card stack' },
    el('p', { class: 'muted', text: 'Trashed notes are permanently deleted after 30 days.' }),
    items.length ? el('table', {}, el('thead', {}, el('tr', {}, ['Title', 'Deleted', ''].map(h => el('th', { text: h })))), el('tbody', {}, items.map(n => el('tr', {},
      el('td', { text: n.title }), el('td', { text: fmt(n.deletedAt) }),
      el('td', {}, el('button', { class: 'small', onclick: async () => { if (await attempt(() => call('POST', `${NOTES()}/${n.id}/restore`, { body: {} }), 'Note restored')) trashTab(body); } }, 'Restore')))))) : el('div', { class: 'empty', text: 'Trash is empty.' })));
}

// ---------------------------------------------------------------- members & invites
async function membersTab(body) {
  const base = `/teams/${S.team.id}`;
  const [members, invites] = await Promise.all([attempt(() => call('GET', `${base}/members`)), attempt(() => call('GET', `${base}/invites`))]);
  S.members = members ? members.data : S.members;
  const email = el('input', { type: 'email', placeholder: 'person@example.com', class: 'grow', 'aria-label': 'Invite email' });
  const role = select(ROLES.map(r => [r, r]), 'Editor');
  const me = S.auth.user.id;

  body.replaceChildren(
    el('div', { class: 'card stack' }, el('h3', { text: 'Members' }),
      el('table', {}, el('thead', {}, el('tr', {}, ['Name', 'Email', 'Role', 'Joined', ''].map(h => el('th', { text: h })))), el('tbody', {}, S.members.map(m => el('tr', {},
        el('td', { text: m.displayName + (m.userId === me ? ' (you)' : '') }), el('td', { text: m.email }),
        el('td', {}, select(ROLES.map(r => [r, r]), m.role, {
          'aria-label': `Role of ${m.displayName}`,
          onchange: async ev => {
            if (await attempt(() => call('PATCH', `${base}/members/${m.userId}`, { body: { role: ev.target.value } }), 'Role changed')) membersTab(body); else membersTab(body);
          }
        })),
        el('td', { text: fmt(m.joinedAt) }),
        el('td', {}, el('button', {
          class: 'small danger',
          onclick: async () => {
            if (!confirmAction(m.userId === me ? 'Leave this team?' : `Remove ${m.displayName}?`)) return;
            if (await attempt(() => call('DELETE', `${base}/members/${m.userId}`), m.userId === me ? 'You left the team' : 'Member removed')) {
              if (m.userId === me) { S.teamsLoaded = false; S.teams = []; S.team = null; render(); } else membersTab(body);
            }
          }
        }, m.userId === me ? 'Leave' : 'Remove')))))),
      el('p', { class: 'muted', text: 'Only owners can assign Admin/Owner. A team always keeps at least one owner.' })),
    el('div', { class: 'card stack' }, el('h3', { text: 'Invite someone' }),
      el('div', { class: 'row' }, email, role, el('button', {
        onclick: async () => {
          if (await attempt(() => call('POST', `${base}/invites`, { body: { email: email.value, role: role.value } }), 'Invite created')) membersTab(body);
        }
      }, 'Invite')),
      el('p', { class: 'muted', text: 'The invite link is emailed. In Development the email is written to the API console (Debug level) within about a minute: copy the token from /invites/<token> and use "Accept invite" on the left while signed in as the invited address.' }),
      invites?.data?.length ? el('table', {}, el('thead', {}, el('tr', {}, ['Email', 'Role', 'Expires', ''].map(h => el('th', { text: h })))), el('tbody', {}, invites.data.map(i => el('tr', {},
        el('td', { text: i.email }), el('td', { text: i.role }), el('td', { text: fmt(i.expiresAt) }),
        el('td', {}, el('button', { class: 'small danger', onclick: async () => { if (await attempt(() => call('DELETE', `${base}/invites/${i.id}`), 'Invite revoked')) membersTab(body); } }, 'Revoke')))))) : el('div', { class: 'muted', text: 'No pending invites.' })));
}

// ---------------------------------------------------------------- folders & tags
function organizeTab(body) {
  const base = `/teams/${S.team.id}`;
  const refresh = async () => { await loadTeamData(); organizeTab(body); };

  const newName = el('input', { placeholder: 'New folder name', class: 'grow', 'aria-label': 'New folder name' });
  const newParent = select(folderOptions(false), '');
  const folderRows = S.folders.map(f => {
    const name = el('input', { value: f.name, 'aria-label': `Name of ${f.name}` });
    const parent = select(folderOptions(false).filter(([id]) => id !== f.id), f.parentId || '');
    return el('tr', {}, el('td', {}, name), el('td', {}, parent),
      el('td', {},
        el('button', {
          class: 'small',
          onclick: async () => {
            const patch = { name: name.value };
            if (!parent.value && f.parentId) patch.moveToRoot = true; else if (parent.value && parent.value !== f.parentId) patch.parentId = parent.value;
            await attempt(() => call('PATCH', `${base}/folders/${f.id}`, { body: patch }), 'Folder updated');
            refresh();
          }
        }, 'Save'), ' ',
        el('button', { class: 'small danger', onclick: async () => { if (confirmAction(`Delete "${f.name}"? Its notes move up one level.`) && await attempt(() => call('DELETE', `${base}/folders/${f.id}`), 'Folder deleted')) refresh(); } }, 'Delete')));
  });

  const tagName = el('input', { placeholder: 'New tag', class: 'grow', 'aria-label': 'New tag name' });
  body.replaceChildren(
    el('div', { class: 'card stack' }, el('h3', { text: 'Folders' }),
      S.folders.length ? el('table', {}, el('thead', {}, el('tr', {}, ['Name', 'Parent', ''].map(h => el('th', { text: h })))), el('tbody', {}, folderRows)) : el('div', { class: 'muted', text: 'No folders yet.' }),
      el('div', { class: 'row' }, newName, newParent, el('button', {
        onclick: async () => {
          if (await attempt(() => call('POST', `${base}/folders`, { body: { name: newName.value, parentId: newParent.value || null } }), 'Folder created')) refresh();
        }
      }, 'Add folder')),
      el('p', { class: 'muted', text: 'Folder management needs the Admin role.' })),
    el('div', { class: 'card stack' }, el('h3', { text: 'Tags' }),
      S.tags.length ? el('table', {}, el('thead', {}, el('tr', {}, ['Tag', 'Notes', ''].map(h => el('th', { text: h })))), el('tbody', {}, S.tags.map(t => el('tr', {},
        el('td', { text: t.name }), el('td', { text: t.noteCount }),
        el('td', {}, el('button', { class: 'small danger', onclick: async () => { if (confirmAction(`Delete tag "${t.name}" from all notes?`) && await attempt(() => call('DELETE', `${base}/tags/${t.id}`), 'Tag deleted')) refresh(); } }, 'Delete')))))) : el('div', { class: 'muted', text: 'No tags yet.' }),
      el('div', { class: 'row' }, tagName, el('button', { onclick: async () => { if (await attempt(() => call('POST', `${base}/tags`, { body: { name: tagName.value } }), 'Tag created')) refresh(); } }, 'Add tag')),
      el('p', { class: 'muted', text: 'Tags are lower-cased and unique per team. Creating needs Editor; deleting needs Admin.' })));
}

// ---------------------------------------------------------------- team settings
function teamTab(body) {
  const name = el('input', { value: S.team.name, maxlength: 100, 'aria-label': 'Team name' });
  body.replaceChildren(el('div', { class: 'card stack' },
    el('h3', { text: 'Team settings' }),
    el('p', { class: 'muted', text: `Your role: ${S.team.role} · id ${S.team.id}` }),
    el('div', { class: 'row' }, name, el('button', {
      onclick: async () => {
        const result = await attempt(() => call('PATCH', `/teams/${S.team.id}`, { body: { name: name.value } }), 'Team renamed');
        if (result) { S.teams = S.teams.map(t => (t.id === result.data.id ? result.data : t)); S.team = result.data; render(); }
      }
    }, 'Rename')),
    el('div', {}, el('button', {
      class: 'danger',
      onclick: async () => {
        if (!confirmAction(`Permanently delete "${S.team.name}" and everything in it?`)) return;
        if (await attempt(() => call('DELETE', `/teams/${S.team.id}`), 'Team deleted')) { S.teamsLoaded = false; S.teams = []; S.team = null; render(); }
      }
    }, 'Delete team (Owner only)'))));
}

// ---------------------------------------------------------------- start
function init() {
  const apiInput = $('#apiBase');
  apiInput.value = S.apiBase;
  apiInput.addEventListener('change', () => {
    S.apiBase = apiInput.value.trim() || 'http://localhost:5172';
    localStorage.setItem('apiBase', S.apiBase);
    checkHealth();
  });
  $('#logoutBtn').addEventListener('click', signOut);
  $('#clearLog').addEventListener('click', ev => { ev.preventDefault(); requestLog.length = 0; renderLog(); });
  consumeEmailLinks();
  checkHealth();
  setInterval(checkHealth, 15000);
  render();
}

init();
