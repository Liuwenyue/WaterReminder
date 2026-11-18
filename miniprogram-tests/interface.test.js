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
global.wx = {
  getStorageSync: key => structuredClone(storage.get(key)),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  hideTabBar: () => { tabVisible = false; },
  showTabBar: () => { tabVisible = true; },
  showModal: options => { modal = options; },
  switchTab() {},
  showToast() {}
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

test('settings preserve unsaved edits between tabs, validate before writing and preview the chosen days', () => {
  const settings = mount(settingsPage);
  settings.onShow();
  settings.updateField(control('dailyGoalMl', '0'));
  settings.save();
  assert.ok(settings.data.error);
  assert.equal(store.load().settings.dailyGoalMl, calc.defaultSettings().dailyGoalMl);
  settings.updateField(control('dailyGoalMl', '1800'));
  settings.onShow();
  assert.equal(settings.data.dailyGoalMl, '1800');
  settings.updateField(control('lunchEnabled', false));
  settings.data.activeDays.slice().forEach((active, index) => {
    if (active) settings.onDay({ currentTarget: { dataset: { index } } });
  });
  assert.equal(settings.data.dayCount, 0);
  assert.match(settings.data.preview, /未选择提醒日/);
  settings.onDay({ currentTarget: { dataset: { index: 6 } } });
  assert.equal(settings.data.dayCount, 1);
  assert.match(settings.data.preview, /首次约在/);
  settings.save();
  assert.equal(settings.data.dirty, false);
  const saved = store.load().settings;
  assert.equal(saved.dailyGoalMl, 1800);
  assert.equal(saved.lunchEnabled, false);
  assert.deepEqual(saved.activeDays, [false, false, false, false, false, false, true]);
});
