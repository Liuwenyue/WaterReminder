'use strict';

const assert = require('node:assert/strict');
const { test, beforeEach } = require('node:test');

const storage = new Map();
global.wx = {
  getStorageSync: key => (storage.has(key) ? structuredClone(storage.get(key)) : undefined),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  request(options) { options.fail({ errMsg: 'request:fail' }); }
};
const server = require('../miniprogram/utils/server.js');
const store = require('../miniprogram/utils/storage.js');

const sample = () => ({
  dailyGoalMl: 2500,
  workCupMl: 370,
  homeCupMl: 800,
  workStart: '08:30',
  workEnd: '17:30',
  lunchEnabled: true,
  lunchStart: '11:50',
  lunchEnd: '13:30',
  noDrinkBeforeEndMinutes: 30,
  quietStart: '23:00',
  minReminderGapMinutes: 20,
  mobilePushEnabled: true,
  activeDays: [true, true, true, true, true, false, false],
  remindersPaused: true,
  pauseUntil: '2026-11-19T18:00:00'
});

beforeEach(() => storage.clear());

test('connection keeps the token local and rejects addresses that would expose it', () => {
  assert.match(server.validateConnection('cup.example.com', 'token').error, /http/);
  assert.match(server.validateConnection('https://user:secret@cup.example.com', 'token').error, /令牌/);
  assert.match(server.validateConnection('https://cup.example.com/api/settings?token=secret', 'token').error, /参数/);
  const saved = server.saveConnection({ serverUrl: 'http://127.0.0.1:8787/api/settings/', apiToken: ' desktop-api-token ' });
  assert.equal(saved.error, '');
  assert.equal(saved.serverUrl, 'http://127.0.0.1:8787');
  assert.equal(server.loadConnection().apiToken, 'desktop-api-token');
  assert.match(server.connectionLabel(saved.serverUrl), /https/);
  server.clearConnection();
  assert.equal(server.loadConnection().configured, false);
});

test('validation matches the server rules and echoes pause without a secret', () => {
  const form = {
    dailyGoalMl: '1800',
    workCupCapacityMl: '370',
    homeCupCapacityMl: '800',
    workStart: '08:30',
    workEnd: '17:30',
    lunchEnabled: true,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: '30',
    minReminderGapMinutes: '20',
    quietStart: '23:00',
    mobilePushEnabled: true,
    activeDays: [true, true, true, true, true, false, false],
    serverPaused: true,
    serverPauseUntil: '2026-11-19T18:00:00'
  };
  const ok = server.validateSettings(form);
  assert.equal(ok.error, '');
  assert.equal(ok.update.workCupMl, 370);
  assert.equal(ok.update.remindersPaused, true);
  assert.equal(JSON.stringify(ok.update).includes('token'), false);
  assert.match(server.validateSettings(Object.assign({}, form, { dailyGoalMl: '50' })).error, /100–10000/);
  assert.match(server.validateSettings(Object.assign({}, form, { quietStart: '17:30' })).error, /晚于下班/);
  assert.match(server.validateSettings(Object.assign({}, form, { activeDays: [false, false, false, false, false, false, false] })).error, /至少选择一天/);
});

test('refresh caches server settings and ignores a stale response after a save', async () => {
  let releaseGet;
  const remote = sample();
  wx.request = options => {
    if ((options.method || 'GET') === 'GET') {
      releaseGet = () => options.success({ statusCode: 200, data: remote });
      return;
    }
    options.success({ statusCode: 200, data: Object.assign({}, remote, options.data, { dailyGoalMl: 1800 }) });
  };
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  const refreshing = server.refreshCachedSettings();
  const saved = await server.saveSettings(Object.assign(server.validateSettings({
    dailyGoalMl: '1800',
    workCupCapacityMl: '370',
    homeCupCapacityMl: '800',
    workStart: '08:30',
    workEnd: '17:30',
    lunchEnabled: false,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: '30',
    minReminderGapMinutes: '20',
    quietStart: '23:00',
    mobilePushEnabled: false,
    activeDays: [false, false, false, false, false, false, true],
    serverPaused: true,
    serverPauseUntil: remote.pauseUntil
  }).update));
  assert.equal(saved.ok, true);
  releaseGet();
  assert.equal(await refreshing, false);
  assert.equal(store.load().settings.dailyGoalMl, 1800);
  assert.equal(store.load().settings.lunchEnabled, false);
  assert.equal(store.load().settings.remindersPaused, false);
  assert.equal(JSON.stringify(wx.getStorageSync('water-reminder-v1')).includes('desktop-api-token'), false);
});

test('network and auth failures stay on the form and do not invent settings', async () => {
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  wx.request = options => options.fail({ errMsg: 'request:fail' });
  const offline = await server.getSettings();
  assert.equal(offline.ok, false);
  assert.match(offline.error, /连不上服务器/);
  wx.request = options => options.success({ statusCode: 401, data: {} });
  const denied = await server.getSettings();
  assert.match(denied.error, /令牌不正确/);
  assert.equal(store.load().settings.dailyGoalMl, store.load().settings.dailyGoalMl);
});
