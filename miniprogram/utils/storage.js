'use strict';

const calc = require('./calculator.js');

const KEY = 'water-reminder-v1';
const BOOTSTRAP_KEY = 'water-reminder-bootstrap';
const CONNECTION_KEY = 'water-reminder-server';
let bootstrapPaused = false;
function pauseBootstrap(paused) { bootstrapPaused = paused; }
function legacyAccountKey(c) {
  let hash = 2166136261;
  for (let i = 0; i < c.apiToken.length; i++) hash = Math.imul(hash ^ c.apiToken.charCodeAt(i), 16777619);
  return 'water-member:' + (c.ledgerUrl || c.serverUrl) + ':' + (hash >>> 0).toString(16) + ':' + c.apiToken.length;
}
function needsConnectionConfirmation(connection) {
  return !connection.confirmed && connection.serverUrl === 'https://wednesday-holly-warcraft-beef.trycloudflare.com';
}
function accountKey() {
  const c = wx.getStorageSync(CONNECTION_KEY) || {};
  if (!c.serverUrl || !c.apiToken || needsConnectionConfirmation(c)) return '';
  const legacy = legacyAccountKey(c);
  if (!c.memberId) return legacy;
  const stable = 'water-member-id:' + (c.ledgerUrl || c.serverUrl) + ':' + c.memberId;
  const previous = wx.getStorageSync(legacy);
  if (previous) {
    const state = wx.getStorageSync(stable) || previous;
    const known = new Set((state.queue || []).map(op => op.id));
    state.queue = (state.queue || []).concat((previous.queue || []).filter(op => !known.has(op.id)));
    wx.setStorageSync(stable, state);
    wx.setStorageSync(legacy, null);
  }
  return stable;
}
function storageKey() { return accountKey() || KEY; }

function bootstrap() { return wx.getStorageSync(BOOTSTRAP_KEY) || null; }

function beginBootstrap(serverUrl, method) {
  const previous = bootstrap();
  const attempt = { id: previous ? previous.id : uuid(), serverUrl, method };
  wx.setStorageSync(BOOTSTRAP_KEY, attempt);
  return attempt;
}

function endBootstrap() { wx.setStorageSync(BOOTSTRAP_KEY, null); }

function finishBootstrap(connection) {
  const attempt = bootstrap();
  if (!attempt || attempt.id !== connection.bootstrapId || !connection.memberId || !accountKey()) return;
  const state = load();
  const local = wx.getStorageSync(KEY) || {};
  for (const day of Object.values(local.records || {})) {
    if (state.bootstrapId !== connection.bootstrapId) {
      for (const event of day.events || []) {
        if (event.bootstrapId !== connection.bootstrapId) continue;
        const at = shanghaiTime(new Date(event.at));
        state.queue.push({ id: event.id, kind: 'drink', date: at.slice(0, 10),
          amountMl: event.amountMl, at, source: event.source });
      }
    }
    day.events = (day.events || []).filter(event => event.bootstrapId !== connection.bootstrapId);
  }
  state.bootstrapId = connection.bootstrapId;
  save(state);
  wx.setStorageSync(KEY, local);
  endBootstrap();
}

function preserveSubscription(connection) {
  const target = accountKey();
  const legacy = legacyAccountKey(connection);
  const prefix = 'water-reminder-wechat-pending';
  const binding = wx.getStorageSync('water-reminder-wechat-bound');
  const old = binding && binding.token === connection.apiToken ? wx.getStorageSync(prefix) : null;
  const tokenQueue = target !== legacy ? wx.getStorageSync(prefix + legacy) : null;
  if (!Array.isArray(old) && !Array.isArray(tokenQueue)) return;
  const merged = new Map();
  for (const queue of [wx.getStorageSync(prefix + target), tokenQueue, old]) {
    if (!Array.isArray(queue)) continue;
    for (const entry of queue) {
      if (entry && entry.status === 'accept' && typeof entry.id === 'string') merged.set(entry.id, entry);
    }
  }
  wx.setStorageSync(prefix + target, [...merged.values()]);
  if (Array.isArray(tokenQueue)) wx.setStorageSync(prefix + legacy, []);
  if (Array.isArray(old)) {
    wx.setStorageSync(prefix, []);
    wx.setStorageSync('water-reminder-wechat-bound', null);
  }
}

