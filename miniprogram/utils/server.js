'use strict';

const calc = require('./calculator.js');
const store = require('./storage.js');
const config = require('./config.js');

const CONNECTION_KEY = 'water-reminder-server';
const UNREADABLE = '设置内容无法识别。';
let revision = 0;
let connectionRevision = 0;
let invitationRevision = 0;
let previewedInvite = null;
let joining = false;
const switched = () => ({ ok: false, stale: true, error: '账本连接已变更，请重试' });

function loadConnection() {
  let raw = {};
  try {
    raw = wx.getStorageSync(CONNECTION_KEY) || {};
  } catch (error) {
    raw = {};
  }
  const serverUrl = typeof raw.serverUrl === 'string' ? raw.serverUrl : '';
  const apiToken = typeof raw.apiToken === 'string' ? raw.apiToken : '';
  const memberName = typeof raw.memberName === 'string' ? raw.memberName : '';
  const memberId = typeof raw.memberId === 'string' ? raw.memberId : '';
  const needsConfirmation = store.needsConnectionConfirmation(raw);
  return { serverUrl, apiToken, memberName, memberId, sharedWithOwner: raw.sharedWithOwner === true,
    ledgerUrl: raw.ledgerUrl || serverUrl, loginMethod: raw.loginMethod || (raw.sharedWithOwner === true ? 'invite' : ''),
    bootstrapId: raw.bootstrapId || '', needsConfirmation,
    configured: !!(serverUrl && apiToken && !needsConfirmation) };
}

