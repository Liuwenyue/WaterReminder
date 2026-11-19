'use strict';

const assert = require('node:assert/strict');
const { test, beforeEach } = require('node:test');
const calc = require('../miniprogram/utils/calculator.js');
const store = require('../miniprogram/utils/storage.js');
const journal = require('../miniprogram/utils/journal.js');

const storage = new Map();
let app;
let definition;
let modal;
let tabVisible;
global.App = options => { app = options; };
global.getApp = () => app;
global.Page = global.Component = options => { definition = options; };
let requestHandler = options => options.fail({ errMsg: 'request:fail' });
global.wx = {
  getStorageSync: key => structuredClone(storage.get(key)),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  hideTabBar: () => { tabVisible = false; },
  showTabBar: () => { tabVisible = true; },
  showModal: options => { modal = options; },
  switchTab() {},
  showToast() {},
  request: options => requestHandler(options)
};
require('../miniprogram/app.js');

function loadDefinition(path) {
  require('../miniprogram/' + path);
  return definition;
}
const todayPage = loadDefinition('pages/today/today.js');
const recordsPage = loadDefinition('pages/records/records.js');
const settingsPage = loadDefinition('pages/settings/settings.js');
const sheet = loadDefinition('components/amount-sheet/index.js');

function mount(options, extraData) {
  return Object.assign({}, options, options.methods, {
    data: Object.assign(structuredClone(options.data), extraData),
    setData(patch, complete) {
      Object.assign(this.data, patch);
      if (complete) complete();
    }
  });
}
function control(key, value) {
  return { currentTarget: { dataset: { key } }, detail: { value } };
}
beforeEach(() => {
  storage.clear();
  requestHandler = options => options.fail({ errMsg: 'request:fail' });
  app.globalData.prompting = app.globalData.amountEditorOpen = false;
  app.globalData.openTodayRecords = false;
  tabVisible = true;
  modal = null;
});

test('one tap updates the amount, progress and recent entry; a rapid second tap is ignored', () => {
  const today = mount(todayPage);
  today.onShow();
  today.drinkCup();
  today.drinkCup();
  assert.equal(today.data.recent.length, 1);
  assert.equal(today.data.consumedText, store.formatNumber(today.data.cupAmount));
  assert.equal(today.data.fraction, today.data.cupAmount / store.load().settings.dailyGoalMl);
  today.togglePause();
  assert.equal(today.data.paused, true);
  assert.equal(today.data.hasNext, false);
  today.togglePause();
  assert.equal(today.data.paused, false);
  today.onHide();
  assert.equal(today.data.feedback, '');
});

test('custom entry validates, persists once and releases the reminder and tab bar on dismissal', () => {
  const today = mount(todayPage);
  today.onShow();
  today.customAmount();
  const editor = mount(sheet, { initialValue: '', minimum: 1, maximum: 5000 });
  today.selectComponent = () => editor;
  editor.triggerEvent = (name, detail) => {
    if (name === 'confirm') today.addCustom({ detail });
  };
  sheet.lifetimes.attached.call(editor);
  assert.equal(tabVisible, false);
  assert.equal(app.globalData.amountEditorOpen, true);
  editor.onInput({ detail: { value: '5001' } });
  editor.confirm();
  assert.ok(editor.data.error);
  store.setTotal(new Date(), 19900);
  editor.pick({ currentTarget: { dataset: { amount: 250 } } });
  editor.confirm();
  assert.match(editor.data.error, /20,000/);
  assert.equal(editor.data.closing, false);
  editor.onInput({ detail: { value: '100' } });
  editor.confirm();
  editor.confirm();
  assert.equal(editor.data.closing, true);
  assert.equal(today.data.consumedText, '20,000');
  assert.equal(today.data.recent[0].glyph, 'measure');
  sheet.lifetimes.detached.call(editor);
  today.closeAmount();
  today.onHide();
  assert.equal(tabVisible, true);
  assert.equal(app.globalData.amountEditorOpen, false);
});

test('chart selection and total corrections target the selected day, including the oldest visible day', () => {
  const now = new Date();
  const older = journal.dayAtOffset(now, 30);
  store.addEvent(370, 'cup', now);
  store.addEvent(250, 'custom', older);
  const records = mount(recordsPage);
  records.onShow();
  records.pickDay({ currentTarget: { dataset: { offset: 5 } } });
  assert.equal(records.data.bars.find(bar => bar.selected).offset, 5);
  records.selectDay(30);
  records.prev();
  assert.equal(records.data.canPrev, false);
  assert.deepEqual(records.data.bars.map(bar => bar.offset), [30, 29, 28, 27, 26, 25, 24]);
  assert.equal(records.data.total, 250);
  records.adjust();
  records.selectComponent = () => ({ dismiss() {} });
  records.saveTotal({ detail: { amount: 0 } });
  assert.equal(records.data.total, 0);
  assert.equal(records.data.empty, false);
  assert.ok(records.data.events.some(entry => entry.negative));
  records.clearDay();
  modal.success({ confirm: false });
  modal.complete();
  assert.equal(records.data.empty, false);
  records.clearDay();
  modal.success({ confirm: true });
  modal.complete();
  assert.equal(records.data.empty, true);
  mount(todayPage).goRecords();
  records.onShow();
  assert.equal(records.data.total, 370);
  assert.equal(records.data.canNext, false);
  assert.equal(app.globalData.openTodayRecords, false);
});

