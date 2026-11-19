'use strict';

const calc = require('./calculator.js');

const KEY = 'water-reminder-v1';
const TIP_KEY = 'water-reminder-tip-dismissed';
function accountKey() {
  let c; try { c = wx.getStorageSync('water-reminder-server') || {}; } catch (_) { return ''; }
  if (!c.serverUrl || !c.apiToken) return '';
  let hash = 2166136261;
  for (let i = 0; i < c.apiToken.length; i++) hash = Math.imul(hash ^ c.apiToken.charCodeAt(i), 16777619);
  return 'water-member:' + c.serverUrl + ':' + (hash >>> 0).toString(16) + ':' + c.apiToken.length;
}
function storageKey() { return accountKey() || KEY; }


function formatNumber(n) {
  return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

function load() {
  let raw = {};
  try {
    raw = wx.getStorageSync(storageKey()) || {};
  } catch (error) {
    raw = {};
  }
  return {
    settings: calc.normalizeSettings(raw.settings || {}),
    records: raw.records && typeof raw.records === 'object' && !Array.isArray(raw.records) ? raw.records : {},
    queue: Array.isArray(raw.queue) ? raw.queue : [], status: raw.status || null,
    syncError: raw.syncError || '', blocked: !!raw.blocked, syncedAt: raw.syncedAt || null, imported: !!raw.imported
  };
}

function prune(records) {
  const cutoff = new Date();
  cutoff.setDate(cutoff.getDate() - 60);
  const limit = calc.dateKey(cutoff);
  Object.keys(records).forEach(function (key) {
    if (key < limit) delete records[key];
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
  if (accountKey() && state.lastReminderAt) record.lastReminderAt = state.lastReminderAt;
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
  record.events.push({
    id: newId(),
    at: now.toISOString(),
    amountMl: amount,
    source: source || 'custom'
  });
  rememberGoal(state, record, now);
  save(state);
  return { ok: true, total: calc.consumedMl(model(record)) };
}

function markReminded(now, amount) {
  if (accountKey()) {
    const state = load();
    state.lastReminderAt = now.toISOString();
    save(state);
    return amount > 0 ? addEvent(amount, 'reminder', now) : { ok: true };
  }
  const state = load();
  const record = ensure(state, now);
  record.lastReminderAt = now.toISOString();
  if (amount > 0 && rawSum(record) + amount <= calc.MAX_DAILY_TOTAL_ML) {
    record.events.push({
      id: newId(),
      at: now.toISOString(),
      amountMl: amount,
      source: 'reminder'
    });
  }
  save(state);
}

function setTotal(date, total) {
  if (accountKey()) return enqueue({ kind: 'setTotal', date: calc.dateKey(date), totalMl: total });
  const state = load();
  const record = ensure(state, date);
  const delta = calc.adjustmentFor(model(record), total);
  if (delta === 0) return { ok: true, unchanged: true };
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

function tipDismissed() {
  try { return !!wx.getStorageSync(TIP_KEY); } catch (error) { return false; }
}

function dismissTip() {
  wx.setStorageSync(TIP_KEY, 1);
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
function projected(state) {
  const view = JSON.parse(JSON.stringify(state));
  view.queue.forEach(function (op) {
    const day = view.records[op.date] || (view.records[op.date] = { date: op.date, events: [], goalKnown: false });
    if (op.kind === 'drink' || op.kind === 'import') day.events.push({ id: op.eventId || op.id, at: op.at + '+08:00', amountMl: op.amountMl, source: op.source });
    if (op.kind === 'setTotal') {
      const delta = op.totalMl - calc.consumedMl(model(day));
      if (delta) day.events.push({ id: op.id, at: op.date + 'T12:00:00+08:00', amountMl: delta, source: 'adjust' });
    }
    if (op.kind === 'delete') day.events = day.events.filter(function (event) { return event.id !== op.eventId; });
    if (op.kind === 'clear') day.events = [];
    if (op.date === shanghaiDate(new Date()) && (op.kind === 'pause' || op.kind === 'resume')) view.settings.remindersPaused = op.kind === 'pause';
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
    state.settings.pauseUntil = null; state.settings.dailyGoalMl = payload.status.goalMl;
  }
  state.syncError = ''; state.blocked = false; state.syncedAt = new Date().toISOString(); save(state);
}
function syncFailure(error, blocked) { const state = load(); state.syncError = error; state.blocked = !!blocked; save(state); }
function dropQueue() {
  const state = load();
  state.queue = [];
  state.blocked = false;
  state.syncError = '';
  save(state);
}
function syncInfo() {
  const state = load(); const count = state.queue.length;
  return { pending: count, blocked: state.blocked, error: state.syncError,
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
  Object.keys(legacy.records || {}).sort().forEach(function (date) {
    (legacy.records[date].events || []).forEach(function (event) {
      state.queue.push({ id: uuid(), kind: 'import', date: date, amountMl: Number(event.amountMl), at: shanghaiTime(new Date(event.at)), eventId: uuid(), source: event.source || 'legacy' });
    });
  });
  state.imported = true; save(state); return { ok: true };
}

module.exports = {
  accountKey, uuid, shanghaiDate, shanghaiTime, projected, applyRemote, syncFailure, syncInfo, dropQueue, legacyCount, importLegacy,
  formatNumber: formatNumber,
  load: load,
  snapshot: snapshot,
  recordOn: recordOn,
  model: model,
  addEvent: addEvent,
  markReminded: markReminded,
  setTotal: setTotal,
  deleteEvent: deleteEvent,
  clearDay: clearDay,
  pauseToday: pauseToday,
  resume: resume,
  saveSettings: saveSettings,
  tipDismissed: tipDismissed,
  dismissTip: dismissTip,
  sourceLabel: sourceLabel
};