'use strict';

const calc = require('./calculator.js');
const store = require('./storage.js');

const CONNECTION_KEY = 'water-reminder-server';
const UNREADABLE = '设置内容无法识别。';
let revision = 0;

function loadConnection() {
  let raw = {};
  try {
    raw = wx.getStorageSync(CONNECTION_KEY) || {};
  } catch (error) {
    raw = {};
  }
  const serverUrl = typeof raw.serverUrl === 'string' ? raw.serverUrl : '';
  const apiToken = typeof raw.apiToken === 'string' ? raw.apiToken : '';
  return { serverUrl: serverUrl, apiToken: apiToken, configured: !!(serverUrl && apiToken) };
}

function validateConnection(serverUrl, apiToken) {
  const token = String(apiToken == null ? '' : apiToken).trim();
  const rawUrl = String(serverUrl == null ? '' : serverUrl).trim();
  if (!rawUrl || !token) return { error: '请填写服务器地址和 API 令牌' };
  if (token.length > 2000) return { error: 'API 令牌太长' };
  let parsed;
  try {
    parsed = new URL(rawUrl);
  } catch (error) {
    return { error: '服务器地址需要以 http:// 或 https:// 开头' };
  }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
    return { error: '服务器地址需要以 http:// 或 https:// 开头' };
  }
  if (parsed.username || parsed.password) return { error: '不要把令牌写在地址里' };
  if (parsed.search || parsed.hash) return { error: '服务器地址不要带参数' };
  let path = parsed.pathname.replace(/\/+$/, '');
  if (/\/api\/settings$/i.test(path)) path = path.replace(/\/api\/settings$/i, '');
  path = path.replace(/\/+$/, '');
  return { error: '', serverUrl: parsed.origin + path, apiToken: token };
}

function saveConnection(connection) {
  const checked = validateConnection(connection && connection.serverUrl, connection && connection.apiToken);
  if (checked.error) return checked;
  wx.setStorageSync(CONNECTION_KEY, { serverUrl: checked.serverUrl, apiToken: checked.apiToken });
  return { error: '', serverUrl: checked.serverUrl };
}

function clearConnection() {
  try {
    if (typeof wx.removeStorageSync === 'function') wx.removeStorageSync(CONNECTION_KEY);
    else wx.setStorageSync(CONNECTION_KEY, {});
  } catch (error) {
    wx.setStorageSync(CONNECTION_KEY, {});
  }
}

function connectionLabel(serverUrl) {
  let host = serverUrl || '服务器';
  let insecure = false;
  try {
    const url = new URL(serverUrl);
    host = url.host;
    insecure = url.protocol === 'http:';
  } catch (error) {
    host = serverUrl || '服务器';
  }
  const text = '已连接 ' + host + '，保存后和桌面端是同一份设置';
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
    activeDays: data.activeDays.map(Boolean),
    remindersPaused: !!data.remindersPaused,
    pauseUntil: pauseUntil
  };
}

function toLocal(settings) {
  return {
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
    remindersPaused: false,
    pauseUntil: null
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
    activeDays: days,
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
  return error ? { error: error } : { error: '', update: update };
}

function httpError(status, data) {
  if (status === 401 || status === 403) return '令牌不正确，或这台服务器拒绝访问';
  const message = data && typeof data.error === 'string' ? data.error.trim() : '';
  if (!message || message.length > 120) return '服务器返回 ' + status;
  return message;
}

function request(method, path, data) {
  const connection = loadConnection();
  if (!connection.configured) return Promise.resolve({ ok: false, error: '请先填写服务器地址和 API 令牌' });
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
        else resolve({ ok: false, error: httpError(status || 0, res && res.data) });
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

function getSettings() {
  const seen = revision;
  return request('GET', '/api/settings').then(interpret).then(function (result) {
    if (result.ok) result.stale = seen !== revision;
    return result;
  });
}

function saveSettings(update) {
  revision += 1;
  return request('PUT', '/api/settings', update).then(interpret).then(function (result) {
    if (result.ok) store.saveSettings(toLocal(result.settings));
    return result;
  });
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
  toLocal: toLocal,
  getSettings: getSettings,
  saveSettings: saveSettings,
  refreshCachedSettings: refreshCachedSettings
};