function serverSettings(extra) {
  return Object.assign({
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
    pauseUntil: '2026-11-18T23:59:00'
  }, extra);
}

test('settings read and write the server configuration without sending the token', async () => {
  const serverApi = require('../miniprogram/utils/server.js');
  const requests = [];
  let remote = serverSettings();
  requestHandler = options => {
    requests.push({
      url: options.url,
      method: options.method || 'GET',
      data: options.data,
      authorization: options.header && options.header.Authorization
    });
    if ((options.method || 'GET') === 'GET') options.success({ statusCode: 200, data: remote });
    else {
      remote = Object.assign({}, remote, options.data);
      options.success({ statusCode: 200, data: remote });
    }
  };
  assert.equal(serverApi.saveConnection({ serverUrl: 'https://cup.example.com/api/settings', apiToken: 'desktop-api-token' }).serverUrl, 'https://cup.example.com');
  const settings = mount(settingsPage);
  assert.equal(await settings.onShow(), true);
  assert.equal(settings.data.loaded, true);
  assert.equal(settings.data.dailyGoalMl, '2500');
  assert.equal(settings.data.mobilePushEnabled, true);
  assert.equal(settings.data.serverPaused, true);
  assert.equal(requests[0].url, 'https://cup.example.com/api/settings');
  assert.equal(requests[0].authorization, 'Bearer desktop-api-token');
  assert.equal(store.load().settings.dailyGoalMl, 2500);
  assert.equal(store.load().settings.remindersPaused, true);

  settings.updateField(control('dailyGoalMl', '0'));
  assert.equal(await settings.save(), false);
  assert.match(settings.data.error, /100–10000/);
  assert.equal(requests.filter(item => item.method === 'PUT').length, 0);
  assert.equal(remote.dailyGoalMl, 2500);

  settings.updateField(control('dailyGoalMl', '1800'));
  settings.onShow();
  assert.equal(settings.data.dailyGoalMl, '1800');
  settings.updateField(control('lunchEnabled', false));
  settings.updateField(control('mobilePushEnabled', false));
  settings.data.activeDays.slice().forEach((active, index) => {
    if (active) settings.onDay({ currentTarget: { dataset: { index } } });
  });
  assert.equal(settings.data.dayCount, 0);
  assert.match(settings.data.preview, /未选择提醒日/);
  assert.equal(await settings.save(), false);
  assert.match(settings.data.error, /至少选择一天/);
  settings.onDay({ currentTarget: { dataset: { index: 6 } } });
  assert.equal(settings.data.dayCount, 1);
  assert.match(settings.data.preview, /首次约在/);
  assert.equal(await settings.save(), true);
  assert.equal(settings.data.dirty, false);
  assert.equal(settings.data.apiToken, '');
  const put = requests.at(-1);
  assert.equal(put.method, 'PUT');
  assert.equal(put.authorization, 'Bearer desktop-api-token');
  assert.deepEqual(Object.keys(put.data).sort(), [
    'activeDays', 'dailyGoalMl', 'homeCupMl', 'lunchEnabled', 'lunchEnd', 'lunchStart',
    'minReminderGapMinutes', 'mobilePushEnabled', 'noDrinkBeforeEndMinutes', 'pauseUntil',
    'quietStart', 'remindersPaused', 'workCupMl', 'workEnd', 'workStart'
  ]);
  assert.equal(JSON.stringify(put.data).includes('desktop-api-token'), false);
  assert.equal(put.data.dailyGoalMl, 1800);
  assert.equal(put.data.workCupMl, 370);
  assert.equal(put.data.lunchEnabled, false);
  assert.equal(put.data.mobilePushEnabled, false);
  assert.equal(put.data.remindersPaused, true);
  assert.equal(put.data.pauseUntil, '2026-11-18T23:59:00');
  assert.deepEqual(put.data.activeDays, [false, false, false, false, false, false, true]);
  assert.equal(store.load().settings.dailyGoalMl, 1800);
  assert.equal(store.load().settings.remindersPaused, true);
  assert.equal(JSON.stringify(wx.getStorageSync('water-reminder-v1') || {}).includes('desktop-api-token'), false);
  assert.equal(JSON.stringify(store.load()).includes('desktop-api-token'), false);

  settings.disconnect();
  assert.equal(serverApi.loadConnection().configured, false);
  assert.equal(settings.data.loaded, false);
});

test('a rejected server save keeps the draft and the previous server values', async () => {
  const serverApi = require('../miniprogram/utils/server.js');
  const remote = serverSettings();
  requestHandler = options => {
    if ((options.method || 'GET') === 'GET') options.success({ statusCode: 200, data: remote });
    else options.success({ statusCode: 400, data: { error: '每日目标需为 100–10000 ml 的整数。' } });
  };
  serverApi.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  const settings = mount(settingsPage);
  await settings.onShow();
  settings.updateField(control('dailyGoalMl', '1800'));
  assert.equal(await settings.save(), false);
  assert.equal(settings.data.dirty, true);
  assert.equal(settings.data.dailyGoalMl, '1800');
  assert.match(settings.data.error, /100–10000/);
  assert.equal(store.load().settings.dailyGoalMl, 2500);
  assert.equal(remote.dailyGoalMl, 2500);
});
