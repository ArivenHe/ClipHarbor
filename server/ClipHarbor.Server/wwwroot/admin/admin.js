'use strict';
const $ = id => document.getElementById(id);
const base = new URL('../', window.location.href);
const apiBase = new URL('api/v1/admin/', base);
let session = null, page = 1, total = 0, selected = null, listVersion = 0, detailVersion = 0;
const number = value => new Intl.NumberFormat('zh-CN').format(value);
const date = value => value ? new Date(value).toLocaleString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }) : '尚未连接';
function bytes(value) {
  if (value < 1024) return number(value) + ' B';
  const units = ['KiB', 'MiB', 'GiB', 'TiB']; let index = -1;
  do { value /= 1024; index++; } while (value >= 1024 && index < units.length - 1);
  return value.toLocaleString('zh-CN', { maximumFractionDigits: 1 }) + ' ' + units[index];
}
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
function notice(message, error = false) {
  $('notice').textContent = message;
  $('notice').classList.toggle('error-notice', error);
  $('notice').hidden = false;
}
function showLogin(message = '') {
  session = null; selected = null; listVersion++; detailVersion++;
  document.querySelectorAll('dialog[open]').forEach(dialog => dialog.close());
  $('console').hidden = true; $('login-screen').hidden = false; $('boot').hidden = true;
  $('login-error').textContent = message; $('login-password').value = '';
  $('user-rows').replaceChildren(); $('device-list').replaceChildren(); $('audit-list').replaceChildren();
}
async function api(path, options = {}) {
  const headers = { ...options.headers };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (session && options.method && options.method !== 'GET') headers['X-CSRF-Token'] = session.csrfToken;
  let response;
  try {
    response = await fetch(new URL(path, apiBase), { ...options, headers, credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(20000), body: options.body === undefined ? undefined : JSON.stringify(options.body) });
  } catch { throw new Error('连接失败或请求超时，请检查网络后重试。'); }
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    const message = data.message || (response.status === 429 ? '登录尝试过于频繁，请稍后重试。' : '操作失败，请重试。');
    if (response.status === 401 && path !== 'login') showLogin('会话已失效，请重新登录。');
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}
async function busy(form, operation, errorId) {
  const buttons = [...form.querySelectorAll('button[type="submit"]')];
  buttons.forEach(button => { button.disabled = true; button.setAttribute('aria-busy', 'true'); });
  if (errorId) $(errorId).textContent = '';
  try { await operation(); }
  catch (error) { if (errorId) $(errorId).textContent = error.message; else notice(error.message, true); }
  finally { buttons.forEach(button => { button.disabled = false; button.removeAttribute('aria-busy'); }); }
}
async function enter(value) {
  session = value; $('boot').hidden = true; $('login-screen').hidden = true; $('console').hidden = false;
  $('admin-name').textContent = value.username; $('admin-name').title = value.username; $('admin-avatar').textContent = value.username.slice(0, 1).toUpperCase();
  $('server-address').textContent = base.href.replace(/\/$/, '');
  $('notice').hidden = true; page = 1; switchView('users');
  await refreshUsers();
}
function userRow(user) {
  const row = el('tr'); const identity = el('div', undefined, 'user-identity');
  identity.append(el('span', user.username.slice(0, 1).toUpperCase(), 'avatar'));
  const name = el('div'); name.append(el('strong', user.username), el('small', user.isAdmin ? '管理员 · ' + number(user.deviceCount) + ' 台设备' : '普通用户 · ' + number(user.deviceCount) + ' 台设备')); identity.append(name);
  const cell = el('td'); cell.append(identity); row.append(cell);
  const status = el('td'); status.append(el('span', user.enabled ? '已启用' : '已禁用', 'badge' + (user.enabled ? '' : ' disabled'))); row.append(status);
  row.append(el('td', number(user.recordCount)));
  const storage = el('td'); const usage = el('div', undefined, 'user-usage'); usage.append(el('span', bytes(user.usedBytes)), el('small', '/ ' + bytes(user.quotaBytes)));
  const meter = el('div', undefined, 'meter'); const progress = el('span');
  // A CSSOM property is used only for a bounded numeric storage value.
  progress.style.width = Math.min(100, Math.max(0, user.usedBytes / user.quotaBytes * 100)) + '%';
  meter.append(progress); usage.append(meter); storage.append(usage); row.append(storage);
  row.append(el('td', user.lastSeen ? date(user.lastSeen) : '尚未连接', 'muted'));
  const action = el('td'); const button = el('button', '管理 →', 'manage-button'); button.setAttribute('aria-label', '管理用户 ' + user.username); button.addEventListener('click', () => openUser(user.id)); action.append(button); row.append(action);
  return row;
}
async function refreshUsers() {
  const version = ++listVersion;
  $('users-loading').hidden = false; $('user-rows').replaceChildren(); $('empty-users').hidden = true;
  $('refresh').disabled = true;
  const query = new URLSearchParams({ search: $('search').value.trim(), status: $('status-filter').value, page });
  try {
    const [overview, users] = await Promise.all([api('overview'), api('users?' + query)]);
    if (version !== listVersion || !session) return;
    total = users.total;
    if (page > 1 && users.items.length === 0 && total > 0) { page = Math.ceil(total / 25); return refreshUsers(); }
    $('stat-users').textContent = number(overview.users); $('stat-enabled').textContent = number(overview.enabledUsers); $('stat-devices').textContent = number(overview.devices); $('stat-storage').textContent = bytes(overview.usedBytes);
    $('user-total').textContent = number(total); $('user-rows').replaceChildren(...users.items.map(userRow)); $('empty-users').hidden = users.items.length > 0;
    $('page-label').textContent = total ? `${(page - 1) * 25 + 1}–${Math.min(page * 25, total)} / ${number(total)} 位用户` : '0 位用户';
    $('previous-page').disabled = page <= 1; $('next-page').disabled = page * 25 >= total;
  } catch (error) { if (version === listVersion) { notice(error.message, true); $('page-label').textContent = '加载失败，请刷新重试'; $('previous-page').disabled = true; $('next-page').disabled = true; } }
  finally { if (version === listVersion) { $('users-loading').hidden = true; $('refresh').disabled = false; } }
}
function summary(label, value) { const item = el('div', label); item.append(el('strong', value)); return item; }
async function openUser(id) {
  selected = { id }; const version = ++detailVersion;
  $('permissions-form').hidden = false; $('password-form').hidden = false;
  $('detail-summary').replaceChildren(); $('device-list').replaceChildren();
  $('detail-name').textContent = '用户详情'; $('detail-error').textContent = ''; $('reset-password').value = '';
  $('detail-content').hidden = true; $('detail-loading').hidden = false;
  if (!$('user-dialog').open) $('user-dialog').showModal();
  try {
    const [user, devices] = await Promise.all([api('users/' + id), api('users/' + id + '/devices')]);
    if (!session || version !== detailVersion || !$('user-dialog').open) return;
    selected = user; $('detail-name').textContent = user.username;
    $('detail-summary').replaceChildren(summary('同步记录', number(user.recordCount) + ' 条'), summary('存储用量', bytes(user.usedBytes) + ' / ' + bytes(user.quotaBytes)));
    $('detail-enabled').checked = user.enabled; $('detail-admin').checked = user.isAdmin;
    const self = id === session.accountId;
    $('detail-enabled').disabled = self; $('detail-admin').disabled = self; $('self-hint').hidden = !self;
    $('revoke-all').disabled = user.activeSessions === 0;
    const rows = devices.map(device => {
      const row = el('div', undefined, 'device'); const name = el('div');
      name.append(el('strong', device.name), el('small', (device.activeSessions ? '已登录' : '已退出') + ' · 最近活动 ' + date(device.lastSeen)));
      const button = el('button', '退出设备', 'secondary'); button.disabled = device.activeSessions === 0; button.setAttribute('aria-label', '退出设备 ' + device.name);
      button.addEventListener('click', async () => {
        if (!await confirmAction('退出设备', '退出「' + device.name + '」的同步会话？该设备需重新登录才能继续同步。', '退出设备')) return;
        button.disabled = true;
        try { await api(`users/${id}/devices/${device.id}/session`, { method: 'DELETE' }); notice('已退出设备 ' + device.name); await openUser(id); await refreshUsers(); }
        catch (error) { $('detail-error').textContent = error.message; button.disabled = false; }
      });
      row.append(name, button); return row;
    });
    $('device-list').replaceChildren(...(rows.length ? rows : [el('div', '尚未连接设备。用户首次登录应用后，设备会显示在这里。', 'empty')]));
    $('detail-content').hidden = false;
  } catch (error) { $('detail-content').hidden = false; $('detail-error').textContent = error.message; $('permissions-form').hidden = true; $('password-form').hidden = true; $('device-list').replaceChildren(); }
  finally { if (version === detailVersion) $('detail-loading').hidden = true; }
}
function confirmAction(title, description, action) {
  const dialog = $('confirm-dialog'); $('confirm-title').textContent = title; $('confirm-description').textContent = description; $('confirm-action').textContent = action;
  return new Promise(resolve => {
    dialog.returnValue = '';
    dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true });
    dialog.showModal();
  });
}
$('confirm-form').addEventListener('submit', event => { event.preventDefault(); $('confirm-dialog').close('confirm'); });
document.querySelectorAll('.close-dialog').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));
$('user-dialog').addEventListener('close', () => { selected = null; detailVersion++; $('reset-password').value = ''; $('permissions-form').hidden = false; $('password-form').hidden = false; });
$('create-dialog').addEventListener('close', () => { $('create-form').reset(); $('create-error').textContent = ''; });
$('login-form').addEventListener('submit', event => {
  event.preventDefault();
  busy(event.currentTarget, async () => {
    const value = await api('login', { method: 'POST', body: { username: $('login-username').value.trim(), password: $('login-password').value } });
    $('login-password').value = ''; await enter(value);
  }, 'login-error');
});
$('logout').addEventListener('click', async () => {
  $('logout').disabled = true;
  try { await api('logout', { method: 'POST' }); showLogin(); }
  catch (error) { notice(error.message, true); }
  finally { $('logout').disabled = false; }
});
$('create-user').addEventListener('click', () => { $('create-form').reset(); $('create-error').textContent = ''; $('create-dialog').showModal(); });
$('create-form').addEventListener('submit', event => {
  event.preventDefault();
  busy(event.currentTarget, async () => {
    const user = await api('users', { method: 'POST', body: { username: $('new-username').value.trim(), password: $('new-password').value, isAdmin: $('new-admin').checked } });
    $('create-dialog').close(); notice('已创建用户 ' + user.username + '。现在可以在应用中登录。'); page = 1; $('search').value = ''; $('status-filter').value = 'all'; await refreshUsers();
  }, 'create-error');
});
$('permissions-form').addEventListener('submit', event => {
  event.preventDefault(); if (!selected?.username) return;
  const user = selected; const enabled = $('detail-enabled').checked, isAdmin = $('detail-admin').checked;
  busy(event.currentTarget, async () => {
    if (!enabled && user.enabled && !await confirmAction('禁用用户', '禁用「' + user.username + '」后，该账号所有设备和后台会话将退出，且无法再次登录。已有同步数据会保留。', '禁用用户')) return;
    if (isAdmin && !user.isAdmin && !await confirmAction('授予管理员权限', '「' + user.username + '」将可以管理全部用户、密码与设备。确认授予此权限？', '授予权限')) return;
    await api('users/' + user.id, { method: 'PATCH', body: { enabled, isAdmin } }); notice('已更新 ' + user.username + ' 的权限。'); await openUser(user.id); await refreshUsers();
  }, 'detail-error');
});
$('password-form').addEventListener('submit', event => {
  event.preventDefault(); if (!selected?.username) return; const user = selected;
  busy(event.currentTarget, async () => {
    if (!await confirmAction('重置密码', '重置「' + user.username + '」的密码并退出所有会话？' + (user.id === session.accountId ? '你也将退出管理后台。' : ''), '重置密码')) return;
    await api('users/' + user.id + '/password', { method: 'POST', body: { password: $('reset-password').value } });
    $('reset-password').value = '';
    if (user.id === session.accountId) { showLogin('密码已重置，请用新密码登录。'); return; }
    notice('密码已重置，用户需用新密码重新登录。'); await openUser(user.id); await refreshUsers();
  }, 'detail-error');
});
$('revoke-all').addEventListener('click', async () => {
  if (!selected?.username) return; const user = selected;
  if (!await confirmAction('退出所有设备', '退出「' + user.username + '」的所有同步设备？设备需重新登录，本机历史会保留。', '退出所有设备')) return;
  $('revoke-all').disabled = true;
  try { await api('users/' + user.id + '/sessions', { method: 'DELETE' }); notice('已退出 ' + user.username + ' 的所有同步设备。'); await openUser(user.id); await refreshUsers(); }
  catch (error) { $('detail-error').textContent = error.message; $('revoke-all').disabled = false; }
});
let searchTimer;
$('search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => { page = 1; refreshUsers(); }, 250); });
$('status-filter').addEventListener('change', () => { page = 1; refreshUsers(); });
$('refresh').addEventListener('click', refreshUsers);
$('previous-page').addEventListener('click', () => { if (page > 1) { page--; refreshUsers(); } });
$('next-page').addEventListener('click', () => { if (page * 25 < total) { page++; refreshUsers(); } });
$('copy-address').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText(base.href.replace(/\/$/, '')); notice('已复制服务器地址。'); }
  catch { notice('无法自动复制，请选中下方服务器地址手动复制。', true); }
});
function switchView(view) {
  const users = view === 'users'; $('users-view').hidden = !users; $('audit-view').hidden = users;
  $('breadcrumb').textContent = users ? '用户与同步' : '操作记录';
  for (const name of ['users', 'audit']) { const button = $('nav-' + name); button.classList.toggle('active', name === view); if (name === view) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current'); }
  if (!users) refreshAudit();
}
const actions = { create_admin: '创建管理员', create_user: '创建用户', enable_admin: '启用账号并授予管理员权限', enable_user: '启用普通用户', disable_user: '禁用用户', reset_password: '重置密码', revoke_device: '退出设备', revoke_all_devices: '退出所有设备' };
async function refreshAudit() {
  $('refresh-audit').disabled = true; $('audit-list').replaceChildren(el('div', '正在加载操作记录…', 'loading'));
  try {
    const entries = await api('audit'); if (!session) return;
    const rows = entries.map(entry => { const row = el('div', undefined, 'audit-entry'); const body = el('div'); body.append(el('p', actions[entry.action] || entry.action), el('small', entry.actor + ' → ' + entry.target)); const time = el('time', date(entry.createdAt)); time.dateTime = entry.createdAt; row.append(body, time); return row; });
    $('audit-list').replaceChildren(...(rows.length ? rows : [el('div', '还没有管理操作记录。', 'empty')]));
  } catch (error) { $('audit-list').replaceChildren(el('div', error.message, 'empty')); }
  finally { $('refresh-audit').disabled = false; }
}
$('nav-users').addEventListener('click', () => { switchView('users'); $('main').focus(); });
$('nav-audit').addEventListener('click', () => { switchView('audit'); $('main').focus(); });
$('refresh-audit').addEventListener('click', refreshAudit);
(async () => { try { await enter(await api('me')); } catch (error) { showLogin(error.message.includes('登录') ? '' : error.message); } })();
