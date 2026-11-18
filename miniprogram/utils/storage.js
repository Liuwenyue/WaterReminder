'use strict';

const calc = require('./calculator.js');

const KEY = 'water-reminder-v1';
const TIP_KEY = 'water-reminder-tip-dismissed';

function formatNumber(n) {
  return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

function load() {
  let raw = {};
  try {
    raw = wx.getStorageSync(KEY) || {};
  } catch (error) {
    raw = {};
  }
  return {
    settings: calc.normalizeSettings(raw.settings || {}),
    records: raw.records && typeof raw.records === 'object' && !Array.isArray(raw.records) ? raw.records : {}
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
  prune(state.records);
  wx.setStorageSync(KEY, { settings: state.settings, records: state.records });
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
  const record = ensure(state, now);
  return { state: state, record: record, model: model(record) };
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
  save(state);
  return { ok: true, total: calc.consumedMl(model(record)) };
}

function markReminded(now, amount) {
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
  const state = load();
  const record = ensure(state, date);
  const delta = calc.adjustmentFor(model(record), total);
  if (delta === 0) return { ok: true, unchanged: true };
  const when = calc.sameDay(date, new Date())
    ? new Date()
    : new Date(date.getFullYear(), date.getMonth(), date.getDate(), 12, 0, 0, 0);
  record.events.push({ id: newId(), at: when.toISOString(), amountMl: delta, source: 'adjust' });
  save(state);
  return { ok: true };
}

function deleteEvent(date, id) {
  const state = load();
  const record = ensure(state, date);
  record.events = (record.events || []).filter(function (event) { return event.id !== id; });
  save(state);
}

function clearDay(date) {
  const state = load();
  const key = calc.dateKey(date);
  state.records[key] = { date: key, events: [], lastReminderAt: null };
  save(state);
}

function pauseToday(now) {
  const state = load();
  const until = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  state.settings.remindersPaused = true;
  state.settings.pauseUntil = until.toISOString();
  save(state);
}

function resume() {
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
  state.settings.remindersPaused = paused;
  state.settings.pauseUntil = until;
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

module.exports = {
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