function validateConnection(serverUrl, apiToken) {
  const token = String(apiToken == null ? '' : apiToken).trim();
  const rawUrl = String(serverUrl == null ? '' : serverUrl).trim();
  if (!rawUrl || !token) return { error: '请填写服务器地址和 API 令牌' };
  if (token.length > 2000) return { error: 'API 令牌太长' };
  if (/[?#]/.test(rawUrl)) return { error: '服务器地址不要带参数' };
  const match = /^(https?):\/\/([^/]+)(\/.*)?$/i.exec(rawUrl);
  if (!match) return { error: '服务器地址需要以 http:// 或 https:// 开头' };
  const authority = match[2];
  if (authority.indexOf('@') !== -1) return { error: '不要把令牌写在地址里' };
  const hostMatch = /^([a-z0-9.-]+|\[[0-9a-f:.]+\])(?::(\d{1,5}))?$/i.exec(authority);
  if (!hostMatch || /(^\.|\.$|\.\.)/.test(hostMatch[1]) ||
      (hostMatch[2] && (+hostMatch[2] < 1 || +hostMatch[2] > 65535)) ||
      /[\s\\]/.test(rawUrl)) {
    return { error: '服务器地址格式不正确' };
  }
  let path = (match[3] || '').replace(/\/+$/, '');
  if (/\/api\/settings$/i.test(path)) path = path.replace(/\/api\/settings$/i, '');
  path = path.replace(/\/+$/, '');
  return { error: '', serverUrl: match[1].toLowerCase() + '://' + authority.toLowerCase() + path, apiToken: token };
}

function saveConnection(connection) {
  const checked = validateConnection(connection && connection.serverUrl, connection && connection.apiToken);
  if (checked.error) return checked;
  const current = loadConnection();
  const sameCredentials = current.serverUrl === checked.serverUrl && current.apiToken === checked.apiToken;
  const sameLedger = sameCredentials && (!connection.memberId || !current.memberId || connection.memberId === current.memberId);
  if (!sameLedger || !current.configured) {
    if (current.configured) store.finishBootstrap(current);
    revision++;
    connectionRevision++;
    if (previewedInvite) store.pauseBootstrap(false);
  }
  wx.setStorageSync(CONNECTION_KEY, {
    confirmed: true,
    serverUrl: checked.serverUrl,
    apiToken: checked.apiToken,
    ledgerUrl: connection.ledgerUrl || (sameLedger ? current.ledgerUrl : checked.serverUrl),
    loginMethod: connection.loginMethod || (sameLedger ? current.loginMethod : ''),
    bootstrapId: connection.bootstrapId || (sameLedger ? current.bootstrapId : ''),
    memberId: connection.memberId || (sameLedger ? current.memberId : ''),
    memberName: connection.memberName || (sameLedger ? current.memberName : ''),
    sharedWithOwner: typeof connection.sharedWithOwner === 'boolean' ? connection.sharedWithOwner :
      (sameLedger && current.sharedWithOwner)
  });
  if (!connection.bootstrapId && !sameLedger) store.endBootstrap();
  return { error: '', serverUrl: checked.serverUrl };
}

function clearConnection() {
  store.finishBootstrap(loadConnection());
  revision++;
  connectionRevision++;
  store.endBootstrap();
  try {
    if (typeof wx.removeStorageSync === 'function') wx.removeStorageSync(CONNECTION_KEY);
    else wx.setStorageSync(CONNECTION_KEY, {});
  } catch (error) {
    wx.setStorageSync(CONNECTION_KEY, {});
  }
}

function connectionLabel(serverUrl) {
  const match = /^(https?):\/\/([^/]+)/i.exec(serverUrl || '');
  const host = match ? match[2] : (serverUrl || '服务器');
  const insecure = !!match && match[1].toLowerCase() === 'http';
  const text = '已连接 ' + host;
  return insecure ? text + '。手机正式版需要 https，http 只能在开发者工具里调试' : text;
}

function whole(value) {
  if (typeof value === 'number' && Number.isInteger(value)) return value;
  const text = String(value == null ? '' : value).trim();
  if (!/^\d+$/.test(text)) return null;
  return Number(text);
}

function canonicalClock(text) {
  const value = String(text == null ? '' : text).trim().replace('：', ':');
  const match = /^(\d{1,2}):(\d{2})(?::\d{2})?$/.exec(value);
  if (!match) return '';
  const hour = Number(match[1]);
  const minute = Number(match[2]);
  if (hour > 23 || minute > 59) return '';
  return String(hour).padStart(2, '0') + ':' + match[2];
}

function minutes(text) {
  const clock = canonicalClock(text);
  if (!clock) return null;
  const parts = clock.split(':');
  return Number(parts[0]) * 60 + Number(parts[1]);
}

function fromResponse(data) {
  if (!data || typeof data !== 'object' || Array.isArray(data)) return null;
  const dailyGoalMl = whole(data.dailyGoalMl);
  const workCupMl = whole(data.workCupMl);
  const homeCupMl = whole(data.homeCupMl);
  const noDrinkBeforeEndMinutes = whole(data.noDrinkBeforeEndMinutes);
  const minReminderGapMinutes = whole(data.minReminderGapMinutes);
  if (dailyGoalMl == null || workCupMl == null || homeCupMl == null || noDrinkBeforeEndMinutes == null || minReminderGapMinutes == null) {
    return null;
  }
  if (!Array.isArray(data.activeDays) || data.activeDays.length !== 7) return null;
  const clocks = ['workStart', 'workEnd', 'lunchStart', 'lunchEnd', 'quietStart'].map(function (key) {
    return canonicalClock(data[key]);
  });
  if (clocks.some(function (value) { return !value; })) return null;
  let pauseUntil = null;
  if (data.pauseUntil != null) {
    if (typeof data.pauseUntil !== 'string' || !data.pauseUntil.trim() || data.pauseUntil.length > 40) return null;
    pauseUntil = data.pauseUntil.trim();
  }
  return {
    dailyGoalMl: dailyGoalMl,
    revision: data.revision,
    workCupMl: workCupMl,
    homeCupMl: homeCupMl,
    workStart: clocks[0],
    workEnd: clocks[1],
    lunchEnabled: !!data.lunchEnabled,
    lunchStart: clocks[2],
    lunchEnd: clocks[3],
    noDrinkBeforeEndMinutes: noDrinkBeforeEndMinutes,
    quietStart: clocks[4],
    minReminderGapMinutes: minReminderGapMinutes,
    mobilePushEnabled: !!data.mobilePushEnabled,
    pushWardConfigured: typeof data.pushWardConfigured === 'boolean' ? data.pushWardConfigured : undefined,
    phoneMode: data.phoneMode || 'Auto',
    activeDays: data.activeDays.map(Boolean),
    remindersPaused: !!data.remindersPaused,
    pauseUntil: pauseUntil
  };
}

function toLocal(settings) {
  return {
    phoneMode: settings.phoneMode,
    mobilePushEnabled: settings.mobilePushEnabled,
    dailyGoalMl: settings.dailyGoalMl,
    workCupCapacityMl: settings.workCupMl,
    homeCupCapacityMl: settings.homeCupMl,
    workStart: settings.workStart,
    workEnd: settings.workEnd,
    lunchEnabled: !!settings.lunchEnabled,
    lunchStart: settings.lunchStart,
    lunchEnd: settings.lunchEnd,
    noDrinkBeforeEndMinutes: settings.noDrinkBeforeEndMinutes,
    minReminderGapMinutes: settings.minReminderGapMinutes,
    quietStart: settings.quietStart,
    activeDays: (settings.activeDays || []).slice(),
    remindersPaused: !!settings.remindersPaused,
    pauseUntil: settings.pauseUntil && !/(?:Z|[+-]\d\d:\d\d)$/.test(settings.pauseUntil)
      ? settings.pauseUntil + '+08:00' : settings.pauseUntil || null
  };
}

function validateSettings(data) {
  const goal = whole(data.dailyGoalMl);
  const workCup = whole(data.workCupCapacityMl);
  const homeCup = whole(data.homeCupCapacityMl);
  const noDrink = whole(data.noDrinkBeforeEndMinutes);
  const gap = whole(data.minReminderGapMinutes);
  let goalError = null;
  if (goal == null || goal < 100 || goal > 10000) goalError = '每日目标需为 100–10000 ml 的整数。';
  else if (workCup == null || homeCup == null || workCup < 50 || workCup > 3000 || homeCup < 50 || homeCup > 3000) {
    goalError = '工作杯和家庭杯容量需为 50–3000 ml 的整数。';
  }

  const workStart = minutes(data.workStart);
  const workEnd = minutes(data.workEnd);
  let timeError = null;
  if (workStart == null || workEnd == null) timeError = '请输入有效时间，例如 09:00。';
  else if (workEnd <= workStart) timeError = '下班时间必须晚于上班时间。';

  const lunchStart = minutes(data.lunchStart);
  const lunchEnd = minutes(data.lunchEnd);
  let lunchError = null;
  if (lunchStart == null || lunchEnd == null) lunchError = '请输入有效的午休时间。';
  else if (data.lunchEnabled && timeError == null) {
    if (lunchEnd <= lunchStart) lunchError = '午休结束必须晚于午休开始。';
    else if (lunchStart < workStart || lunchEnd > workEnd) lunchError = '午休需在上下班时间之内。';
  }

  let gapError = gap == null || gap < 5 || gap > 180 ? '最小间隔需为 5–180 分钟。' : null;
  let noDrinkError = null;
  if (noDrink == null || noDrink < 0 || noDrink > 240) noDrinkError = '请输入 0–240 分钟。';
  else if (timeError == null && noDrink >= workEnd - workStart) noDrinkError = '不喝水时段必须短于上班时长。';

  const quiet = minutes(data.quietStart);
  let quietError = null;
  if (quiet == null) quietError = '请输入有效的免打扰开始时间。';
  else if (timeError == null && quiet <= workEnd) quietError = '免打扰开始必须晚于下班时间。';

  const days = Array.isArray(data.activeDays) && data.activeDays.length === 7 ? data.activeDays.map(Boolean) : [];
  const daysError = days.some(Boolean) ? null : '至少选择一天。';

  const update = {
    dailyGoalMl: goal,
    workCupMl: workCup,
    homeCupMl: homeCup,
    workStart: canonicalClock(data.workStart),
    workEnd: canonicalClock(data.workEnd),
    lunchEnabled: !!data.lunchEnabled,
    lunchStart: canonicalClock(data.lunchStart),
    lunchEnd: canonicalClock(data.lunchEnd),
    noDrinkBeforeEndMinutes: noDrink,
    quietStart: canonicalClock(data.quietStart),
    minReminderGapMinutes: gap,
    mobilePushEnabled: !!data.mobilePushEnabled,
    phoneMode: data.phoneMode || 'Auto',
    activeDays: days,
    preservePause: true,
    remindersPaused: !!data.serverPaused,
    pauseUntil: data.serverPauseUntil || null
  };

  if (!goalError && !timeError && !lunchError && !noDrinkError && !quietError && !gapError && !daysError) {
    const normalized = calc.normalizeSettings(toLocal(update));
    if (calc.schedule(normalized, new Date(2026, 0, 5)).length === 0) {
      noDrinkError = '去掉午休和下班前时段后没有可喝水的时间。';
    }
  }

  const error = goalError || timeError || lunchError || noDrinkError || quietError || gapError || daysError || '';
  const fieldErrors = {};
  if (goal == null || goal < 100 || goal > 10000) fieldErrors.dailyGoalMl = '每日目标需为 100–10000 ml 的整数。';
  if (workCup == null || workCup < 50 || workCup > 3000) fieldErrors.workCupCapacityMl = '工作杯容量需为 50–3000 ml 的整数。';
  if (homeCup == null || homeCup < 50 || homeCup > 3000) fieldErrors.homeCupCapacityMl = '家庭杯容量需为 50–3000 ml 的整数。';
  if (workStart == null) fieldErrors.workStart = timeError;
  if (workEnd == null || (workStart != null && timeError)) fieldErrors.workEnd = timeError;
  if (lunchStart == null || (lunchError && lunchStart < workStart)) fieldErrors.lunchStart = lunchError;
  if (lunchEnd == null || (lunchStart != null && lunchError && (lunchEnd <= lunchStart || lunchEnd > workEnd))) fieldErrors.lunchEnd = lunchError;
  if (noDrinkError) fieldErrors.noDrinkBeforeEndMinutes = noDrinkError;
  if (quietError) fieldErrors.quietStart = quietError;
  if (gapError) fieldErrors.minReminderGapMinutes = gapError;
  if (daysError) fieldErrors.activeDays = daysError;
  if (data.settingsRevision) update.expectedRevision = data.settingsRevision;
  return error ? { error, fieldErrors } : { error: '', update, fieldErrors };
}

function httpError(status, data) {
  if (status === 401 || status === 403) return '令牌不正确，或这台服务器拒绝访问';
  const message = data && typeof data.error === 'string' ? data.error.trim() : '';
  if (!message || message.length > 120) return '服务器返回 ' + status;
  return message;
}

function request(method, path, data, candidate) {
  const connection = candidate || loadConnection();
  if (!candidate && !connection.configured) return Promise.resolve({ ok: false, error: '请先填写服务器地址和 API 令牌' });
  if (typeof wx.request !== 'function') return Promise.resolve({ ok: false, error: '当前环境不能发起网络请求' });
  return new Promise(function (resolve) {
    const header = { Authorization: 'Bearer ' + connection.apiToken };
    const options = {
      url: connection.serverUrl + path,
      method: method,
      header: header,
      timeout: 15000,
      success: function (res) {
        const status = res && res.statusCode;
        if (status >= 200 && status < 300) resolve({ ok: true, data: res.data });
        else resolve({ ok: false, status, error: httpError(status || 0, res && res.data) });
      },
      fail: function () {
        resolve({ ok: false, error: '连不上服务器。手机需要能访问这个 https 域名，并在小程序后台把它配进 request 合法域名。' });
      }
    };
    if (method !== 'GET') {
      header['content-type'] = 'application/json';
      options.data = data;
    }
    wx.request(options);
  });
}

function interpret(result) {
  if (!result.ok) return result;
  const settings = fromResponse(result.data);
  if (!settings) return { ok: false, error: UNREADABLE };
  return { ok: true, settings: settings };
}

function getSettings(candidate) {
  const seen = revision;
  return request('GET', '/api/settings', undefined, candidate).then(interpret).then(function (result) {
    result.stale = seen !== revision;
    return result;
  });
}

function saveSettings(update) {
  const seen = ++revision;
  return request('PUT', '/api/settings', update).then(interpret).then(function (result) {
    if (seen !== revision) return { ok: false, error: '账本已切换，请重新读取设置' };
    if (result.ok) store.saveSettings(toLocal(result.settings));
    return result;
  });
}

function parseInvite(text) {
  const raw = String(text || '').trim();
  if (raw.startsWith('wi_') && raw.length === 67) return { inviteToken: raw, serverUrl: '' };
  const parts = raw.split('|');
  if (parts.length === 3 && parts[0] === 'waterreminder' && parts[2].startsWith('wi_')) {
    return { serverUrl: parts[1], inviteToken: parts[2] };
  }
  return null;
}
function invitationRequest(serverUrl, path, inviteToken) {
  const checked = validateConnection(serverUrl, 'invite');
  if (checked.error) return Promise.resolve({ ok: false, error: checked.error });
  return new Promise(function (resolve) {
    wx.request({
      url: checked.serverUrl + path,
      method: 'POST',
      data: { inviteToken: inviteToken },
      header: { 'content-type': 'application/json' },
      timeout: 15000,
      success: function (res) {
        if (res.statusCode >= 200 && res.statusCode < 300) resolve({ ok: true, data: res.data });
        else resolve({ ok: false, error: httpError(res.statusCode, res.data) });
      },
      fail: function () { resolve({ ok: false, error: '连不上邀请里的服务器' }); }
    });
  });
}
function previewInvite(serverUrl, inviteToken) {
  const attempt = ++invitationRevision;
  const seen = connectionRevision;
  const preview = { serverUrl, inviteToken, seen };
  previewedInvite = preview;
  store.pauseBootstrap(true);
  return invitationRequest(serverUrl, '/api/invitations/preview', inviteToken).then(result => {
    if (attempt !== invitationRevision) return switched();
    if (!result.ok || seen !== connectionRevision) cancelInvite();
    else result.previewId = attempt;
    return seen === connectionRevision ? result : switched();
  });
}
function cancelInvite(previewId) {
  if (previewId !== undefined && previewId !== invitationRevision) return;
  invitationRevision++;
  previewedInvite = null;
  store.pauseBootstrap(joining);
}
function redeemInvite(serverUrl, inviteToken, previewId) {
  if (joining) return Promise.resolve(switched());
  const preview = previewedInvite;
  if (previewId !== undefined && (!preview || previewId !== invitationRevision)) return Promise.resolve(switched());
  if (preview && (preview.seen !== connectionRevision || preview.serverUrl !== serverUrl ||
      preview.inviteToken !== inviteToken)) return Promise.resolve(switched());
  const attempt = ++invitationRevision;
  const seen = connectionRevision;
  joining = true;
  store.pauseBootstrap(true);
  return invitationRequest(serverUrl, '/api/invitations/redeem', inviteToken).then(function (result) {
    if (attempt !== invitationRevision || seen !== connectionRevision) return switched();
    if (!result.ok) return result;
    const data = result.data || {};
    const member = data.member || {};
    if (typeof member.id !== 'string' || !member.id || typeof member.name !== 'string')
      return { ok: false, error: '邀请结果无法识别' };
    const checked = validateConnection(data.serverUrl || serverUrl, data.accessCode);
    if (checked.error) return { ok: false, error: checked.error };
    const current = loadConnection();
    const nextUrl = upgradedUrl(checked.serverUrl);
    const sameMember = current.configured && current.memberId === member.id &&
      upgradedUrl(current.serverUrl) === nextUrl;
    if (sameMember) store.preserveSubscription(current);
    const saved = saveConnection({
      serverUrl: nextUrl,
      apiToken: checked.apiToken,
      memberId: member.id,
      memberName: member.name,
      ledgerUrl: sameMember ? current.ledgerUrl : nextUrl,
      loginMethod: 'invite',
      sharedWithOwner: true
    });
    return saved.error ? { ok: false, error: saved.error } : { ok: true, memberName: data.member && data.member.name };
  }).finally(() => {
    joining = false;
    if (attempt === invitationRevision) cancelInvite();
    else if (!previewedInvite) store.pauseBootstrap(false);
  });
}
let syncing = null;
function syncLedger(options) {
  const connection = loadConnection();
  if (!connection.configured) return Promise.resolve({ ok: false, skipped: true });
  store.finishBootstrap(connection);
  const full = !(options && options.operationsOnly) || !store.load().syncedAt;
  const seen = connectionRevision;
  if (syncing && syncing.seen === seen) {
    if (full && !syncing.full) {
      if (!syncing.refresh) syncing.refresh = syncing.task.then(() => active() ? syncLedger() : switched());
      return syncing.refresh;
    }
    return syncing.task;
  }
  const session = { full, seen };
  function active() {
    const current = loadConnection();
    return seen === connectionRevision && current.configured &&
      current.serverUrl === connection.serverUrl && current.apiToken === connection.apiToken;
  }
  syncing = session;
  session.task = runSession();
  return session.task;

  async function runSession() {
    try {
      return await sync();
    } finally {
      if (syncing === session) syncing = null;
    }
  }

  async function sync() {
    if (full) {
      const result = await refreshSnapshot();
      if (!result.ok) return result;
    }
    while (active()) {
      const batch = store.load().queue.slice(0, 100);
      if (!batch.length) return { ok: true };
      const sent = await request('POST', '/api/sync/operations', { operations: batch }, connection);
      if (!active()) return switched();
      if (!sent.ok) {
        store.syncFailure(sent.error, sent.status === 400 || sent.status === 409);
        return sent;
      }
      const receipt = sent.data || {};
      const ids = Array.isArray(receipt.results) ? receipt.results.map(item => item && item.id) : [];
      if (ids.length !== batch.length || !batch.every(op => ids.includes(op.id)) || !Array.isArray(receipt.days) ||
          !batch.every(op => receipt.days.some(day => day && day.date === op.date)) ||
          !receipt.status || !Number.isInteger(receipt.status.goalMl)) {
        const error = '同步回执不完整，请重试';
        store.syncFailure(error, false);
        return { ok: false, error };
      }
      store.applyRemote(receipt, ids);
    }
    return switched();
  }

  async function refreshSnapshot() {
    const result = await request('GET', '/api/sync/snapshot?days=31', undefined, connection);
    if (!active()) return { ok: false, skipped: true };
    if (!result.ok) {
      store.syncFailure(result.error, store.load().blocked);
      return result;
    }
    const data = result.data || {};
    if (!Array.isArray(data.days) || !data.status || !Number.isInteger(data.status.goalMl) ||
        (connection.memberId && data.memberId && connection.memberId !== data.memberId)) {
      const error = '无法读取账本快照';
      store.syncFailure(error, store.load().blocked);
      return { ok: false, error };
    }
    if (data.memberName) {
      const current = loadConnection();
      saveConnection({ serverUrl: current.serverUrl, apiToken: current.apiToken, memberId: data.memberId, memberName: data.memberName });
    }
    store.applyRemote({ days: data.days || [], status: data.status }, []);
    return { ok: true };
  }
}
function wechatStatus() {
  return request('GET', '/api/wechat');
}
function bindWeChat(code) {
  return request('POST', '/api/wechat/bind', { code: code });
}
function reportSubscription(body) {
  return request('POST', '/api/wechat/subscription', body);
}
function requestWeChatLogin(serverUrl, active, expectedMemberId) {
  const checked = validateConnection(serverUrl, 'login');
  if (checked.error || !/^https:\/\//.test(checked.serverUrl || ''))
    return Promise.resolve({ ok: false, error: '暂时无法连接，请稍后重试。' });
  if (typeof wx.login !== 'function') return Promise.resolve({ ok: false, status: 503, error: '无法使用微信登录。' });
  return new Promise(function (resolve) {
    wx.login({
      success: function (login) {
        if (!active()) return resolve(switched());
        if (!login || !login.code) return resolve({ ok: false, error: '微信登录失败，请重试。' });
        wx.request({
          url: checked.serverUrl + '/api/wechat/login', method: 'POST',
          header: { 'content-type': 'application/json' }, timeout: 15000,
          data: { code: login.code, shareToken: null, ...(expectedMemberId ? { expectedMemberId } : {}) },
          success: function (res) {
            const data = res.data || {};
            if (res.statusCode < 200 || res.statusCode >= 300)
              return resolve({ ok: false, status: res.statusCode, error: httpError(res.statusCode, data) });
            if (typeof data.accessCode !== 'string' || !/^wr_[0-9a-f]{64}$/.test(data.accessCode) ||
                typeof data.memberId !== 'string' || !data.memberId || typeof data.memberName !== 'string')
              return resolve({ ok: false, error: '登录结果无法识别。' });
            resolve(loginResult(checked.serverUrl, data, 'wechat'));
          },
          fail: function () { resolve({ ok: false, error: '暂时无法连接，请稍后重试。' }); }
        });
      },
      fail: function () { resolve({ ok: false, error: '微信登录失败，请重试。' }); }
    });
  });
}
function loginResult(serverUrl, data, loginMethod) {
  return { ok: true, connection: { serverUrl, apiToken: data.accessCode, loginMethod,
    memberId: data.memberId, memberName: data.memberName, sharedWithOwner: data.sharedWithOwner === true } };
}

function commitLogin(result, extra) {
  const saved = saveConnection(Object.assign({}, result.connection, extra));
  if (saved.error) return { ok: false, error: saved.error };
  store.finishBootstrap(loadConnection());
  return { ok: true };
}

let signingIn = null;
function deviceId() {
  const key = 'water-device-id';
  let id = '';
  try { id = wx.getStorageSync(key) || ''; } catch (error) { id = ''; }
  if (typeof id === 'string' && /^[0-9a-f]{64}$/.test(id)) return id;
  const bytes = new Uint8Array(32);
  if (typeof crypto !== 'undefined' && crypto.getRandomValues) crypto.getRandomValues(bytes);
  else for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  id = Array.from(bytes, function (b) { return b.toString(16).padStart(2, '0'); }).join('');
  wx.setStorageSync(key, id);
  return id;
}
function loginDevice(serverUrl, expectedMemberId) {
  const checked = validateConnection(serverUrl, 'login');
  if (checked.error || !/^https:\/\//.test(checked.serverUrl || ''))
    return Promise.resolve({ ok: false, error: '暂时无法连接，请稍后重试。' });
  return new Promise(function (resolve) {
    wx.request({
      url: checked.serverUrl + '/api/device/login', method: 'POST',
      header: { 'content-type': 'application/json' }, timeout: 15000,
      data: { deviceId: deviceId(), ...(expectedMemberId ? { expectedMemberId } : {}) },
      success: function (res) {
        const data = res.data || {};
        if (res.statusCode < 200 || res.statusCode >= 300)
          return resolve({ ok: false, status: res.statusCode, error: httpError(res.statusCode, data) });
        if (typeof data.accessCode !== 'string' || !/^wr_[0-9a-f]{64}$/.test(data.accessCode) ||
            typeof data.memberId !== 'string' || !data.memberId || typeof data.memberName !== 'string')
          return resolve({ ok: false, error: '登录结果无法识别。' });
        resolve(loginResult(checked.serverUrl, data, 'device'));
      },
      fail: function () { resolve({ ok: false, error: '暂时无法连接，请稍后重试。' }); }
    });
  });
}
let checkedRevision = -1;
function authFailure(status) {
  return status === 401 || status === 403;
}

function upgradedUrl(serverUrl) {
  const next = validateConnection(config.serverUrl, 'upgrade');
  return /^https:\/\//.test(next.serverUrl || '') && next.serverUrl !== serverUrl &&
    config.previousDefaultUrls.includes(serverUrl) ? next.serverUrl : serverUrl;
}

async function createAccount(active) {
  const existing = store.bootstrap();
  const current = validateConnection(config.serverUrl, 'login').serverUrl;
  const base = existing ? upgradedUrl(existing.serverUrl) : current;
  if (!current || !/^https:\/\//.test(current) || base !== current) {
    store.pauseBootstrap(true);
    return { ok: false, error: '暂时无法继续创建账本，待归账饮水已保留，请确认默认服务器地址后重试。' };
  }
  const method = existing ? existing.method : 'wechat';
  const attempt = store.beginBootstrap(base, method);
  let result = method === 'device' ? await loginDevice(base) : await requestWeChatLogin(base, active);
  if (!active()) return switched();
  if (method === 'wechat' && (authFailure(result.status) || result.status === 404 || result.status === 503)) {
    store.beginBootstrap(base, 'device');
    result = await loginDevice(base);
  }
  if (!active()) return switched();
  if (!result.ok) return result;
  if (result.connection.sharedWithOwner)
    return commitLogin(result, { loginMethod: 'invite' });
  return commitLogin(result, { bootstrapId: attempt.id });
}

async function checkConnection(existing, active) {
  const checked = await getSettings(existing);
  if (!active()) return switched();
  if (checked.ok) return { ok: true };
  if (!authFailure(checked.status)) return checked;
  if (existing.loginMethod === 'invite')
    return Object.assign({}, checked, { error: '邀请连接已失效，请联系家人重新领取邀请' });
  if (!existing.memberId) return checked;
  const trustedDefault = existing.serverUrl === config.serverUrl || config.previousDefaultUrls.includes(existing.serverUrl);
  const legacy = !existing.loginMethod && trustedDefault && /^wr_[0-9a-f]{64}$/.test(existing.apiToken);
  if (!legacy && existing.loginMethod !== 'device' && existing.loginMethod !== 'wechat')
    return Object.assign({}, checked, { error: '连接凭据已失效，请重新连接；如通过邀请加入，请重新领取邀请' });
  const hasDeviceId = /^[0-9a-f]{64}$/.test(wx.getStorageSync('water-device-id') || '');
  if (existing.loginMethod === 'device' && !hasDeviceId) return checked;
  const expectedMemberId = existing.memberId;
  let result = existing.loginMethod === 'device'
    ? await loginDevice(existing.serverUrl, expectedMemberId)
    : await requestWeChatLogin(existing.serverUrl, active, expectedMemberId);
  if (!active()) return switched();
  if (legacy && hasDeviceId && (!result.ok || result.connection.memberId !== expectedMemberId))
    result = await loginDevice(existing.serverUrl, expectedMemberId);
  if (!active()) return switched();
  if (!result.ok || result.connection.memberId !== expectedMemberId) return checked;
  store.preserveSubscription(existing);
  return commitLogin(result, { ledgerUrl: existing.ledgerUrl, bootstrapId: existing.bootstrapId });
}

function signIn(options) {
  if (signingIn) return signingIn;
  let existing = loadConnection();
  const previewing = previewedInvite && previewedInvite.seen === connectionRevision;
  store.pauseBootstrap(joining || !!previewing);
  if (joining || (!existing.configured && previewing))
    return Promise.resolve(switched());
  if (!existing.configured && (existing.serverUrl || existing.apiToken)) {
    return Promise.resolve({ ok: false, error: '请确认服务器连接或使用邀请加入账本' });
  }
  if (existing.configured) {
    const next = upgradedUrl(existing.serverUrl);
    if (next !== existing.serverUrl) {
      store.preserveSubscription(existing);
      saveConnection(Object.assign({}, existing, { serverUrl: next }));
      existing = loadConnection();
    }
    store.finishBootstrap(existing);
    if (checkedRevision === connectionRevision && !(options && options.force)) return Promise.resolve({ ok: true });
  }
  const seen = connectionRevision;
  const intent = invitationRevision;
  const active = () => seen === connectionRevision && intent === invitationRevision && !joining;
  const task = (existing.configured ? checkConnection(existing, active) : createAccount(active)).then(result => {
    if (result.ok) checkedRevision = connectionRevision;
    return result;
  });
  signingIn = task.finally(function () { signingIn = null; });
  return signingIn;
}
function refreshCachedSettings() {
  if (!loadConnection().configured) return Promise.resolve(false);
  return getSettings().then(function (result) {
    if (!result.ok || result.stale) return false;
    store.saveSettings(toLocal(result.settings));
    return true;
  });
}

module.exports = {
  loadConnection: loadConnection,
  validateConnection: validateConnection,
  saveConnection: saveConnection,
  clearConnection: clearConnection,
  connectionLabel: connectionLabel,
  validateSettings: validateSettings,
  fromResponse: fromResponse,
  toLocal: toLocal,
  getSettings: getSettings,
  saveSettings: saveSettings,
  refreshCachedSettings: refreshCachedSettings,
  parseInvite: parseInvite,
  previewInvite: previewInvite,
  cancelInvite: cancelInvite,
  redeemInvite: redeemInvite,
  syncLedger: syncLedger,
  wechatStatus: wechatStatus,
  bindWeChat: bindWeChat,
  reportSubscription: reportSubscription,
  signIn: signIn
};