function formatNumber(n) {
  return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

function load() {
  const raw = wx.getStorageSync(storageKey()) || {};
  return {
    settings: calc.normalizeSettings(raw.settings || {}),
    records: raw.records && typeof raw.records === 'object' && !Array.isArray(raw.records) ? raw.records : {},
    queue: Array.isArray(raw.queue) ? raw.queue : [], status: raw.status || null,
    syncError: raw.syncError || '', blocked: !!raw.blocked, syncedAt: raw.syncedAt || null, imported: !!raw.imported,
    bootstrapId: raw.bootstrapId || ''
  };
}

function prune(records) {
  const cutoff = new Date();
  cutoff.setDate(cutoff.getDate() - 60);
  const limit = calc.dateKey(cutoff);
  Object.keys(records).forEach(function (key) {
    if (key < limit) {
      records[key].events = (records[key].events || []).filter(event => event.bootstrapId);
      if (!records[key].events.length) delete records[key];
    }
  });
}

function save(state) {
  if (!accountKey()) prune(state.records);
  wx.setStorageSync(storageKey(), state);
}

function ensure(state, date) {
  const key = calc.dateKey(date);
  if (!state.records[key]) state.records[key] = { date: key, events: [], lastReminderAt: null };
  return state.records[key];
}

function model(record) {
  return {
    events: ((record && record.events) || []).map(function (event) {
      return {
        id: event.id,
        at: new Date(event.at),
        amountMl: Number(event.amountMl) || 0,
        source: event.source || 'custom'
      };
    }),
    lastReminderAt: record && record.lastReminderAt
  };
}

function clearExpiredPause(state, now) {
  if (!state.settings.remindersPaused || !state.settings.pauseUntil) return false;
  if (new Date(state.settings.pauseUntil) <= now) {
    state.settings.remindersPaused = false;
    state.settings.pauseUntil = null;
    return true;
  }
  return false;
}

function snapshot(now) {
  const state = load();
  if (clearExpiredPause(state, now)) save(state);
  const view = accountKey() ? projected(state) : state;
  const day = accountKey() ? new Date(shanghaiDate(now) + "T12:00:00") : now;
  const record = ensure(view, day);
  return { state: view, record: record, model: model(record) };
}

function recordOn(state, date) {
  const key = calc.dateKey(date);
  return state.records[key] || { date: key, events: [], lastReminderAt: null };
}

function newId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

function rawSum(record) {
  return (record.events || []).reduce(function (sum, event) {
    return sum + (Number(event.amountMl) || 0);
  }, 0);
}

function addEvent(amount, source, at) {
  const now = at || new Date();
  if (accountKey()) return enqueue({ kind: 'drink', date: shanghaiDate(now), amountMl: amount, at: shanghaiTime(now), source: source || 'custom' });
  const state = load();
  clearExpiredPause(state, now);
  const record = ensure(state, now);
  if (amount === 0) return { ok: false, error: '数量没有变化' };
  if (rawSum(record) + amount > calc.MAX_DAILY_TOTAL_ML) return { ok: false, error: '最多 20,000 ml' };
  const attempt = bootstrap();
  const connection = wx.getStorageSync(CONNECTION_KEY) || {};
  const pending = attempt && !bootstrapPaused && !connection.serverUrl && !connection.apiToken &&
    Number.isInteger(amount) && amount > 0;
  record.events.push({
    id: pending ? uuid() : newId(),
    ...(pending ? { bootstrapId: attempt.id } : {}),
    at: now.toISOString(),
    amountMl: amount,
    source: source || 'custom'
  });
  rememberGoal(state, record, now);
  save(state);
  return { ok: true, total: calc.consumedMl(model(record)) };
}

function setTotal(date, total) {
  if (accountKey()) return enqueue({ kind: 'setTotal', date: calc.dateKey(date), totalMl: total });
  const state = load();
  const record = ensure(state, date);
  const delta = calc.adjustmentFor(model(record), total);
  if (delta === 0) return { ok: true, unchanged: true };
  const attempt = bootstrap();
  if (attempt && (record.events || []).some(event => event.bootstrapId === attempt.id)) {
    return { ok: false, error: '当天有待归账饮水，请等账本建好后再校正，或删除有误的单笔记录。' };
  }
  const when = calc.sameDay(date, new Date())
    ? new Date()
    : new Date(date.getFullYear(), date.getMonth(), date.getDate(), 12, 0, 0, 0);
  record.events.push({ id: newId(), at: when.toISOString(), amountMl: delta, source: 'adjust' });
  rememberGoal(state, record, date);
  save(state);
  return { ok: true };
}

function deleteEvent(date, id) {
  if (accountKey()) return enqueue({ kind: 'delete', date: calc.dateKey(date), eventId: id });
  const state = load();
  const record = ensure(state, date);
  record.events = (record.events || []).filter(function (event) { return event.id !== id; });
  save(state);
}

function clearDay(date) {
  if (accountKey()) return enqueue({ kind: 'clear', date: calc.dateKey(date) });
  const state = load();
  const key = calc.dateKey(date);
  state.records[key] = { date: key, events: [], lastReminderAt: null };
  save(state);
}

function pauseToday(now) {
  if (accountKey()) return enqueue({ kind: 'pause', date: shanghaiDate(now) });
  const state = load();
  const until = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  state.settings.remindersPaused = true;
  state.settings.pauseUntil = until.toISOString();
  save(state);
}

function resume() {
  if (accountKey()) return enqueue({ kind: 'resume', date: shanghaiDate(new Date()) });
  const state = load();
  state.settings.remindersPaused = false;
  state.settings.pauseUntil = null;
  save(state);
}

function saveSettings(settings) {
  const state = load();
  const paused = state.settings.remindersPaused;
  const until = state.settings.pauseUntil;
  state.settings = calc.normalizeSettings(settings);
  state.settings.remindersPaused = accountKey() ? !!settings.remindersPaused : paused;
  state.settings.pauseUntil = accountKey() ? (settings.pauseUntil || null) : until;
  const todayKey = calc.dateKey(new Date());
  if (!accountKey() && state.records[todayKey]) rememberGoal(state, state.records[todayKey], new Date());
  save(state);
}

function sourceLabel(source) {
  if (source === 'cup') return '喝一杯';
  if (source === 'reminder') return '提醒记下';
  if (source === 'adjust') return '校正';
  return '自定义';
}


function rememberGoal(state, record, date) {
  const today = calc.sameDay(date, new Date());
  if (record.goalKnown === true && !today) return;
  if (record.goalKnown === false) return;
  record.goalMl = state.settings.dailyGoalMl;
  record.goalKnown = true;
}
function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
    const n = Math.floor(Math.random() * 16); return (c === 'x' ? n : (n & 3) | 8).toString(16);
  });
}
function shanghaiTime(date) { return new Date(date.getTime() + 8 * 3600000).toISOString().slice(0, 19); }
function shanghaiDate(date) { return shanghaiTime(date).slice(0, 10); }
function shanghaiClock(date) { return new Date(shanghaiTime(date)); }
function projected(state) {
  const view = JSON.parse(JSON.stringify(state));
  view.queue.forEach(function (op) {
    const day = view.records[op.date] || (view.records[op.date] = { date: op.date, events: [], goalKnown: false });
    if ((op.kind === 'drink' || op.kind === 'import') && !day.events.some(event => event.id === (op.eventId || op.id)))
      day.events.push({ id: op.eventId || op.id, at: op.at + '+08:00', amountMl: op.amountMl, source: op.source });
    if (op.kind === 'setTotal') {
      const delta = op.totalMl - rawSum(day);
      if (delta) day.events.push({ id: op.id, at: op.date + 'T12:00:00+08:00', amountMl: delta, source: 'adjust' });
    }
    if (op.kind === 'delete') day.events = day.events.filter(function (event) { return event.id !== op.eventId; });
    if (op.kind === 'clear') day.events = [];
    if (op.date === shanghaiDate(new Date()) && (op.kind === 'pause' || op.kind === 'resume')) {
      view.settings.remindersPaused = op.kind === 'pause';
      view.settings.pauseUntil = op.kind === 'pause'
        ? new Date(Date.parse(op.date + 'T00:00:00+08:00') + 86400000).toISOString() : null;
    }
  });
  return view;
}
function enqueue(operation) {
  const state = load();
  if (state.blocked) return { ok: false, error: '请先处理同步失败，再继续记水' };
  const current = projected(state).records[operation.date] || { events: [] };
  if (operation.kind === 'drink' && (!Number.isInteger(operation.amountMl) || operation.amountMl <= 0 || calc.consumedMl(model(current)) + operation.amountMl > calc.MAX_DAILY_TOTAL_ML)) return { ok: false, error: '最多 20,000 ml' };
  if (operation.kind === 'setTotal' && (!Number.isInteger(operation.totalMl) || operation.totalMl < 0 || operation.totalMl > calc.MAX_DAILY_TOTAL_ML)) return { ok: false, error: '总量需为 0–20,000 ml' };
  if (operation.kind === 'delete' && state.queue.some(function (op) { return op.id === operation.eventId || op.eventId === operation.eventId; })) return { ok: false, error: '这笔记录待同步，请同步后再删除' };
  state.queue.push(Object.assign({ id: uuid() }, operation)); state.syncError = ''; save(state);
  return { ok: true, pending: true };
}
function applyRemote(payload, receiptIds) {
  const state = load(); const done = receiptIds || [];
  state.queue = state.queue.filter(function (op) { return done.indexOf(op.id) < 0; });
  (payload.days || []).forEach(function (day) {
    state.records[day.date] = Object.assign({}, day, { events: (day.events || []).map(function (event) {
      return Object.assign({}, event, { at: /(?:Z|[+-]\d\d:\d\d)$/.test(event.at) ? event.at : event.at + '+08:00' });
    }) });
  });
  if (payload.status) {
    state.status = payload.status; state.settings.remindersPaused = !!payload.status.remindersPaused;
    state.settings.pauseUntil = payload.status.pauseUntil || null; state.settings.dailyGoalMl = payload.status.goalMl;
  }
  state.syncError = ''; state.blocked = false; state.syncedAt = new Date().toISOString(); save(state);
}
function syncFailure(error, blocked) { const state = load(); state.syncError = error; state.blocked = !!blocked; save(state); }
function dropQueue(ids) {
  const state = load();
  if (!state.blocked || state.queue.length !== ids.length || !state.queue.every((op, index) => op.id === ids[index])) return false;
  if (state.queue.some(op => op.kind === 'import')) state.imported = false;
  state.queue = [];
  state.blocked = false;
  state.syncError = '';
  save(state);
  return true;
}
function syncInfo() {
  const state = load(); const count = state.queue.length;
  return { pending: count, blocked: state.blocked, error: state.syncError, synced: !!state.syncedAt,
    text: state.syncError ? ((state.blocked ? '同步失败：' : '离线：') + state.syncError + (count ? '，待同步 ' + count + ' 笔' : '')) : (count ? '待同步 ' + count + ' 笔' : (state.syncedAt ? '已与服务器同步' : '等待首次同步')) };
}
function legacyCount() {
  if (load().imported) return 0;
  let legacy; try { legacy = wx.getStorageSync(KEY) || {}; } catch (_) { return 0; }
  return Object.values(legacy.records || {}).reduce(function (n, day) { return n + (day.events || []).length; }, 0);
}
function importLegacy() {
  const state = load();
  if (!accountKey() || !state.status || state.imported) return { ok: false, error: '请先同步成员账本' };
  if (state.blocked) return { ok: false, error: '请先处理同步失败' };
  const legacy = wx.getStorageSync(KEY) || {};
  const guid = /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
  for (const day of Object.values(legacy.records || {})) {
    for (const event of day.events || []) {
      const when = new Date(event.at);
      const amount = Number(event.amountMl);
      if (!Number.isFinite(when.getTime()) || !Number.isInteger(amount) || !amount || Math.abs(amount) > calc.MAX_DAILY_TOTAL_ML)
        return { ok: false, error: '旧记录中有无效的时间或水量，请先检查本机记录' };
      const at = shanghaiTime(when);
      event.importId = event.importId || uuid();
      event.importEventId = guid.test(event.id) ? event.id.toLowerCase() : event.importEventId || uuid();
      state.queue.push({ id: event.importId, kind: 'import', date: at.slice(0, 10), amountMl: amount,
        at, eventId: event.importEventId, source: event.source || 'legacy' });
    }
  }
  wx.setStorageSync(KEY, legacy);
  state.imported = true; save(state); return { ok: true };
}

module.exports = {
  needsConnectionConfirmation, legacyAccountKey, bootstrap, beginBootstrap, endBootstrap, finishBootstrap,
  pauseBootstrap, preserveSubscription,
  accountKey, uuid, shanghaiDate, shanghaiTime, shanghaiClock, projected, applyRemote, syncFailure, syncInfo, dropQueue, legacyCount, importLegacy,
  formatNumber: formatNumber,
  load: load,
  snapshot: snapshot,
  recordOn: recordOn,
  model: model,
  addEvent: addEvent,
  setTotal: setTotal,
  deleteEvent: deleteEvent,
  clearDay: clearDay,
  pauseToday: pauseToday,
  resume: resume,
  saveSettings: saveSettings,
  sourceLabel: sourceLabel
};
